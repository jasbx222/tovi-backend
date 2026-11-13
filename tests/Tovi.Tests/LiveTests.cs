using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tovi.Api.Data;
using Tovi.Domain;

namespace Tovi.Tests;

public sealed partial class BehaviorTests
{
    [Fact]
    public void Live_Phase_OpensLobbyTenMinutesEarly()
    {
        var start = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("upcoming", MeetingRules.Phase(start, 60, start.AddMinutes(-11)));
        Assert.Equal("lobby", MeetingRules.Phase(start, 60, start.AddMinutes(-10)));
        Assert.Equal("live", MeetingRules.Phase(start, 60, start));
        Assert.Equal("ended", MeetingRules.Phase(start, 60, start.AddMinutes(60)));
        Assert.True(MeetingRules.CanJoin(start, 60, start.AddMinutes(-5), true, host: true).Ok);
        Assert.False(MeetingRules.CanJoin(start, 60, start.AddMinutes(-5), true).Ok);
    }

    [Fact]
    public void Live_Rules_ValidateReactionsQuestionsAndPolls()
    {
        Assert.True(LiveRules.Reaction("clap").Ok);
        Assert.Equal("reaction_unknown", LiveRules.Reaction("boom").Code);
        Assert.Equal("question_length", LiveRules.QuestionText("  ").Code);
        Assert.Equal("markup_rejected", LiveRules.QuestionText("<b>hi</b>").Code);
        Assert.Equal("poll_options_count", LiveRules.Poll("سؤال", ["أ"]).Code);
        Assert.Equal("poll_option_duplicate", LiveRules.Poll("سؤال", ["أ", "أ"]).Code);
        Assert.True(LiveRules.Poll("سؤال", ["أ", "ب", "ج"]).Ok);
        Assert.Equal(45, LiveRules.Credit(DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow));
        Assert.Equal(1800, LiveRules.AttendanceSeconds(60));
    }

    [Fact]
    public void Progress_Streak_CountsConsecutiveDays()
    {
        var days = new[] { "2026-05-01", "2026-05-02", "2026-05-03", "2026-05-05", "2026-05-06" };
        var today = ProgressRules.Streak(days, "2026-05-06");
        Assert.Equal((2, 3, true), today);
        var tomorrow = ProgressRules.Streak(days, "2026-05-07");
        Assert.Equal((2, 3, false), tomorrow);
        Assert.Equal(0, ProgressRules.Streak(days, "2026-05-09").Current);
        Assert.Equal("2026-05-02", ProgressRules.Day(new DateTime(2026, 5, 1, 21, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task Live_Session_TracksLobbyPresenceQuestionsPollsAndAttendance()
    {
        var start = factory.Clock.UtcNow;
        try
        {
            var teacher = await User(UserType.Teacher, "07750000001");
            var student = await User(UserType.Student, "07750000002");
            var peer = await User(UserType.Student, "07750000003");
            await User(UserType.Student, "07750000004");
            var room = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var startsAt = start.AddMinutes(5);
            await Mutate(db =>
            {
                db.Classrooms.Add(new VirtualClassroom { Id = room, TeacherId = teacher, Title = "فيزياء السادس", CreatedAt = start });
                db.SessionsSchedule.Add(new ClassSession { Id = sessionId, ClassroomId = room, TeacherId = teacher, Title = "قانون نيوتن", StartsAt = startsAt, DurationMinutes = 60, CreatedAt = start });
                foreach (var who in new[] { student, peer })
                    db.Enrollments.Add(new Enrollment { Id = Guid.NewGuid(), StudentId = who, TargetType = AccessTarget.VirtualClassroom, TargetId = room, CodeId = Guid.NewGuid(), CreatedAt = start });
                return Task.CompletedTask;
            });
            var host = Client(await Login("teacher", "07750000001"));
            var me = Client(await Login("student", "07750000002"));
            var other = Client(await Login("student", "07750000003"));
            var outsider = Client(await Login("student", "07750000004"));
            var path = $"/api/student/sessions/{sessionId}";
            var hostPath = $"/api/teacher/sessions/{sessionId}";

            var lobby = await Json(await me.GetAsync($"{path}/live"));
            Assert.Equal("lobby", lobby.GetProperty("phase").GetString());
            Assert.Equal("قانون نيوتن", lobby.GetProperty("session").GetProperty("title").GetString());
            Assert.Equal("فيزياء السادس", lobby.GetProperty("session").GetProperty("classroomTitle").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"{path}/live")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsync($"{path}/token", null)).StatusCode);
            var hostToken = await Json(await host.PostAsync($"{hostPath}/token", null));
            Assert.False(string.IsNullOrEmpty(hostToken.GetProperty("token").GetString()));
            Assert.Equal(0, (await Json(await me.PostAsync($"{path}/presence", null))).GetProperty("liveSeconds").GetInt32());
            Assert.Equal(1, (await Json(await host.GetAsync($"{hostPath}/live"))).GetProperty("present").GetInt32());

            factory.Clock.UtcNow = startsAt.AddSeconds(10);
            Assert.Equal(HttpStatusCode.OK, (await me.PostAsync($"{path}/token", null)).StatusCode);
            factory.Clock.UtcNow = startsAt.AddSeconds(40);
            Assert.Equal(40, (await Json(await me.PostAsync($"{path}/presence", null))).GetProperty("liveSeconds").GetInt32());
            factory.Clock.UtcNow = startsAt.AddMinutes(10);
            Assert.Equal(85, (await Json(await me.PostAsync($"{path}/presence", null))).GetProperty("liveSeconds").GetInt32());

            Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync($"{path}/reactions", new { kind = "boom" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await me.PostAsJsonAsync($"{path}/reactions", new { kind = "clap" })).StatusCode);
            Assert.Equal((HttpStatusCode)429, (await me.PostAsJsonAsync($"{path}/reactions", new { kind = "clap" })).StatusCode);
            factory.Clock.UtcNow = factory.Clock.UtcNow.AddSeconds(2);
            Assert.Equal(HttpStatusCode.OK, (await me.PostAsJsonAsync($"{path}/reactions", new { kind = "idea" })).StatusCode);

            var asked = await Json(await me.PostAsJsonAsync($"{path}/questions", new { text = "ليش التسارع يقل؟" }));
            var questionId = asked.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsJsonAsync($"{hostPath}/questions", new { text = "سؤال" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsync($"{path}/questions/{questionId}/vote", null)).StatusCode);
            var voted = await Json(await other.PostAsync($"{path}/questions/{questionId}/vote", null));
            Assert.True(voted.GetProperty("voted").GetBoolean());
            Assert.Equal(1, voted.GetProperty("votes").GetInt32());
            Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsync($"{path}/questions/{questionId}/answered", null)).StatusCode);
            Assert.True((await Json(await host.PostAsync($"{hostPath}/questions/{questionId}/answered", null))).GetProperty("answered").GetBoolean());
            var list = await Json(await other.GetAsync($"{path}/questions"));
            Assert.Equal(1, list.GetArrayLength());
            Assert.True(list[0].GetProperty("voted").GetBoolean());

            Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsJsonAsync($"{path}/polls", new { question = "؟", options = new[] { "أ", "ب" } })).StatusCode);
            var poll = await Json(await host.PostAsJsonAsync($"{hostPath}/polls", new { question = "فهمت المثال؟", options = new[] { "نعم", "لا" } }));
            var pollId = poll.GetProperty("id").GetGuid();
            var after = await Json(await me.PostAsJsonAsync($"{path}/polls/{pollId}/vote", new { option = 0 }));
            Assert.Equal(1, after.GetProperty("total").GetInt32());
            Assert.Equal(0, after.GetProperty("myOption").GetInt32());
            Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync($"{path}/polls/{pollId}/vote", new { option = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync($"{path}/polls/{pollId}/vote", new { option = 7 })).StatusCode);
            Assert.False((await Json(await host.PostAsync($"{hostPath}/polls/{pollId}/close", null))).GetProperty("open").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync($"{path}/polls/{pollId}/vote", new { option = 1 })).StatusCode);

            JsonElement beat = default;
            for (var i = 0; i < 40 && !(beat.ValueKind == JsonValueKind.Object && beat.GetProperty("attended").GetBoolean()); i++)
            {
                factory.Clock.UtcNow = factory.Clock.UtcNow.AddSeconds(45);
                beat = await Json(await me.PostAsync($"{path}/presence", null));
            }
            Assert.True(beat.GetProperty("attended").GetBoolean());
            Assert.Equal(30, beat.GetProperty("xp").GetInt32());

            var progress = await Json(await me.GetAsync("/api/student/progress"));
            Assert.Equal(30, progress.GetProperty("xp").GetInt32());
            Assert.Equal(1, progress.GetProperty("counts").GetProperty("lives").GetInt32());
            Assert.Equal(1, progress.GetProperty("streak").GetProperty("current").GetInt32());
            Assert.Contains(progress.GetProperty("achievements").EnumerateArray(), a => a.GetProperty("key").GetString() == "first_live" && a.GetProperty("unlocked").GetBoolean());

            var recap = await Json(await me.GetAsync($"{path}/summary"));
            Assert.True(recap.GetProperty("student").GetBoolean());
            Assert.True(recap.GetProperty("me").GetProperty("attended").GetBoolean());
            Assert.Equal(1, recap.GetProperty("me").GetProperty("asked").GetInt32());
            Assert.False(recap.TryGetProperty("attendance", out _));
            var summary = await Json(await host.GetAsync($"{hostPath}/summary"));
            Assert.Equal(2, summary.GetProperty("enrolled").GetInt32());
            Assert.Equal(1, summary.GetProperty("joined").GetInt32());
            Assert.Equal(1, summary.GetProperty("attended").GetInt32());
            Assert.Equal(2, summary.GetProperty("reactions").GetArrayLength());
            Assert.Equal(1, summary.GetProperty("questions").GetProperty("answered").GetInt32());
            Assert.Equal(1, summary.GetProperty("polls").GetArrayLength());

            var recording = await host.PostAsync($"{hostPath}/recording", null);
            Assert.Equal(HttpStatusCode.Conflict, recording.StatusCode);
            Assert.Contains("recording_disabled", await recording.Content.ReadAsStringAsync());
            Assert.False((await Json(await me.GetAsync($"{path}/recordings"))).GetProperty("enabled").GetBoolean());

            factory.Clock.UtcNow = startsAt.AddMinutes(61);
            Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsJsonAsync($"{path}/questions", new { text = "متأخر" })).StatusCode);
            Assert.Equal("ended", (await Json(await me.GetAsync($"{path}/live"))).GetProperty("phase").GetString());
        }
        finally
        {
            factory.Clock.UtcNow = start;
        }
    }

    [Fact]
    public async Task Progress_LessonAndExamAwardOnceAndShowOnDashboards()
    {
        var teacher = await User(UserType.Teacher, "07750000011");
        var student = await User(UserType.Student, "07750000012");
        var course = await CourseFor(teacher);
        var lesson = Guid.NewGuid();
        await Mutate(db =>
        {
            var chapter = new Chapter { Id = Guid.NewGuid(), CourseId = course, Title = "الفصل الأول" };
            db.Chapters.Add(chapter);
            db.Lessons.Add(new Lesson { Id = lesson, ChapterId = chapter.Id, Title = "الدرس الأول" });
            db.Enrollments.Add(new Enrollment { Id = Guid.NewGuid(), StudentId = student, TargetType = AccessTarget.Course, TargetId = course, CodeId = Guid.NewGuid(), CreatedAt = factory.Clock.UtcNow });
            return Task.CompletedTask;
        });
        var me = Client(await Login("student", "07750000012"));
        Assert.Equal(10, (await Json(await me.PostAsync($"/api/student/lessons/{lesson}/progress", null))).GetProperty("xp").GetInt32());
        Assert.Equal(0, (await Json(await me.PostAsync($"/api/student/lessons/{lesson}/progress", null))).GetProperty("xp").GetInt32());
        var progress = await Json(await me.GetAsync("/api/student/progress"));
        Assert.Equal(10, progress.GetProperty("xp").GetInt32());
        Assert.Equal(10, progress.GetProperty("todayXp").GetInt32());
        Assert.Equal(7, progress.GetProperty("week").GetArrayLength());
        Assert.Contains(progress.GetProperty("achievements").EnumerateArray(), a => a.GetProperty("key").GetString() == "first_lesson" && a.GetProperty("unlocked").GetBoolean());

        var admin = Client(await Login("admin", await Phone(UserType.Admin, "07750000013")));
        var students = await Json(await admin.GetAsync("/api/admin/students?pageSize=50"));
        var row = students.GetProperty("items").EnumerateArray().First(r => r.GetProperty("id").GetGuid() == student);
        Assert.Equal(10, row.GetProperty("xp").GetInt32());
        Assert.Equal(1, row.GetProperty("streak").GetInt32());
    }

    [Fact]
    public async Task Schema_Upgrade_AddsMissingTablesToAnOlderDatabase()
    {
        await Mutate(async db => await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Polls\""));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
            await SchemaUpgrade.ApplyAsync(db);
            Assert.Equal(0, await db.Polls.CountAsync(p => p.Question == "غير موجود"));
        }
    }
}
