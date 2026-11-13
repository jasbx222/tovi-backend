using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Api.Services;
using Tovi.Domain;

namespace Tovi.Api.Endpoints;

public record AttemptBody(List<AttemptAnswer> Answers);
public record AttemptAnswer(Guid QuestionId, string Response);
public record DeviceBody(string Token, string Platform);
public record SearchQuery(string? Q);

public static class StudentApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var student = api.MapGroup("/student");
        student.MapGet("/home", Home);
        student.MapGet("/search", Search);
        student.MapGet("/learning-domains", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Student);
            return Results.Ok(await db.LearningDomains.OrderBy(x => x.Name).ToListAsync(ct));
        });
        student.MapGet("/sections", async (HttpContext http, ToviDb db, CancellationToken ct) => { Http.Me(http, UserType.Student); return Results.Ok(await db.Sections.OrderBy(x => x.Name).ToListAsync(ct)); });
        student.MapGet("/grades", async (HttpContext http, ToviDb db, CancellationToken ct) => { Http.Me(http, UserType.Student); return Results.Ok(await db.Grades.OrderBy(x => x.Name).ToListAsync(ct)); });
        student.MapGet("/subjects", async (HttpContext http, ToviDb db, CancellationToken ct) => { Http.Me(http, UserType.Student); return Results.Ok(await db.Subjects.OrderBy(x => x.Name).ToListAsync(ct)); });
        student.MapGet("/governorates", async (HttpContext http, ToviDb db, CancellationToken ct) => { Http.Me(http, UserType.Student); return Results.Ok(await db.Governorates.OrderBy(x => x.Name).ToListAsync(ct)); });
        student.MapGet("/teachers", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Student);
            return await Http.Page(db.Users.Where(u => u.Type == UserType.Teacher && u.Status == UserStatus.Active).OrderBy(u => u.FirstName), page, pageSize, u => new { u.Id, u.FirstName, u.LastName }, ct);
        });
        student.MapPost("/teachers/{id:guid}/follow", Follow);
        student.MapGet("/institutes", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Student);
            return await Http.Page(db.Users.Where(u => u.Type == UserType.InstituteManager && u.Status == UserStatus.Active).OrderBy(u => u.InstituteName), page, pageSize, u => new { u.Id, u.InstituteName, u.LogoFileId }, ct);
        });
        student.MapGet("/courses", async (HttpContext http, ToviDb db, Guid? subjectId, Guid? gradeId, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Student);
            var query = db.Courses.AsQueryable();
            if (subjectId is not null) query = query.Where(c => c.SubjectId == subjectId);
            if (gradeId is not null) query = query.Where(c => c.GradeId == gradeId);
            return await Http.Page(query.OrderByDescending(c => c.CreatedAt), page, pageSize, c => new { c.Id, c.Title, c.TeacherId, c.SubjectId, c.GradeId }, ct);
        });
        student.MapGet("/courses/{id:guid}", CourseDetail);
        student.MapGet("/summaries", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Student);
            return await Http.Page(db.Summaries.OrderBy(s => s.Title), page, pageSize, s => new { s.Id, s.Title, s.TeacherId, s.CourseId }, ct);
        });
        student.MapGet("/summaries/{id:guid}", SummaryDetail);
        student.MapPost("/courses/{id:guid}/like", (Guid id, HttpContext http, ToviDb db, CancellationToken ct) => Toggle(http, db, id, true, ct));
        student.MapPost("/courses/{id:guid}/save", (Guid id, HttpContext http, ToviDb db, CancellationToken ct) => Toggle(http, db, id, false, ct));
        student.MapPost("/codes/redeem", async (RedeemBody body, HttpContext http, CodeWork codes, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.Student);
            var code = await codes.RedeemAsync(actor.User.Id, body.Code, ct);
            return Results.Ok(new { code.Id, code.Status, code.TargetType, code.TargetId });
        });
        student.MapGet("/exams/{id:guid}", ExamPaper);
        student.MapPost("/exams/{id:guid}/attempts", Submit);
        student.MapGet("/exams/{id:guid}/result", Result);
        student.MapPost("/lessons/{id:guid}/progress", Progress);
        student.MapGet("/notifications", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.Student);
            return await Http.Page(db.Notifications.Where(n => n.UserId == actor.User.Id).OrderByDescending(n => n.CreatedAt), page, pageSize, n => n, ct);
        });
        student.MapPut("/device-token", Device);
        student.MapGet("/advertisements", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Student);
            return Results.Ok(await db.Advertisements.Where(a => a.Active).OrderByDescending(a => a.CreatedAt).ToListAsync(ct));
        });
        student.MapGet("/parameters", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Student);
            return Results.Ok(await db.Parameters.Where(p => p.Key == "privacy_url").OrderBy(p => p.Key).ToListAsync(ct));
        });
        student.MapGet("/profile", (HttpContext http) =>
        {
            var actor = Http.Me(http, UserType.Student);
            return Results.Ok(new { actor.User.Id, actor.User.FirstName, actor.User.LastName, actor.User.Phone, actor.User.Gender, actor.User.GradeId });
        });
        MeetingApi.Map(student);
    }

    static async Task<IResult> Home(HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        var enrolled = await db.Enrollments.Where(e => e.StudentId == actor.User.Id).ToListAsync(ct);
        var courseIds = enrolled.Where(e => e.TargetType == AccessTarget.Course).Select(e => e.TargetId).ToList();
        var courses = await db.Courses.Where(c => courseIds.Contains(c.Id)).Select(c => new { c.Id, c.Title }).ToListAsync(ct);
        var roomIds = enrolled.Where(e => e.TargetType == AccessTarget.VirtualClassroom).Select(e => e.TargetId).ToList();
        var sessions = await db.SessionsSchedule.Where(s => roomIds.Contains(s.ClassroomId) && s.Status == SessionStatus.Scheduled).OrderBy(s => s.StartsAt).ToListAsync(ct);
        var ads = await db.Advertisements.Where(a => a.Active).OrderByDescending(a => a.CreatedAt).Take(5).ToListAsync(ct);
        return Results.Ok(new { courses, sessions, ads });
    }

    static async Task<IResult> Search(HttpContext http, ToviDb db, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        Http.Me(http, UserType.Student);
        var query = db.Courses.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(c => c.Title.Contains(q));
        return await Http.Page(query.OrderBy(c => c.Title), page, pageSize, c => new { c.Id, c.Title, c.TeacherId }, ct);
    }

    static async Task<IResult> CourseDetail(Guid id, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new RuleException("not_found", "Course was not found.", 404);
        var chapters = await db.Chapters.Where(c => c.CourseId == id).OrderBy(c => c.SortOrder).ToListAsync(ct);
        var lessons = await db.Lessons.Where(l => chapters.Select(c => c.Id).Contains(l.ChapterId)).OrderBy(l => l.SortOrder).ToListAsync(ct);
        var enrolled = await db.Enrollments.AnyAsync(e => e.StudentId == actor.User.Id && e.TargetType == AccessTarget.Course && e.TargetId == id, ct);
        return Results.Ok(new
        {
            course,
            chapters,
            lessons = lessons.Select(l => new { l.Id, l.ChapterId, l.Title, l.SortOrder, videoReference = enrolled ? l.VideoReference : null }),
            enrolled
        });
    }

    static async Task<IResult> SummaryDetail(Guid id, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        var summary = await db.Summaries.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new RuleException("not_found", "Summary was not found.", 404);
        var enrolled = await db.Enrollments.AnyAsync(e => e.StudentId == actor.User.Id && e.TargetType == AccessTarget.Summary && e.TargetId == id, ct);
        return Results.Ok(new { summary.Id, summary.Title, summary.TeacherId, body = enrolled ? summary.Body : null, enrolled });
    }

    static async Task<IResult> Follow(Guid id, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        var exists = await db.Follows.AnyAsync(f => f.StudentId == actor.User.Id && f.TeacherId == id, ct);
        if (!exists) db.Follows.Add(new TeacherFollow { Id = Guid.NewGuid(), StudentId = actor.User.Id, TeacherId = id });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { following = true });
    }

    static async Task<IResult> Toggle(HttpContext http, ToviDb db, Guid courseId, bool like, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        if (like)
        {
            if (!await db.Likes.AnyAsync(x => x.StudentId == actor.User.Id && x.CourseId == courseId, ct))
                db.Likes.Add(new CourseLike { Id = Guid.NewGuid(), StudentId = actor.User.Id, CourseId = courseId });
        }
        else if (!await db.SavedCourses.AnyAsync(x => x.StudentId == actor.User.Id && x.CourseId == courseId, ct))
        {
            db.SavedCourses.Add(new SavedCourse { Id = Guid.NewGuid(), StudentId = actor.User.Id, CourseId = courseId });
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { saved = true });
    }

    static async Task<IResult> ExamPaper(Guid id, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        var exam = await db.Exams.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new RuleException("not_found", "Exam was not found.", 404);
        var enrolled = await db.Enrollments.AnyAsync(e => e.StudentId == actor.User.Id && e.TargetType == AccessTarget.Exam && e.TargetId == id, ct);
        if (!enrolled) throw new RuleException("not_enrolled", "Redeem an exam code before opening it.", 403);
        var questions = await db.Questions.Where(q => q.ExamId == id).OrderBy(q => q.SortOrder).ToListAsync(ct);
        var ids = questions.Select(q => q.Id).ToList();
        var options = await db.QuestionOptions.Where(o => ids.Contains(o.QuestionId)).ToListAsync(ct);
        return Results.Ok(new
        {
            exam.Id,
            exam.Title,
            exam.Mark,
            exam.Status,
            exam.EndDate,
            questions = questions.Select(q => new
            {
                q.Id,
                q.Type,
                q.Text,
                q.Mark,
                options = options.Where(o => o.QuestionId == q.Id).Select(o => new { o.Id, o.Text })
            })
        });
    }

    static async Task<IResult> Submit(Guid id, AttemptBody body, HttpContext http, LearningWork learning, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        var attempt = await learning.SubmitAsync(actor.User.Id, id, body.Answers.Select(a => (a.QuestionId, a.Response)).ToList(), ct);
        return Results.Ok(new { attempt.Id, attempt.TotalMark });
    }

    static async Task<IResult> Result(Guid id, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.ExamId == id && a.StudentId == actor.User.Id, ct)
            ?? throw new RuleException("not_found", "No attempt was found.", 404);
        var answers = await db.Answers.Where(a => a.AttemptId == attempt.Id).ToListAsync(ct);
        return Results.Ok(new { attempt.TotalMark, attempt.SubmittedAt, answers });
    }

    static async Task<IResult> Progress(Guid id, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        var lesson = await db.Lessons.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new RuleException("not_found", "Lesson was not found.", 404);
        var chapter = await db.Chapters.FirstAsync(c => c.Id == lesson.ChapterId, ct);
        var enrolled = await db.Enrollments.AnyAsync(e => e.StudentId == actor.User.Id && e.TargetType == AccessTarget.Course && e.TargetId == chapter.CourseId, ct);
        if (!enrolled) throw new RuleException("not_enrolled", "Redeem a course code before opening lessons.", 403);
        if (!await db.LessonProgress.AnyAsync(p => p.StudentId == actor.User.Id && p.LessonId == id, ct))
            db.LessonProgress.Add(new LessonProgress { Id = Guid.NewGuid(), StudentId = actor.User.Id, LessonId = id, CompletedAt = clock.UtcNow });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { completed = true });
    }

    static async Task<IResult> Device(DeviceBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        if (string.IsNullOrWhiteSpace(body.Token) || body.Token.Contains("hardcoded", StringComparison.OrdinalIgnoreCase))
            throw new RuleException("token_required", "Send the device token from this device.");
        var existing = await db.DeviceTokens.FirstOrDefaultAsync(t => t.Token == body.Token, ct);
        if (existing is null)
            db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = actor.User.Id, Token = body.Token, Platform = body.Platform, CreatedAt = clock.UtcNow });
        else existing.UserId = actor.User.Id;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { stored = true });
    }
}

public static class MeetingApi
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/sessions/{id:guid}/token", Token);
        group.MapGet("/sessions/{id:guid}/chat", Chat);
        group.MapPost("/sessions/{id:guid}/participants/mute", Mute);
        group.MapPost("/sessions/{id:guid}/participants/remove", Remove);
        group.MapPost("/sessions/{id:guid}/recording", Recording);
    }

    static async Task<IResult> Token(Guid id, HttpContext http, ToviDb db, IClock clock, IConfiguration configuration, CancellationToken ct)
    {
        var actor = http.Items["actor"] as Actor ?? throw new RuleException("session_dead", "The session is missing or no longer valid.", 401);
        var session = await db.SessionsSchedule.FirstOrDefaultAsync(s => s.Id == id && s.Status == SessionStatus.Scheduled, ct)
            ?? throw new RuleException("not_found", "Session was not found.", 404);
        var allowed = false;
        var admin = false;
        var publish = false;
        string identity;
        if (actor.Portal == UserType.Teacher && session.TeacherId == actor.Owner.Id)
        {
            allowed = true;
            admin = true;
            publish = true;
            identity = $"teacher:{actor.User.Id}";
        }
        else if (actor.User.Type == UserType.Employee && actor.Portal == UserType.Teacher && session.TeacherId == actor.Owner.Id && actor.Allows(PermissionCatalog.ManageMeeting))
        {
            allowed = true;
            admin = true;
            publish = true;
            identity = $"assistant:{actor.User.Id}";
        }
        else if (actor.Portal == UserType.Student)
        {
            allowed = await db.Enrollments.AnyAsync(e => e.StudentId == actor.User.Id && e.TargetType == AccessTarget.VirtualClassroom && e.TargetId == session.ClassroomId, ct);
            publish = true;
            identity = $"student:{actor.User.Id}";
        }
        else identity = $"user:{actor.User.Id}";

        RuleException.Require(MeetingRules.CanJoin(session.StartsAt, session.DurationMinutes, clock.UtcNow, allowed));
        var environment = configuration["Firebase:Environment"];
        var host = configuration["LiveKit:Host"];
        var key = configuration["LiveKit:ApiKey"];
        var secret = configuration["LiveKit:ApiSecret"];
        if (string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            throw new RuleException("livekit_unconfigured", "LiveKit is not configured on the server.", 503);
        var minutes = int.TryParse(configuration["LiveKit:TokenMinutes"], out var parsed) ? parsed : 10;
        var room = MeetingRules.Room(environment, session.Id);
        var token = LiveKitTokens.Create(key, secret, identity, room, admin, publish, clock.UtcNow.AddMinutes(minutes));
        return Results.Ok(new { url = host, token, room, identity, admin, publish, startsAt = session.StartsAt, durationMinutes = session.DurationMinutes });
    }

    static async Task<IResult> Chat(Guid id, HttpContext http, ToviDb db, IConfiguration configuration, CancellationToken ct)
    {
        var actor = http.Items["actor"] as Actor ?? throw new RuleException("session_dead", "The session is missing or no longer valid.", 401);
        var session = await db.SessionsSchedule.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new RuleException("not_found", "Session was not found.", 404);
        var allowed = actor.Portal == UserType.Teacher && session.TeacherId == actor.Owner.Id
            || actor.Portal == UserType.Student && await db.Enrollments.AnyAsync(e => e.StudentId == actor.User.Id && e.TargetType == AccessTarget.VirtualClassroom && e.TargetId == session.ClassroomId, ct);
        if (!allowed) throw new RuleException("forbidden", "You cannot open this chat.", 403);
        var environment = configuration["Firebase:Environment"];
        if (string.IsNullOrWhiteSpace(environment))
            throw new RuleException("chat_unconfigured", "Firestore environment is not configured.", 503);
        return Results.Ok(new { environment, path = MeetingRules.ChatPath(environment, session.Id) });
    }

    static async Task<IResult> Mute(Guid id, ParticipantBody body, HttpContext http, ToviDb db, IConfiguration configuration, CancellationToken ct)
    {
        var actor = RequireHost(http);
        var session = await OwnSession(actor, db, id, ct);
        var room = RoomOrThrow(configuration, session.Id);
        if (body.AllStudents)
        {
            await LiveKitRoom.MuteStudentsAsync(configuration, room, ct);
        }
        else if (!string.IsNullOrWhiteSpace(body.Identity))
        {
            await LiveKitRoom.SendAsync(configuration, room, "MutePublishedTrack", new { room, identity = body.Identity, muted = true }, ct);
        }
        else throw new RuleException("identity_required", "Choose a participant or mute all students.");
        return Results.Ok(new { requested = true, session = session.Id });
    }

    static async Task<IResult> Remove(Guid id, ParticipantBody body, HttpContext http, ToviDb db, IConfiguration configuration, CancellationToken ct)
    {
        var actor = RequireHost(http);
        var session = await OwnSession(actor, db, id, ct);
        if (string.IsNullOrWhiteSpace(body.Identity))
            throw new RuleException("identity_required", "Choose a participant.");
        var room = RoomOrThrow(configuration, session.Id);
        await LiveKitRoom.SendAsync(configuration, room, "RemoveParticipant", new { room, identity = body.Identity }, ct);
        return Results.Ok(new { removed = body.Identity });
    }

    static IResult Recording(Guid id, HttpContext http, IConfiguration configuration)
    {
        RequireHost(http);
        if (!configuration.GetValue<bool>("LiveKit:EgressEnabled"))
            throw new RuleException("recording_disabled", "Recording is disabled until egress is configured.", 409);
        return Results.Json(new { error = "recording_unconfigured" }, statusCode: 503);
    }

    static string RoomOrThrow(IConfiguration configuration, Guid sessionId)
    {
        var environment = configuration["Firebase:Environment"];
        if (string.IsNullOrWhiteSpace(configuration["LiveKit:Host"]) || string.IsNullOrWhiteSpace(environment))
            throw new RuleException("livekit_unconfigured", "LiveKit is not configured on the server.", 503);
        return MeetingRules.Room(environment, sessionId);
    }

    static Actor RequireHost(HttpContext http)
    {
        var actor = http.Items["actor"] as Actor ?? throw new RuleException("session_dead", "The session is missing or no longer valid.", 401);
        var teacher = actor.User.Type == UserType.Teacher;
        var assistant = actor.User.Type == UserType.Employee && actor.Portal == UserType.Teacher && actor.Allows(PermissionCatalog.ManageMeeting);
        if (!teacher && !assistant)
            throw new RuleException("forbidden", "Only the teacher or a permitted assistant can manage the meeting.", 403);
        return actor;
    }

    static async Task<ClassSession> OwnSession(Actor actor, ToviDb db, Guid id, CancellationToken ct)
    {
        var session = await db.SessionsSchedule.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new RuleException("not_found", "Session was not found.", 404);
        if (session.TeacherId != actor.Owner.Id)
            throw new RuleException("forbidden", "This meeting belongs to another teacher.", 403);
        return session;
    }

    public record ParticipantBody(string? Identity, bool AllStudents);
}

public static class FilesApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/files/{id:guid}", async (Guid id, HttpContext http, ToviDb db, IWebHostEnvironment env, IConfiguration config, CancellationToken ct) =>
        {
            var file = await db.Files.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new RuleException("not_found", "File was not found.", 404);
            if (!file.Public && http.Items["actor"] is null)
                throw new RuleException("session_dead", "The session is missing or no longer valid.", 401);
            var path = StoragePath.Combine(Root(env, config), file.RelativePath);
            if (!File.Exists(path)) throw new RuleException("not_found", "File was not found.", 404);
            return Results.File(path, file.ContentType, file.OriginalName);
        });

        app.MapPost("/api/payments/webhook", Webhook);
        app.MapGet("/api/payments/{id:guid}/checkout", Checkout);
        app.MapPost("/api/payments/{id:guid}/dev-confirm", DevConfirm);
    }

    public static async Task<StoredFile> SaveAsync(ToviDb db, IWebHostEnvironment env, IConfiguration config, IClock clock, IFormFile file, bool isPublic, CancellationToken ct)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp", "application/pdf" };
        if (!allowed.Contains(file.ContentType))
            throw new RuleException("file_type", "Upload a PNG, JPEG, WEBP, or PDF.");
        if (file.Length is <= 0 or > 15 * 1024 * 1024)
            throw new RuleException("file_size", "The file is empty or too large.");
        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (!SignatureMatches(file.ContentType, buffer.ToArray()))
            throw new RuleException("file_type", "The file contents do not match an allowed PNG, JPEG, WEBP, or PDF.");
        var root = Root(env, config);
        Directory.CreateDirectory(root);
        var ext = file.ContentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png"
            : file.ContentType.Contains("webp", StringComparison.OrdinalIgnoreCase) ? ".webp"
            : file.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) ? ".pdf" : ".jpg";
        var name = Guid.NewGuid().ToString("N") + ext;
        await using (var stream = File.Create(StoragePath.Combine(root, name)))
            await stream.WriteAsync(buffer.ToArray(), ct);
        var stored = new StoredFile
        {
            Id = Guid.NewGuid(),
            OriginalName = Path.GetFileName(file.FileName),
            ContentType = file.ContentType,
            RelativePath = name,
            Public = isPublic,
            CreatedAt = clock.UtcNow
        };
        db.Files.Add(stored);
        return stored;
    }

    static string Root(IWebHostEnvironment env, IConfiguration config)
    {
        var configured = config["Storage:Root"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "data", "uploads"));
    }

    public static bool SignatureMatches(string contentType, ReadOnlySpan<byte> bytes)
    {
        if (contentType.Contains("png", StringComparison.OrdinalIgnoreCase))
            return bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        if (contentType.Contains("webp", StringComparison.OrdinalIgnoreCase))
            return bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';
        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
            return bytes.Length >= 4 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F';
        return bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
    }

    static async Task<IResult> Webhook(HttpContext http, CommerceWork commerce, IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (body.Contains("cardNumber", StringComparison.OrdinalIgnoreCase) || body.Contains("cvv", StringComparison.OrdinalIgnoreCase))
            throw new RuleException("card_data_rejected", "Card data is not accepted.");
        var secret = configuration["Payments:WebhookSecret"] ?? "";
        if (!Signatures.Matches(secret, body, http.Request.Headers["X-Tovi-Signature"]))
            throw new RuleException("bad_signature", "The payment signature is invalid.", 401);
        var payload = System.Text.Json.JsonSerializer.Deserialize<WebhookBody>(body, Http.Json)
            ?? throw new RuleException("invalid_body", "The payment body is invalid.");
        await commerce.ApplyPaymentAsync(payload.PaymentId, payload.Status == "succeeded", ct);
        loggerFactory.CreateLogger("payments").LogInformation("Payment webhook {PaymentId} {Status}", payload.PaymentId, payload.Status);
        return Results.Ok(new { accepted = true });
    }

    static IResult Checkout(Guid id, IHostEnvironment env)
    {
        if (!env.IsDevelopment())
            throw new RuleException("not_found", "Checkout is handled by the payment gateway.", 404);
        var html = $$"""
        <!doctype html><html lang="ar" dir="rtl"><meta charset="utf-8"><title>TOVI</title>
        <body style="font-family:sans-serif;padding:40px">
        <h1>تأكيد الدفع</h1>
        <p>هذه بوابة تطوير. لا تُدخل بيانات بطاقة.</p>
        <form method="post" action="/api/payments/{{id}}/dev-confirm"><button type="submit">تأكيد الدفع</button></form>
        </body></html>
        """;
        return Results.Content(html, "text/html");
    }

    static async Task<IResult> DevConfirm(Guid id, IHostEnvironment env, CommerceWork commerce, CancellationToken ct)
    {
        if (!env.IsDevelopment())
            throw new RuleException("forbidden", "This confirmation exists only in development.", 403);
        await commerce.ApplyPaymentAsync(id, true, ct);
        return Results.Ok(new { activated = true });
    }

    sealed record WebhookBody(Guid PaymentId, string Status);
}

static class LiveKitRoom
{
    public static async Task<string> SendAsync(IConfiguration config, string room, string method, object body, CancellationToken ct)
    {
        var host = config["LiveKit:Host"]!.TrimEnd('/');
        var token = LiveKitTokens.Create(config["LiveKit:ApiKey"] ?? "", config["LiveKit:ApiSecret"] ?? "", "tovi-server", room, true, false, DateTime.UtcNow.AddMinutes(5));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/twirp/livekit.RoomService/{method}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new RuleException("livekit_rejected", "LiveKit rejected the room command.", 502);
        return text;
    }

    public static async Task MuteStudentsAsync(IConfiguration config, string room, CancellationToken ct)
    {
        var json = await SendAsync(config, room, "ListParticipants", new { room }, ct);
        using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (!doc.RootElement.TryGetProperty("participants", out var people)) return;
        foreach (var person in people.EnumerateArray())
        {
            var identity = person.TryGetProperty("identity", out var id) ? id.GetString() : null;
            if (identity is null || !identity.StartsWith("student:", StringComparison.Ordinal)) continue;
            if (!person.TryGetProperty("tracks", out var tracks)) continue;
            foreach (var track in tracks.EnumerateArray())
            {
                var sid = track.TryGetProperty("sid", out var value) ? value.GetString() : null;
                if (sid is null) continue;
                await SendAsync(config, room, "MutePublishedTrack", new { room, identity, trackSid = sid, muted = true }, ct);
            }
        }
    }
}
