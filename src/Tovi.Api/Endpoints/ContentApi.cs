using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Api.Services;
using Tovi.Domain;

namespace Tovi.Api.Endpoints;

public record CourseBody(string Title, string? Description, Guid? TeacherId, Guid? LearningDomainId, Guid? SectionId, Guid? GradeId, Guid? SubjectId);
public record ChapterBody(Guid CourseId, string Title);
public record LessonBody(Guid ChapterId, string Title, string? VideoReference);
public record OrderBody(List<Guid> LessonIds);
public record SummaryBody(string Title, string Body, Guid? CourseId);
public record ExamBody(string Title, Guid? CourseId, Guid? ChapterId, Guid? LessonId, int Mark, DateTime EndDate, List<Guid>? GroupIds);
public record QuestionBody(QuestionType Type, string Text, int Mark, bool? Correct, List<OptionBody>? Options);
public record OptionBody(string Text, bool Correct);
public record GroupNameBody(string Name);
public record MemberBody(Guid StudentId);
public record CodeCountBody(int Count);
public record AnswerMarkBody(int Mark);
public record ClassroomBody(string Title);
public record SessionBody(Guid ClassroomId, DateTime StartsAt, int? DurationMinutes);
public record RedeemBody(string Code);
public record StudentBody(string FirstName, string LastName, string Phone, string Password, StudentKind Kind, Guid? GradeId, Guid? SectionId, Guid? SchoolId);
public record SchoolBody(string Name);
public record ProfileBody(string FirstName, string LastName, string? Address, string? Description);

public static class ContentApi
{
    public static void MapTeacher(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/teacher");
        group.MapGet("/home", Home);
        MapShared(group);
        group.MapGet("/students", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.Teacher);
            actor.Require(PermissionCatalog.View("students"));
            var ids = await db.Enrollments.Where(e => db.Codes.Any(c => c.Id == e.CodeId && c.OwnerUserId == actor.Owner.Id)).Select(e => e.StudentId).Distinct().ToListAsync(ct);
            var rule = PageRules.Resolve(page, pageSize, out var p, out var s);
            RuleException.Require(rule);
            var query = db.Users.Where(u => ids.Contains(u.Id));
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(u => u.FirstName).Skip((p - 1) * s).Take(s).Select(u => new { u.Id, u.FirstName, u.LastName, u.Phone }).ToListAsync(ct);
            return Results.Ok(new { items, page = p, pageSize = s, total });
        });
        group.MapPost("/assistants", CreateStaff);
        group.MapPost("/employees", CreateStaff);
        group.MapPost("/permission-groups", async (GroupBody body, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.Teacher);
            actor.Require(PermissionCatalog.Create("employees"));
            return Results.Ok(await People.CreateGroup(actor, body, db, ct));
        });
        group.MapGet("/employees", ListStaff);
        group.MapPost("/hour-packages/redeem", async (RedeemBody body, HttpContext http, HourWork hours, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.Teacher);
            await Guards.EnsureTrialAsync(http.RequestServices.GetRequiredService<ToviDb>(), actor, http.RequestServices.GetRequiredService<IClock>(), ct);
            return Results.Ok(await hours.RedeemPackageAsync(actor.Owner.Id, body.Code, ct));
        });
        group.MapGet("/profile", (HttpContext http) =>
        {
            var user = Http.Me(http, UserType.Teacher).Owner;
            return Results.Ok(new { user.Id, user.FirstName, user.LastName, user.Phone, user.Address, user.Description, user.TeacherApproved, user.TrialEndsAt });
        });
        group.MapPut("/profile", UpdateProfile);
    }

    public static void MapInstitute(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/institute");
        group.MapGet("/dashboard", Dashboard);
        group.MapGet("/teachers", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.InstituteManager);
            actor.Require(AccessMap.Teachers);
            var ids = db.InstituteTeachers.Where(x => x.InstituteId == actor.Owner.Id).Select(x => x.TeacherId);
            return await Http.Page(db.Users.Where(u => ids.Contains(u.Id)).OrderBy(u => u.FirstName), page, pageSize, u => new { u.Id, u.FirstName, u.LastName, u.Phone, u.Status }, ct);
        });
        group.MapPost("/teachers", LinkOrCreateTeacher);
        MapShared(group);
        group.MapGet("/students", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.InstituteManager);
            actor.Require(AccessMap.Students);
            return await Http.Page(db.Users.Where(u => u.Type == UserType.Student && u.SchoolId != null && db.Schools.Any(s => s.Id == u.SchoolId && s.InstituteId == actor.Owner.Id)).OrderBy(u => u.FirstName), page, pageSize, u => new { u.Id, u.FirstName, u.LastName, u.Phone, u.StudentKind, u.SchoolId }, ct);
        });
        group.MapPost("/students", CreateStudent);
        group.MapGet("/employees", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.InstituteManager);
            actor.Require(AccessMap.Employees);
            return await Http.Page(db.Users.Where(u => u.EmployerUserId == actor.Owner.Id).OrderBy(u => u.FirstName), page, pageSize, u => new { u.Id, u.FirstName, u.LastName, u.Phone, u.EmployeeStyle, u.Status }, ct);
        });
        group.MapPost("/employees", CreateStaff);
        group.MapPost("/permission-groups", async (GroupBody body, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.InstituteManager);
            actor.Require(PermissionCatalog.Create("employees"));
            return Results.Ok(await People.CreateGroup(actor, body, db, ct));
        });
        group.MapGet("/schools", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.InstituteManager);
            actor.Require(PermissionCatalog.View("schools"));
            return await Http.Page(db.Schools.Where(s => s.InstituteId == actor.Owner.Id).OrderBy(s => s.Name), page, pageSize, s => s, ct);
        });
        group.MapPost("/schools", async (SchoolBody body, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.InstituteManager);
            actor.Require(PermissionCatalog.Create("schools"));
            var row = new School { Id = Guid.NewGuid(), InstituteId = actor.Owner.Id, Name = body.Name };
            db.Schools.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(row);
        });
        group.MapGet("/advertisements", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.InstituteManager).Require(AccessMap.Advertisements);
            return await Http.Page(db.Advertisements.Where(a => a.Active).OrderByDescending(a => a.CreatedAt), page, pageSize, a => a, ct);
        });
        group.MapGet("/faq", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.InstituteManager).Require(PermissionCatalog.View("faq"));
            return Results.Ok(await db.Faqs.OrderBy(f => f.SortOrder).ToListAsync(ct));
        });
        group.MapGet("/pool", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.InstituteManager);
            actor.Require(PermissionCatalog.View("hour-pool"));
            var pool = await db.HourPools.FirstAsync(p => p.InstituteId == actor.Owner.Id, ct);
            return Results.Ok(new { pool.RemainingMinutes, hours = HourRules.DisplayHours(pool.RemainingMinutes) });
        });
        group.MapGet("/settings", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.InstituteManager);
            return Results.Ok(await db.Users.Where(u => u.Id == actor.Owner.Id).Select(u => new { u.Id, u.InstituteName, u.FirstName, u.Phone, u.Phone2, u.Address, u.Description, u.LogoFileId }).FirstAsync(ct));
        });
    }

    static void MapShared(RouteGroupBuilder group)
    {
        group.MapGet("/courses", ListCourses);
        group.MapPost("/courses", CreateCourse);
        group.MapGet("/courses/{id:guid}/chapters", async (Guid id, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            await OwnCourse(http, db, id, ct);
            return Results.Ok(await db.Chapters.Where(c => c.CourseId == id).OrderBy(c => c.SortOrder).ToListAsync(ct));
        });
        group.MapPost("/chapters", CreateChapter);
        group.MapGet("/chapters/{id:guid}/lessons", async (Guid id, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var chapter = await db.Chapters.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new RuleException("not_found", "Chapter was not found.", 404);
            await OwnCourse(http, db, chapter.CourseId, ct);
            return Results.Ok(await db.Lessons.Where(l => l.ChapterId == id).OrderBy(l => l.SortOrder).ToListAsync(ct));
        });
        group.MapPost("/lessons", CreateLesson);
        group.MapPut("/chapters/{id:guid}/lessons/order", Reorder);
        group.MapPost("/courses/{id:guid}/format", FormatCourse);
        group.MapGet("/summaries", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var actor = Portal(http);
            actor.Require(PermissionCatalog.View("summaries"));
            var teacherIds = await TeacherIds(db, actor, ct);
            return await Http.Page(db.Summaries.Where(s => teacherIds.Contains(s.TeacherId)).OrderByDescending(s => s.Title), page, pageSize, s => s, ct);
        });
        group.MapPost("/summaries", CreateSummary);
        group.MapPost("/summaries/{id:guid}/format", FormatSummary);
        group.MapGet("/exams", ListExams);
        group.MapPost("/exams", CreateExam);
        group.MapGet("/exams/{id:guid}/questions", async (Guid id, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Portal(http);
            actor.Require(AccessMap.Questions);
            await OwnExam(actor, db, id, ct);
            var questions = await db.Questions.Where(q => q.ExamId == id).OrderBy(q => q.SortOrder).ToListAsync(ct);
            var questionIds = questions.Select(q => q.Id).ToList();
            var options = await db.QuestionOptions.Where(o => questionIds.Contains(o.QuestionId)).ToListAsync(ct);
            return Results.Ok(questions.Select(q => new { q.Id, q.Type, q.Text, q.Mark, q.CorrectBool, options = options.Where(o => o.QuestionId == q.Id) }));
        });
        group.MapPost("/exams/{id:guid}/questions", CreateQuestion);
        group.MapDelete("/questions/{id:guid}", DeleteQuestion);
        group.MapPost("/answers/{id:guid}/correction", async (Guid id, AnswerMarkBody body, HttpContext http, LearningWork learning, CancellationToken ct) =>
        {
            var actor = Portal(http);
            await learning.CorrectAsync(actor, id, body.Mark, ct);
            return Results.Ok(new { corrected = true });
        });
        group.MapGet("/groups", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Portal(http);
            actor.Require(PermissionCatalog.View("groups"));
            if (actor.Portal != UserType.Teacher) throw new RuleException("forbidden", "Groups belong to a teacher.", 403);
            return Results.Ok(await db.Groups.Where(g => g.TeacherId == actor.Owner.Id).ToListAsync(ct));
        });
        group.MapPost("/groups", async (GroupNameBody body, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.Teacher);
            actor.Require(PermissionCatalog.Create("groups"));
            var row = new StudentGroup { Id = Guid.NewGuid(), TeacherId = actor.Owner.Id, Name = body.Name };
            db.Groups.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(row);
        });
        group.MapPost("/groups/{id:guid}/members", async (Guid id, MemberBody body, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.Teacher);
            actor.Require(PermissionCatalog.Update("groups"));
            var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id && g.TeacherId == actor.Owner.Id, ct) ?? throw new RuleException("not_found", "Group was not found.", 404);
            if (!await db.Users.AnyAsync(u => u.Id == body.StudentId && u.Type == UserType.Student, ct))
                throw new RuleException("not_found", "Student was not found.", 404);
            db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, StudentId = body.StudentId });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { added = true });
        });
        group.MapPost("/codes", Generate);
        group.MapGet("/codes/balance", async (HttpContext http, CodeWork codes, CancellationToken ct) =>
        {
            var actor = Portal(http);
            actor.Require(AccessMap.Codes);
            return Results.Ok(new { remaining = await codes.BalanceAsync(actor.Owner.Id, ct) });
        });
        group.MapPost("/codes/{id:guid}/cancel", async (Guid id, HttpContext http, CodeWork codes, CancellationToken ct) =>
        {
            var actor = Portal(http);
            actor.Require(PermissionCatalog.Delete("codes"));
            await codes.CancelAsync(actor, id, ct);
            return Results.Ok(new { canceled = true });
        });
        group.MapGet("/courses/{id:guid}/codes/export", (Guid id, HttpContext http, CodeWork codes, CancellationToken ct) => Export(http, codes, AccessTarget.Course, id, ct));
        group.MapGet("/exams/{id:guid}/codes/export", (Guid id, HttpContext http, CodeWork codes, CancellationToken ct) => Export(http, codes, AccessTarget.Exam, id, ct));
        group.MapGet("/classrooms", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Portal(http);
            actor.Require(PermissionCatalog.View("classrooms"));
            var ids = await TeacherIds(db, actor, ct);
            return Results.Ok(await db.Classrooms.Where(c => ids.Contains(c.TeacherId)).ToListAsync(ct));
        });
        group.MapPost("/classrooms", OpenClassroom);
        group.MapGet("/sessions", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Portal(http);
            actor.Require(PermissionCatalog.View("sessions"));
            var query = actor.Portal == UserType.InstituteManager
                ? db.SessionsSchedule.Where(s => s.InstituteId == actor.Owner.Id || db.InstituteTeachers.Any(t => t.InstituteId == actor.Owner.Id && t.TeacherId == s.TeacherId))
                : db.SessionsSchedule.Where(s => s.TeacherId == actor.Owner.Id);
            return Results.Ok(await query.OrderBy(s => s.StartsAt).ToListAsync(ct));
        });
        group.MapPost("/sessions", ScheduleTeacher);
        group.MapPost("/sessions/{id:guid}/cancel", CancelSession);
        group.MapGet("/plans", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Portal(http);
            actor.Require(PermissionCatalog.View("plans"));
            var audience = actor.Portal == UserType.InstituteManager ? PlanAudience.ForInstitute : PlanAudience.ForTeacher;
            return Results.Ok(await db.Plans.Where(p => p.Audience == audience).OrderBy(p => p.PriceYearly).ToListAsync(ct));
        });
        group.MapPost("/subscriptions", SelfServe);
        group.MapGet("/subscriptions", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            var actor = Portal(http);
            actor.Require(PermissionCatalog.View("subscriptions"));
            return Results.Ok(await db.Subscriptions.Where(s => s.UserId == actor.Owner.Id).OrderByDescending(s => s.CreatedAt).ToListAsync(ct));
        });
        group.MapGet("/reports/codes", ReportCodes);
        MeetingApi.Map(group);
    }

    static async Task<IResult> Home(HttpContext http, ToviDb db, CodeWork codes, IClock clock, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Teacher);
        var balance = await codes.BalanceAsync(actor.Owner.Id, ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad");
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc), zone);
        var monthStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(new DateTime(local.Year, local.Month, 1), DateTimeKind.Unspecified), zone);
        var monthly = await db.Codes.Where(c => c.OwnerUserId == actor.Owner.Id && c.RedeemedAt >= monthStart && c.RedeemedByStudentId != null)
            .Select(c => c.RedeemedByStudentId).Distinct().CountAsync(ct);
        var minutes = await db.HourSubscriptions.Where(s => s.TeacherId == actor.Owner.Id && s.Active && s.EndsAt > clock.UtcNow).Select(s => (int?)s.RemainingMinutes).FirstOrDefaultAsync(ct) ?? 0;
        var ends = await db.Subscriptions.Where(s => s.UserId == actor.Owner.Id && s.Status == SubscriptionStatus.Active).OrderByDescending(s => s.EndsAt).Select(s => s.EndsAt).FirstOrDefaultAsync(ct);
        return Results.Ok(new { codeBalance = balance, monthlyStudents = monthly, hourHours = HourRules.DisplayHours(minutes), subscriptionEndsAt = ends });
    }

    static async Task<IResult> Dashboard(HttpContext http, ToviDb db, CodeWork codes, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.InstituteManager);
        var teachers = await db.InstituteTeachers.CountAsync(t => t.InstituteId == actor.Owner.Id, ct);
        var courses = await db.Courses.CountAsync(c => c.InstituteId == actor.Owner.Id, ct);
        var students = await db.Users.CountAsync(u => u.Type == UserType.Student && db.Schools.Any(s => s.Id == u.SchoolId && s.InstituteId == actor.Owner.Id), ct);
        var pool = await db.HourPools.Where(p => p.InstituteId == actor.Owner.Id).Select(p => p.RemainingMinutes).FirstAsync(ct);
        return Results.Ok(new { teachers, courses, students, codeBalance = await codes.BalanceAsync(actor.Owner.Id, ct), poolMinutes = pool, poolHours = HourRules.DisplayHours(pool) });
    }

    static async Task<IResult> ListCourses(HttpContext http, ToviDb db, Guid? teacherId, int? page, int? pageSize, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.View("courses"));
        var ids = await TeacherIds(db, actor, ct);
        if (teacherId is not null)
        {
            if (!ids.Contains(teacherId.Value)) throw new RuleException("forbidden", "That teacher is outside this account.", 403);
            ids = [teacherId.Value];
        }
        return await Http.Page(db.Courses.Where(c => ids.Contains(c.TeacherId)).OrderByDescending(c => c.CreatedAt), page, pageSize, c => c, ct);
    }

    static async Task<IResult> CreateCourse(CourseBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.Create("courses"));
        await Guards.EnsureTrialAsync(db, actor, clock, ct);
        Guid teacherId;
        Guid? instituteId = null;
        if (actor.Portal == UserType.Teacher)
        {
            if (body.TeacherId is not null && body.TeacherId != actor.Owner.Id)
                throw new RuleException("forbidden", "A teacher cannot create a course for another teacher.", 403);
            teacherId = actor.Owner.Id;
        }
        else
        {
            teacherId = body.TeacherId ?? throw new RuleException("teacher_required", "Choose the teacher for this course.");
            var linked = await db.InstituteTeachers.AnyAsync(x => x.InstituteId == actor.Owner.Id && x.TeacherId == teacherId, ct);
            if (!linked) throw new RuleException("forbidden", "That teacher is not in this institute.", 403);
            instituteId = actor.Owner.Id;
        }

        var course = new Course
        {
            Id = Guid.NewGuid(),
            TeacherId = teacherId,
            InstituteId = instituteId,
            Title = body.Title,
            Description = body.Description,
            LearningDomainId = body.LearningDomainId,
            SectionId = body.SectionId,
            GradeId = body.GradeId,
            SubjectId = body.SubjectId,
            CreatedAt = clock.UtcNow
        };
        db.Courses.Add(course);
        await db.SaveChangesAsync(ct);
        return Results.Ok(course);
    }

    static async Task<IResult> CreateChapter(ChapterBody body, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.Create("chapters"));
        await OwnCourse(http, db, body.CourseId, ct);
        var order = await db.Chapters.Where(c => c.CourseId == body.CourseId).CountAsync(ct);
        var row = new Chapter { Id = Guid.NewGuid(), CourseId = body.CourseId, Title = body.Title, SortOrder = order };
        db.Chapters.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Ok(row);
    }

    static async Task<IResult> CreateLesson(LessonBody body, HttpContext http, ToviDb db, CancellationToken ct)
    {
        Portal(http).Require(PermissionCatalog.Create("lessons"));
        var chapter = await db.Chapters.FirstOrDefaultAsync(c => c.Id == body.ChapterId, ct) ?? throw new RuleException("not_found", "Chapter was not found.", 404);
        await OwnCourse(http, db, chapter.CourseId, ct);
        var order = await db.Lessons.Where(l => l.ChapterId == body.ChapterId).CountAsync(ct);
        var row = new Lesson { Id = Guid.NewGuid(), ChapterId = body.ChapterId, Title = body.Title, SortOrder = order, VideoReference = body.VideoReference };
        db.Lessons.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Ok(row);
    }

    static async Task<IResult> Reorder(Guid id, OrderBody body, HttpContext http, ToviDb db, LearningWork learning, CancellationToken ct)
    {
        Portal(http).Require(PermissionCatalog.Update("lessons"));
        var chapter = await db.Chapters.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new RuleException("not_found", "Chapter was not found.", 404);
        await OwnCourse(http, db, chapter.CourseId, ct);
        await learning.ReorderLessonsAsync(id, body.LessonIds, ct);
        return Results.Ok(new { reordered = true });
    }

    static async Task<IResult> FormatCourse(Guid id, HttpContext http, ToviDb db, LearningWork learning, CancellationToken ct)
    {
        Portal(http).Require(PermissionCatalog.FormatCourse);
        await OwnCourse(http, db, id, ct);
        await learning.FormatCourseAsync(id, ct);
        return Results.Ok(new { formatted = true });
    }

    static async Task<IResult> CreateSummary(SummaryBody body, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.Create("summaries"));
        if (actor.Portal != UserType.Teacher) throw new RuleException("forbidden", "A summary belongs to a teacher.", 403);
        var row = new Summary { Id = Guid.NewGuid(), TeacherId = actor.Owner.Id, CourseId = body.CourseId, Title = body.Title, Body = body.Body };
        db.Summaries.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Ok(row);
    }

    static async Task<IResult> FormatSummary(Guid id, HttpContext http, ToviDb db, LearningWork learning, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.FormatSummary);
        var summary = await db.Summaries.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new RuleException("not_found", "Summary was not found.", 404);
        if (actor.Portal == UserType.Teacher && summary.TeacherId != actor.Owner.Id)
            throw new RuleException("forbidden", "This summary belongs to another teacher.", 403);
        await learning.FormatSummaryAsync(id, ct);
        return Results.Ok(new { formatted = true });
    }

    static async Task<IResult> ListExams(HttpContext http, ToviDb db, Guid? courseId, Guid? chapterId, Guid? lessonId, int? page, int? pageSize, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(AccessMap.Exams);
        var ids = await TeacherIds(db, actor, ct);
        var query = db.Exams.Where(e => ids.Contains(e.TeacherId));
        if (courseId is not null) query = query.Where(e => e.CourseId == courseId);
        if (chapterId is not null) query = query.Where(e => e.ChapterId == chapterId);
        if (lessonId is not null) query = query.Where(e => e.LessonId == lessonId);
        return await Http.Page(query.OrderByDescending(e => e.CreatedAt), page, pageSize, e => e, ct);
    }

    static async Task<IResult> CreateExam(ExamBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.Create("exams"));
        RuleException.Require(ExamRules.Mark(body.Mark, "Exam"));
        var teacherId = actor.Owner.Id;
        if (actor.Portal == UserType.InstituteManager)
        {
            if (body.CourseId is null) throw new RuleException("course_required", "Choose a course for this exam.");
            var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == body.CourseId && c.InstituteId == actor.Owner.Id, ct)
                ?? throw new RuleException("forbidden", "That course is outside this institute.", 403);
            teacherId = course.TeacherId;
        }
        var exam = new Exam
        {
            Id = Guid.NewGuid(),
            TeacherId = teacherId,
            CourseId = body.CourseId,
            ChapterId = body.ChapterId,
            LessonId = body.LessonId,
            Title = body.Title,
            Mark = body.Mark,
            EndDate = body.EndDate,
            CreatedAt = clock.UtcNow
        };
        db.Exams.Add(exam);
        if (body.GroupIds is not null)
        {
            foreach (var groupId in body.GroupIds.Distinct())
            {
                var owned = await db.Groups.AnyAsync(g => g.Id == groupId && g.TeacherId == teacherId, ct);
                if (!owned) throw new RuleException("forbidden", "A selected group belongs to another teacher.", 403);
                db.ExamGroups.Add(new ExamGroup { Id = Guid.NewGuid(), ExamId = exam.Id, GroupId = groupId });
            }
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(exam);
    }

    static async Task<IResult> CreateQuestion(Guid id, QuestionBody body, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Teacher);
        actor.Require(PermissionCatalog.Create("questions"));
        RuleException.Require(ExamRules.Mark(body.Mark, "Question"));
        await OwnExam(actor, db, id, ct);
        var question = new Question
        {
            Id = Guid.NewGuid(),
            ExamId = id,
            Type = body.Type,
            Text = body.Text,
            Mark = body.Mark,
            CorrectBool = body.Correct,
            SortOrder = await db.Questions.CountAsync(q => q.ExamId == id, ct)
        };
        db.Questions.Add(question);
        if (body.Type == QuestionType.MultipleChoice)
        {
            foreach (var option in body.Options ?? [])
                db.QuestionOptions.Add(new QuestionOption { Id = Guid.NewGuid(), QuestionId = question.Id, Text = option.Text, IsCorrect = option.Correct });
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(question);
    }

    static async Task<IResult> DeleteQuestion(Guid id, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(AccessMap.DeleteQuestions);
        var question = await db.Questions.FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw new RuleException("not_found", "Question was not found.", 404);
        await OwnExam(actor, db, question.ExamId, ct);
        db.Questions.Remove(question);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { deleted = true });
    }

    static async Task<IResult> Generate(HttpContext http, ToviDb db, CodeWork codes, CodeCountBody? body, Guid? courseId, Guid? summaryId, Guid? examId, Guid? classroomId, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.Create("codes"));
        var (target, id) = Target(courseId, summaryId, examId, classroomId);
        var created = await codes.GenerateAsync(actor, target, id, body?.Count ?? 1, ct);
        return Results.Ok(new { remaining = await codes.BalanceAsync(actor.Owner.Id, ct), codes = created.Select(c => new { c.Id, c.Code, c.Status, c.ExpiresAt, c.SubscriptionId }) });
    }

    static async Task<IResult> Export(HttpContext http, CodeWork codes, AccessTarget target, Guid id, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(AccessMap.Codes);
        var bytes = await codes.ExportAsync(actor, target, id, ct);
        return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "tovi-codes.xlsx");
    }

    static async Task<IResult> OpenClassroom(ClassroomBody body, HttpContext http, HourWork hours, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Teacher);
        actor.Require(PermissionCatalog.Create("classrooms"));
        return Results.Ok(await hours.OpenClassroomAsync(actor.Owner.Id, body.Title, ct));
    }

    static async Task<IResult> ScheduleTeacher(SessionBody body, HttpContext http, HourWork hours, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.Create("sessions"));
        if (actor.Portal == UserType.InstituteManager)
            return Results.Ok(await hours.ScheduleInstituteAsync(actor.Owner.Id, body.ClassroomId, body.StartsAt, body.DurationMinutes, ct));
        return Results.Ok(await hours.ScheduleTeacherAsync(actor.Owner.Id, body.ClassroomId, body.StartsAt, body.DurationMinutes, ct));
    }

    static async Task<IResult> ScheduleInstitute(SessionBody body, HttpContext http, HourWork hours, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.InstituteManager);
        actor.Require(PermissionCatalog.Create("sessions"));
        return Results.Ok(await hours.ScheduleInstituteAsync(actor.Owner.Id, body.ClassroomId, body.StartsAt, body.DurationMinutes, ct));
    }

    static async Task<IResult> CancelSession(Guid id, HttpContext http, HourWork hours, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.Delete("sessions"));
        if (actor.Portal == UserType.InstituteManager)
            await hours.CancelAsync(id, null, actor.Owner.Id, ct);
        else
            await hours.CancelAsync(id, actor.Owner.Id, null, ct);
        return Results.Ok(new { canceled = true });
    }

    static async Task<IResult> SelfServe(AssignBody body, HttpContext http, CommerceWork commerce, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.Create("subscriptions"));
        if (body.UserId != actor.Owner.Id)
            throw new RuleException("forbidden", "You can only buy a plan for your own account.", 403);
        var (subscription, payment, url) = await commerce.AssignAsync(actor.Owner.Id, body.PlanId, false, ct);
        return Results.Ok(new { subscription.Id, subscription.Status, paymentId = payment.Id, payment.AmountIqd, paymentUrl = url });
    }

    static async Task<IResult> CreateStaff(EmployeeBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.Create("employees"));
        var created = await People.CreateEmployee(actor, body, db, clock, ct);
        return Results.Ok(new { created.Id, created.FirstName, created.LastName, created.Phone, created.EmployeeStyle, created.Status });
    }

    static async Task<IResult> ListStaff(HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(AccessMap.Employees);
        return await Http.Page(db.Users.Where(u => u.EmployerUserId == actor.Owner.Id).OrderBy(u => u.FirstName), page, pageSize, u => new { u.Id, u.FirstName, u.LastName, u.Phone, u.EmployeeStyle, u.Status }, ct);
    }

    static async Task<IResult> LinkOrCreateTeacher(TeacherBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.InstituteManager);
        actor.Require(PermissionCatalog.Create("teachers"));
        RuleException.Require(AccountRules.Phone(body.Phone));
        RuleException.Require(AccountRules.Password(body.Password));
        var phone = Http.Digits(body.Phone);
        if (await db.Users.AnyAsync(u => u.Phone == phone, ct))
            throw new RuleException("phone_taken", "This phone is already registered.", 409);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Type = UserType.Teacher,
            Phone = phone,
            FirstName = body.FirstName,
            LastName = body.LastName,
            SectionId = body.SectionId,
            CreatedAt = clock.UtcNow
        };
        user.PasswordHash = Passwords.Hash(user, body.Password!);
        db.Users.Add(user);
        db.InstituteTeachers.Add(new InstituteTeacher { Id = Guid.NewGuid(), InstituteId = actor.Owner.Id, TeacherId = user.Id });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { user.Id, user.FirstName, user.Phone });
    }

    static async Task<IResult> CreateStudent(StudentBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.InstituteManager);
        actor.Require(PermissionCatalog.Create("students"));
        RuleException.Require(AccountRules.Phone(body.Phone));
        RuleException.Require(AccountRules.Password(body.Password));
        if (body.SchoolId is not null && !await db.Schools.AnyAsync(s => s.Id == body.SchoolId && s.InstituteId == actor.Owner.Id, ct))
            throw new RuleException("forbidden", "That school is outside this institute.", 403);
        var phone = Http.Digits(body.Phone);
        if (await db.Users.AnyAsync(u => u.Phone == phone, ct))
            throw new RuleException("phone_taken", "This phone is already registered.", 409);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Type = UserType.Student,
            Phone = phone,
            FirstName = body.FirstName,
            LastName = body.LastName,
            StudentKind = body.Kind,
            GradeId = body.GradeId,
            SectionId = body.SectionId,
            SchoolId = body.SchoolId,
            CreatedAt = clock.UtcNow
        };
        user.PasswordHash = Passwords.Hash(user, body.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { user.Id, user.FirstName, user.StudentKind });
    }

    static async Task<IResult> UpdateProfile(ProfileBody body, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Portal(http);
        var user = await db.Users.FirstAsync(u => u.Id == actor.User.Id, ct);
        user.FirstName = body.FirstName;
        user.LastName = body.LastName;
        user.Address = body.Address;
        user.Description = body.Description;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { updated = true });
    }

    static async Task<IResult> ReportCodes(HttpContext http, ToviDb db, int? page, int? pageSize, CodeStatus? status, CancellationToken ct)
    {
        var actor = Portal(http);
        actor.Require(PermissionCatalog.View("reports"));
        var query = db.Codes.Where(c => c.OwnerUserId == actor.Owner.Id);
        if (status is not null) query = query.Where(c => c.Status == status);
        return await Http.Page(query.OrderByDescending(c => c.CreatedAt), page, pageSize, c => c, ct);
    }

    static Actor Portal(HttpContext http) => Http.Me(http, UserType.Teacher, UserType.InstituteManager);

    static async Task<List<Guid>> TeacherIds(ToviDb db, Actor actor, CancellationToken ct)
    {
        if (actor.Portal == UserType.Teacher) return [actor.Owner.Id];
        return await db.InstituteTeachers.Where(x => x.InstituteId == actor.Owner.Id).Select(x => x.TeacherId).ToListAsync(ct);
    }

    static async Task OwnCourse(HttpContext http, ToviDb db, Guid courseId, CancellationToken ct)
    {
        var actor = Portal(http);
        var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == courseId, ct) ?? throw new RuleException("not_found", "Course was not found.", 404);
        var ids = await TeacherIds(db, actor, ct);
        if (!ids.Contains(course.TeacherId))
            throw new RuleException("forbidden", "This course is outside your account.", 403);
    }

    static async Task OwnExam(Actor actor, ToviDb db, Guid examId, CancellationToken ct)
    {
        var exam = await db.Exams.FirstOrDefaultAsync(e => e.Id == examId, ct) ?? throw new RuleException("not_found", "Exam was not found.", 404);
        var ids = await TeacherIds(db, actor, ct);
        if (!ids.Contains(exam.TeacherId))
            throw new RuleException("forbidden", "This exam is outside your account.", 403);
    }

    static (AccessTarget Target, Guid Id) Target(Guid? courseId, Guid? summaryId, Guid? examId, Guid? classroomId)
    {
        var set = new[] { courseId, summaryId, examId, classroomId }.Count(x => x is not null);
        if (set != 1) throw new RuleException("target_required", "Bind the codes to exactly one course, summary, exam, or classroom.");
        if (courseId is not null) return (AccessTarget.Course, courseId.Value);
        if (summaryId is not null) return (AccessTarget.Summary, summaryId.Value);
        if (examId is not null) return (AccessTarget.Exam, examId.Value);
        return (AccessTarget.VirtualClassroom, classroomId!.Value);
    }
}
