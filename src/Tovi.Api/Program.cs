using System.Text;
using System.Threading.RateLimiting;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Tovi.Api.Data;
using Tovi.Api.Endpoints;
using Tovi.Api.Jobs;
using Tovi.Api.Security;
using Tovi.Api.Services;
using Tovi.Domain;

var builder = WebApplication.CreateBuilder(args);
var testHost = builder.Environment.IsEnvironment("Test");
if (!testHost)
    builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5088");
ProductionGuard.Ensure(builder.Environment, builder.Configuration);

var sqlite = builder.Configuration["Database:Provider"] == "Sqlite";
builder.Services.AddDbContext<ToviDb>(options =>
{
    var connection = builder.Configuration.GetConnectionString("Tovi");
    if (sqlite) options.UseSqlite(connection);
    else options.UseSqlServer(connection);
});
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<CodeWork>();
builder.Services.AddScoped<LearningWork>();
builder.Services.AddScoped<CommerceWork>();
builder.Services.AddScoped<HourWork>();
builder.Services.AddScoped<NotifyWork>();
builder.Services.AddScoped<ProgressWork>();
builder.Services.AddSingleton<ToviJobs>();
builder.Services.AddSingleton<IOtpSender, LoggingOtpSender>();
builder.Services.AddSingleton<IPaymentGateway, ConfiguredPaymentGateway>();
builder.Services.AddSingleton<IPushSender, FirebasePushSender>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidateAudience = true,
        ValidAudience = builder.Configuration["Jwt:Audience"],
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1)
    };
});
builder.Services.AddAuthorization();
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()?.Where(origin => !string.IsNullOrWhiteSpace(origin)).ToArray() ?? [];
if (corsOrigins.Length == 0)
    corsOrigins = ["http://localhost:4181", "http://localhost:4182", "http://localhost:4183", "http://127.0.0.1:4181", "http://127.0.0.1:4182", "http://127.0.0.1:4183"];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));
var authPermit = int.TryParse(builder.Configuration["Auth:PermitPerMinute"], out var parsedPermit) ? parsedPermit : 30;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("user", http =>
    {
        var key = http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = testHost ? 100_000 : 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
    options.AddPolicy("auth", http =>
    {
        var key = http.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter("auth:" + key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authPermit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});
if (!testHost && !sqlite)
{
    builder.Services.AddHangfire(config => config.UseSqlServerStorage(builder.Configuration.GetConnectionString("Tovi")));
    builder.Services.AddHangfireServer();
}

var app = builder.Build();
app.Use(async (context, next) =>
{
    try
    {
        await next();
        if (context.Response.StatusCode == StatusCodes.Status400BadRequest && !context.Response.HasStarted)
            await context.Response.WriteAsJsonAsync(new { error = "invalid_body", message = "The request body is not valid." });
    }
    catch (RuleException ex)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex.Status;
        await context.Response.WriteAsJsonAsync(new { error = ex.Code, message = ex.Message, remaining = ex.Remaining });
    }
    catch (BadHttpRequestException)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "invalid_body", message = "The request body is not valid." });
    }
    catch (DbUpdateException ex) when (IsUniqueConflict(ex))
    {
        if (context.Response.HasStarted) throw;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("tovi");
        logger.LogWarning("Unique constraint rejected {Method} {Path}", context.Request.Method, context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = "conflict", message = "This record already exists." });
    }
    catch (Exception ex)
    {
        if (context.Response.HasStarted) throw;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("tovi");
        logger.LogError(ex, "Unhandled request failure {Method} {Path}", context.Request.Method, context.Request.Path);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "server_error", message = "The server could not complete this request." });
    }
});
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var actor = await ActorLoader.LoadAsync(context, context.RequestServices.GetRequiredService<ToviDb>(), context.RequestServices.GetRequiredService<IClock>(), context.RequestAborted);
        if (actor is null)
        {
            if (!OpensWithoutSession(context.Request))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "session_dead", message = "The session is missing or no longer valid." });
                return;
            }
        }
        else
            context.Items["actor"] = actor;
    }
    await next();
});

var api = app.MapGroup("/api").AddEndpointFilter<PlainTextFilter>().RequireRateLimiting("user");
AuthApi.Map(api.MapGroup("").RequireRateLimiting("auth"));
AdminApi.Map(api);
ContentApi.MapTeacher(api);
ContentApi.MapInstitute(api);
StudentApi.Map(api);
FilesApi.Map(app);
app.MapGet("/api/health", () => Results.Ok(new { name = "TOVI", ok = true }));

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ToviDb>();
    await db.Database.EnsureCreatedAsync();
    await SchemaUpgrade.ApplyAsync(db);
    if (app.Environment.IsDevelopment() && !await db.Users.AnyAsync())
    {
        var admin = new User
        {
            Id = Guid.NewGuid(),
            Type = UserType.Admin,
            Phone = AccountRules.Digits(builder.Configuration["Seed:AdminPhone"] ?? "07700000001"),
            FirstName = "TOVI",
            LastName = "Admin",
            CreatedAt = DateTime.UtcNow
        };
        admin.PasswordHash = Passwords.Hash(admin, builder.Configuration["Seed:AdminPassword"] ?? "ToviAdmin#1");
        db.Users.Add(admin);
        db.Parameters.Add(new SystemParameter { Key = "otp_length", Value = "6" });
        db.Parameters.Add(new SystemParameter { Key = "otp_minutes", Value = "10" });
        await db.SaveChangesAsync();
    }
}

if (!testHost && !sqlite)
{
    using var scope = app.Services.CreateScope();
    var jobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    jobs.AddOrUpdate<ToviJobs>("tovi-exams", job => job.ExamTransitions(), Cron.Minutely);
    jobs.AddOrUpdate<ToviJobs>("tovi-subscriptions", job => job.Subscriptions(), Cron.Minutely);
    jobs.AddOrUpdate<ToviJobs>("tovi-payments", job => job.Payments(), Cron.Minutely);
    jobs.AddOrUpdate<ToviJobs>("tovi-notifications", job => job.Notifications(), Cron.Minutely);
    jobs.AddOrUpdate<ToviJobs>("tovi-trials", job => job.Trials(), Cron.Hourly);
    jobs.AddOrUpdate<ToviJobs>("tovi-session-reminders", job => job.SessionReminders(), Cron.Minutely);
}

app.Run();

static bool OpensWithoutSession(HttpRequest request)
{
    var path = request.Path.Value ?? "";
    if (HttpMethods.IsGet(request.Method) && path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        return true;
    if (!HttpMethods.IsPost(request.Method) || !path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
        return false;
    return path.EndsWith("/login", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/register", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/otp", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/otp/verify", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/password-reset", StringComparison.OrdinalIgnoreCase);
}

static bool IsUniqueConflict(Exception exception)
{
    for (var current = exception; current is not null; current = current.InnerException)
    {
        if (current.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || current.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}

public partial class Program;
