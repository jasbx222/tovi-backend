using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tovi.Api.Data;
using Tovi.Api.Jobs;
using Tovi.Domain;

namespace Tovi.Tests;

public sealed partial class BehaviorTests
{
    [Fact]
    public async Task Session_Access_RejectsAnonymousWrongPortalAndDeadEmployer()
    {
        const string hidden = "07741000001";
        await User(UserType.Student, hidden);
        var anonymous = await factory.CreateClient().GetAsync("/api/admin/students");
        var anonymousBody = await anonymous.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("session_dead", anonymousBody);
        Assert.DoesNotContain(hidden, anonymousBody);

        var student = Client(await Login("student", hidden));
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/admin/students")).StatusCode);

        var admin = await User(UserType.Admin, "07741000002");
        var teacher = await User(UserType.Teacher, "07741000003");
        var employee = await Employee(teacher, "07741000004", "can-view-students");
        var staff = Client(await Login("teacher", employee.Phone));
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/teacher/students")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().PostAsJsonAsync("/api/auth/admin/login", new { phone = employee.Phone, password = "password1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().PostAsJsonAsync("/api/auth/institute/login", new { phone = employee.Phone, password = "password1" })).StatusCode);

        var outside = await Client(await Login("teacher", "07741000003")).PostAsJsonAsync("/api/teacher/employees", new
        {
            firstName = "خارج",
            lastName = "الجهة",
            phone = "07741000005",
            password = "password1",
            style = "Assistant",
            permissions = new[] { "can-view-schools" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Contains("unknown_permission", await outside.Content.ReadAsStringAsync());
        Assert.False(await Read(db => db.Users.AnyAsync(u => u.Phone == "07741000005")));

        Assert.Equal(HttpStatusCode.OK, (await Client(await Login("admin", "07741000002")).PostAsync($"/api/admin/teachers/{teacher}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/teacher/students")).StatusCode);
        var relogin = await factory.CreateClient().PostAsJsonAsync("/api/auth/teacher/login", new { phone = employee.Phone, password = "password1" });
        Assert.Equal(HttpStatusCode.Forbidden, relogin.StatusCode);
        Assert.Contains("employer", await relogin.Content.ReadAsStringAsync());
        _ = admin;
    }

    [Fact]
    public async Task Ownership_StaysInsideTheSessionParty()
    {
        var teacher = await User(UserType.Teacher, "07741000011");
        var other = await User(UserType.Teacher, "07741000012");
        var client = Client(await Login("teacher", "07741000011"));
        var crossed = await client.PostAsJsonAsync("/api/teacher/courses", new { title = "ليست لي", teacherId = other });
        Assert.Equal(HttpStatusCode.Forbidden, crossed.StatusCode);
        Assert.Equal(0, await Read(db => db.Courses.CountAsync(c => c.TeacherId == other && c.Title == "ليست لي")));

        var instituteA = await User(UserType.InstituteManager, "07741000013");
        var instituteB = await User(UserType.InstituteManager, "07741000014");
        var schoolA = await Json(await Client(await Login("institute", "07741000013")).PostAsJsonAsync("/api/institute/schools", new { name = "مدرسة أ" }));
        var schoolB = await Json(await Client(await Login("institute", "07741000014")).PostAsJsonAsync("/api/institute/schools", new { name = "مدرسة ب" }));
        var foreignSchool = await Client(await Login("institute", "07741000013")).PostAsJsonAsync("/api/institute/students", new
        {
            firstName = "طالب",
            lastName = "غريب",
            phone = "07741000015",
            password = "password1",
            kind = "External",
            schoolId = schoolB.GetProperty("id").GetGuid()
        });
        Assert.Equal(HttpStatusCode.Forbidden, foreignSchool.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(await Login("institute", "07741000014")).PostAsJsonAsync("/api/institute/students", new
        {
            firstName = "طالب",
            lastName = "باء",
            phone = "07741000016",
            password = "password1",
            kind = "External",
            schoolId = schoolB.GetProperty("id").GetGuid()
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(await Login("institute", "07741000013")).PostAsJsonAsync("/api/institute/students", new
        {
            firstName = "طالب",
            lastName = "ألف",
            phone = "07741000017",
            password = "password1",
            kind = "External",
            schoolId = schoolA.GetProperty("id").GetGuid()
        })).StatusCode);

        var listA = await Client(await Login("institute", "07741000013")).GetStringAsync("/api/institute/students");
        Assert.Contains("07741000017", listA);
        Assert.DoesNotContain("07741000016", listA);
        var listB = await Client(await Login("institute", "07741000014")).GetStringAsync("/api/institute/students");
        Assert.Contains("07741000016", listB);
        Assert.DoesNotContain("07741000017", listB);

        var bought = await Client(await Login("teacher", "07741000011")).PostAsJsonAsync("/api/teacher/subscriptions", new { userId = other, planId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, bought.StatusCode);
        _ = instituteA;
        _ = instituteB;
        _ = teacher;
    }

    [Fact]
    public async Task Trial_Write_FollowsOpenTrialOrRemainingSubscription()
    {
        var teacher = await User(UserType.Teacher, "07741000021", trialEndsAt: factory.Clock.UtcNow.AddDays(2));
        var client = Client(await Login("teacher", "07741000021"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/teacher/courses", new { title = "أثناء التجربة" })).StatusCode);

        await Mutate(async db =>
        {
            (await db.Users.FirstAsync(u => u.Id == teacher)).TrialEndsAt = factory.Clock.UtcNow.AddMinutes(-1);
        });
        var ended = await client.PostAsJsonAsync("/api/teacher/courses", new { title = "بعد التجربة" });
        Assert.Equal(HttpStatusCode.Forbidden, ended.StatusCode);
        Assert.Contains("trial_ended", await ended.Content.ReadAsStringAsync());

        var plan = await Plan(PlanAudience.ForTeacher, 10, 3);
        await SubscriptionFor(teacher, plan, factory.Clock.UtcNow.AddDays(-2), 3, factory.Clock.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/teacher/courses", new { title = "اشتراك منته" })).StatusCode);

        await Mutate(async db =>
        {
            var row = await db.Subscriptions.SingleAsync(s => s.UserId == teacher);
            row.EndsAt = factory.Clock.UtcNow.AddDays(10);
            row.CodesIssued = row.PlanCodesCount;
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/teacher/courses", new { title = "رصيد نافد" })).StatusCode);

        await Mutate(async db =>
        {
            (await db.Subscriptions.SingleAsync(s => s.UserId == teacher)).CodesIssued = 0;
        });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/teacher/courses", new { title = "رصيد باق" })).StatusCode);

        var institute = await User(UserType.InstituteManager, "07741000022", trialEndsAt: factory.Clock.UtcNow.AddMinutes(-1));
        var linked = await User(UserType.Teacher, "07741000023");
        await Mutate(db =>
        {
            db.InstituteTeachers.Add(new InstituteTeacher { Id = Guid.NewGuid(), InstituteId = institute, TeacherId = linked });
            return Task.CompletedTask;
        });
        var instituteClient = Client(await Login("institute", "07741000022"));
        Assert.Equal(HttpStatusCode.Forbidden, (await instituteClient.PostAsJsonAsync("/api/institute/courses", new { title = "معهد بعد التجربة", teacherId = linked })).StatusCode);
        await SubscriptionFor(institute, await Plan(PlanAudience.ForInstitute, 10, 2), factory.Clock.UtcNow.AddDays(-1), 2);
        Assert.Equal(HttpStatusCode.OK, (await instituteClient.PostAsJsonAsync("/api/institute/courses", new { title = "معهد برصيد", teacherId = linked })).StatusCode);
    }

    [Fact]
    public async Task Export_FollowsApprovalLimitAndInstituteSubscription()
    {
        var limited = await User(UserType.Teacher, "07741000031");
        var open = await User(UserType.Teacher, "07741000032");
        var approved = await User(UserType.Teacher, "07741000033", approved: true);
        var subscribed = await User(UserType.Teacher, "07741000034", approved: true);
        await Mutate(async db =>
        {
            (await db.Users.FirstAsync(u => u.Id == limited)).ExportCodesLimit = 1;
            (await db.Users.FirstAsync(u => u.Id == approved)).ExportCodesLimit = 100;
        });
        var limitedCourse = await CourseFor(limited);
        var openCourse = await CourseFor(open);
        var approvedCourse = await CourseFor(approved);
        var subscribedCourse = await CourseFor(subscribed);
        await AddCode(limited, limitedCourse, "4100000000000031");
        await AddCode(open, openCourse, "4100000000000032");
        await AddCode(open, openCourse, "4100000000000033");
        await AddCode(approved, approvedCourse, "4100000000000034");
        await AddCode(subscribed, subscribedCourse, "4100000000000035");
        await SubscriptionFor(subscribed, await Plan(PlanAudience.ForTeacher, 10, 2), factory.Clock.UtcNow.AddDays(-1), 2);

        Assert.Equal(HttpStatusCode.OK, (await Client(await Login("teacher", "07741000031")).GetAsync($"/api/teacher/courses/{limitedCourse}/codes/export")).StatusCode);
        await AddCode(limited, limitedCourse, "4100000000000036");
        var over = await Client(await Login("teacher", "07741000031")).GetAsync($"/api/teacher/courses/{limitedCourse}/codes/export");
        Assert.Equal(HttpStatusCode.Forbidden, over.StatusCode);
        Assert.Contains("export_limit", await over.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Client(await Login("teacher", "07741000032")).GetAsync($"/api/teacher/courses/{openCourse}/codes/export")).StatusCode);
        var needsPlan = await Client(await Login("teacher", "07741000033")).GetAsync($"/api/teacher/courses/{approvedCourse}/codes/export");
        Assert.Equal(HttpStatusCode.Forbidden, needsPlan.StatusCode);
        Assert.Contains("subscription_required", await needsPlan.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Client(await Login("teacher", "07741000034")).GetAsync($"/api/teacher/courses/{subscribedCourse}/codes/export")).StatusCode);

        var institute = await User(UserType.InstituteManager, "07741000035");
        var teacher = await User(UserType.Teacher, "07741000036");
        var course = Guid.NewGuid();
        await Mutate(async db =>
        {
            (await db.Users.FirstAsync(u => u.Id == institute)).ExportCodesLimit = 50;
            db.InstituteTeachers.Add(new InstituteTeacher { Id = Guid.NewGuid(), InstituteId = institute, TeacherId = teacher });
            db.Courses.Add(new Course { Id = course, TeacherId = teacher, InstituteId = institute, Title = "تصدير المعهد", CreatedAt = factory.Clock.UtcNow });
            db.Codes.Add(Code(institute, course, "4100000000000037"));
        });
        var instituteClient = Client(await Login("institute", "07741000035"));
        var instituteDenied = await instituteClient.GetAsync($"/api/institute/courses/{course}/codes/export");
        Assert.Equal(HttpStatusCode.Forbidden, instituteDenied.StatusCode);
        Assert.Contains("subscription_required", await instituteDenied.Content.ReadAsStringAsync());
        await SubscriptionFor(institute, await Plan(PlanAudience.ForInstitute, 10, 2), factory.Clock.UtcNow.AddDays(-1), 2);
        Assert.Equal(HttpStatusCode.OK, (await instituteClient.GetAsync($"/api/institute/courses/{course}/codes/export")).StatusCode);
    }

    [Fact]
    public async Task Codes_IssueConsumesOldestRejectsBeforeSaveAndKeepsOneTarget()
    {
        var teacher = await User(UserType.Teacher, "07741000041", approved: true);
        var student = await User(UserType.Student, "07741000042");
        var course = await CourseFor(teacher);
        var other = await CourseFor(teacher);
        var olderPlan = await Plan(PlanAudience.ForTeacher, 10, 2);
        var newerPlan = await Plan(PlanAudience.ForTeacher, 10, 4);
        await SubscriptionFor(teacher, olderPlan, factory.Clock.UtcNow.AddDays(-10), 2, factory.Clock.UtcNow.AddDays(100));
        await SubscriptionFor(teacher, newerPlan, factory.Clock.UtcNow.AddDays(-1), 4, factory.Clock.UtcNow.AddDays(300));
        await Mutate(async db =>
        {
            (await db.Subscriptions.SingleAsync(s => s.PlanId == olderPlan)).CodesIssued = 1;
        });
        var client = Client(await Login("teacher", "07741000041"));

        var both = await client.PostAsJsonAsync($"/api/teacher/codes?courseId={course}&summaryId={other}", new { count = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Contains("target_required", await both.Content.ReadAsStringAsync());
        Assert.Equal(0, await Read(db => db.Codes.CountAsync(c => c.OwnerUserId == teacher)));

        var created = await Json(await client.PostAsJsonAsync($"/api/teacher/codes?courseId={course}", new { count = 3 }));
        Assert.Equal(2, created.GetProperty("remaining").GetInt32());
        var codes = created.GetProperty("codes").EnumerateArray().ToList();
        Assert.Equal(3, codes.Count);
        Assert.All(codes, code =>
        {
            Assert.Matches("^[0-9]{16}$", code.GetProperty("code").GetString());
            Assert.Equal("Inactive", code.GetProperty("status").GetString());
        });
        var olderEnd = await Read(async db => (await db.Subscriptions.SingleAsync(s => s.PlanId == olderPlan)).EndsAt);
        var newerEnd = await Read(async db => (await db.Subscriptions.SingleAsync(s => s.PlanId == newerPlan)).EndsAt);
        Assert.Equal(1, codes.Count(code => code.GetProperty("expiresAt").GetDateTime().ToUniversalTime() == olderEnd!.Value.ToUniversalTime()));
        Assert.Equal(2, codes.Count(code => code.GetProperty("expiresAt").GetDateTime().ToUniversalTime() == newerEnd!.Value.ToUniversalTime()));
        Assert.Equal(2, await Read(db => db.Subscriptions.Where(s => s.PlanId == olderPlan).Select(s => s.CodesIssued).SingleAsync()));
        Assert.Equal(2, await Read(db => db.Subscriptions.Where(s => s.PlanId == newerPlan).Select(s => s.CodesIssued).SingleAsync()));

        var balance = await client.GetFromJsonAsync<JsonElement>("/api/teacher/codes/balance");
        Assert.Equal(2, balance.GetProperty("remaining").GetInt32());
        var denied = await client.PostAsJsonAsync($"/api/teacher/codes?courseId={course}", new { count = 3 });
        var deniedBody = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal("code_quota_exceeded", deniedBody.GetProperty("error").GetString());
        Assert.Equal(2, deniedBody.GetProperty("remaining").GetInt32());
        Assert.Equal(3, await Read(db => db.Codes.CountAsync(c => c.OwnerUserId == teacher)));
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>("/api/teacher/codes/balance")).GetProperty("remaining").GetInt32());

        var code = codes[0].GetProperty("code").GetString()!;
        var redeemed = await Json(await Client(await Login("student", "07741000042")).PostAsJsonAsync("/api/student/codes/redeem", new { code }));
        Assert.Equal("Active", redeemed.GetProperty("status").GetString());
        Assert.Equal(course, redeemed.GetProperty("targetId").GetGuid());
        Assert.NotEqual(other, redeemed.GetProperty("targetId").GetGuid());
        Assert.Equal(student, await Read(db => db.Codes.Where(c => c.Code == code).Select(c => c.RedeemedByStudentId).SingleAsync()));
        Assert.Equal(1, await Read(db => db.Enrollments.CountAsync(e => e.StudentId == student && e.TargetId == course)));
        Assert.Equal(0, await Read(db => db.Enrollments.CountAsync(e => e.StudentId == student && e.TargetId == other)));
    }

    [Fact]
    public async Task Codes_OldLimitDoesNotReplaceSubscriptionBalance()
    {
        var teacher = await User(UserType.Teacher, "07741000043");
        var course = await CourseFor(teacher);
        await Mutate(async db =>
        {
            (await db.Users.FirstAsync(u => u.Id == teacher)).ActivationCodeLimit = 2;
        });
        var client = Client(await Login("teacher", "07741000043"));
        var first = await Json(await client.PostAsJsonAsync($"/api/teacher/codes?courseId={course}", new { count = 2 }));
        Assert.Equal(0, first.GetProperty("remaining").GetInt32());
        Assert.All(first.GetProperty("codes").EnumerateArray(), code => Assert.Equal(JsonValueKind.Null, code.GetProperty("subscriptionId").ValueKind));
        var blocked = await client.PostAsJsonAsync($"/api/teacher/codes?courseId={course}", new { count = 1 });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(0, (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("remaining").GetInt32());

        await SubscriptionFor(teacher, await Plan(PlanAudience.ForTeacher, 10, 4), factory.Clock.UtcNow.AddDays(-1), 4);
        var funded = await Json(await client.PostAsJsonAsync($"/api/teacher/codes?courseId={course}", new { count = 4 }));
        Assert.Equal(0, funded.GetProperty("remaining").GetInt32());
        Assert.All(funded.GetProperty("codes").EnumerateArray(), code => Assert.NotEqual(JsonValueKind.Null, code.GetProperty("subscriptionId").ValueKind));
        var afterPlan = await client.PostAsJsonAsync($"/api/teacher/codes?courseId={course}", new { count = 1 });
        Assert.Equal(HttpStatusCode.Conflict, afterPlan.StatusCode);
        Assert.Equal(6, await Read(db => db.Codes.CountAsync(c => c.OwnerUserId == teacher)));
    }

    [Fact]
    public async Task Codes_BatchRejectsOver100BeforeSave()
    {
        var teacher = await User(UserType.Teacher, "07741000044");
        var course = await CourseFor(teacher);
        await SubscriptionFor(teacher, await Plan(PlanAudience.ForTeacher, 10, 200), factory.Clock.UtcNow.AddDays(-1), 200);
        var client = Client(await Login("teacher", "07741000044"));
        var over = await client.PostAsJsonAsync($"/api/teacher/codes?courseId={course}", new { count = 101 });
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Contains("code_batch_limit", await over.Content.ReadAsStringAsync());
        Assert.Equal(0, await Read(db => db.Codes.CountAsync(c => c.OwnerUserId == teacher)));
        var created = await Json(await client.PostAsJsonAsync($"/api/teacher/codes?courseId={course}", new { count = 100 }));
        Assert.Equal(100, created.GetProperty("codes").GetArrayLength());
        Assert.Equal(100, created.GetProperty("remaining").GetInt32());
        Assert.Equal(100, await Read(db => db.Subscriptions.Where(s => s.UserId == teacher).Select(s => s.CodesIssued).SingleAsync()));
    }

    [Fact]
    public async Task Codes_RemovalDoesNotRestoreIssuedBalance()
    {
        var teacher = await User(UserType.Teacher, "07741000045", approved: true);
        var student = await User(UserType.Student, "07741000046");
        var course = await CourseFor(teacher);
        await SubscriptionFor(teacher, await Plan(PlanAudience.ForTeacher, 10, 4), factory.Clock.UtcNow.AddDays(-1), 4);
        var client = Client(await Login("teacher", "07741000045"));
        var created = await Json(await client.PostAsJsonAsync($"/api/teacher/codes?courseId={course}", new { count = 2 }));
        var used = created.GetProperty("codes")[0].GetProperty("code").GetString()!;
        var unusedId = created.GetProperty("codes")[1].GetProperty("id").GetGuid();
        var unused = created.GetProperty("codes")[1].GetProperty("code").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Client(await Login("student", "07741000046")).PostAsJsonAsync("/api/student/codes/redeem", new { code = used })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/teacher/courses/{course}/format", null)).StatusCode);
        Assert.Equal(0, await Read(db => db.Enrollments.CountAsync(e => e.TargetId == course)));
        Assert.Equal(2, await Issued(teacher));

        var summary = (await Json(await client.PostAsJsonAsync("/api/teacher/summaries", new { title = "ملخص", body = "نص" }))).GetProperty("id").GetGuid();
        var summaryCode = await Json(await client.PostAsJsonAsync($"/api/teacher/codes?summaryId={summary}", new { count = 1 }));
        var summaryValue = summaryCode.GetProperty("codes")[0].GetProperty("code").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Client(await Login("student", "07741000046")).PostAsJsonAsync("/api/student/codes/redeem", new { code = summaryValue })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/teacher/summaries/{summary}/format", null)).StatusCode);
        Assert.Equal(0, await Read(db => db.Enrollments.CountAsync(e => e.TargetId == summary)));
        Assert.Equal(3, await Issued(teacher));

        var removed = await client.DeleteAsync($"/api/teacher/courses/{course}/students/{student}");
        Assert.NotEqual(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(3, await Issued(teacher));

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/teacher/codes/{unusedId}/cancel", null)).StatusCode);
        Assert.Equal(CodeStatus.Canceled, await Read(db => db.Codes.Where(c => c.Id == unusedId).Select(c => c.Status).SingleAsync()));
        Assert.Equal(3, await Issued(teacher));
        var again = await Client(await Login("student", "07741000046")).PostAsJsonAsync("/api/student/codes/redeem", new { code = unused });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("code_canceled", await again.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Cancelled", Enum.GetNames<CodeStatus>());
        Assert.DoesNotContain("Cancelled", Enum.GetNames<SubscriptionStatus>());
    }

    [Fact]
    public async Task Plans_AssignFollowsAudienceUniquenessPaymentAndLifecycle()
    {
        Assert.DoesNotContain("Cancelled", Enum.GetNames<SubscriptionStatus>());
        var admin = Client(await Login("admin", await Phone(UserType.Admin, "07741000051")));
        var title = "خطة " + Guid.NewGuid().ToString("N")[..8];
        var teacherPlan = await Json(await admin.PostAsJsonAsync("/api/admin/plans", new { title, description = "وصف", audience = "ForTeacher", priceYearly = 250000, codesCount = 5 }));
        var duplicate = await admin.PostAsJsonAsync("/api/admin/plans", new { title, description = "وصف", audience = "ForTeacher", priceYearly = 250000, codesCount = 5 });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("title_taken", await duplicate.Content.ReadAsStringAsync());

        var teacher = await User(UserType.Teacher, "07741000052");
        var institute = await User(UserType.InstituteManager, "07741000053");
        var planId = teacherPlan.GetProperty("id").GetGuid();
        var mismatch = await admin.PostAsJsonAsync("/api/admin/subscriptions", new { userId = institute, planId });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("plan_audience", await mismatch.Content.ReadAsStringAsync());
        Assert.Equal(0, await Read(db => db.Subscriptions.CountAsync(s => s.UserId == institute)));

        var assigned = await Json(await admin.PostAsJsonAsync("/api/admin/subscriptions", new { userId = teacher, planId }));
        Assert.Equal("Active", assigned.GetProperty("subscription").GetProperty("status").GetString());
        Assert.Equal(5, assigned.GetProperty("subscription").GetProperty("planCodesCount").GetInt32());
        Assert.Equal(0, assigned.GetProperty("subscription").GetProperty("codesIssued").GetInt32());
        Assert.Equal("Cash", assigned.GetProperty("payment").GetProperty("method").GetString());
        Assert.Equal(250000, assigned.GetProperty("payment").GetProperty("amountIqd").GetInt64());
        Assert.False(assigned.GetProperty("payment").TryGetProperty("commission", out _));
        var ends = await Read(async db => (await db.Subscriptions.SingleAsync(s => s.UserId == teacher)).EndsAt);
        Assert.Equal(factory.Clock.UtcNow.AddDays(365), ends);

        var otherPlan = (await Json(await admin.PostAsJsonAsync("/api/admin/plans", new { title = title + "ب", description = "وصف", audience = "ForTeacher", priceYearly = 10, codesCount = 2 }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/admin/subscriptions", new { userId = teacher, planId = otherPlan })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/subscriptions", new { userId = teacher, planId })).StatusCode);

        await Mutate(async db =>
        {
            var row = await db.Subscriptions.SingleAsync(s => s.UserId == teacher && s.PlanId == planId);
            row.CodesIssued = row.PlanCodesCount;
        });
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/admin/subscriptions", new { userId = teacher, planId })).StatusCode);

        var buyer = await User(UserType.Teacher, "07741000054");
        var selfPlan = (await Json(await admin.PostAsJsonAsync("/api/admin/plans", new { title = title + "ج", description = "وصف", audience = "ForTeacher", priceYearly = 90000, codesCount = 4 }))).GetProperty("id").GetGuid();
        var buyerClient = Client(await Login("teacher", "07741000054"));
        var waiting = await Json(await buyerClient.PostAsJsonAsync("/api/teacher/subscriptions", new { userId = buyer, planId = selfPlan }));
        Assert.Equal("Waiting", waiting.GetProperty("status").GetString());
        Assert.StartsWith("https://payments.test/checkout/", waiting.GetProperty("paymentUrl").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/subscriptions", new { userId = buyer, planId = selfPlan })).StatusCode);
        await SignedWebhook(waiting.GetProperty("paymentId").GetGuid(), "failed");
        Assert.Equal(SubscriptionStatus.Failed, await Read(db => db.Subscriptions.Where(s => s.UserId == buyer).Select(s => s.Status).SingleAsync()));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/admin/subscriptions", new { userId = buyer, planId = selfPlan })).StatusCode);

        var expired = await User(UserType.Teacher, "07741000055");
        var consumed = await User(UserType.Teacher, "07741000056");
        var both = await User(UserType.Teacher, "07741000057");
        var lifePlan = await Plan(PlanAudience.ForTeacher, 10, 3);
        await SubscriptionFor(expired, lifePlan, factory.Clock.UtcNow.AddDays(-10), 3, factory.Clock.UtcNow.AddMinutes(-1));
        await SubscriptionFor(consumed, lifePlan, factory.Clock.UtcNow.AddDays(-10), 3, factory.Clock.UtcNow.AddDays(10));
        await SubscriptionFor(both, lifePlan, factory.Clock.UtcNow.AddDays(-10), 3, factory.Clock.UtcNow.AddMinutes(-1));
        await Mutate(async db =>
        {
            (await db.Subscriptions.SingleAsync(s => s.UserId == consumed)).CodesIssued = 3;
            (await db.Subscriptions.SingleAsync(s => s.UserId == both)).CodesIssued = 3;
        });
        await factory.Services.GetRequiredService<ToviJobs>().Subscriptions();
        Assert.Equal(SubscriptionStatus.Expired, await Read(db => db.Subscriptions.Where(s => s.UserId == expired).Select(s => s.Status).SingleAsync()));
        Assert.Equal(SubscriptionStatus.Consumed, await Read(db => db.Subscriptions.Where(s => s.UserId == consumed).Select(s => s.Status).SingleAsync()));
        Assert.Equal(SubscriptionStatus.Consumed, await Read(db => db.Subscriptions.Where(s => s.UserId == both).Select(s => s.Status).SingleAsync()));
    }

    [Fact]
    public async Task Hours_PackageReplaceScheduleAndPoolFollowTheBalance()
    {
        await User(UserType.Admin, "07741000061");
        var admin = Client(await Login("admin", "07741000061"));
        var firstPackage = await Json(await admin.PostAsJsonAsync("/api/admin/hour-packages", new
        {
            name = "باقة " + Guid.NewGuid().ToString("N")[..6],
            monthlyHoursLimit = 2,
            validityDays = 0,
            priceIqd = 0,
            reportLevel = "Basic",
            attendanceEnabled = true,
            exportEnabled = false,
            isActive = true,
            codeExpiresAt = factory.Clock.UtcNow.AddDays(2)
        }));
        Assert.Equal(120, firstPackage.GetProperty("monthlyMinutesLimit").GetInt32());
        Assert.Equal(30, firstPackage.GetProperty("validityDays").GetInt32());
        Assert.Equal(0, firstPackage.GetProperty("priceIqd").GetInt64());
        Assert.True(firstPackage.GetProperty("attendanceEnabled").GetBoolean());
        var secondPackage = await Json(await admin.PostAsJsonAsync("/api/admin/hour-packages", new
        {
            name = "باقة " + Guid.NewGuid().ToString("N")[..6],
            monthlyHoursLimit = 1,
            validityDays = 30,
            priceIqd = 0,
            reportLevel = "Basic",
            attendanceEnabled = true,
            exportEnabled = false,
            isActive = true,
            codeExpiresAt = factory.Clock.UtcNow.AddDays(2)
        }));

        var teacher = await User(UserType.Teacher, "07741000062");
        var client = Client(await Login("teacher", "07741000062"));
        var firstCode = await Read(db => db.HourPackageCodes.Where(c => c.PackageId == firstPackage.GetProperty("id").GetGuid()).Select(c => c.Code).SingleAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/teacher/hour-packages/redeem", new { code = firstCode })).StatusCode);
        Assert.Equal(factory.Clock.UtcNow.AddDays(30), await Read(async db => (await db.HourSubscriptions.SingleAsync(s => s.TeacherId == teacher)).EndsAt));
        var room = (await Json(await client.PostAsJsonAsync("/api/teacher/classrooms", new { title = "قاعة" }))).GetProperty("id").GetGuid();
        var start = factory.Clock.UtcNow.AddHours(5);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/teacher/sessions", new { classroomId = room, startsAt = start, durationMinutes = 30 })).StatusCode);
        Assert.Equal(90, await Read(db => db.HourSubscriptions.Where(s => s.TeacherId == teacher && s.Active).Select(s => s.RemainingMinutes).SingleAsync()));

        var secondCode = await Read(db => db.HourPackageCodes.Where(c => c.PackageId == secondPackage.GetProperty("id").GetGuid()).Select(c => c.Code).SingleAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/teacher/hour-packages/redeem", new { code = secondCode })).StatusCode);
        Assert.Equal(60, await Read(db => db.HourSubscriptions.Where(s => s.TeacherId == teacher && s.Active).Select(s => s.RemainingMinutes).SingleAsync()));
        Assert.Equal(90, await Read(db => db.HourSubscriptions.Where(s => s.TeacherId == teacher && !s.Active).Select(s => s.RemainingMinutes).SingleAsync()));

        var invalid = await client.PostAsJsonAsync("/api/teacher/sessions", new { classroomId = room, startsAt = start.AddHours(3), durationMinutes = 15 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("invalid_duration", await invalid.Content.ReadAsStringAsync());
        var fullHour = await Json(await client.PostAsJsonAsync("/api/teacher/sessions", new { classroomId = room, startsAt = start.AddHours(3) }));
        Assert.Equal(60, fullHour.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(0, await Read(db => db.HourSubscriptions.Where(s => s.TeacherId == teacher && s.Active).Select(s => s.RemainingMinutes).SingleAsync()));
        var sessionId = fullHour.GetProperty("id").GetGuid();
        Assert.NotEqual(HttpStatusCode.OK, (await client.DeleteAsync($"/api/teacher/sessions/{sessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/teacher/sessions/{sessionId}/cancel", null)).StatusCode);
        Assert.Equal(0, await Read(db => db.HourSubscriptions.Where(s => s.TeacherId == teacher && s.Active).Select(s => s.RemainingMinutes).SingleAsync()));
        Assert.Equal(0, await Read(db => Task.FromResult(db.Model.GetEntityTypes().Count(t => t.ClrType.Name.Contains("Attendance")))));

        var bare = Client(await Login("teacher", await Phone(UserType.Teacher, "07741000063")));
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.PostAsJsonAsync("/api/teacher/classrooms", new { title = "بلا باقة" })).StatusCode);

        var institute = await User(UserType.InstituteManager, "07741000064");
        var otherInstitute = await User(UserType.InstituteManager, "07741000065");
        var poolTeacher = await User(UserType.Teacher, "07741000066");
        var poolRoom = Guid.NewGuid();
        await Mutate(async db =>
        {
            (await db.HourPools.FirstAsync(p => p.InstituteId == institute)).RemainingMinutes = 90;
            db.InstituteTeachers.Add(new InstituteTeacher { Id = Guid.NewGuid(), InstituteId = institute, TeacherId = poolTeacher });
            db.Classrooms.Add(new VirtualClassroom { Id = poolRoom, TeacherId = poolTeacher, Title = "مختبر", CreatedAt = factory.Clock.UtcNow });
        });
        var poolClient = Client(await Login("institute", "07741000064"));
        var pooled = await Json(await poolClient.PostAsJsonAsync("/api/institute/sessions", new { classroomId = poolRoom, startsAt = factory.Clock.UtcNow.AddHours(4), durationMinutes = 60 }));
        Assert.Equal(poolRoom, pooled.GetProperty("classroomId").GetGuid());
        Assert.Equal(30, await Read(db => db.HourPools.Where(p => p.InstituteId == institute).Select(p => p.RemainingMinutes).SingleAsync()));
        Assert.Equal(0, await Read(db => db.HourPools.Where(p => p.InstituteId == otherInstitute).Select(p => p.RemainingMinutes).SingleAsync()));
        var overlap = await poolClient.PostAsJsonAsync("/api/institute/sessions", new { classroomId = poolRoom, startsAt = factory.Clock.UtcNow.AddHours(4).AddMinutes(10), durationMinutes = 30 });
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Equal(30, await Read(db => db.HourPools.Where(p => p.InstituteId == institute).Select(p => p.RemainingMinutes).SingleAsync()));
    }

    [Fact]
    public async Task Exams_ScoreAcceptedMarksAndCloseWhenTimeEnds()
    {
        var teacher = await User(UserType.Teacher, "07741000071");
        var student = await User(UserType.Student, "07741000072");
        var client = Client(await Login("teacher", "07741000071"));
        var examId = (await Json(await client.PostAsJsonAsync("/api/teacher/exams", new { title = "جمع العلامات", mark = 60, endDate = factory.Clock.UtcNow.AddDays(2), groupIds = Array.Empty<Guid>() }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/teacher/exams/{examId}/questions", new { type = "TrueFalse", text = "صح؟", mark = 10, correct = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/teacher/exams/{examId}/questions", new { type = "MultipleChoice", text = "اختر", mark = 20, options = new[] { new { text = "نعم", correct = true }, new { text = "لا", correct = false } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/teacher/exams/{examId}/questions", new { type = "OpenEnded", text = "اشرح", mark = 30 })).StatusCode);
        await SubscriptionFor(teacher, await Plan(PlanAudience.ForTeacher, 10, 2), factory.Clock.UtcNow.AddDays(-1), 2);
        var code = (await Json(await client.PostAsJsonAsync($"/api/teacher/codes?examId={examId}", new { count = 1 }))).GetProperty("codes")[0].GetProperty("code").GetString()!;
        var studentClient = Client(await Login("student", "07741000072"));
        Assert.Equal(HttpStatusCode.OK, (await studentClient.PostAsJsonAsync("/api/student/codes/redeem", new { code })).StatusCode);

        var wrong = await Read(db => db.QuestionOptions.Where(o => !o.IsCorrect && db.Questions.Any(q => q.Id == o.QuestionId && q.ExamId == examId)).Select(o => o.Id).SingleAsync());
        var tf = await Read(db => db.Questions.Where(q => q.ExamId == examId && q.Type == QuestionType.TrueFalse).Select(q => q.Id).SingleAsync());
        var choice = await Read(db => db.Questions.Where(q => q.ExamId == examId && q.Type == QuestionType.MultipleChoice).Select(q => q.Id).SingleAsync());
        var essay = await Read(db => db.Questions.Where(q => q.ExamId == examId && q.Type == QuestionType.OpenEnded).Select(q => q.Id).SingleAsync());
        var submitted = await Json(await studentClient.PostAsJsonAsync($"/api/student/exams/{examId}/attempts", new
        {
            answers = new[]
            {
                new { questionId = tf, response = "true" },
                new { questionId = choice, response = wrong.ToString() },
                new { questionId = essay, response = "جواب" }
            }
        }));
        Assert.Equal(10, submitted.GetProperty("totalMark").GetInt32());
        Assert.Equal(10, await Read(db => db.Answers.Where(a => a.QuestionId == tf).Select(a => a.Mark).SingleAsync()));
        Assert.Equal(0, await Read(db => db.Answers.Where(a => a.QuestionId == choice).Select(a => a.Mark).SingleAsync()));
        Assert.Null(await Read(db => db.Answers.Where(a => a.QuestionId == essay).Select(a => a.Mark).SingleAsync()));
        Assert.False(await Read(db => db.Answers.Where(a => a.QuestionId == essay).Select(a => a.Corrected).SingleAsync()));

        var answerId = await Read(db => db.Answers.Where(a => a.QuestionId == essay).Select(a => a.Id).SingleAsync());
        var tooHigh = await client.PostAsJsonAsync($"/api/teacher/answers/{answerId}/correction", new { mark = 31 });
        Assert.Equal(HttpStatusCode.BadRequest, tooHigh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/teacher/answers/{answerId}/correction", new { mark = 5 })).StatusCode);
        Assert.Equal(15, (await studentClient.GetFromJsonAsync<JsonElement>($"/api/student/exams/{examId}/result")).GetProperty("totalMark").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/teacher/answers/{answerId}/correction", new { mark = 5 })).StatusCode);
        Assert.Equal(15, (await studentClient.GetFromJsonAsync<JsonElement>($"/api/student/exams/{examId}/result")).GetProperty("totalMark").GetInt32());

        var closed = await User(UserType.Teacher, "07741000073");
        var futureId = Guid.NewGuid();
        var pastId = Guid.NewGuid();
        await Mutate(db =>
        {
            db.Exams.Add(new Exam { Id = futureId, TeacherId = closed, Title = "لم يحن", Mark = 10, EndDate = factory.Clock.UtcNow.AddDays(1), CreatedAt = factory.Clock.UtcNow });
            db.Exams.Add(new Exam { Id = pastId, TeacherId = closed, Title = "انتهى", Mark = 10, EndDate = factory.Clock.UtcNow.AddMinutes(-1), CreatedAt = factory.Clock.UtcNow });
            return Task.CompletedTask;
        });
        await factory.Services.GetRequiredService<ToviJobs>().ExamTransitions();
        Assert.Equal(ExamStatus.New, await Read(async db => (await db.Exams.FirstAsync(e => e.Id == futureId)).Status));
        Assert.Equal(ExamStatus.UnderCorrection, await Read(async db => (await db.Exams.FirstAsync(e => e.Id == pastId)).Status));
        await factory.Services.GetRequiredService<ToviJobs>().ExamTransitions();
        Assert.Equal(ExamStatus.UnderCorrection, await Read(async db => (await db.Exams.FirstAsync(e => e.Id == pastId)).Status));
    }

    [Fact]
    public async Task Invoices_StoreTheAmountAndCountEntered()
    {
        var admin = Client(await Login("admin", await Phone(UserType.Admin, "07741000081")));
        var party = await User(UserType.Teacher, "07741000082");
        var invoice = await Json(await admin.PostAsJsonAsync("/api/admin/invoices", new { partyUserId = party, amountIqd = 1500, activatedCodesCount = 7, note = "دفعة" }));
        Assert.Equal(1500, invoice.GetProperty("amountIqd").GetInt64());
        Assert.Equal(7, invoice.GetProperty("activatedCodesCount").GetInt32());
        Assert.False(invoice.TryGetProperty("commission", out _));
        Assert.False(invoice.TryGetProperty("profit", out _));
        Assert.False(invoice.TryGetProperty("salary", out _));
        var names = await Read(db => Task.FromResult(db.Model.FindEntityType(typeof(Invoice))!.GetProperties().Select(p => p.Name).ToList()));
        Assert.DoesNotContain("Commission", names);
        Assert.DoesNotContain("Profit", names);
        Assert.DoesNotContain("Salary", names);
    }

    [Fact]
    public async Task Reports_FilterOwnerAndTypeAndRejectWithoutPermission()
    {
        var adminUser = await User(UserType.Admin, "07741000083");
        var teacherA = await User(UserType.Teacher, "07741000084");
        var teacherB = await User(UserType.Teacher, "07741000085");
        var courseA = await CourseFor(teacherA);
        var examA = Guid.NewGuid();
        var courseB = await CourseFor(teacherB);
        await Mutate(db =>
        {
            db.Exams.Add(new Exam { Id = examA, TeacherId = teacherA, Title = "تقرير", Mark = 10, EndDate = factory.Clock.UtcNow.AddDays(1), CreatedAt = factory.Clock.UtcNow });
            db.Codes.Add(Code(teacherA, courseA, "4100000000000084"));
            db.Codes.Add(new ActivationCode { Id = Guid.NewGuid(), Code = "4100000000000085", OwnerUserId = teacherA, TargetType = AccessTarget.Exam, TargetId = examA, Status = CodeStatus.Inactive, CreatedAt = factory.Clock.UtcNow });
            db.Codes.Add(Code(teacherB, courseB, "4100000000000086"));
            return Task.CompletedTask;
        });

        var admin = Client(await Login("admin", "07741000083"));
        var filtered = await admin.GetStringAsync($"/api/admin/reports/codes?ownerId={teacherA}&target=Course");
        Assert.Contains("4100000000000084", filtered);
        Assert.DoesNotContain("4100000000000085", filtered);
        Assert.DoesNotContain("4100000000000086", filtered);

        var own = await Client(await Login("teacher", "07741000084")).GetStringAsync("/api/teacher/reports/codes");
        Assert.Contains("4100000000000084", own);
        Assert.DoesNotContain("4100000000000086", own);

        var staff = await Employee(adminUser, "07741000087", "can-view-advertisements");
        var denied = await Client(await Login("admin", staff.Phone)).GetAsync("/api/admin/reports/codes");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("4100000000000084", await denied.Content.ReadAsStringAsync());
        Assert.Equal(3, await Read(db => db.Codes.CountAsync(c => c.Code.StartsWith("410000000000008"))));
    }

    [Fact]
    public async Task Pages_DefaultTo20AndRejectNegativeBeforeReturningRows()
    {
        for (var i = 0; i < 25; i++)
            await User(UserType.Student, $"0774300{i:0000}");
        var client = Client(await Login("admin", await Phone(UserType.Admin, "07741000088")));
        var negative = await client.GetAsync("/api/admin/students?page=-1");
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Contains("invalid_page", await negative.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/students?pageSize=-5")).StatusCode);

        var first = await client.GetFromJsonAsync<JsonElement>("/api/admin/students");
        var second = await client.GetFromJsonAsync<JsonElement>("/api/admin/students?page=2");
        Assert.Equal(20, first.GetProperty("pageSize").GetInt32());
        Assert.Equal(20, second.GetProperty("pageSize").GetInt32());
        Assert.Equal(20, first.GetProperty("items").GetArrayLength());
        Assert.True(first.GetProperty("total").GetInt32() >= 25);
        var firstIds = first.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
        var secondIds = second.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Empty(firstIds.Intersect(secondIds));
    }

    [Fact]
    public async Task Notifications_ReachTheChosenAudienceAndKeepStoredText()
    {
        var admin = Client(await Login("admin", await Phone(UserType.Admin, "07741000091")));
        var teacher = await User(UserType.Teacher, "07741000092");
        var student = await User(UserType.Student, "07741000093");
        var gradeId = Guid.NewGuid();
        await Mutate(async db =>
        {
            db.Grades.Add(new EducationalGrade { Id = gradeId, Name = "صف السيناريو" });
            (await db.Users.FirstAsync(u => u.Id == student)).GradeId = gradeId;
        });
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/admin/notifications", new
        {
            title = "تنبيه الصف",
            body = "النص الكامل",
            type = "Warning",
            audience = "EducationalGrade",
            audienceRefId = gradeId
        })).StatusCode);
        var stored = await Read(db => db.Notifications.SingleAsync(n => n.Title == "تنبيه الصف"));
        Assert.Equal(student, stored.UserId);
        Assert.Equal("النص الكامل", stored.Body);
        Assert.Equal(NotificationType.Warning, stored.Type);
        Assert.Equal(0, await Read(db => db.Notifications.CountAsync(n => n.Title == "تنبيه الصف" && n.UserId == teacher)));

        var studentClient = Client(await Login("student", "07741000093"));
        await Mutate(async db =>
        {
            (await db.Users.FirstAsync(u => u.Id == student)).Status = UserStatus.Disabled;
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await studentClient.GetAsync("/api/student/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/admin/notifications", new
        {
            title = "بعد الإيقاف",
            body = "لا يصل",
            type = "Info",
            audience = "EducationalGrade",
            audienceRefId = gradeId
        })).StatusCode);
        var kept = await Read(db => db.Notifications.SingleAsync(n => n.UserId == student));
        Assert.Equal("تنبيه الصف", kept.Title);
        Assert.Equal("النص الكامل", kept.Body);
        Assert.Equal(NotificationType.Warning, kept.Type);
        Assert.Equal(0, await Read(db => db.Notifications.CountAsync(n => n.Title == "بعد الإيقاف" && n.UserId == student)));

        var scheduledId = Guid.NewGuid();
        var next = factory.Clock.UtcNow.AddMinutes(-5);
        await Mutate(db =>
        {
            db.ScheduledNotifications.Add(new ScheduledNotification
            {
                Id = scheduledId,
                Title = "تكرار يومي",
                Body = "يبقى حتى الحذف",
                Type = NotificationType.Info,
                Audience = NotificationAudience.Teacher,
                NextRunAt = next,
                Repeat = RepeatInterval.Daily,
                Enabled = true
            });
            return Task.CompletedTask;
        });
        await factory.Services.GetRequiredService<ToviJobs>().Notifications();
        Assert.Equal(next.AddDays(1), await Read(async db => (await db.ScheduledNotifications.FirstAsync(n => n.Id == scheduledId)).NextRunAt));
        Assert.True(await Read(db => db.Notifications.AnyAsync(n => n.Title == "تكرار يومي" && n.UserId == teacher)));
        Assert.False(await Read(db => db.Notifications.AnyAsync(n => n.Title == "تكرار يومي" && n.UserId == student)));
        await Mutate(async db =>
        {
            db.ScheduledNotifications.Remove(await db.ScheduledNotifications.FirstAsync(n => n.Id == scheduledId));
        });
        var before = await Read(db => db.Notifications.CountAsync(n => n.Title == "تكرار يومي"));
        await factory.Services.GetRequiredService<ToviJobs>().Notifications();
        Assert.Equal(before, await Read(db => db.Notifications.CountAsync(n => n.Title == "تكرار يومي")));

        var room = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var joined = await User(UserType.Student, "07741000094");
        var absent = await User(UserType.Student, "07741000095");
        await Mutate(db =>
        {
            db.Classrooms.Add(new VirtualClassroom { Id = room, TeacherId = teacher, Title = "تذكير", CreatedAt = factory.Clock.UtcNow });
            db.SessionsSchedule.Add(new ClassSession { Id = sessionId, ClassroomId = room, TeacherId = teacher, StartsAt = factory.Clock.UtcNow.AddHours(2), DurationMinutes = 60, Status = SessionStatus.Scheduled, CreatedAt = factory.Clock.UtcNow });
            db.Enrollments.Add(new Enrollment { Id = Guid.NewGuid(), StudentId = joined, TargetType = AccessTarget.VirtualClassroom, TargetId = room, CodeId = Guid.NewGuid(), CreatedAt = factory.Clock.UtcNow });
            return Task.CompletedTask;
        });
        await factory.Services.GetRequiredService<ToviJobs>().SessionReminders();
        Assert.False(await Read(db => db.Notifications.AnyAsync(n => n.UserId == joined && n.Title == "بدأت الجلسة")));
        await Mutate(async db =>
        {
            (await db.SessionsSchedule.FirstAsync(s => s.Id == sessionId)).StartsAt = factory.Clock.UtcNow.AddMinutes(-1);
        });
        await factory.Services.GetRequiredService<ToviJobs>().SessionReminders();
        Assert.True(await Read(db => db.Notifications.AnyAsync(n => n.UserId == joined && n.Title == "بدأت الجلسة" && n.Body.Length > 0 && n.Type == NotificationType.Info)));
        Assert.False(await Read(db => db.Notifications.AnyAsync(n => n.UserId == absent && n.Title == "بدأت الجلسة")));
    }

    [Fact]
    public async Task Chat_UsesOneEnvironmentScopedPath()
    {
        var teacher = await User(UserType.Teacher, "07741000101");
        var student = await User(UserType.Student, "07741000102");
        var outsider = await User(UserType.Student, "07741000103");
        var course = await CourseFor(teacher);
        var room = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await Mutate(db =>
        {
            db.Classrooms.Add(new VirtualClassroom { Id = room, TeacherId = teacher, Title = "دردشة", CreatedAt = factory.Clock.UtcNow });
            db.SessionsSchedule.Add(new ClassSession { Id = sessionId, ClassroomId = room, TeacherId = teacher, StartsAt = factory.Clock.UtcNow.AddHours(1), DurationMinutes = 60, CreatedAt = factory.Clock.UtcNow });
            db.Enrollments.Add(new Enrollment { Id = Guid.NewGuid(), StudentId = student, TargetType = AccessTarget.VirtualClassroom, TargetId = room, CodeId = Guid.NewGuid(), CreatedAt = factory.Clock.UtcNow });
            return Task.CompletedTask;
        });

        var first = await Json(await Client(await Login("teacher", "07741000101")).GetAsync($"/api/teacher/sessions/{sessionId}/chat"));
        var second = await Json(await Client(await Login("teacher", "07741000101")).GetAsync($"/api/teacher/sessions/{sessionId}/chat"));
        var joined = await Json(await Client(await Login("student", "07741000102")).GetAsync($"/api/student/sessions/{sessionId}/chat"));
        var expected = $"test/meetings/{sessionId}/messages";
        Assert.Equal("test", first.GetProperty("environment").GetString());
        Assert.Equal(expected, first.GetProperty("path").GetString());
        Assert.Equal(expected, second.GetProperty("path").GetString());
        Assert.Equal(expected, joined.GetProperty("path").GetString());
        Assert.DoesNotContain("production", expected);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(await Login("student", "07741000103")).GetAsync($"/api/student/sessions/{sessionId}/chat")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client(await Login("teacher", "07741000101")).GetAsync($"/api/teacher/courses/{course}/chat")).StatusCode);
        _ = outsider;
    }

    [Fact]
    public async Task Absences_DoNotExposeSalaryHomeworkOrAttendance()
    {
        var client = Client(await Login("admin", await Phone(UserType.Admin, "07741000111")));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/salaries")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/homework")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/attendance")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/academic-years")).StatusCode);
        var names = await Read(db => Task.FromResult(db.Model.GetEntityTypes().Select(t => t.ClrType.Name).ToList()));
        Assert.DoesNotContain(names, name => name.Contains("Salary", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Commission", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Homework", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Attendance", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("AcademicYear", StringComparison.OrdinalIgnoreCase));
    }

    async Task SignedWebhook(Guid paymentId, string status)
    {
        var payload = JsonSerializer.Serialize(new { paymentId, status });
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        message.Headers.Add("X-Tovi-Signature", Tovi.Api.Security.Signatures.Sign("test-webhook", payload));
        var response = await factory.CreateClient().SendAsync(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    async Task<string> Phone(UserType type, string phone)
    {
        await User(type, phone);
        return phone;
    }

    async Task Mutate(Func<ToviDb, Task> work)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
        await work(db);
        await db.SaveChangesAsync();
    }

    async Task<T> Read<T>(Func<ToviDb, Task<T>> work)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<ToviDb>());
    }

    async Task AddCode(Guid owner, Guid course, string code)
    {
        await Mutate(db =>
        {
            db.Codes.Add(Code(owner, course, code));
            return Task.CompletedTask;
        });
    }

    ActivationCode Code(Guid owner, Guid course, string code) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        Status = CodeStatus.Inactive,
        TargetType = AccessTarget.Course,
        TargetId = course,
        OwnerUserId = owner,
        CreatedAt = factory.Clock.UtcNow
    };

    static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }
}
