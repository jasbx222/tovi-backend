using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Tovi.Api.Data;
using Tovi.Api.Endpoints;
using Tovi.Api.Jobs;
using Tovi.Api.Security;
using Tovi.Domain;

namespace Tovi.Tests;

public sealed class MutableClock : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
}

public sealed class CapturingOtp : IOtpSender
{
    public string? LastCode { get; private set; }
    public bool Deliver { get; set; } = true;
    public Task<bool> SendAsync(string phone, string code, CancellationToken cancellationToken)
    {
        LastCode = code;
        return Task.FromResult(Deliver);
    }
}

public sealed class FakeGateway : IPaymentGateway
{
    public long LastAmount { get; private set; }
    public RemotePaymentStatus Status { get; set; } = RemotePaymentStatus.Pending;
    public Task<string> StartAsync(Guid paymentId, long amountIqd, CancellationToken cancellationToken)
    {
        LastAmount = amountIqd;
        return Task.FromResult($"https://payments.test/checkout/{paymentId}");
    }
    public Task<RemotePaymentStatus> GetStatusAsync(Guid paymentId, CancellationToken cancellationToken) => Task.FromResult(Status);
}

public sealed class ToviFactory : WebApplicationFactory<Program>
{
    public MutableClock Clock { get; } = new();
    public CapturingOtp Otp { get; } = new();
    public FakeGateway Gateway { get; } = new();
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"tovi-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Tovi", $"Data Source={DbPath}");
        builder.UseSetting("Jwt:Key", "test-signing-key-tovi-academy-32bytes-ok");
        builder.UseSetting("Jwt:Issuer", "tovi");
        builder.UseSetting("Jwt:Audience", "tovi");
        builder.UseSetting("Payments:WebhookSecret", "test-webhook");
        builder.UseSetting("Auth:PermitPerMinute", "1000");
        builder.UseSetting("Firebase:Environment", "test");
        builder.UseSetting("LiveKit:Host", "https://livekit.test");
        builder.UseSetting("LiveKit:ApiKey", "devkey");
        builder.UseSetting("LiveKit:ApiSecret", "devsecret-devsecret-devsecret");
        builder.UseSetting("LiveKit:EgressEnabled", "false");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            services.RemoveAll<IOtpSender>();
            services.AddSingleton<IOtpSender>(Otp);
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(Gateway);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(DbPath); } catch { /* the host may still hold the file */ }
    }
}

public sealed class BehaviorTests : IClassFixture<ToviFactory>
{
    readonly ToviFactory factory;
    public BehaviorTests(ToviFactory factory) => this.factory = factory;

    [Fact]
    public void Codes_Allocate_ConsumesOldestSubscriptionFirst()
    {
        var now = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var allocation = CodeQuota.Allocate(
        [
            (newer, now.AddDays(2), now.AddDays(300), 10, 0),
            (older, now.AddDays(1), now.AddDays(200), 3, 1)
        ], 4, now);
        Assert.True(allocation.Ok);
        Assert.Equal(older, allocation.Slices[0].SubscriptionId);
        Assert.Equal(2, allocation.Slices[0].Count);
        Assert.Equal(2, allocation.Slices[1].Count);
    }

    [Fact]
    public void Codes_Allocate_RejectsWhenRequestExceedsBalance()
    {
        var now = DateTime.UtcNow;
        var allocation = CodeQuota.Allocate([(Guid.NewGuid(), now, now.AddDays(10), 5, 4)], 2, now);
        Assert.False(allocation.Ok);
        Assert.Equal(1, allocation.Remaining);
        Assert.Empty(allocation.Slices);
    }

    [Fact]
    public async Task Codes_Generate_RejectsCrossTeacherAndKeepsSharedBalance()
    {
        var teacher = await User(UserType.Teacher, "07710000001");
        var other = await User(UserType.Teacher, "07710000002");
        var course = await CourseFor(teacher);
        var client = Client(await Login("teacher", "07710000002"));
        var response = await client.PostAsJsonAsync($"/api/teacher/codes?courseId={course}", new { count = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var leaked = await scope.ServiceProvider.GetRequiredService<ToviDb>().Codes.CountAsync(c => c.TargetId == course);
        Assert.Equal(0, leaked);
    }

    [Fact]
    public async Task Codes_GenerateRedeemCancel_UsesIssuedBalanceAndDoesNotRestore()
    {
        var teacher = await User(UserType.Teacher, "07710000003", approved: true);
        var student = await User(UserType.Student, "07710000004");
        var course = await CourseFor(teacher);
        var plan = await Plan(PlanAudience.ForTeacher, 1000, 3);
        await SubscriptionFor(teacher, plan, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 3);
        var teacherClient = Client(await Login("teacher", "07710000003"));
        var created = await teacherClient.PostAsJsonAsync($"/api/teacher/codes?courseId={course}", new { count = 2 });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("remaining").GetInt32());
        var code = body.GetProperty("codes")[0].GetProperty("code").GetString()!;
        Assert.Matches("^[0-9]{16}$", code);
        var codeId = body.GetProperty("codes")[0].GetProperty("id").GetGuid();

        var studentClient = Client(await Login("student", "07710000004"));
        var redeem = await studentClient.PostAsJsonAsync("/api/student/codes/redeem", new { code });
        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
        var again = await studentClient.PostAsJsonAsync("/api/student/codes/redeem", new { code });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        var issued = await Issued(teacher);
        var cancel = await teacherClient.PostAsync($"/api/teacher/codes/{codeId}/cancel", null);
        var cancelBody = await cancel.Content.ReadAsStringAsync();
        Assert.True(cancel.StatusCode == HttpStatusCode.BadRequest, cancelBody);
        Assert.Equal(issued, await Issued(teacher));

        var inactive = body.GetProperty("codes")[1].GetProperty("id").GetGuid();
        var cancelInactive = await teacherClient.PostAsync($"/api/teacher/codes/{inactive}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelInactive.StatusCode);
        Assert.Equal(issued, await Issued(teacher));

        var balance = await teacherClient.GetFromJsonAsync<JsonElement>("/api/teacher/codes/balance");
        Assert.Equal(1, balance.GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task Codes_Redeem_RejectsExpired()
    {
        var teacher = await User(UserType.Teacher, "07710000005", approved: true);
        var student = await User(UserType.Student, "07710000006");
        var course = await CourseFor(teacher);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
        db.Codes.Add(new ActivationCode
        {
            Id = Guid.NewGuid(),
            Code = "1234567890123456",
            Status = CodeStatus.Inactive,
            TargetType = AccessTarget.Course,
            TargetId = course,
            OwnerUserId = teacher,
            ExpiresAt = factory.Clock.UtcNow.AddMinutes(-1),
            CreatedAt = factory.Clock.UtcNow
        });
        await db.SaveChangesAsync();
        var response = await Client(await Login("student", "07710000006")).PostAsJsonAsync("/api/student/codes/redeem", new { code = "1234567890123456" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Plans_Assign_RejectsOpenPlanAndCashHasNoCommission()
    {
        var admin = await User(UserType.Admin, "07710000007");
        var teacher = await User(UserType.Teacher, "07710000008");
        var plan = await Plan(PlanAudience.ForTeacher, 250000, 10);
        var client = Client(await Login("admin", "07710000007"));
        var first = await client.PostAsJsonAsync("/api/admin/subscriptions", new { userId = teacher, planId = plan });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(250000, body.GetProperty("payment").GetProperty("amountIqd").GetInt64());
        Assert.Equal("Cash", body.GetProperty("payment").GetProperty("method").GetString());
        Assert.False(body.GetProperty("payment").TryGetProperty("commission", out _));
        var second = await client.PostAsJsonAsync("/api/admin/subscriptions", new { userId = teacher, planId = plan });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Plans_SelfServe_StaysWaitingUntilSignedWebhook()
    {
        var teacher = await User(UserType.Teacher, "07710000009");
        var plan = await Plan(PlanAudience.ForTeacher, 90000, 4);
        var client = Client(await Login("teacher", "07710000009"));
        var started = await client.PostAsJsonAsync("/api/teacher/subscriptions", new { userId = teacher, planId = plan });
        var body = await started.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Waiting", body.GetProperty("status").GetString());
        Assert.Equal(90000, factory.Gateway.LastAmount);
        Assert.StartsWith("https://payments.test/checkout/", body.GetProperty("paymentUrl").GetString());
        var paymentId = body.GetProperty("paymentId").GetGuid();
        var payload = JsonSerializer.Serialize(new { paymentId, status = "succeeded" });
        var bad = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        bad.Headers.Add("X-Tovi-Signature", "nope");
        var rejected = await factory.CreateClient().SendAsync(bad);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        var ok = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        ok.Headers.Add("X-Tovi-Signature", Signatures.Sign("test-webhook", payload));
        var accepted = await factory.CreateClient().SendAsync(ok);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<ToviDb>().Subscriptions.SingleAsync(s => s.UserId == teacher);
        Assert.Equal(SubscriptionStatus.Active, row.Status);
        Assert.Equal(factory.Clock.UtcNow.AddDays(365), row.EndsAt);
    }

    [Fact]
    public async Task Hours_CreateSession_DeductsDurationAndCancelDoesNotRefund()
    {
        var teacher = await User(UserType.Teacher, "07710000010");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            var package = new HourPackage { Id = Guid.NewGuid(), Name = "شهر", MonthlyHoursLimit = 2, MonthlyMinutesLimit = 120, ValidityDays = 30, IsActive = true, AttendanceEnabled = true };
            db.HourPackages.Add(package);
            db.HourPackageCodes.Add(new HourPackageCode { Id = Guid.NewGuid(), PackageId = package.Id, Code = "9999000011112222", ExpiresAt = factory.Clock.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
        }
        var client = Client(await Login("teacher", "07710000010"));
        var redeemed = await client.PostAsJsonAsync("/api/teacher/hour-packages/redeem", new { code = "9999000011112222" });
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var room = await client.PostAsJsonAsync("/api/teacher/classrooms", new { title = "قاعة" });
        var roomId = (await room.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var start = factory.Clock.UtcNow.AddHours(1);
        var session = await client.PostAsJsonAsync("/api/teacher/sessions", new { classroomId = roomId, startsAt = start, durationMinutes = 90 });
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var sessionId = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var overlap = await client.PostAsJsonAsync("/api/teacher/sessions", new { classroomId = roomId, startsAt = start.AddMinutes(30), durationMinutes = 30 });
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        var cancel = await client.PostAsync($"/api/teacher/sessions/{sessionId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await using var check = factory.Services.CreateAsyncScope();
        var left = await check.ServiceProvider.GetRequiredService<ToviDb>().HourSubscriptions.Where(s => s.TeacherId == teacher && s.Active).Select(s => s.RemainingMinutes).SingleAsync();
        Assert.Equal(30, left);
        Assert.Equal(0.50m, HourRules.DisplayHours(left));
    }

    [Fact]
    public async Task Hours_InstitutePool_UsesRequestedClassroom()
    {
        var institute = await User(UserType.InstituteManager, "07710000011");
        var teacher = await User(UserType.Teacher, "07710000012");
        Guid roomId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            var pool = await db.HourPools.FirstAsync(p => p.InstituteId == institute);
            pool.RemainingMinutes = 60;
            db.InstituteTeachers.Add(new InstituteTeacher { Id = Guid.NewGuid(), InstituteId = institute, TeacherId = teacher });
            roomId = Guid.NewGuid();
            db.Classrooms.Add(new VirtualClassroom { Id = roomId, TeacherId = teacher, Title = "مختبر", CreatedAt = factory.Clock.UtcNow });
            await db.SaveChangesAsync();
        }
        var client = Client(await Login("institute", "07710000011"));
        var response = await client.PostAsJsonAsync("/api/institute/sessions", new { classroomId = roomId, startsAt = factory.Clock.UtcNow.AddHours(2), durationMinutes = 60 });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(roomId, session.GetProperty("classroomId").GetGuid());
        Assert.NotEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), session.GetProperty("classroomId").GetGuid());
    }

    [Fact]
    public async Task Exams_Correct_RejectsSecondCorrectionAndMarkAboveQuestion()
    {
        var teacher = await User(UserType.Teacher, "07710000013");
        var student = await User(UserType.Student, "07710000014");
        var client = Client(await Login("teacher", "07710000013"));
        var examResponse = await client.PostAsJsonAsync("/api/teacher/exams", new { title = "اختبار النص", mark = 100, endDate = factory.Clock.UtcNow.AddDays(2), groupIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.OK, examResponse.StatusCode);
        var examId = (await examResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await client.PostAsJsonAsync($"/api/teacher/exams/{examId}/questions", new { type = "OpenEnded", text = "اشرح", mark = 40 });
        await client.PostAsJsonAsync($"/api/teacher/exams/{examId}/questions", new { type = "MultipleChoice", text = "اختر", mark = 60, options = new[] { new { text = "نعم", correct = true }, new { text = "لا", correct = false } } });
        Guid optionId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            optionId = await db.QuestionOptions.Select(o => o.Id).FirstAsync();
            db.Codes.Add(new ActivationCode { Id = Guid.NewGuid(), Code = "5555666677778888", TargetType = AccessTarget.Exam, TargetId = examId, OwnerUserId = teacher, Status = CodeStatus.Inactive, CreatedAt = factory.Clock.UtcNow });
            await db.SaveChangesAsync();
        }
        var studentClient = Client(await Login("student", "07710000014"));
        Assert.Equal(HttpStatusCode.OK, (await studentClient.PostAsJsonAsync("/api/student/codes/redeem", new { code = "5555666677778888" })).StatusCode);
        var questions = await client.GetFromJsonAsync<JsonElement>($"/api/teacher/exams/{examId}/questions");
        var openId = questions.EnumerateArray().First(q => q.GetProperty("type").GetString() == "OpenEnded").GetProperty("id").GetGuid();
        var choiceId = questions.EnumerateArray().First(q => q.GetProperty("type").GetString() == "MultipleChoice").GetProperty("id").GetGuid();
        var submit = await studentClient.PostAsJsonAsync($"/api/student/exams/{examId}/attempts", new { answers = new[] { new { questionId = openId, response = "جواب" }, new { questionId = choiceId, response = optionId.ToString() } } });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        await using var read = factory.Services.CreateAsyncScope();
        var answerId = await read.ServiceProvider.GetRequiredService<ToviDb>().Answers.Where(a => !a.Corrected).Select(a => a.Id).SingleAsync();
        var tooHigh = await client.PostAsJsonAsync($"/api/teacher/answers/{answerId}/correction", new { mark = 41 });
        Assert.Equal(HttpStatusCode.BadRequest, tooHigh.StatusCode);
        var ok = await client.PostAsJsonAsync($"/api/teacher/answers/{answerId}/correction", new { mark = 10 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var second = await client.PostAsJsonAsync($"/api/teacher/answers/{answerId}/correction", new { mark = 10 });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Exams_StatusJob_MovesToUnderCorrectionThenCompleted()
    {
        var teacher = await User(UserType.Teacher, "07710000015");
        var student = await User(UserType.Student, "07710000016");
        Guid examId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            examId = Guid.NewGuid();
            db.Exams.Add(new Exam { Id = examId, TeacherId = teacher, Title = "حالة", Mark = 10, EndDate = factory.Clock.UtcNow.AddDays(1), CreatedAt = factory.Clock.UtcNow });
            var question = new Question { Id = Guid.NewGuid(), ExamId = examId, Type = QuestionType.OpenEnded, Text = "سؤال", Mark = 10 };
            db.Questions.Add(question);
            db.Enrollments.Add(new Enrollment { Id = Guid.NewGuid(), StudentId = student, TargetType = AccessTarget.Exam, TargetId = examId, CodeId = Guid.NewGuid(), CreatedAt = factory.Clock.UtcNow });
            await db.SaveChangesAsync();
        }
        await factory.Services.GetRequiredService<ToviJobs>().ExamTransitions();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            Assert.Equal(ExamStatus.New, (await db.Exams.FindAsync(examId))!.Status);
            var attemptId = Guid.NewGuid();
            db.Attempts.Add(new ExamAttempt { Id = attemptId, ExamId = examId, StudentId = student, SubmittedAt = factory.Clock.UtcNow });
            db.Answers.Add(new ExamAnswer { Id = Guid.NewGuid(), AttemptId = attemptId, QuestionId = db.Questions.First(q => q.ExamId == examId).Id, Response = "x", Corrected = false });
            await db.SaveChangesAsync();
        }
        await factory.Services.GetRequiredService<ToviJobs>().ExamTransitions();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var status = (await scope.ServiceProvider.GetRequiredService<ToviDb>().Exams.FindAsync(examId))!.Status;
            Assert.Equal(ExamStatus.UnderCorrection, status);
            var dbFix = scope.ServiceProvider.GetRequiredService<ToviDb>();
            var answer = dbFix.Answers.Single(a => dbFix.Attempts.Any(t => t.Id == a.AttemptId && t.ExamId == examId));
            answer.Corrected = true;
            answer.Mark = 4;
            await scope.ServiceProvider.GetRequiredService<ToviDb>().SaveChangesAsync();
        }
        await factory.Services.GetRequiredService<ToviJobs>().ExamTransitions();
        await using var done = factory.Services.CreateAsyncScope();
        Assert.Equal(ExamStatus.Completed, (await done.ServiceProvider.GetRequiredService<ToviDb>().Exams.FindAsync(examId))!.Status);
    }

    [Fact]
    public async Task Auth_Login_Returns401WhenSessionDead()
    {
        await User(UserType.Admin, "07710000017");
        var teacher = await User(UserType.Teacher, "07710000018");
        var admin = Client(await Login("admin", "07710000017"));
        var token = await Login("teacher", "07710000018");
        var teacherClient = Client(token);
        Assert.Equal(HttpStatusCode.OK, (await teacherClient.GetAsync("/api/auth/me")).StatusCode);
        var disabled = await admin.PostAsync($"/api/admin/teachers/{teacher}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await teacherClient.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Auth_Access_ReplacesLegacyPermissionGates()
    {
        var admin = await User(UserType.Admin, "07710000019");
        await Employee(admin, "07710000020", "can-view-teachers");
        var adsOnly = await Employee(admin, "07710000021", "can-view-advertisements");
        var codesOnly = await Employee(admin, "07710000022", "can-view-codes");
        var teachers = Client(await Login("admin", "07710000020"));
        Assert.Equal(HttpStatusCode.Forbidden, (await teachers.GetAsync("/api/admin/institutes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await teachers.GetAsync("/api/admin/teachers")).StatusCode);
        var guides = Client(await Login("admin", adsOnly.Phone));
        Assert.Equal(HttpStatusCode.Forbidden, (await guides.GetAsync("/api/admin/guides")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guides.GetAsync("/api/admin/advertisements")).StatusCode);
        var invoices = Client(await Login("admin", codesOnly.Phone));
        Assert.Equal(HttpStatusCode.Forbidden, (await invoices.GetAsync("/api/admin/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await invoices.GetAsync("/api/admin/codes")).StatusCode);

        var institute = await User(UserType.InstituteManager, "07710000023");
        await Employee(institute, "07710000024", "can-view-students");
        await Employee(institute, "07710000025", "can-delete-chapter");
        var students = Client(await Login("institute", "07710000024"));
        Assert.Equal(HttpStatusCode.Forbidden, (await students.GetAsync("/api/institute/employees")).StatusCode);
        var exams = Client(await Login("institute", "07710000025"));
        Assert.Equal(HttpStatusCode.Forbidden, (await exams.GetAsync("/api/institute/exams")).StatusCode);
    }

    [Fact]
    public async Task Questions_Delete_IsNotGatedByDeleteCourse()
    {
        var teacher = await User(UserType.Teacher, "07710000026");
        var assistant = await Employee(teacher, "07710000027", "can-delete-course", "can-view-questions");
        Guid questionId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            var examId = Guid.NewGuid();
            db.Exams.Add(new Exam { Id = examId, TeacherId = teacher, Title = "س", Mark = 10, EndDate = factory.Clock.UtcNow.AddDays(1), CreatedAt = factory.Clock.UtcNow });
            questionId = Guid.NewGuid();
            db.Questions.Add(new Question { Id = questionId, ExamId = examId, Type = QuestionType.OpenEnded, Text = "نص", Mark = 10 });
            await db.SaveChangesAsync();
        }
        var client = Client(await Login("teacher", assistant.Phone));
        var response = await client.DeleteAsync($"/api/teacher/questions/{questionId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Pagination_RejectsOversizedPageBeforeQuery()
    {
        await User(UserType.Admin, "07710000028");
        var client = Client(await Login("admin", "07710000028"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/students?pageSize=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/students?pageSize=51")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/students?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/students")).StatusCode);
    }

    [Fact]
    public async Task Content_Format_DeletesEnrollmentsButReorderDoesNot()
    {
        var teacher = await User(UserType.Teacher, "07710000029");
        var student = await User(UserType.Student, "07710000030");
        var course = await CourseFor(teacher);
        Guid lessonA;
        Guid lessonB;
        Guid chapter;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            chapter = Guid.NewGuid();
            db.Chapters.Add(new Chapter { Id = chapter, CourseId = course, Title = "فصل", SortOrder = 0 });
            lessonA = Guid.NewGuid();
            lessonB = Guid.NewGuid();
            db.Lessons.Add(new Lesson { Id = lessonA, ChapterId = chapter, Title = "أ", SortOrder = 0 });
            db.Lessons.Add(new Lesson { Id = lessonB, ChapterId = chapter, Title = "ب", SortOrder = 1 });
            db.Enrollments.Add(new Enrollment { Id = Guid.NewGuid(), StudentId = student, TargetType = AccessTarget.Course, TargetId = course, CodeId = Guid.NewGuid(), CreatedAt = factory.Clock.UtcNow });
            db.LessonProgress.Add(new LessonProgress { Id = Guid.NewGuid(), StudentId = student, LessonId = lessonA, CompletedAt = factory.Clock.UtcNow });
            await db.SaveChangesAsync();
        }
        var client = Client(await Login("teacher", "07710000029"));
        var reorder = await client.PutAsJsonAsync($"/api/teacher/chapters/{chapter}/lessons/order", new { lessonIds = new[] { lessonB, lessonA } });
        Assert.Equal(HttpStatusCode.OK, reorder.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            Assert.Equal(1, await db.Enrollments.CountAsync(e => e.TargetId == course));
            Assert.Equal(1, await db.LessonProgress.CountAsync());
        }
        var format = await client.PostAsync($"/api/teacher/courses/{course}/format", null);
        Assert.Equal(HttpStatusCode.OK, format.StatusCode);
        await using var after = factory.Services.CreateAsyncScope();
        var check = after.ServiceProvider.GetRequiredService<ToviDb>();
        Assert.Equal(0, await check.Enrollments.CountAsync(e => e.TargetId == course));
        Assert.Equal(0, await check.LessonProgress.CountAsync());
    }

    [Fact]
    public async Task Teachers_Expiry_IsSubscriptionEndNotCreatedPlusOneYear()
    {
        var created = factory.Clock.UtcNow.AddDays(-400);
        var ends = factory.Clock.UtcNow.AddDays(10);
        var admin = await User(UserType.Admin, "07710000031");
        var teacher = await User(UserType.Teacher, "07710000032", createdAt: created);
        var plan = await Plan(PlanAudience.ForTeacher, 10, 2);
        await SubscriptionFor(teacher, plan, factory.Clock.UtcNow.AddDays(-1), 2, ends);
        var client = Client(await Login("admin", "07710000031"));
        var page = await client.GetFromJsonAsync<JsonElement>("/api/admin/teachers?q=07710000032");
        var row = page.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(ends, row.GetProperty("subscriptionEndsAt").GetDateTime().ToUniversalTime());
        Assert.NotEqual(created.AddYears(1), row.GetProperty("subscriptionEndsAt").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Trial_Write_RequiresQualifyingSubscription()
    {
        var teacher = await User(UserType.Teacher, "07710000033", trialEndsAt: factory.Clock.UtcNow.AddMinutes(-5));
        var client = Client(await Login("teacher", "07710000033"));
        var response = await client.PostAsJsonAsync("/api/teacher/courses", new { title = "بعد التجربة" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("trial_ended", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Auth_RegisterInstitute_DoesNotRequireAgreement()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/institute/register", new
        {
            instituteName = "معهد النور",
            firstName = "ليث",
            phone = "07720000001",
            phone2 = "07720000002",
            address = "بغداد",
            description = "وصف",
            password = "password1"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Auth_RegisterInstitute_RejectsAPhoneAlreadyUsed()
    {
        await User(UserType.Student, "07720000011");
        var client = factory.CreateClient();
        var taken = await client.PostAsJsonAsync("/api/auth/institute/register", new
        {
            instituteName = "معهد مكرر",
            firstName = "ليث",
            phone = "07720000011",
            phone2 = "07720000012",
            address = "بغداد",
            description = "وصف",
            password = "password1"
        });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("phone_taken", await taken.Content.ReadAsStringAsync());

        var same = await client.PostAsJsonAsync("/api/auth/institute/register", new
        {
            instituteName = "معهد مكرر",
            firstName = "ليث",
            phone = "07720000013",
            phone2 = "07720000013",
            address = "بغداد",
            description = "وصف",
            password = "password1"
        });
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
    }

    [Fact]
    public async Task Students_Create_RejectsAPhoneAlreadyUsed()
    {
        await User(UserType.InstituteManager, "07720000021");
        await User(UserType.Student, "07720000022");
        var client = Client(await Login("institute", "07720000021"));
        var response = await client.PostAsJsonAsync("/api/institute/students", new
        {
            firstName = "طالب",
            lastName = "مكرر",
            phone = "07720000022",
            password = "password1",
            kind = "External"
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("phone_taken", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Exams_Paper_HidesAnswersUntilTheStudentIsEnrolled()
    {
        var teacher = await User(UserType.Teacher, "07710000038");
        var student = await User(UserType.Student, "07710000039");
        Guid examId;
        Guid questionId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            examId = Guid.NewGuid();
            questionId = Guid.NewGuid();
            var codeId = Guid.NewGuid();
            db.Exams.Add(new Exam { Id = examId, TeacherId = teacher, Title = "ورقة", Mark = 10, EndDate = factory.Clock.UtcNow.AddDays(1), CreatedAt = factory.Clock.UtcNow });
            db.Questions.Add(new Question { Id = questionId, ExamId = examId, Type = QuestionType.TrueFalse, Text = "صح؟", Mark = 10, CorrectBool = true });
            db.Codes.Add(new ActivationCode { Id = codeId, Code = "1000000000000038", OwnerUserId = teacher, TargetType = AccessTarget.Exam, TargetId = examId, Status = CodeStatus.Active, CreatedAt = factory.Clock.UtcNow });
            await db.SaveChangesAsync();
        }

        var locked = await Client(await Login("student", "07710000039")).GetAsync($"/api/student/exams/{examId}");
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            var codeId = await db.Codes.Where(c => c.TargetId == examId).Select(c => c.Id).SingleAsync();
            db.Enrollments.Add(new Enrollment { Id = Guid.NewGuid(), StudentId = student, TargetType = AccessTarget.Exam, TargetId = examId, CodeId = codeId, CreatedAt = factory.Clock.UtcNow });
            await db.SaveChangesAsync();
        }

        var paper = await Client(await Login("student", "07710000039")).GetStringAsync($"/api/student/exams/{examId}");
        Assert.Contains(questionId.ToString(), paper);
        Assert.DoesNotContain("correctBool", paper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isCorrect", paper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Auth_Login_AcceptsFreshPasswordWhenStoredSessionIsGone()
    {
        var admin = await User(UserType.Admin, "07710000041");
        var stale = await Login("admin", "07710000041");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            var rows = await db.Sessions.Where(session => session.UserId == admin).ToListAsync();
            db.Sessions.RemoveRange(rows);
            await db.SaveChangesAsync();
        }

        var blocked = await Client(stale).GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stale);
        var again = await client.PostAsJsonAsync("/api/auth/admin/login", new { phone = "07710000041", password = "password1" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Auth_Login_AllowsOnlyLinkedTeacherOnInstitutePortal()
    {
        var institute = await User(UserType.InstituteManager, "07710000035");
        var teacher = await User(UserType.Teacher, "07710000036");
        await User(UserType.Teacher, "07710000037");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            db.InstituteTeachers.Add(new InstituteTeacher { Id = Guid.NewGuid(), InstituteId = institute, TeacherId = teacher });
            await db.SaveChangesAsync();
        }

        var denied = await factory.CreateClient().PostAsJsonAsync("/api/auth/institute/login", new { phone = "07710000037", password = "password1" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var token = await Login("institute", "07710000036");
        var me = await Client(token).GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("Teacher", me.GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(token).GetAsync("/api/institute/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(token).GetAsync("/api/teacher/sessions")).StatusCode);
    }

    [Fact]
    public async Task Auth_Profile_DoesNotReturnPasswordHash()
    {
        await User(UserType.Teacher, "07710000040");
        var body = await Client(await Login("teacher", "07710000040")).GetStringAsync("/api/teacher/profile");
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password1", body);
    }

    [Fact]
    public async Task Auth_Otp_DoesNotClaimDeliveryWhenSmsIsUnconfigured()
    {
        factory.Otp.Deliver = false;
        try
        {
            var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/student/otp", new { phone = "07730000001" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("sms_unconfigured", await response.Content.ReadAsStringAsync());
            await using var scope = factory.Services.CreateAsyncScope();
            var stored = await scope.ServiceProvider.GetRequiredService<ToviDb>().Otps.AnyAsync(o => o.Phone == "07730000001");
            Assert.False(stored);
        }
        finally
        {
            factory.Otp.Deliver = true;
        }
    }

    [Fact]
    public async Task Students_Parameters_ReturnOnlyThePrivacyUrl()
    {
        await User(UserType.Student, "07710000042");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            if (!await db.Parameters.AnyAsync(p => p.Key == "privacy_url"))
                db.Parameters.Add(new SystemParameter { Key = "privacy_url", Value = "/api/files/privacy" });
            if (!await db.Parameters.AnyAsync(p => p.Key == "otp_length"))
                db.Parameters.Add(new SystemParameter { Key = "otp_length", Value = "6" });
            await db.SaveChangesAsync();
        }
        var body = await Client(await Login("student", "07710000042")).GetStringAsync("/api/student/parameters");
        Assert.Contains("privacy_url", body);
        Assert.DoesNotContain("otp_length", body);
    }

    [Fact]
    public async Task Meetings_Mute_RequiresTheManageMeetingPermission()
    {
        var teacher = await User(UserType.Teacher, "07710000043");
        await Employee(teacher, "07710000044", "can-view-sessions");
        var assistant = Client(await Login("teacher", "07710000044"));
        var denied = await assistant.PostAsJsonAsync($"/api/teacher/sessions/{Guid.NewGuid()}/participants/mute", new { identity = "student:x", allStudents = false });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var owner = Client(await Login("teacher", "07710000043"));
        var missing = await owner.PostAsJsonAsync($"/api/teacher/sessions/{Guid.NewGuid()}/participants/mute", new { identity = "student:x", allStudents = false });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public void Production_RejectsDevelopmentSecrets()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "dev-only-change-me-tovi-academy-signing-key",
            ["Payments:WebhookSecret"] = "dev-webhook-secret",
            ["ConnectionStrings:Tovi"] = "Password=Tovi_Local_Sql#2026"
        }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => ProductionGuard.Ensure(new ProductionEnvironment(), config));
        Assert.Contains("JWT", error.Message);
    }

    [Fact]
    public void Files_RejectTraversalAndSpoofedBytes()
    {
        Assert.Throws<RuleException>(() => StoragePath.Combine(Path.GetTempPath(), "../" + Guid.NewGuid().ToString("N")));
        Assert.False(FilesApi.SignatureMatches("image/png", Encoding.ASCII.GetBytes("<script>")));
        Assert.True(FilesApi.SignatureMatches("application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4")));
    }

    [Fact]
    public async Task Auth_Login_RejectsMalformedJson()
    {
        var response = await factory.CreateClient().PostAsync("/api/auth/admin/login", new StringContent("{phone:1}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_body", body);
        Assert.DoesNotContain("JsonException", body);
    }

    [Fact]
    public async Task Auth_Markup_IsRejected()
    {
        await User(UserType.Admin, "07710000034");
        var client = Client(await Login("admin", "07710000034"));
        var response = await client.PostAsJsonAsync("/api/admin/subjects", new { name = "<script>alert(1)</script>" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    async Task<string> Login(string portal, string phone)
    {
        var response = await factory.CreateClient().PostAsJsonAsync($"/api/auth/{portal}/login", new { phone, password = "password1" });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    HttpClient Client(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    async Task<Guid> User(UserType type, string phone, bool approved = false, DateTime? trialEndsAt = null, DateTime? createdAt = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Type = type,
            Phone = phone,
            FirstName = "مستخدم",
            LastName = phone[^4..],
            InstituteName = type == UserType.InstituteManager ? "معهد" : null,
            TeacherApproved = approved,
            TrialEndsAt = trialEndsAt,
            CreatedAt = createdAt ?? factory.Clock.UtcNow
        };
        user.PasswordHash = Passwords.Hash(user, "password1");
        db.Users.Add(user);
        if (type == UserType.InstituteManager)
            db.HourPools.Add(new InstituteHourPool { InstituteId = user.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    async Task<User> Employee(Guid employer, string phone, params string[] permissions)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Type = UserType.Employee,
            Phone = phone,
            FirstName = "مساعد",
            EmployerUserId = employer,
            EmployeeStyle = EmployeeStyle.Assistant,
            CreatedAt = factory.Clock.UtcNow
        };
        user.PasswordHash = Passwords.Hash(user, "password1");
        db.Users.Add(user);
        foreach (var slug in permissions)
            db.PermissionGrants.Add(new PermissionGrant { Id = Guid.NewGuid(), UserId = user.Id, Slug = slug });
        await db.SaveChangesAsync();
        return user;
    }

    async Task<Guid> CourseFor(Guid teacher)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
        var course = new Course { Id = Guid.NewGuid(), TeacherId = teacher, Title = "دورة", CreatedAt = factory.Clock.UtcNow };
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        return course.Id;
    }

    async Task<Guid> Plan(PlanAudience audience, long price, int codes)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
        var plan = new CodePlan { Id = Guid.NewGuid(), Title = "خطة " + Guid.NewGuid().ToString("N")[..8], Description = "وصف", Audience = audience, PriceYearly = price, CodesCount = codes };
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    async Task SubscriptionFor(Guid user, Guid plan, DateTime starts, int count, DateTime? ends = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = user,
            PlanId = plan,
            Status = SubscriptionStatus.Active,
            StartsAt = starts,
            EndsAt = ends ?? starts.AddDays(365),
            PlanCodesCount = count,
            CreatedAt = starts
        });
        await db.SaveChangesAsync();
    }

    async Task<int> Issued(Guid user)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ToviDb>().Subscriptions.Where(s => s.UserId == user).SumAsync(s => s.CodesIssued);
    }

    async Task<int> Count<T>() where T : class
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ToviDb>().Set<T>().CountAsync();
    }
}

sealed class ProductionEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ApplicationName { get; set; } = "tovi";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
