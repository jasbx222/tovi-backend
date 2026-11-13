using System.Net;
using System.Net.Http.Json;
using Tovi.Domain;

namespace Tovi.Tests;

public sealed partial class BehaviorTests
{
    [Fact]
    public void Student_Rules_ExamStateRankAndNames()
    {
        var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("locked", StudentRules.ExamState(false, false, false, ExamStatus.New, now.AddDays(1), now));
        Assert.Equal("open", StudentRules.ExamState(true, false, false, ExamStatus.New, now.AddDays(1), now));
        Assert.Equal("closed", StudentRules.ExamState(true, false, false, ExamStatus.New, now.AddDays(-1), now));
        Assert.Equal("pending", StudentRules.ExamState(true, true, true, ExamStatus.New, now, now));
        Assert.Equal("done", StudentRules.ExamState(true, true, false, ExamStatus.Completed, now, now));

        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var ranked = StudentRules.Rank([(a, 50), (b, 80), (c, 50), (Guid.NewGuid(), 0)]);
        Assert.Equal(3, ranked.Count);
        Assert.Equal((b, 1), (ranked[0].StudentId, ranked[0].Rank));
        Assert.Equal(2, ranked[1].Rank);
        Assert.Equal(2, ranked[2].Rank);

        Assert.Equal("علي ح.", StudentRules.PublicName("علي", "حسن"));
        Assert.Equal("علي", StudentRules.PublicName("علي", ""));
        Assert.Equal("2026-05-02", StudentRules.WeekStart("2026-05-07"));
        Assert.Equal("2026-05-02", StudentRules.WeekStart("2026-05-02"));
        Assert.Equal("password_wrong", StudentRules.ChangePassword(false, "newpassword", "old").Code);
        Assert.Equal("password_short", StudentRules.ChangePassword(true, "short", "old").Code);
        Assert.Equal("password_same", StudentRules.ChangePassword(true, "password1", "password1").Code);
        Assert.Equal((null, null), StudentRules.Link("payment", Guid.NewGuid()));
    }

    [Fact]
    public async Task Student_Hub_ExamsInboxSavedAccountAndLeaderboard()
    {
        var teacher = await User(UserType.Teacher, "07760000001");
        var student = await User(UserType.Student, "07760000002");
        await User(UserType.Student, "07760000003");
        var course = await CourseFor(teacher);
        var exam = Guid.NewGuid();
        var question = Guid.NewGuid();
        var right = Guid.NewGuid();
        var note = Guid.NewGuid();
        var now = factory.Clock.UtcNow;
        await Mutate(db =>
        {
            db.Exams.Add(new Exam { Id = exam, TeacherId = teacher, CourseId = course, Title = "اختبار الفصل", Mark = 5, EndDate = now.AddDays(3), CreatedAt = now });
            db.Questions.Add(new Question { Id = question, ExamId = exam, Type = QuestionType.MultipleChoice, Text = "٢+٢", Mark = 5 });
            db.QuestionOptions.Add(new QuestionOption { Id = right, QuestionId = question, Text = "4", IsCorrect = true });
            db.QuestionOptions.Add(new QuestionOption { Id = Guid.NewGuid(), QuestionId = question, Text = "5" });
            db.Enrollments.Add(new Enrollment { Id = Guid.NewGuid(), StudentId = student, TargetType = AccessTarget.Course, TargetId = course, CodeId = Guid.NewGuid(), CreatedAt = now });
            db.Notifications.Add(new UserNotification { Id = note, UserId = student, Title = "بدأت الجلسة", Body = "ادخل", CreatedAt = now, TargetType = "exam", TargetId = exam });
            return Task.CompletedTask;
        });
        var me = Client(await Login("student", "07760000002"));
        var other = Client(await Login("student", "07760000003"));

        var list = await Json(await me.GetAsync("/api/student/exams"));
        Assert.Equal("locked", list.GetProperty("items")[0].GetProperty("state").GetString());
        Assert.Equal(0, (await Json(await other.GetAsync("/api/student/exams"))).GetProperty("total").GetInt32());
        await Mutate(db =>
        {
            db.Enrollments.Add(new Enrollment { Id = Guid.NewGuid(), StudentId = student, TargetType = AccessTarget.Exam, TargetId = exam, CodeId = Guid.NewGuid(), CreatedAt = now });
            return Task.CompletedTask;
        });
        Assert.Equal("open", (await Json(await me.GetAsync("/api/student/exams"))).GetProperty("items")[0].GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await me.GetAsync($"/api/student/exams/{exam}/review")).StatusCode);
        await Json(await me.PostAsJsonAsync($"/api/student/exams/{exam}/attempts", new { answers = new[] { new { questionId = question, response = right.ToString() } } }));
        var review = await Json(await me.GetAsync($"/api/student/exams/{exam}/review"));
        Assert.Equal(1, review.GetProperty("right").GetInt32());
        Assert.True(review.GetProperty("items")[0].GetProperty("options").EnumerateArray().Any(o => o.GetProperty("chosen").GetBoolean() && o.GetProperty("isCorrect").GetBoolean()));
        Assert.Equal("done", (await Json(await me.GetAsync("/api/student/exams"))).GetProperty("items")[0].GetProperty("state").GetString());

        var inbox = await Json(await me.GetAsync("/api/student/inbox"));
        Assert.Equal(1, inbox.GetProperty("unread").GetInt32());
        Assert.Equal("exam", inbox.GetProperty("items")[0].GetProperty("targetType").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/student/inbox/{note}/read", null)).StatusCode);
        Assert.Equal(0, (await Json(await me.PostAsync($"/api/student/inbox/{note}/read", null))).GetProperty("unread").GetInt32());

        await Json(await me.PostAsync($"/api/student/courses/{course}/save", null));
        Assert.Equal(1, (await Json(await me.GetAsync("/api/student/saved"))).GetProperty("total").GetInt32());
        Assert.False((await Json(await me.DeleteAsync($"/api/student/courses/{course}/save"))).GetProperty("saved").GetBoolean());
        Assert.Equal(0, (await Json(await me.GetAsync("/api/student/saved"))).GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await me.PutAsJsonAsync("/api/student/account", new { firstName = "" })).StatusCode);
        var account = await Json(await me.PutAsJsonAsync("/api/student/account", new { firstName = "زهراء", lastName = "علي" }));
        Assert.Equal("زهراء", account.GetProperty("firstName").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/student/account/password", new { current = "wrong-pass", next = "newpassword1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await me.PostAsJsonAsync("/api/student/account/password", new { current = "password1", next = "newpassword1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/student/login", new { phone = "07760000002", password = "newpassword1" })).StatusCode);

        var board = await Json(await me.GetAsync("/api/student/leaderboard?period=all"));
        Assert.Equal(20, board.GetProperty("me").GetProperty("xp").GetInt32());
        Assert.True(board.GetProperty("me").GetProperty("rank").GetInt32() >= 1);
        Assert.Contains(board.GetProperty("items").EnumerateArray(), r => r.GetProperty("name").GetString() == "زهراء ع." && r.GetProperty("me").GetBoolean());

        var unlocked = await Json(await me.GetAsync("/api/student/unlocked"));
        Assert.Equal(2, unlocked.GetProperty("total").GetInt32());
        var found = await Json(await me.GetAsync("/api/student/find?q=" + Uri.EscapeDataString("")));
        Assert.True(found.GetProperty("totals").GetProperty("courses").GetInt32() >= 1);
        Assert.Equal(HttpStatusCode.OK, (await me.GetAsync("/api/student/explore")).StatusCode);
    }

    [Fact]
    public async Task Student_Recap_ReturnsOwnSummaryInsteadOfForbidden()
    {
        var teacher = await User(UserType.Teacher, "07770000001");
        var student = await User(UserType.Student, "07770000002");
        var room = Guid.NewGuid();
        var session = Guid.NewGuid();
        var start = factory.Clock.UtcNow.AddHours(-3);
        await Mutate(db =>
        {
            db.Classrooms.Add(new VirtualClassroom { Id = room, TeacherId = teacher, Title = "كيمياء", CreatedAt = start });
            db.SessionsSchedule.Add(new ClassSession { Id = session, ClassroomId = room, TeacherId = teacher, Title = "الأحماض", StartsAt = start, DurationMinutes = 60, CreatedAt = start });
            db.Enrollments.Add(new Enrollment { Id = Guid.NewGuid(), StudentId = student, TargetType = AccessTarget.VirtualClassroom, TargetId = room, CodeId = Guid.NewGuid(), CreatedAt = start });
            db.Recordings.Add(new Recording { Id = Guid.NewGuid(), SessionId = session, Status = "ready", PlaybackPath = "https://example.org/r.mp4", CreatedAt = start, EndedAt = start.AddMinutes(50) });
            return Task.CompletedTask;
        });
        var me = Client(await Login("student", "07770000002"));
        var recap = await Json(await me.GetAsync($"/api/student/sessions/{session}/summary"));
        Assert.True(recap.GetProperty("student").GetBoolean());
        Assert.Equal(1, recap.GetProperty("recordings").GetArrayLength());
        var past = await Json(await me.GetAsync("/api/student/sessions?when=past"));
        Assert.Equal(1, past.GetProperty("total").GetInt32());
        Assert.Equal(1, past.GetProperty("items")[0].GetProperty("replays").GetInt32());
        var replays = await Json(await me.GetAsync("/api/student/replays"));
        Assert.Equal(50, replays.GetProperty("items")[0].GetProperty("durationMinutes").GetInt32());
    }
}
