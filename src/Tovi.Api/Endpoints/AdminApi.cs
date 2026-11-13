using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Api.Services;
using Tovi.Domain;

namespace Tovi.Api.Endpoints;

public record NameBody(string Name);
public record TeacherBody(string FirstName, string LastName, string Phone, string? Password, Guid? SectionId, bool Approved, DateTime? TrialEndsAt, int? ActivationCodeLimit, int? ExportCodesLimit);
public record PasswordBody(string Password);
public record EmployeeBody(string FirstName, string LastName, string Phone, string Password, EmployeeStyle Style, Guid? GroupId, List<string>? Permissions);
public record GroupBody(string Name, List<string> Permissions);
public record PlanBody(string Title, string Description, PlanAudience Audience, long PriceYearly, int CodesCount);
public record AssignBody(Guid UserId, Guid PlanId);
public record InvoiceBody(Guid PartyUserId, long AmountIqd, int ActivatedCodesCount, string? Note);
public record AdBody(string Title, string Body, string? LinkUrl, bool Active);
public record NoticeBody(string Title, string Body, NotificationType Type, NotificationAudience Audience, Guid? AudienceRefId, Gender? Gender, DateTime? NextRunAt, RepeatInterval? Repeat);
public record FaqBody(string Question, string Answer, int SortOrder);
public record GuideBody(string Title, string Body);
public record ParameterBody(string Key, string Value);
public record HourPackageBody(string Name, int MonthlyHoursLimit, int ValidityDays, long PriceIqd, ReportLevel ReportLevel, bool AttendanceEnabled, bool ExportEnabled, bool IsActive, DateTime? CodeExpiresAt);
public record PoolBody(int Minutes, string Note);

public static class AdminApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin");
        MapNamed(admin, "/learning-domains", db => db.LearningDomains, (id, name) => new LearningDomain { Id = id, Name = name }, "learning-domains");
        MapNamed(admin, "/sections", db => db.Sections, (id, name) => new Section { Id = id, Name = name }, "sections");
        MapNamed(admin, "/grades", db => db.Grades, (id, name) => new EducationalGrade { Id = id, Name = name }, "grades");
        MapNamed(admin, "/subjects", db => db.Subjects, (id, name) => new Subject { Id = id, Name = name }, "subjects");
        MapNamed(admin, "/governorates", db => db.Governorates, (id, name) => new Governorate { Id = id, Name = name }, "governorates");

        admin.MapGet("/teachers", async (HttpContext http, ToviDb db, int? page, int? pageSize, string? q, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.Admin);
            actor.Require(AccessMap.Teachers);
            var query = db.Users.Where(u => u.Type == UserType.Teacher);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(u => u.FirstName.Contains(q) || u.Phone.Contains(q));
            var rule = PageRules.Resolve(page, pageSize, out var resolvedPage, out var resolvedSize);
            RuleException.Require(rule);
            var ordered = query.OrderByDescending(u => u.CreatedAt);
            var total = await ordered.CountAsync(ct);
            var users = await ordered.Skip((resolvedPage - 1) * resolvedSize).Take(resolvedSize).ToListAsync(ct);
            var ids = users.Select(u => u.Id).ToList();
            var subs = await db.Subscriptions.Where(s => ids.Contains(s.UserId) && s.Status == SubscriptionStatus.Active).ToListAsync(ct);
            var items = users.Select(user => new
            {
                user.Id,
                user.FirstName,
                user.LastName,
                user.Phone,
                user.Status,
                user.TeacherApproved,
                user.SectionId,
                user.TrialEndsAt,
                user.ActivationCodeLimit,
                user.ExportCodesLimit,
                user.CreatedAt,
                subscriptionEndsAt = subs.Where(s => s.UserId == user.Id).OrderByDescending(s => s.EndsAt).Select(s => s.EndsAt).FirstOrDefault()
            });
            return Results.Ok(new { items, page = resolvedPage, pageSize = resolvedSize, total });
        });
        admin.MapPost("/teachers", CreateTeacher);
        admin.MapPut("/teachers/{id:guid}", UpdateTeacher);
        admin.MapPost("/teachers/{id:guid}/disable", (Guid id, HttpContext http, ToviDb db, IClock clock, CancellationToken ct) => Disable(id, http, db, clock, AccessMap.Teachers, ct));
        admin.MapPost("/teachers/{id:guid}/password", ResetUserPassword);

        admin.MapGet("/institutes", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.Admin);
            actor.Require(AccessMap.Institutes);
            var query = db.Users.Where(u => u.Type == UserType.InstituteManager).OrderByDescending(u => u.CreatedAt);
            return await Http.Page(query, page, pageSize, user => new { user.Id, user.InstituteName, user.FirstName, user.Phone, user.Phone2, user.Status, user.LogoFileId, user.CreatedAt }, ct);
        });
        admin.MapPost("/institutes", CreateInstitute);
        admin.MapPut("/institutes/{id:guid}", UpdateInstitute);
        admin.MapPost("/institutes/{id:guid}/pool", CreditPool);

        admin.MapGet("/students", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(AccessMap.Students);
            var progress = http.RequestServices.GetRequiredService<ProgressWork>();
            return await Http.PageWith(db.Users.Where(u => u.Type == UserType.Student).OrderByDescending(u => u.CreatedAt), page, pageSize, async rows =>
            {
                var totals = await progress.TotalsAsync(rows.Select(r => r.Id).ToList(), ct);
                return rows.Select(u => (object)new { u.Id, u.FirstName, u.LastName, u.Phone, u.Gender, u.StudentKind, u.GradeId, u.SectionId, u.Status, xp = totals.GetValueOrDefault(u.Id).Xp, streak = totals.GetValueOrDefault(u.Id).Streak }).ToList();
            }, ct);
        });

        admin.MapGet("/employees", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var actor = Http.Me(http, UserType.Admin);
            actor.Require(AccessMap.Employees);
            var owner = actor.Owner.Id;
            return await Http.Page(db.Users.Where(u => u.Type == UserType.Employee && u.EmployerUserId == owner).OrderByDescending(u => u.CreatedAt), page, pageSize, u => new { u.Id, u.FirstName, u.LastName, u.Phone, u.EmployeeStyle, u.Status }, ct);
        });
        admin.MapPost("/employees", CreateEmployee);
        admin.MapPost("/permission-groups", CreateGroup);

        admin.MapGet("/plans", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.View("plans"));
            return await Http.Page(db.Plans.OrderBy(p => p.Title), page, pageSize, p => p, ct);
        });
        admin.MapPost("/plans", CreatePlan);
        admin.MapPost("/subscriptions", Assign);
        admin.MapGet("/subscriptions", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.View("subscriptions"));
            return await Http.Page(db.Subscriptions.OrderByDescending(s => s.CreatedAt), page, pageSize, s => s, ct);
        });

        admin.MapGet("/invoices", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(AccessMap.Invoices);
            return await Http.Page(db.Invoices.OrderByDescending(i => i.CreatedAt), page, pageSize, i => i, ct);
        });
        admin.MapPost("/invoices", CreateInvoice);

        admin.MapGet("/advertisements", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(AccessMap.Advertisements);
            return await Http.Page(db.Advertisements.OrderByDescending(a => a.CreatedAt), page, pageSize, a => a, ct);
        });
        admin.MapPost("/advertisements", async (AdBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create("advertisements"));
            var row = new Advertisement { Id = Guid.NewGuid(), Title = body.Title, Body = body.Body, LinkUrl = body.LinkUrl, Active = body.Active, CreatedAt = clock.UtcNow };
            db.Advertisements.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(row);
        });

        admin.MapGet("/guides", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(AccessMap.Guides);
            return await Http.Page(db.Guides.OrderBy(g => g.Title), page, pageSize, g => g, ct);
        });
        admin.MapPost("/guides", async (GuideBody body, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create("guides"));
            var row = new Guide { Id = Guid.NewGuid(), Title = body.Title, Body = body.Body };
            db.Guides.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(row);
        });
        admin.MapGet("/faq", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.View("faq"));
            return Results.Ok(await db.Faqs.OrderBy(f => f.SortOrder).ToListAsync(ct));
        });
        admin.MapPost("/faq", async (FaqBody body, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create("faq"));
            var row = new FaqItem { Id = Guid.NewGuid(), Question = body.Question, Answer = body.Answer, SortOrder = body.SortOrder };
            db.Faqs.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(row);
        });

        admin.MapPost("/notifications", async (NoticeBody body, HttpContext http, NotifyWork work, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create("notifications"));
            var notice = new ScheduledNotification { Id = Guid.NewGuid(), Title = body.Title, Body = body.Body, Type = body.Type, Audience = body.Audience, AudienceRefId = body.AudienceRefId, Gender = body.Gender, NextRunAt = DateTime.UtcNow, Enabled = false };
            var push = await work.SendAsync(notice, ct);
            return Results.Ok(new { stored = true, pushDelivered = push.Delivered, push = push.Reason });
        });
        admin.MapPost("/scheduled-notifications", async (NoticeBody body, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create("notifications"));
            if (body.NextRunAt is null) throw new RuleException("schedule_required", "A scheduled notification needs a next run time.");
            var row = new ScheduledNotification
            {
                Id = Guid.NewGuid(),
                Title = body.Title,
                Body = body.Body,
                Type = body.Type,
                Audience = body.Audience,
                AudienceRefId = body.AudienceRefId,
                Gender = body.Gender,
                NextRunAt = body.NextRunAt.Value,
                Repeat = body.Repeat,
                Enabled = true
            };
            db.ScheduledNotifications.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(row);
        });
        admin.MapGet("/scheduled-notifications", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.View("notifications"));
            return await Http.Page(db.ScheduledNotifications.OrderBy(n => n.NextRunAt), page, pageSize, n => n, ct);
        });

        admin.MapGet("/parameters", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.View("parameters"));
            return Results.Ok(await db.Parameters.OrderBy(p => p.Key).ToListAsync(ct));
        });
        admin.MapPut("/parameters", async (ParameterBody body, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.Update("parameters"));
            var row = await db.Parameters.FindAsync([body.Key], ct);
            if (row is null) db.Parameters.Add(new SystemParameter { Key = body.Key, Value = body.Value });
            else row.Value = body.Value;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { body.Key, body.Value });
        });
        admin.MapPost("/privacy", UploadPrivacy);

        admin.MapGet("/reports/codes", async (HttpContext http, ToviDb db, int? page, int? pageSize, CodeStatus? status, AccessTarget? target, Guid? ownerId, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.View("reports"));
            var query = db.Codes.AsQueryable();
            if (status is not null) query = query.Where(c => c.Status == status);
            if (target is not null) query = query.Where(c => c.TargetType == target);
            if (ownerId is not null) query = query.Where(c => c.OwnerUserId == ownerId);
            return await Http.Page(query.OrderByDescending(c => c.CreatedAt), page, pageSize, c => c, ct);
        });

        admin.MapGet("/hour-packages", async (HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.View("hour-packages"));
            return Results.Ok(await db.HourPackages.OrderBy(p => p.Name).ToListAsync(ct));
        });
        admin.MapPost("/hour-packages", CreateHourPackage);

        admin.MapGet("/codes", async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(AccessMap.Codes);
            return await Http.Page(db.Codes.OrderByDescending(c => c.CreatedAt), page, pageSize, c => c, ct);
        });
    }

    static void MapNamed<T>(RouteGroupBuilder admin, string route, Func<ToviDb, DbSet<T>> set, Func<Guid, string, T> create, string module) where T : class, INamed
    {
        admin.MapGet(route, async (HttpContext http, ToviDb db, int? page, int? pageSize, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.View(module));
            return await Http.Page(set(db).AsQueryable(), page, pageSize, row => new { row.Id, row.Name }, ct);
        });
        admin.MapPost(route, async (NameBody body, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create(module));
            var row = create(Guid.NewGuid(), body.Name);
            set(db).Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { row.Id, row.Name });
        });
        admin.MapPut(route + "/{itemId:guid}", async (Guid itemId, NameBody body, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.Update(module));
            var row = await set(db).FirstOrDefaultAsync(x => x.Id == itemId, ct) ?? throw new RuleException("not_found", "Item was not found.", 404);
            row.Name = body.Name;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { row.Id, row.Name });
        });
        admin.MapDelete(route + "/{itemId:guid}", async (Guid itemId, HttpContext http, ToviDb db, CancellationToken ct) =>
        {
            Http.Me(http, UserType.Admin).Require(PermissionCatalog.Delete(module));
            var row = await set(db).FirstOrDefaultAsync(x => x.Id == itemId, ct) ?? throw new RuleException("not_found", "Item was not found.", 404);
            set(db).Remove(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { deleted = true });
        });
    }

    static async Task<IResult> CreateTeacher(TeacherBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create("teachers"));
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
            TeacherApproved = body.Approved,
            TrialEndsAt = body.TrialEndsAt,
            ActivationCodeLimit = body.ActivationCodeLimit,
            ExportCodesLimit = body.ExportCodesLimit,
            CreatedAt = clock.UtcNow
        };
        user.PasswordHash = Passwords.Hash(user, body.Password!);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await TeacherView(db, user));
    }

    static async Task<IResult> UpdateTeacher(Guid id, TeacherBody body, HttpContext http, ToviDb db, CancellationToken ct)
    {
        Http.Me(http, UserType.Admin).Require(PermissionCatalog.Update("teachers"));
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Teacher, ct)
            ?? throw new RuleException("not_found", "Teacher was not found.", 404);
        user.FirstName = body.FirstName;
        user.LastName = body.LastName;
        user.SectionId = body.SectionId;
        user.TeacherApproved = body.Approved;
        user.TrialEndsAt = body.TrialEndsAt;
        user.ActivationCodeLimit = body.ActivationCodeLimit;
        user.ExportCodesLimit = body.ExportCodesLimit;
        if (!string.IsNullOrWhiteSpace(body.Phone))
        {
            RuleException.Require(AccountRules.Phone(body.Phone));
            var phone = Http.Digits(body.Phone);
            if (await db.Users.AnyAsync(u => u.Phone == phone && u.Id != user.Id, ct))
                throw new RuleException("phone_taken", "This phone is already registered.", 409);
            user.Phone = phone;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(await TeacherView(db, user));
    }

    static async Task<object> TeacherView(ToviDb db, User user)
    {
        var ends = await db.Subscriptions.Where(s => s.UserId == user.Id && s.Status == SubscriptionStatus.Active)
            .OrderByDescending(s => s.EndsAt).Select(s => s.EndsAt).FirstOrDefaultAsync();
        return new
        {
            user.Id,
            user.FirstName,
            user.LastName,
            user.Phone,
            user.Status,
            user.TeacherApproved,
            user.SectionId,
            user.TrialEndsAt,
            user.ActivationCodeLimit,
            user.ExportCodesLimit,
            user.CreatedAt,
            subscriptionEndsAt = ends
        };
    }

    static async Task<IResult> CreateInstitute(HttpContext http, ToviDb db, IClock clock, IWebHostEnvironment env, IConfiguration config, CancellationToken ct)
    {
        Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create("institutes"));
        var form = await http.Request.ReadFormAsync(ct);
        var logo = form.Files["logo"] ?? throw new RuleException("logo_required", "Upload the institute logo.");
        var phone = Required(form, "phone");
        var phone2 = Required(form, "phone2");
        RuleException.Require(AccountRules.Phone(phone));
        RuleException.Require(AccountRules.Phone(phone2));
        RuleException.Require(AccountRules.Password(form["password"]));
        var digits = Http.Digits(phone);
        var digits2 = Http.Digits(phone2);
        if (digits == digits2 || await db.Users.AnyAsync(u => u.Phone == digits || u.Phone == digits2, ct))
            throw new RuleException("phone_taken", "This phone is already registered.", 409);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Type = UserType.InstituteManager,
            Phone = digits,
            Phone2 = digits2,
            FirstName = Required(form, "firstName"),
            InstituteName = Required(form, "instituteName"),
            Address = form["address"],
            Description = form["description"],
            CreatedAt = clock.UtcNow
        };
        user.PasswordHash = Passwords.Hash(user, form["password"]!);
        var file = await FilesApi.SaveAsync(db, env, config, clock, logo, false, ct);
        user.LogoFileId = file.Id;
        db.Users.Add(user);
        db.HourPools.Add(new InstituteHourPool { InstituteId = user.Id, RemainingMinutes = 0 });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { user.Id, user.InstituteName, user.LogoFileId });
    }

    static async Task<IResult> UpdateInstitute(Guid id, HttpContext http, ToviDb db, IClock clock, IWebHostEnvironment env, IConfiguration config, CancellationToken ct)
    {
        Http.Me(http, UserType.Admin).Require(PermissionCatalog.Update("institutes"));
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.InstituteManager, ct)
            ?? throw new RuleException("not_found", "Institute was not found.", 404);
        var form = await http.Request.ReadFormAsync(ct);
        if (!string.IsNullOrWhiteSpace(form["instituteName"])) user.InstituteName = form["instituteName"].ToString();
        if (!string.IsNullOrWhiteSpace(form["firstName"])) user.FirstName = form["firstName"].ToString();
        if (!string.IsNullOrWhiteSpace(form["address"])) user.Address = form["address"].ToString();
        if (!string.IsNullOrWhiteSpace(form["description"])) user.Description = form["description"].ToString();
        if (form.Files["logo"] is { Length: > 0 } logo)
        {
            var file = await FilesApi.SaveAsync(db, env, config, clock, logo, false, ct);
            user.LogoFileId = file.Id;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { user.Id, user.InstituteName, user.LogoFileId });
    }

    static async Task<IResult> CreditPool(Guid id, PoolBody body, HttpContext http, HourWork hours, CancellationToken ct)
    {
        Http.Me(http, UserType.Admin).Require(PermissionCatalog.Update("hour-pool"));
        await hours.CreditPoolAsync(id, body.Minutes, body.Note, ct);
        return Results.Ok(new { credited = body.Minutes });
    }

    static async Task<IResult> Disable(Guid id, HttpContext http, ToviDb db, IClock clock, string permission, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Admin);
        actor.Require(permission.Replace("view", "activate", StringComparison.Ordinal));
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new RuleException("not_found", "User was not found.", 404);
        user.Status = UserStatus.Disabled;
        var ids = await db.Users.Where(u => u.Id == id || u.EmployerUserId == id).Select(u => u.Id).ToListAsync(ct);
        var sessions = await db.Sessions.Where(s => ids.Contains(s.UserId) && s.RevokedAt == null).ToListAsync(ct);
        foreach (var session in sessions) session.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { disabled = true });
    }

    static async Task<IResult> ResetUserPassword(Guid id, PasswordBody body, HttpContext http, ToviDb db, CancellationToken ct)
    {
        Http.Me(http, UserType.Admin).Require("can-reset-password-teachers");
        RuleException.Require(AccountRules.Password(body.Password));
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new RuleException("not_found", "User was not found.", 404);
        user.PasswordHash = Passwords.Hash(user, body.Password);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { reset = true });
    }

    static async Task<IResult> CreateEmployee(EmployeeBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Admin);
        actor.Require(PermissionCatalog.Create("employees"));
        var created = await People.CreateEmployee(actor, body, db, clock, ct);
        return Results.Ok(new { created.Id, created.FirstName, created.LastName, created.Phone, created.EmployeeStyle, created.Status });
    }

    static async Task<IResult> CreateGroup(GroupBody body, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var actor = Http.Me(http, UserType.Admin);
        actor.Require(PermissionCatalog.Create("employees"));
        return Results.Ok(await People.CreateGroup(actor, body, db, ct));
    }

    static async Task<IResult> CreatePlan(PlanBody body, HttpContext http, ToviDb db, CancellationToken ct)
    {
        Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create("plans"));
        if (await db.Plans.AnyAsync(p => p.Title == body.Title, ct))
            throw new RuleException("title_taken", "Plan title must be unique.", 409);
        if (body.PriceYearly < 0 || body.CodesCount < 1)
            throw new RuleException("invalid_plan", "Price and code count are invalid.");
        var plan = new CodePlan { Id = Guid.NewGuid(), Title = body.Title, Description = body.Description, Audience = body.Audience, PriceYearly = body.PriceYearly, CodesCount = body.CodesCount };
        db.Plans.Add(plan);
        await db.SaveChangesAsync(ct);
        return Results.Ok(plan);
    }

    static async Task<IResult> Assign(AssignBody body, HttpContext http, CommerceWork commerce, CancellationToken ct)
    {
        Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create("subscriptions"));
        var (subscription, payment, _) = await commerce.AssignAsync(body.UserId, body.PlanId, true, ct);
        return Results.Ok(new { subscription, payment });
    }

    static async Task<IResult> CreateInvoice(InvoiceBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create("invoices"));
        var row = new Invoice { Id = Guid.NewGuid(), PartyUserId = body.PartyUserId, AmountIqd = body.AmountIqd, ActivatedCodesCount = body.ActivatedCodesCount, Note = body.Note, CreatedAt = clock.UtcNow };
        db.Invoices.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Ok(row);
    }

    static async Task<IResult> UploadPrivacy(HttpContext http, ToviDb db, IClock clock, IWebHostEnvironment env, IConfiguration config, CancellationToken ct)
    {
        Http.Me(http, UserType.Admin).Require(PermissionCatalog.Update("parameters"));
        var file = http.Request.Form.Files["file"] ?? throw new RuleException("file_required", "Upload the privacy PDF.");
        var stored = await FilesApi.SaveAsync(db, env, config, clock, file, true, ct);
        var url = $"/api/files/{stored.Id}";
        var row = await db.Parameters.FindAsync(["privacy_url"], ct);
        if (row is null) db.Parameters.Add(new SystemParameter { Key = "privacy_url", Value = url });
        else row.Value = url;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { url });
    }

    static async Task<IResult> CreateHourPackage(HourPackageBody body, HttpContext http, ToviDb db, HourWork hours, CancellationToken ct)
    {
        Http.Me(http, UserType.Admin).Require(PermissionCatalog.Create("hour-packages"));
        var package = hours.CreatePackage(body.Name, body.MonthlyHoursLimit, body.ValidityDays, body.PriceIqd, body.ReportLevel, body.AttendanceEnabled, body.ExportEnabled, body.IsActive);
        if (body.CodeExpiresAt is not null)
        {
            db.HourPackageCodes.Add(new HourPackageCode
            {
                Id = Guid.NewGuid(),
                PackageId = package.Id,
                Code = CodeNumbers.New16(),
                Status = HourCodeStatus.Unused,
                ExpiresAt = body.CodeExpiresAt
            });
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(package);
    }

    static string Required(IFormCollection form, string key)
    {
        var value = form[key].ToString();
        if (string.IsNullOrWhiteSpace(value))
            throw new RuleException("field_required", $"{key} is required.");
        if (TextGuard.HasMarkup(value))
            throw new RuleException("markup_rejected", "Script markup is not allowed in text.");
        return value;
    }
}

public static class People
{
    public static async Task<User> CreateEmployee(Actor actor, EmployeeBody body, ToviDb db, IClock clock, CancellationToken ct)
    {
        RuleException.Require(AccountRules.Phone(body.Phone));
        RuleException.Require(AccountRules.Password(body.Password));
        IEnumerable<string> allowed = actor.Portal == UserType.Admin ? PermissionCatalog.All : actor.Portal == UserType.Teacher ? PermissionCatalog.Teacher : PermissionCatalog.Institute;
        var phone = Http.Digits(body.Phone);
        if (await db.Users.AnyAsync(u => u.Phone == phone, ct))
            throw new RuleException("phone_taken", "This phone is already registered.", 409);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Type = UserType.Employee,
            Phone = phone,
            FirstName = body.FirstName,
            LastName = body.LastName,
            EmployerUserId = actor.Owner.Id,
            EmployeeStyle = body.Style,
            CreatedAt = clock.UtcNow
        };
        user.PasswordHash = Passwords.Hash(user, body.Password);
        db.Users.Add(user);
        if (body.Style == EmployeeStyle.Assistant)
        {
            foreach (var slug in body.Permissions ?? [])
            {
                if (!allowed.Contains(slug))
                    throw new RuleException("unknown_permission", "That permission is outside this portal.");
                db.PermissionGrants.Add(new PermissionGrant { Id = Guid.NewGuid(), UserId = user.Id, Slug = slug });
            }
        }
        else if (body.GroupId is not null)
        {
            db.UserPermissionGroups.Add(new UserPermissionGroup { Id = Guid.NewGuid(), UserId = user.Id, GroupId = body.GroupId.Value });
        }
        await db.SaveChangesAsync(ct);
        return user;
    }

    public static async Task<PermissionGroup> CreateGroup(Actor actor, GroupBody body, ToviDb db, CancellationToken ct)
    {
        IEnumerable<string> allowed = actor.Portal == UserType.Admin ? PermissionCatalog.All : actor.Portal == UserType.Teacher ? PermissionCatalog.Teacher : PermissionCatalog.Institute;
        var group = new PermissionGroup { Id = Guid.NewGuid(), OwnerUserId = actor.Owner.Id, Name = body.Name };
        db.PermissionGroups.Add(group);
        foreach (var slug in body.Permissions)
        {
            if (!allowed.Contains(slug))
                throw new RuleException("unknown_permission", "That permission is outside this portal.");
            db.PermissionGroupItems.Add(new PermissionGroupItem { Id = Guid.NewGuid(), GroupId = group.Id, Slug = slug });
        }
        await db.SaveChangesAsync(ct);
        return group;
    }
}
