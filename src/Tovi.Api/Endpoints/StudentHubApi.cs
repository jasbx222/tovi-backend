using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Api.Services;
using Tovi.Domain;

namespace Tovi.Api.Endpoints;

public record AccountBody(string FirstName, string? LastName, Gender? Gender, Guid? GradeId, Guid? SectionId, Guid? GovernorateId);
public record PasswordChangeBody(string Current, string Next);

public record CourseTile(Guid Id, string Title, string? Description, string? CoverUrl, Guid TeacherId, string TeacherName, string? TeacherPhotoUrl,
    string? Subject, string? Grade, int Lessons, int Done, bool Unlocked, bool Liked, bool Saved, int Likes);

/// <summary>
/// Student features built on top of the catalog: exams, account, inbox, saved courses, search,
/// sessions and replays, leaderboard, institutes, activations and explore.
/// </summary>
public static class StudentHubApi
{
    public static string? FileUrl(Guid? id) => id is null ? null : $"/api/files/{id}";

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/student");
        g.MapGet("/exams", Exams);
        g.MapGet("/exams/{id:guid}/review", Review);
        g.MapGet("/account", Account);
        g.MapPut("/account", UpdateAccount);
        g.MapPost("/account/photo", Photo);
        g.MapPost("/account/password", ChangePassword);
        g.MapGet("/inbox", Inbox);
        g.MapGet("/inbox/unread", Unread);
        g.MapPost("/inbox/{id:guid}/read", Read);
        g.MapPost("/inbox/read-all", ReadAll);
        g.MapGet("/saved", Saved);
        g.MapGet("/courses/{id:guid}/marks", Marks);
        g.MapDelete("/courses/{id:guid}/like", (Guid id, HttpContext http, ToviDb db, CancellationToken ct) => Unmark(http, db, id, true, ct));
        g.MapDelete("/courses/{id:guid}/save", (Guid id, HttpContext http, ToviDb db, CancellationToken ct) => Unmark(http, db, id, false, ct));
        g.MapGet("/find", Find);
        g.MapGet("/sessions", Sessions);
        g.MapGet("/replays", Replays);
        g.MapGet("/leaderboard", Leaderboard);
        g.MapGet("/places", Places);
        g.MapGet("/institutes/{id:guid}", Institute);
        g.MapGet("/unlocked", Unlocked);
        g.MapGet("/explore", Explore);
        g.MapGet("/explore/courses", ExploreCourses);
    }

    // ---------- shared projections ----------

    public static async Task<List<CourseTile>> TilesAsync(ToviDb db, Guid studentId, List<Course> courses, CancellationToken ct)
    {
        if (courses.Count == 0) return [];
        var ids = courses.Select(c => c.Id).ToList();
        var teacherIds = courses.Select(c => c.TeacherId).Distinct().ToList();
        var teachers = await db.Users.Where(u => teacherIds.Contains(u.Id)).Select(u => new { u.Id, u.FirstName, u.LastName, u.PhotoFileId }).ToDictionaryAsync(u => u.Id, ct);
        var subjectIds = courses.Where(c => c.SubjectId != null).Select(c => c.SubjectId!.Value).Distinct().ToList();
        var gradeIds = courses.Where(c => c.GradeId != null).Select(c => c.GradeId!.Value).Distinct().ToList();
        var subjects = await db.Subjects.Where(s => subjectIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var grades = await db.Grades.Where(s => gradeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var lessons = await (from l in db.Lessons join ch in db.Chapters on l.ChapterId equals ch.Id where ids.Contains(ch.CourseId) select new { l.Id, ch.CourseId }).ToListAsync(ct);
        var lessonIds = lessons.Select(l => l.Id).ToList();
        var done = await db.LessonProgress.Where(p => p.StudentId == studentId && lessonIds.Contains(p.LessonId)).Select(p => p.LessonId).ToListAsync(ct);
        var doneSet = done.ToHashSet();
        var unlocked = (await db.Enrollments.Where(e => e.StudentId == studentId && e.TargetType == AccessTarget.Course && ids.Contains(e.TargetId)).Select(e => e.TargetId).ToListAsync(ct)).ToHashSet();
        var liked = (await db.Likes.Where(l => l.StudentId == studentId && ids.Contains(l.CourseId)).Select(l => l.CourseId).ToListAsync(ct)).ToHashSet();
        var saved = (await db.SavedCourses.Where(l => l.StudentId == studentId && ids.Contains(l.CourseId)).Select(l => l.CourseId).ToListAsync(ct)).ToHashSet();
        var likes = await db.Likes.Where(l => ids.Contains(l.CourseId)).GroupBy(l => l.CourseId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        return courses.Select(c =>
        {
            teachers.TryGetValue(c.TeacherId, out var t);
            var mine = lessons.Where(l => l.CourseId == c.Id).ToList();
            return new CourseTile(c.Id, c.Title, c.Description, FileUrl(c.CoverFileId), c.TeacherId,
                t is null ? "" : $"{t.FirstName} {t.LastName}".Trim(), FileUrl(t?.PhotoFileId),
                c.SubjectId is { } s && subjects.TryGetValue(s, out var sn) ? sn : null,
                c.GradeId is { } gr && grades.TryGetValue(gr, out var gn) ? gn : null,
                mine.Count, mine.Count(l => doneSet.Contains(l.Id)), unlocked.Contains(c.Id), liked.Contains(c.Id), saved.Contains(c.Id),
                likes.TryGetValue(c.Id, out var n) ? n : 0);
        }).ToList();
    }

    static async Task<List<object>> TeacherRowsAsync(ToviDb db, Guid studentId, List<User> teachers, CancellationToken ct)
    {
        if (teachers.Count == 0) return [];
        var ids = teachers.Select(t => t.Id).ToList();
        var followers = await db.Follows.Where(f => ids.Contains(f.TeacherId)).GroupBy(f => f.TeacherId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var mine = (await db.Follows.Where(f => f.StudentId == studentId && ids.Contains(f.TeacherId)).Select(f => f.TeacherId).ToListAsync(ct)).ToHashSet();
        var courses = await db.Courses.Where(c => ids.Contains(c.TeacherId)).Select(c => new { c.TeacherId, c.SubjectId }).ToListAsync(ct);
        var subjectIds = courses.Where(c => c.SubjectId != null).Select(c => c.SubjectId!.Value).Distinct().ToList();
        var subjects = await db.Subjects.Where(s => subjectIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return teachers.Select(t =>
        {
            var own = courses.Where(c => c.TeacherId == t.Id).ToList();
            var subject = own.Where(c => c.SubjectId != null).GroupBy(c => c.SubjectId!.Value).OrderByDescending(x => x.Count()).Select(x => subjects.GetValueOrDefault(x.Key)).FirstOrDefault();
            return (object)new
            {
                t.Id,
                name = $"{t.FirstName} {t.LastName}".Trim(),
                photoUrl = FileUrl(t.PhotoFileId),
                subject,
                courses = own.Count,
                followers = followers.GetValueOrDefault(t.Id),
                following = mine.Contains(t.Id)
            };
        }).ToList();
    }

    static object Paged<T>(List<T> items, int page, int size, int total) => new { items, page, pageSize = size, total, hasMore = page * size < total };

    static (int Page, int Size) Pages(int? page, int? pageSize)
    {
        RuleException.Require(PageRules.Resolve(page, pageSize, out var p, out var s));
        return (p, s);
    }

    // ---------- exams ----------

    static async Task<IResult> Exams(HttpContext http, ToviDb db, IClock clock, string? state, Guid? courseId, int? page, int? pageSize, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var (p, size) = Pages(page, pageSize);
        var examIds = await db.Enrollments.Where(e => e.StudentId == me && e.TargetType == AccessTarget.Exam).Select(e => e.TargetId).ToListAsync(ct);
        var courseIds = await db.Enrollments.Where(e => e.StudentId == me && e.TargetType == AccessTarget.Course).Select(e => e.TargetId).ToListAsync(ct);
        var query = db.Exams.Where(e => examIds.Contains(e.Id) || (e.CourseId != null && courseIds.Contains(e.CourseId.Value)));
        if (courseId is not null) query = query.Where(e => e.CourseId == courseId);
        var exams = await query.OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
        var ids = exams.Select(e => e.Id).ToList();
        var attempts = await db.Attempts.Where(a => a.StudentId == me && ids.Contains(a.ExamId)).ToListAsync(ct);
        var attemptIds = attempts.Select(a => a.Id).ToList();
        var pending = (await db.Answers.Where(a => attemptIds.Contains(a.AttemptId) && !a.Corrected).Select(a => a.AttemptId).Distinct().ToListAsync(ct)).ToHashSet();
        var openEnded = (await db.Questions.Where(q => ids.Contains(q.ExamId) && q.Type == QuestionType.OpenEnded).Select(q => q.Id).ToListAsync(ct)).ToHashSet();
        var counts = await db.Questions.Where(q => ids.Contains(q.ExamId)).GroupBy(q => q.ExamId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var cIds = exams.Where(e => e.CourseId != null).Select(e => e.CourseId!.Value).Distinct().ToList();
        var courseTitles = await db.Courses.Where(c => cIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Title, ct);
        var tIds = exams.Select(e => e.TeacherId).Distinct().ToList();
        var teachers = await db.Users.Where(u => tIds.Contains(u.Id)).Select(u => new { u.Id, u.FirstName, u.LastName, u.PhotoFileId }).ToDictionaryAsync(u => u.Id, ct);
        var now = clock.UtcNow;
        var rows = exams.Select(e =>
        {
            var attempt = attempts.FirstOrDefault(a => a.ExamId == e.Id);
            var waiting = attempt is not null && pending.Contains(attempt.Id);
            var st = StudentRules.ExamState(examIds.Contains(e.Id), attempt is not null, waiting, e.Status, e.EndDate, now);
            teachers.TryGetValue(e.TeacherId, out var t);
            return new
            {
                e.Id, e.Title, e.Mark, e.EndDate, e.CourseId,
                courseTitle = e.CourseId is { } c ? courseTitles.GetValueOrDefault(c) : null,
                teacherName = t is null ? "" : $"{t.FirstName} {t.LastName}".Trim(),
                teacherPhotoUrl = FileUrl(t?.PhotoFileId),
                questions = counts.GetValueOrDefault(e.Id),
                state = st,
                score = attempt?.TotalMark,
                submittedAt = attempt?.SubmittedAt
            };
        }).ToList();
        var counts2 = new
        {
            open = rows.Count(r => r.state == "open"),
            done = rows.Count(r => r.state == "done" || r.state == "pending"),
            locked = rows.Count(r => r.state == "locked")
        };
        if (!string.IsNullOrWhiteSpace(state)) rows = rows.Where(r => r.state == state || (state == "submitted" && r.state is "done" or "pending")).ToList();
        var slice = rows.Skip((p - 1) * size).Take(size).ToList();
        return Results.Ok(new { items = slice, page = p, pageSize = size, total = rows.Count, hasMore = p * size < rows.Count, counts = counts2 });
    }

    static async Task<IResult> Review(Guid id, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var exam = await db.Exams.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new RuleException("not_found", "Exam was not found.", 404);
        var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.ExamId == id && a.StudentId == me, ct)
            ?? throw new RuleException("not_found", "Submit the exam before reviewing it.", 404);
        var questions = await db.Questions.Where(q => q.ExamId == id).OrderBy(q => q.SortOrder).ToListAsync(ct);
        var qIds = questions.Select(q => q.Id).ToList();
        var options = await db.QuestionOptions.Where(o => qIds.Contains(o.QuestionId)).ToListAsync(ct);
        var answers = await db.Answers.Where(a => a.AttemptId == attempt.Id).ToListAsync(ct);
        var items = questions.Select((q, i) =>
        {
            var answer = answers.FirstOrDefault(a => a.QuestionId == q.Id);
            var response = answer?.Response ?? "";
            var own = options.Where(o => o.QuestionId == q.Id).ToList();
            bool? correct = q.Type switch
            {
                QuestionType.OpenEnded => answer is { Corrected: true } ? answer.Mark >= q.Mark : null,
                _ => answer?.Mark is { } m ? m >= q.Mark : null
            };
            return new
            {
                q.Id, number = i + 1, type = q.Type.ToString(), q.Text, q.Mark,
                awarded = answer?.Mark,
                corrected = answer?.Corrected ?? false,
                pending = q.Type == QuestionType.OpenEnded && answer is { Corrected: false },
                correct,
                response,
                correctBool = q.Type == QuestionType.TrueFalse ? q.CorrectBool : null,
                options = own.Select(o => new { o.Id, o.Text, o.IsCorrect, chosen = string.Equals(response, o.Id.ToString(), StringComparison.OrdinalIgnoreCase) || response == o.Text })
            };
        }).ToList();
        return Results.Ok(new
        {
            exam = new { exam.Id, exam.Title, exam.Mark },
            attempt.TotalMark,
            attempt.SubmittedAt,
            right = items.Count(x => x.correct == true),
            wrong = items.Count(x => x.correct == false),
            pending = items.Count(x => x.pending),
            items
        });
    }

    // ---------- account ----------

    static async Task<object> AccountView(ToviDb db, User user, ProgressWork progress, CancellationToken ct)
    {
        var grade = user.GradeId is null ? null : await db.Grades.Where(x => x.Id == user.GradeId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        var section = user.SectionId is null ? null : await db.Sections.Where(x => x.Id == user.SectionId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        var governorate = user.GovernorateId is null ? null : await db.Governorates.Where(x => x.Id == user.GovernorateId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        var totals = await progress.TotalsAsync([user.Id], ct);
        totals.TryGetValue(user.Id, out var mine);
        return new
        {
            user.Id, user.FirstName, user.LastName, user.Phone, gender = user.Gender.ToString(),
            user.GradeId, grade, user.SectionId, section, user.GovernorateId, governorate,
            photoUrl = FileUrl(user.PhotoFileId),
            user.CreatedAt,
            stats = new
            {
                xp = mine.Xp,
                streak = mine.Streak,
                courses = await db.Enrollments.CountAsync(e => e.StudentId == user.Id && e.TargetType == AccessTarget.Course, ct),
                following = await db.Follows.CountAsync(f => f.StudentId == user.Id, ct),
                saved = await db.SavedCourses.CountAsync(s => s.StudentId == user.Id, ct)
            }
        };
    }

    static async Task<IResult> Account(HttpContext http, ToviDb db, ProgressWork progress, CancellationToken ct)
    {
        var id = Http.Me(http, UserType.Student).User.Id;
        var user = await db.Users.FirstAsync(u => u.Id == id, ct);
        return Results.Ok(await AccountView(db, user, progress, ct));
    }

    static async Task<IResult> UpdateAccount(AccountBody body, HttpContext http, ToviDb db, ProgressWork progress, CancellationToken ct)
    {
        var id = Http.Me(http, UserType.Student).User.Id;
        RuleException.Require(StudentRules.Name(body.FirstName));
        if (body.GradeId is { } gr && !await db.Grades.AnyAsync(x => x.Id == gr, ct)) throw new RuleException("not_found", "Grade was not found.", 404);
        if (body.SectionId is { } se && !await db.Sections.AnyAsync(x => x.Id == se, ct)) throw new RuleException("not_found", "Section was not found.", 404);
        if (body.GovernorateId is { } go && !await db.Governorates.AnyAsync(x => x.Id == go, ct)) throw new RuleException("not_found", "Governorate was not found.", 404);
        var user = await db.Users.FirstAsync(u => u.Id == id, ct);
        user.FirstName = body.FirstName.Trim();
        user.LastName = (body.LastName ?? "").Trim();
        if (body.Gender is { } gender) user.Gender = gender;
        user.GradeId = body.GradeId;
        user.SectionId = body.SectionId;
        user.GovernorateId = body.GovernorateId;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await AccountView(db, user, progress, ct));
    }

    static async Task<IResult> Photo(HttpContext http, ToviDb db, IWebHostEnvironment env, IConfiguration config, IClock clock, ProgressWork progress, CancellationToken ct)
    {
        var id = Http.Me(http, UserType.Student).User.Id;
        if (!http.Request.HasFormContentType) throw new RuleException("image_required", "Upload the image as a form file.");
        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files["photo"] ?? form.Files.FirstOrDefault() ?? throw new RuleException("image_required", "Choose an image to upload.");
        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw new RuleException("file_type", "Upload a PNG, JPEG, or WEBP image.");
        var stored = await FilesApi.SaveAsync(db, env, config, clock, file, true, ct);
        var user = await db.Users.FirstAsync(u => u.Id == id, ct);
        user.PhotoFileId = stored.Id;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await AccountView(db, user, progress, ct));
    }

    static async Task<IResult> ChangePassword(PasswordChangeBody body, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        var user = await db.Users.FirstAsync(u => u.Id == actor.User.Id, ct);
        RuleException.Require(StudentRules.ChangePassword(Passwords.Verify(user, body.Current ?? ""), body.Next, body.Current));
        user.PasswordHash = Passwords.Hash(user, body.Next);
        // Other devices sign in again; this device keeps its session.
        var others = await db.Sessions.Where(s => s.UserId == user.Id && s.Id != actor.Session.Id && s.RevokedAt == null).ToListAsync(ct);
        foreach (var other in others) other.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { changed = true, signedOut = others.Count });
    }

    // ---------- inbox ----------

    static async Task<IResult> Inbox(HttpContext http, ToviDb db, bool? unread, int? page, int? pageSize, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var (p, size) = Pages(page, pageSize);
        var query = db.Notifications.Where(n => n.UserId == me);
        if (unread == true) query = query.Where(n => !n.Read);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(n => n.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var items = rows.Select(n =>
        {
            var (type, target) = StudentRules.Link(n.TargetType, n.TargetId);
            return new { n.Id, n.Title, n.Body, type = n.Type.ToString(), n.CreatedAt, n.Read, targetType = type, targetId = target };
        }).ToList();
        var count = await db.Notifications.CountAsync(n => n.UserId == me && !n.Read, ct);
        return Results.Ok(new { items, page = p, pageSize = size, total, hasMore = p * size < total, unread = count });
    }

    static async Task<IResult> Unread(HttpContext http, ToviDb db, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        return Results.Ok(new { unread = await db.Notifications.CountAsync(n => n.UserId == me && !n.Read, ct) });
    }

    static async Task<IResult> Read(Guid id, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var row = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == me, ct) ?? throw new RuleException("not_found", "Notification was not found.", 404);
        if (!row.Read) { row.Read = true; row.ReadAt = clock.UtcNow; await db.SaveChangesAsync(ct); }
        return Results.Ok(new { row.Id, row.Read, unread = await db.Notifications.CountAsync(n => n.UserId == me && !n.Read, ct) });
    }

    static async Task<IResult> ReadAll(HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var rows = await db.Notifications.Where(n => n.UserId == me && !n.Read).ToListAsync(ct);
        foreach (var row in rows) { row.Read = true; row.ReadAt = clock.UtcNow; }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { marked = rows.Count, unread = 0 });
    }

    // ---------- saved / liked ----------

    static async Task<IResult> Saved(HttpContext http, ToviDb db, string? kind, int? page, int? pageSize, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var (p, size) = Pages(page, pageSize);
        var liked = kind == "liked";
        var ids = liked
            ? await db.Likes.Where(l => l.StudentId == me).Select(l => l.CourseId).ToListAsync(ct)
            : await db.SavedCourses.Where(l => l.StudentId == me).Select(l => l.CourseId).ToListAsync(ct);
        var query = db.Courses.Where(c => ids.Contains(c.Id)).OrderBy(c => c.Title);
        var total = await query.CountAsync(ct);
        var rows = await query.Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(Paged(await TilesAsync(db, me, rows, ct), p, size, total));
    }

    static async Task<IResult> Marks(Guid id, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        return Results.Ok(new
        {
            liked = await db.Likes.AnyAsync(l => l.StudentId == me && l.CourseId == id, ct),
            saved = await db.SavedCourses.AnyAsync(l => l.StudentId == me && l.CourseId == id, ct),
            likes = await db.Likes.CountAsync(l => l.CourseId == id, ct)
        });
    }

    static async Task<IResult> Unmark(HttpContext http, ToviDb db, Guid id, bool like, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        if (like) db.Likes.RemoveRange(await db.Likes.Where(l => l.StudentId == me && l.CourseId == id).ToListAsync(ct));
        else db.SavedCourses.RemoveRange(await db.SavedCourses.Where(l => l.StudentId == me && l.CourseId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return await Marks(id, http, db, ct);
    }

    // ---------- search ----------

    static async Task<IResult> Find(HttpContext http, ToviDb db, string? q, string? type, Guid? gradeId, Guid? subjectId, int? page, int? pageSize, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var (p, size) = Pages(page, pageSize);
        var text = (q ?? "").Trim();
        var kind = string.IsNullOrWhiteSpace(type) ? "all" : type;
        var take = kind == "all" ? 6 : size;
        var skip = kind == "all" ? 0 : (p - 1) * size;

        var courses = db.Courses.AsQueryable();
        if (text.Length > 0) courses = courses.Where(c => c.Title.Contains(text) || (c.Description != null && c.Description.Contains(text)));
        if (gradeId is not null) courses = courses.Where(c => c.GradeId == gradeId);
        if (subjectId is not null) courses = courses.Where(c => c.SubjectId == subjectId);
        var teachers = db.Users.Where(u => u.Type == UserType.Teacher && u.Status == UserStatus.Active);
        if (text.Length > 0) teachers = teachers.Where(u => (u.FirstName + " " + u.LastName).Contains(text));
        if (subjectId is not null) teachers = teachers.Where(u => db.Courses.Any(c => c.TeacherId == u.Id && c.SubjectId == subjectId));
        var summaries = db.Summaries.AsQueryable();
        if (text.Length > 0) summaries = summaries.Where(s => s.Title.Contains(text));
        var institutes = db.Users.Where(u => u.Type == UserType.InstituteManager && u.Status == UserStatus.Active);
        if (text.Length > 0) institutes = institutes.Where(u => u.InstituteName != null && u.InstituteName.Contains(text));

        var result = new Dictionary<string, object>();
        var totals = new Dictionary<string, int>
        {
            ["courses"] = await courses.CountAsync(ct),
            ["teachers"] = await teachers.CountAsync(ct),
            ["summaries"] = await summaries.CountAsync(ct),
            ["institutes"] = await institutes.CountAsync(ct),
        };
        if (kind is "all" or "courses")
            result["courses"] = await TilesAsync(db, me, await courses.OrderByDescending(c => c.CreatedAt).Skip(skip).Take(take).ToListAsync(ct), ct);
        if (kind is "all" or "teachers")
            result["teachers"] = await TeacherRowsAsync(db, me, await teachers.OrderBy(u => u.FirstName).Skip(skip).Take(take).ToListAsync(ct), ct);
        if (kind is "all" or "summaries")
        {
            var rows = await summaries.OrderBy(s => s.Title).Skip(skip).Take(take).ToListAsync(ct);
            result["summaries"] = await SummaryRowsAsync(db, me, rows, ct);
        }
        if (kind is "all" or "institutes")
            result["institutes"] = await PlaceRowsAsync(db, await institutes.OrderBy(u => u.InstituteName).Skip(skip).Take(take).ToListAsync(ct), ct);
        var hasMore = kind != "all" && totals.TryGetValue(kind, out var t) && p * size < t;
        return Results.Ok(new { q = text, type = kind, page = p, pageSize = size, totals, hasMore, results = result });
    }

    static async Task<List<object>> SummaryRowsAsync(ToviDb db, Guid me, List<Summary> rows, CancellationToken ct)
    {
        var tIds = rows.Select(s => s.TeacherId).Distinct().ToList();
        var teachers = await db.Users.Where(u => tIds.Contains(u.Id)).Select(u => new { u.Id, u.FirstName, u.LastName, u.PhotoFileId }).ToDictionaryAsync(u => u.Id, ct);
        var ids = rows.Select(s => s.Id).ToList();
        var mine = (await db.Enrollments.Where(e => e.StudentId == me && e.TargetType == AccessTarget.Summary && ids.Contains(e.TargetId)).Select(e => e.TargetId).ToListAsync(ct)).ToHashSet();
        return rows.Select(s =>
        {
            teachers.TryGetValue(s.TeacherId, out var t);
            return (object)new { s.Id, s.Title, s.CourseId, teacherName = t is null ? "" : $"{t.FirstName} {t.LastName}".Trim(), teacherPhotoUrl = FileUrl(t?.PhotoFileId), unlocked = mine.Contains(s.Id) };
        }).ToList();
    }

    static async Task<List<object>> PlaceRowsAsync(ToviDb db, List<User> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var teachers = await db.InstituteTeachers.Where(x => ids.Contains(x.InstituteId)).GroupBy(x => x.InstituteId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var courses = await db.Courses.Where(c => c.InstituteId != null && ids.Contains(c.InstituteId.Value)).GroupBy(c => c.InstituteId!.Value).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        return rows.Select(r => (object)new
        {
            r.Id, name = r.InstituteName ?? r.FirstName, logoUrl = FileUrl(r.LogoFileId), r.Address,
            teachers = teachers.GetValueOrDefault(r.Id), courses = courses.GetValueOrDefault(r.Id)
        }).ToList();
    }

    // ---------- sessions and replays ----------

    static async Task<IResult> Sessions(HttpContext http, ToviDb db, IClock clock, string? when, int? page, int? pageSize, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var (p, size) = Pages(page, pageSize);
        var rooms = await db.Enrollments.Where(e => e.StudentId == me && e.TargetType == AccessTarget.VirtualClassroom).Select(e => e.TargetId).ToListAsync(ct);
        var all = await db.SessionsSchedule.Where(s => rooms.Contains(s.ClassroomId)).ToListAsync(ct);
        var now = clock.UtcNow;
        var past = when == "past";
        var picked = all.Where(s => past
                ? s.Status == SessionStatus.Canceled || s.StartsAt.AddMinutes(s.DurationMinutes) <= now
                : s.Status == SessionStatus.Scheduled && s.StartsAt.AddMinutes(s.DurationMinutes) > now)
            .ToList();
        picked = past ? picked.OrderByDescending(s => s.StartsAt).ToList() : picked.OrderBy(s => s.StartsAt).ToList();
        var slice = picked.Skip((p - 1) * size).Take(size).ToList();
        return Results.Ok(Paged(await SessionRowsAsync(db, me, slice, now, ct), p, size, picked.Count));
    }

    static async Task<List<object>> SessionRowsAsync(ToviDb db, Guid me, List<ClassSession> slice, DateTime now, CancellationToken ct)
    {
        var ids = slice.Select(s => s.Id).ToList();
        var roomIds = slice.Select(s => s.ClassroomId).Distinct().ToList();
        var tIds = slice.Select(s => s.TeacherId).Distinct().ToList();
        var rooms = await db.Classrooms.Where(c => roomIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Title, ct);
        var teachers = await db.Users.Where(u => tIds.Contains(u.Id)).Select(u => new { u.Id, u.FirstName, u.LastName, u.PhotoFileId }).ToDictionaryAsync(u => u.Id, ct);
        var presence = await db.Presences.Where(x => ids.Contains(x.SessionId) && x.UserId == me).ToListAsync(ct);
        var recordings = await db.Recordings.Where(r => ids.Contains(r.SessionId) && r.Status == "ready").Select(r => r.SessionId).ToListAsync(ct);
        var xp = await db.XpEvents.Where(e => e.StudentId == me && e.Kind == ProgressRules.Live && ids.Contains(e.SourceId)).ToDictionaryAsync(e => e.SourceId, e => e.Points, ct);
        return slice.Select(s =>
        {
            teachers.TryGetValue(s.TeacherId, out var t);
            var mine = presence.FirstOrDefault(x => x.SessionId == s.Id);
            return (object)new
            {
                s.Id, s.Title, s.StartsAt, s.DurationMinutes, s.ClassroomId,
                status = s.Status.ToString(),
                classroomTitle = rooms.GetValueOrDefault(s.ClassroomId) ?? "",
                teacherName = t is null ? "" : $"{t.FirstName} {t.LastName}".Trim(),
                teacherPhotoUrl = FileUrl(t?.PhotoFileId),
                phase = s.Status == SessionStatus.Canceled ? "cancelled" : MeetingRules.Phase(s.StartsAt, s.DurationMinutes, now),
                minutes = mine is null ? 0 : (int)Math.Round(mine.LiveSeconds / 60.0),
                attended = mine?.Attended ?? false,
                joined = mine?.JoinedLiveAt is not null,
                replays = recordings.Count(r => r == s.Id),
                xp = xp.GetValueOrDefault(s.Id)
            };
        }).ToList();
    }

    static async Task<IResult> Replays(HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var (p, size) = Pages(page, pageSize);
        var rooms = await db.Enrollments.Where(e => e.StudentId == me && e.TargetType == AccessTarget.VirtualClassroom).Select(e => e.TargetId).ToListAsync(ct);
        var query = from r in db.Recordings
                    join s in db.SessionsSchedule on r.SessionId equals s.Id
                    where rooms.Contains(s.ClassroomId) && r.Status == "ready"
                    select new { r, s };
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.r.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var tIds = rows.Select(x => x.s.TeacherId).Distinct().ToList();
        var roomIds = rows.Select(x => x.s.ClassroomId).Distinct().ToList();
        var teachers = await db.Users.Where(u => tIds.Contains(u.Id)).Select(u => new { u.Id, u.FirstName, u.LastName, u.PhotoFileId }).ToDictionaryAsync(u => u.Id, ct);
        var titles = await db.Classrooms.Where(c => roomIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Title, ct);
        var items = rows.Select(x =>
        {
            teachers.TryGetValue(x.s.TeacherId, out var t);
            var seconds = x.r.EndedAt is { } end ? (int)Math.Max(0, (end - x.r.CreatedAt).TotalSeconds) : 0;
            return new
            {
                x.r.Id, sessionId = x.s.Id, title = x.s.Title ?? titles.GetValueOrDefault(x.s.ClassroomId) ?? "",
                classroomTitle = titles.GetValueOrDefault(x.s.ClassroomId) ?? "",
                teacherName = t is null ? "" : $"{t.FirstName} {t.LastName}".Trim(), teacherPhotoUrl = FileUrl(t?.PhotoFileId),
                url = x.r.PlaybackPath, x.r.CreatedAt, durationMinutes = (int)Math.Round(seconds / 60.0), x.s.StartsAt
            };
        }).ToList();
        return Results.Ok(Paged(items, p, size, total));
    }

    // ---------- leaderboard ----------

    static async Task<IResult> Leaderboard(HttpContext http, ToviDb db, IClock clock, string? period, string? scope, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        var me = await db.Users.FirstAsync(u => u.Id == actor.User.Id, ct);
        var week = period != "all";
        var today = ProgressRules.Day(clock.UtcNow);
        var start = StudentRules.WeekStart(today);
        var students = db.Users.Where(u => u.Type == UserType.Student && u.Status == UserStatus.Active);
        var byGrade = scope == "grade" && me.GradeId is not null;
        if (byGrade) students = students.Where(u => u.GradeId == me.GradeId);
        var ids = await students.Select(u => u.Id).ToListAsync(ct);
        var events = db.XpEvents.Where(e => ids.Contains(e.StudentId));
        if (week) events = events.Where(e => string.Compare(e.Day, start) >= 0);
        var sums = await events.GroupBy(e => e.StudentId).Select(x => new { x.Key, xp = x.Sum(e => e.Points) }).ToListAsync(ct);
        var ranked = StudentRules.Rank(sums.Select(s => (s.Key, s.xp)));
        var top = ranked.Take(50).ToList();
        var topIds = top.Select(t => t.StudentId).Append(me.Id).Distinct().ToList();
        var people = await db.Users.Where(u => topIds.Contains(u.Id)).Select(u => new { u.Id, u.FirstName, u.LastName, u.PhotoFileId }).ToDictionaryAsync(u => u.Id, ct);
        object Row(StudentRules.Standing s)
        {
            people.TryGetValue(s.StudentId, out var u);
            return new { studentId = s.StudentId, s.Rank, s.Xp, name = u is null ? "" : StudentRules.PublicName(u.FirstName, u.LastName), photoUrl = FileUrl(u?.PhotoFileId), me = s.StudentId == me.Id };
        }
        var mine = ranked.FirstOrDefault(r => r.StudentId == me.Id);
        var grade = me.GradeId is null ? null : await db.Grades.Where(x => x.Id == me.GradeId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        return Results.Ok(new
        {
            period = week ? "week" : "all",
            scope = byGrade ? "grade" : "all",
            grade,
            weekStart = week ? start : null,
            players = ranked.Count,
            items = top.Select(Row).ToList(),
            me = mine is null
                ? new { studentId = me.Id, rank = (int?)null, xp = 0, name = StudentRules.PublicName(me.FirstName, me.LastName), photoUrl = FileUrl(me.PhotoFileId), me = true } as object
                : Row(mine)
        });
    }

    // ---------- institutes ----------

    static async Task<IResult> Places(HttpContext http, ToviDb db, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        Http.Me(http, UserType.Student);
        var (p, size) = Pages(page, pageSize);
        var query = db.Users.Where(u => u.Type == UserType.InstituteManager && u.Status == UserStatus.Active);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(u => u.InstituteName != null && u.InstituteName.Contains(q));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(u => u.InstituteName).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(Paged(await PlaceRowsAsync(db, rows, ct), p, size, total));
    }

    static async Task<IResult> Institute(Guid id, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var place = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.InstituteManager && u.Status == UserStatus.Active, ct)
            ?? throw new RuleException("not_found", "Institute was not found.", 404);
        var teacherIds = await db.InstituteTeachers.Where(x => x.InstituteId == id).Select(x => x.TeacherId).ToListAsync(ct);
        var teachers = await db.Users.Where(u => teacherIds.Contains(u.Id) && u.Status == UserStatus.Active).OrderBy(u => u.FirstName).ToListAsync(ct);
        var courses = await db.Courses.Where(c => c.InstituteId == id || teacherIds.Contains(c.TeacherId)).OrderByDescending(c => c.CreatedAt).Take(30).ToListAsync(ct);
        var now = clock.UtcNow;
        var upcoming = await db.SessionsSchedule.Where(s => s.InstituteId == id && s.Status == SessionStatus.Scheduled).OrderBy(s => s.StartsAt).Take(20).ToListAsync(ct);
        var rows = await SessionRowsAsync(db, me, upcoming.Where(s => s.StartsAt.AddMinutes(s.DurationMinutes) > now).Take(10).ToList(), now, ct);
        var governorate = place.GovernorateId is null ? null : await db.Governorates.Where(x => x.Id == place.GovernorateId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        return Results.Ok(new
        {
            institute = new { place.Id, name = place.InstituteName ?? place.FirstName, logoUrl = FileUrl(place.LogoFileId), place.Address, place.Description, governorate, teachers = teachers.Count, courses = courses.Count },
            teachers = await TeacherRowsAsync(db, me, teachers, ct),
            courses = await TilesAsync(db, me, courses, ct),
            upcoming = rows
        });
    }

    // ---------- what I unlocked ----------

    static async Task<IResult> Unlocked(HttpContext http, ToviDb db, string? type, int? page, int? pageSize, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var (p, size) = Pages(page, pageSize);
        var query = db.Enrollments.Where(e => e.StudentId == me);
        if (Enum.TryParse<AccessTarget>(type, true, out var target)) query = query.Where(e => e.TargetType == target);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var all = await db.Enrollments.Where(e => e.StudentId == me).GroupBy(e => e.TargetType).Select(x => new { x.Key, n = x.Count() }).ToListAsync(ct);
        var courseIds = rows.Where(r => r.TargetType == AccessTarget.Course).Select(r => r.TargetId).ToList();
        var tiles = (await TilesAsync(db, me, await db.Courses.Where(c => courseIds.Contains(c.Id)).ToListAsync(ct), ct)).ToDictionary(t => t.Id);
        var summaryIds = rows.Where(r => r.TargetType == AccessTarget.Summary).Select(r => r.TargetId).ToList();
        var summaries = await db.Summaries.Where(s => summaryIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Title, ct);
        var examIds = rows.Where(r => r.TargetType == AccessTarget.Exam).Select(r => r.TargetId).ToList();
        var exams = await db.Exams.Where(s => examIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Title, ct);
        var roomIds = rows.Where(r => r.TargetType == AccessTarget.VirtualClassroom).Select(r => r.TargetId).ToList();
        var rooms = await db.Classrooms.Where(s => roomIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Title, ct);
        var codeIds = rows.Select(r => r.CodeId).ToList();
        var codes = await db.Codes.Where(c => codeIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var items = rows.Select(r =>
        {
            tiles.TryGetValue(r.TargetId, out var tile);
            var title = r.TargetType switch
            {
                AccessTarget.Course => tile?.Title,
                AccessTarget.Summary => summaries.GetValueOrDefault(r.TargetId),
                AccessTarget.Exam => exams.GetValueOrDefault(r.TargetId),
                _ => rooms.GetValueOrDefault(r.TargetId)
            };
            codes.TryGetValue(r.CodeId, out var code);
            var masked = code is null ? null : MaskCode(code.Code);
            return new
            {
                r.Id, type = r.TargetType.ToString(), r.TargetId, title = title ?? "", unlockedAt = r.CreatedAt, code = masked,
                coverUrl = tile?.CoverUrl, teacherName = tile?.TeacherName, lessons = tile?.Lessons ?? 0, done = tile?.Done ?? 0
            };
        }).ToList();
        return Results.Ok(new
        {
            items, page = p, pageSize = size, total, hasMore = p * size < total,
            counts = Enum.GetValues<AccessTarget>().ToDictionary(t => t.ToString(), t => all.FirstOrDefault(a => a.Key == t)?.n ?? 0)
        });
    }

    static string MaskCode(string code)
    {
        var digits = new string((code ?? "").Where(char.IsLetterOrDigit).ToArray());
        return digits.Length <= 4 ? digits : new string('•', 4) + " " + digits[^4..];
    }

    // ---------- explore ----------

    static async Task<IResult> Explore(HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Student);
        var me = await db.Users.FirstAsync(u => u.Id == actor.User.Id, ct);
        var counts = await db.Courses.Where(c => c.SubjectId != null && (me.GradeId == null || c.GradeId == me.GradeId || c.GradeId == null))
            .GroupBy(c => c.SubjectId!.Value).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var subjects = (await db.Subjects.OrderBy(s => s.Name).ToListAsync(ct)).Select(s => new { s.Id, s.Name, courses = counts.GetValueOrDefault(s.Id) }).OrderByDescending(s => s.courses).ThenBy(s => s.Name).ToList();
        var forGrade = me.GradeId is null ? new List<Course>() : await db.Courses.Where(c => c.GradeId == me.GradeId).OrderByDescending(c => c.CreatedAt).Take(10).ToListAsync(ct);
        var fresh = await db.Courses.OrderByDescending(c => c.CreatedAt).Take(10).ToListAsync(ct);
        var popularIds = await db.Likes.GroupBy(l => l.CourseId).OrderByDescending(x => x.Count()).Select(x => x.Key).Take(10).ToListAsync(ct);
        var popular = await db.Courses.Where(c => popularIds.Contains(c.Id)).ToListAsync(ct);
        popular = popular.OrderBy(c => popularIds.IndexOf(c.Id)).ToList();
        var grade = me.GradeId is null ? null : await db.Grades.Where(x => x.Id == me.GradeId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        var teacherIds = await db.Users.Where(u => u.Type == UserType.Teacher && u.Status == UserStatus.Active)
            .OrderByDescending(u => db.Follows.Count(f => f.TeacherId == u.Id)).Take(10).ToListAsync(ct);
        var places = await db.Users.Where(u => u.Type == UserType.InstituteManager && u.Status == UserStatus.Active).OrderBy(u => u.InstituteName).Take(10).ToListAsync(ct);
        return Results.Ok(new
        {
            grade = new { id = me.GradeId, name = grade },
            subjects,
            grades = await db.Grades.OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(ct),
            sections = await db.Sections.OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(ct),
            domains = await db.LearningDomains.OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(ct),
            forGrade = await TilesAsync(db, me.Id, forGrade, ct),
            fresh = await TilesAsync(db, me.Id, fresh, ct),
            popular = await TilesAsync(db, me.Id, popular, ct),
            teachers = await TeacherRowsAsync(db, me.Id, teacherIds, ct),
            institutes = await PlaceRowsAsync(db, places, ct)
        });
    }

    static async Task<IResult> ExploreCourses(HttpContext http, ToviDb db, Guid? subjectId, Guid? gradeId, Guid? sectionId, Guid? domainId, string? sort, int? page, int? pageSize, CancellationToken ct)
    {
        var me = Http.Me(http, UserType.Student).User.Id;
        var (p, size) = Pages(page, pageSize);
        var query = db.Courses.AsQueryable();
        if (subjectId is not null) query = query.Where(c => c.SubjectId == subjectId);
        if (gradeId is not null) query = query.Where(c => c.GradeId == gradeId);
        if (sectionId is not null) query = query.Where(c => c.SectionId == sectionId);
        if (domainId is not null) query = query.Where(c => c.LearningDomainId == domainId);
        var total = await query.CountAsync(ct);
        var ordered = sort == "popular"
            ? query.OrderByDescending(c => db.Likes.Count(l => l.CourseId == c.Id)).ThenByDescending(c => c.CreatedAt)
            : sort == "title" ? query.OrderBy(c => c.Title) : query.OrderByDescending(c => c.CreatedAt);
        var rows = await ordered.Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(Paged(await TilesAsync(db, me, rows, ct), p, size, total));
    }
}
