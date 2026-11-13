using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Tovi.Api.Data;
using Tovi.Domain;

namespace Tovi.Api.Security;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public interface IOtpSender
{
    /// <summary>True only when a configured provider accepted the code. A log line is not delivery.</summary>
    Task<bool> SendAsync(string phone, string code, CancellationToken cancellationToken);
}

public sealed class LoggingOtpSender(ILogger<LoggingOtpSender> logger) : IOtpSender
{
    public Task<bool> SendAsync(string phone, string code, CancellationToken cancellationToken)
    {
        logger.LogInformation("OTP was not sent. No SMS provider is configured for {Phone}", phone);
        return Task.FromResult(false);
    }
}

public enum RemotePaymentStatus
{
    Unknown,
    Pending,
    Succeeded,
    Failed
}

public interface IPaymentGateway
{
    Task<string> StartAsync(Guid paymentId, long amountIqd, CancellationToken cancellationToken);
    Task<RemotePaymentStatus> GetStatusAsync(Guid paymentId, CancellationToken cancellationToken);
}

public sealed class ConfiguredPaymentGateway(IConfiguration configuration, IHostEnvironment environment) : IPaymentGateway
{
    public Task<string> StartAsync(Guid paymentId, long amountIqd, CancellationToken cancellationToken)
    {
        var configured = configuration["Payments:GatewayBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Task.FromResult($"{configured.TrimEnd('/')}/{paymentId}");
        if (environment.IsDevelopment())
            return Task.FromResult($"/api/payments/{paymentId}/checkout");
        throw new RuleException("payment_gateway_unconfigured", "The payment gateway is not configured.", 503);
    }

    public Task<RemotePaymentStatus> GetStatusAsync(Guid paymentId, CancellationToken cancellationToken) =>
        Task.FromResult(RemotePaymentStatus.Pending);
}

public sealed record PushResult(bool Delivered, string Reason);

public interface IPushSender
{
    Task<PushResult> SendAsync(IReadOnlyCollection<string> tokens, string title, string body, CancellationToken cancellationToken);
}

public sealed class FirebasePushSender(IConfiguration configuration, ILogger<FirebasePushSender> logger) : IPushSender
{
    public Task<PushResult> SendAsync(IReadOnlyCollection<string> tokens, string title, string body, CancellationToken cancellationToken)
    {
        var credential = configuration["Firebase:CredentialPath"];
        if (string.IsNullOrWhiteSpace(credential))
        {
            logger.LogInformation("Push not sent. Firebase credentials are not configured.");
            return Task.FromResult(new PushResult(false, "not_configured"));
        }

        if (!File.Exists(credential))
        {
            logger.LogInformation("Push not sent. Firebase credential file is missing.");
            return Task.FromResult(new PushResult(false, "credential_missing"));
        }

        if (tokens.Count == 0)
        {
            logger.LogInformation("Push not sent. No device tokens are stored.");
            return Task.FromResult(new PushResult(false, "no_devices"));
        }

        logger.LogInformation("Push not sent. The Firebase Admin sender is not integrated. Tokens {Count}", tokens.Count);
        return Task.FromResult(new PushResult(false, "sender_not_integrated"));
    }
}

public static class ProductionGuard
{
    public static void Ensure(IHostEnvironment environment, IConfiguration configuration)
    {
        if (!environment.IsProduction()) return;
        var jwt = configuration["Jwt:Key"] ?? "";
        if (jwt.Length < 32 || jwt.Contains("dev-only", StringComparison.Ordinal) || jwt.Contains("change-me", StringComparison.Ordinal))
            throw new InvalidOperationException("Set a production JWT signing key.");
        var webhook = configuration["Payments:WebhookSecret"] ?? "";
        if (webhook.Length < 24 || webhook.Contains("dev-webhook", StringComparison.Ordinal) || webhook.StartsWith("dev-", StringComparison.Ordinal))
            throw new InvalidOperationException("Set a production payment webhook secret.");
        var connection = configuration.GetConnectionString("Tovi") ?? "";
        if (connection.Contains("Tovi_Local_Sql", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Set a production database connection string.");
        var origins = configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
        if (origins.Length == 0 || origins.Any(origin => string.IsNullOrWhiteSpace(origin)))
            throw new InvalidOperationException("Set Cors:Origins for the production dashboards.");
    }
}

public static class StoragePath
{
    public static string Combine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new RuleException("forbidden", "The file path is outside storage.", 403);
        return full;
    }
}

public sealed class Actor
{
    public required User User { get; init; }
    public required User Owner { get; init; }
    public required UserType Portal { get; init; }
    public required HashSet<string> Grants { get; init; }
    public required UserSession Session { get; init; }

    public bool Allows(string permission)
    {
        if (User.Type == UserType.Admin) return true;
        if (User.Type == UserType.Teacher) return PermissionCatalog.Teacher.Contains(permission);
        if (User.Type == UserType.InstituteManager) return PermissionCatalog.Institute.Contains(permission);
        return Grants.Contains(permission);
    }

    public void Require(string permission)
    {
        if (!Allows(permission))
            throw new RuleException("forbidden", "You do not have permission for this action.", 403);
    }
}

public static class ActorLoader
{
    public static async Task<Actor?> LoadAsync(HttpContext http, ToviDb db, IClock clock, CancellationToken cancellationToken)
    {
        var sub = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var sid = http.User.FindFirstValue("sid");
        if (!Guid.TryParse(sub, out var userId) || !Guid.TryParse(sid, out var sessionId))
            return null;

        var session = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, cancellationToken);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= clock.UtcNow)
            return null;

        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
            return null;

        User owner = user;
        var portal = user.Type;
        if (user.Type == UserType.Employee)
        {
            if (user.EmployerUserId is null) return null;
            var employer = await db.Users.FirstOrDefaultAsync(x => x.Id == user.EmployerUserId, cancellationToken);
            if (employer is null || employer.Status != UserStatus.Active)
                return null;
            owner = employer;
            portal = employer.Type;
        }

        var grants = new HashSet<string>(StringComparer.Ordinal);
        if (user.Type == UserType.Employee)
        {
            var direct = await db.PermissionGrants.Where(x => x.UserId == user.Id).Select(x => x.Slug).ToListAsync(cancellationToken);
            var grouped = await (
                from link in db.UserPermissionGroups
                join item in db.PermissionGroupItems on link.GroupId equals item.GroupId
                where link.UserId == user.Id
                select item.Slug).ToListAsync(cancellationToken);
            foreach (var slug in direct.Concat(grouped))
            {
                if (portal == UserType.Admin || (portal == UserType.Teacher && PermissionCatalog.Teacher.Contains(slug)) || (portal == UserType.InstituteManager && PermissionCatalog.Institute.Contains(slug)))
                    grants.Add(slug);
            }
        }

        return new Actor { User = user, Owner = owner, Portal = portal, Grants = grants, Session = session };
    }
}

public static class Passwords
{
    static readonly PasswordHasher<User> Hasher = new();

    public static string Hash(User user, string password) => Hasher.HashPassword(user, password);

    public static bool Verify(User user, string password) =>
        Hasher.VerifyHashedPassword(user, user.PasswordHash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
}

public static class JwtTokens
{
    public static string Create(IConfiguration configuration, User user, UserSession session, DateTime expires)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Jwt:Key"]!));
        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim("sid", session.Id.ToString()),
                new Claim("typ", user.Type.ToString())
            ],
            notBefore: DateTime.UtcNow,
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class LiveKitTokens
{
    /// <summary>
    /// Signs a LiveKit access token (HS256). Signed by hand because LiveKit accepts any secret length,
    /// while the identity model token handler refuses keys shorter than 256 bits.
    /// </summary>
    public static string Create(string apiKey, string apiSecret, string identity, string room, bool roomAdmin, bool canPublish, DateTime expires, string? name = null, bool roomRecord = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new Dictionary<string, object>
        {
            ["sub"] = identity,
            ["iss"] = apiKey,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["nbf"] = now,
            ["exp"] = new DateTimeOffset(DateTime.SpecifyKind(expires, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            ["video"] = new Dictionary<string, object>
            {
                ["room"] = room,
                ["roomJoin"] = true,
                ["canPublish"] = canPublish,
                ["canSubscribe"] = true,
                ["canPublishData"] = true,
                ["roomAdmin"] = roomAdmin,
                ["roomRecord"] = roomRecord
            }
        };
        if (!string.IsNullOrWhiteSpace(name)) payload["name"] = name;
        static string Part(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var head = Part(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var body = Part(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature = Part(HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiSecret), Encoding.UTF8.GetBytes($"{head}.{body}")));
        return $"{head}.{body}.{signature}";
    }
}

public static class Signatures
{
    public static string Sign(string secret, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool Matches(string secret, string body, string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented)) return false;
        var expected = Sign(secret, body);
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(presented.Trim().ToLowerInvariant());
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

public static class CodeNumbers
{
    public static string New16()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        var chars = new char[16];
        for (var i = 0; i < 16; i++)
            chars[i] = (char)('0' + (bytes[i] % 10));
        return new string(chars);
    }
}

public static class PlainText
{
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password", "Token", "DeviceToken", "Code", "Signature"
    };

    public static void Reject(object? value, string? name = null)
    {
        if (value is null || name is not null && Skip.Contains(name)) return;
        if (value is string text)
        {
            if (!Skip.Contains(name ?? "") && TextGuard.HasMarkup(text))
                throw new RuleException("markup_rejected", "Script markup is not allowed in text.");
            return;
        }

        if (value is CancellationToken or IFormFile or Stream or HttpRequest or HttpContext) return;
        var type = value.GetType();
        if (type.Namespace?.StartsWith("Tovi.", StringComparison.Ordinal) != true) return;
        foreach (var property in type.GetProperties())
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
            Reject(property.GetValue(value), property.Name);
        }
    }
}
