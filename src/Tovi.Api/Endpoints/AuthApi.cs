using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Domain;

namespace Tovi.Api.Endpoints;

public record LoginBody(string Phone, string Password);
public record RegisterStudentBody(string FirstName, string? LastName, string Phone, string Password, Gender Gender, Guid? GradeId, Guid? SectionId, Guid? GovernorateId);
public record RegisterInstituteBody(string InstituteName, string FirstName, string Phone, string Phone2, string Address, string Description, string Password);
public record OtpBody(string Phone);
public record OtpVerifyBody(string Phone, string Code);
public record ResetBody(string Phone, string Code, string Password);

public static class AuthApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");
        auth.MapPost("/{portal}/login", Login);
        auth.MapPost("/student/register", RegisterStudent);
        auth.MapPost("/institute/register", RegisterInstitute);
        auth.MapPost("/student/otp", RequestOtp);
        auth.MapPost("/student/otp/verify", VerifyOtp);
        auth.MapPost("/student/password-reset", ResetPassword);
        auth.MapPost("/logout", Logout);
        auth.MapGet("/me", Me);
    }

    static async Task<IResult> Login(string portal, LoginBody body, ToviDb db, IClock clock, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var phone = Http.Digits(body.Phone);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Phone == phone, cancellationToken);
        if (user is null || !Passwords.Verify(user, body.Password) || user.Status != UserStatus.Active)
            throw new RuleException("invalid_login", "The phone or password is wrong, or the account is disabled.", 401);

        var expected = portal switch
        {
            "admin" => UserType.Admin,
            "teacher" => UserType.Teacher,
            "institute" => UserType.InstituteManager,
            "student" => UserType.Student,
            _ => throw new RuleException("unknown_portal", "Unknown portal.", 404)
        };
        var actual = user.Type;
        if (user.Type == UserType.Employee)
        {
            var employer = await db.Users.FirstOrDefaultAsync(u => u.Id == user.EmployerUserId, cancellationToken);
            if (employer is null || employer.Status != UserStatus.Active)
                throw new RuleException("forbidden", "The employer account is not active.", 403);
            actual = employer.Type;
        }

        var teacherInsideInstitute = portal == "institute" && user.Type == UserType.Teacher &&
            await db.InstituteTeachers.AnyAsync(x => x.TeacherId == user.Id, cancellationToken);
        if (!teacherInsideInstitute && actual != expected)
            throw new RuleException("forbidden", "This account cannot use this portal.", 403);

        return Results.Ok(await IssueAsync(user, db, clock, configuration, cancellationToken));
    }

    static async Task<IResult> RegisterStudent(RegisterStudentBody body, ToviDb db, IClock clock, IConfiguration configuration, CancellationToken cancellationToken)
    {
        RuleException.Require(AccountRules.Phone(body.Phone));
        RuleException.Require(AccountRules.Password(body.Password));
        var phone = Http.Digits(body.Phone);
        if (await db.Users.AnyAsync(u => u.Phone == phone, cancellationToken))
            throw new RuleException("phone_taken", "This phone is already registered.", 409);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Type = UserType.Student,
            Phone = phone,
            FirstName = body.FirstName,
            LastName = body.LastName ?? "",
            Gender = body.Gender,
            GradeId = body.GradeId,
            SectionId = body.SectionId,
            GovernorateId = body.GovernorateId,
            CreatedAt = clock.UtcNow
        };
        user.PasswordHash = Passwords.Hash(user, body.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await IssueAsync(user, db, clock, configuration, cancellationToken));
    }

    static async Task<IResult> RegisterInstitute(RegisterInstituteBody body, ToviDb db, IClock clock, IConfiguration configuration, CancellationToken cancellationToken)
    {
        RuleException.Require(AccountRules.Phone(body.Phone));
        RuleException.Require(AccountRules.Phone(body.Phone2));
        RuleException.Require(AccountRules.Password(body.Password));
        if (string.IsNullOrWhiteSpace(body.InstituteName) || string.IsNullOrWhiteSpace(body.FirstName))
            throw new RuleException("name_required", "Institute name and first name are required.");
        var phone = Http.Digits(body.Phone);
        var phone2 = Http.Digits(body.Phone2);
        if (phone == phone2 || await db.Users.AnyAsync(u => u.Phone == phone || u.Phone == phone2, cancellationToken))
            throw new RuleException("phone_taken", "This phone is already registered.", 409);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Type = UserType.InstituteManager,
            Phone = phone,
            Phone2 = phone2,
            FirstName = body.FirstName,
            InstituteName = body.InstituteName,
            Address = body.Address,
            Description = body.Description,
            CreatedAt = clock.UtcNow
        };
        user.PasswordHash = Passwords.Hash(user, body.Password);
        db.Users.Add(user);
        db.HourPools.Add(new InstituteHourPool { InstituteId = user.Id, RemainingMinutes = 0 });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await IssueAsync(user, db, clock, configuration, cancellationToken));
    }

    static async Task<IResult> RequestOtp(OtpBody body, ToviDb db, IClock clock, IOtpSender sender, CancellationToken cancellationToken)
    {
        RuleException.Require(AccountRules.Phone(body.Phone));
        var phone = Http.Digits(body.Phone);
        var lengthRow = await db.Parameters.FindAsync([ "otp_length" ], cancellationToken);
        var minutesRow = await db.Parameters.FindAsync([ "otp_minutes" ], cancellationToken);
        var length = int.TryParse(lengthRow?.Value, out var parsed) ? parsed : 6;
        var minutes = int.TryParse(minutesRow?.Value, out var parsedMinutes) ? parsedMinutes : 10;
        var max = (int)Math.Pow(10, Math.Clamp(length, 4, 8));
        var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, max).ToString().PadLeft(Math.Clamp(length, 4, 8), '0');
        var delivered = await sender.SendAsync(phone, code, cancellationToken);
        if (!delivered)
            throw new RuleException("sms_unconfigured", "SMS verification is not configured, so the code was not sent.", 503);
        db.Otps.Add(new OtpChallenge
        {
            Id = Guid.NewGuid(),
            Phone = phone,
            CodeHash = Signatures.Sign("otp", code),
            ExpiresAt = clock.UtcNow.AddMinutes(minutes),
            Consumed = false
        });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { sent = true });
    }

    static async Task<IResult> VerifyOtp(OtpVerifyBody body, ToviDb db, IClock clock, CancellationToken cancellationToken)
    {
        var row = await FindOtp(body.Phone, body.Code, db, cancellationToken);
        if (row is null || row.ExpiresAt <= clock.UtcNow || row.Consumed)
            throw new RuleException("otp_invalid", "The code is invalid or expired.");
        return Results.Ok(new { verified = true });
    }

    static async Task<IResult> ResetPassword(ResetBody body, ToviDb db, IClock clock, CancellationToken cancellationToken)
    {
        RuleException.Require(AccountRules.Password(body.Password));
        await ConsumeOtp(body.Phone, body.Code, db, clock, cancellationToken);
        var phone = Http.Digits(body.Phone);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Phone == phone && u.Type == UserType.Student, cancellationToken)
            ?? throw new RuleException("not_found", "Student was not found.", 404);
        user.PasswordHash = Passwords.Hash(user, body.Password);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { reset = true });
    }

    static async Task ConsumeOtp(string phone, string code, ToviDb db, IClock clock, CancellationToken cancellationToken)
    {
        var row = await FindOtp(phone, code, db, cancellationToken);
        if (row is null || row.ExpiresAt <= clock.UtcNow || row.Consumed)
            throw new RuleException("otp_invalid", "The code is invalid or expired.");
        row.Consumed = true;
        await db.SaveChangesAsync(cancellationToken);
    }

    static Task<OtpChallenge?> FindOtp(string phone, string code, ToviDb db, CancellationToken cancellationToken)
    {
        var normalized = Http.Digits(phone);
        var hash = Signatures.Sign("otp", code);
        return db.Otps.Where(o => o.Phone == normalized && o.CodeHash == hash)
            .OrderByDescending(o => o.ExpiresAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    static async Task<IResult> Logout(HttpContext http, ToviDb db, IClock clock, CancellationToken cancellationToken)
    {
        var actor = Http.Me(http);
        actor.Session.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { loggedOut = true });
    }

    static async Task<IResult> Me(HttpContext http, ToviDb db, CancellationToken cancellationToken)
    {
        var actor = Http.Me(http);
        var permissions = actor.User.Type switch
        {
            UserType.Admin => PermissionCatalog.All,
            UserType.Teacher => PermissionCatalog.Teacher.OrderBy(x => x).ToList(),
            UserType.InstituteManager => PermissionCatalog.Institute.OrderBy(x => x).ToList(),
            UserType.Employee => actor.Grants.OrderBy(x => x).ToList(),
            _ => new List<string>()
        };
        var ends = await db.Subscriptions
            .Where(s => s.UserId == actor.Owner.Id && s.Status == SubscriptionStatus.Active)
            .OrderByDescending(s => s.EndsAt)
            .Select(s => s.EndsAt)
            .FirstOrDefaultAsync(cancellationToken);
        return Results.Ok(new
        {
            id = actor.User.Id,
            ownerId = actor.Owner.Id,
            portal = actor.Portal.ToString(),
            type = actor.User.Type.ToString(),
            name = Display(actor.User),
            phone = actor.User.Phone,
            instituteName = actor.Owner.InstituteName,
            permissions,
            subscriptionEndsAt = ends,
            approved = actor.Owner.TeacherApproved
        });
    }

    public static async Task<object> IssueAsync(User user, ToviDb db, IClock clock, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var hours = int.TryParse(configuration["Jwt:SessionHours"], out var parsed) ? parsed : 12;
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CreatedAt = clock.UtcNow,
            ExpiresAt = clock.UtcNow.AddHours(hours)
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return new
        {
            token = JwtTokens.Create(configuration, user, session, session.ExpiresAt),
            expiresAt = session.ExpiresAt,
            user = new { id = user.Id, type = user.Type.ToString(), name = Display(user), phone = user.Phone }
        };
    }

    public static string Display(User user) =>
        user.Type == UserType.InstituteManager
            ? user.InstituteName ?? user.FirstName
            : string.Join(" ", new[] { user.FirstName, user.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
}
