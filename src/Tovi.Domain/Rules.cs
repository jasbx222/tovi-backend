namespace Tovi.Domain;

public readonly record struct RuleResult(bool Ok, string? Code, string? Message, int Status, int? Remaining)
{
    public static RuleResult Allow() => new(true, null, null, 200, null);

    public static RuleResult Deny(string code, string message, int status = 400, int? remaining = null) =>
        new(false, code, message, status, remaining);
}

public sealed class RuleException : Exception
{
    public string Code { get; }
    public int Status { get; }
    public int? Remaining { get; }

    public RuleException(string code, string message, int status = 400, int? remaining = null) : base(message)
    {
        Code = code;
        Status = status;
        Remaining = remaining;
    }

    public static void Require(RuleResult result)
    {
        if (!result.Ok)
            throw new RuleException(result.Code ?? "rejected", result.Message ?? "Rejected.", result.Status, result.Remaining);
    }
}

public static class AccountRules
{
    public static string Digits(string? phone) => new((phone ?? "").Where(char.IsDigit).ToArray());

    public static RuleResult Phone(string? phone)
    {
        if (Digits(phone).Length < 11)
            return RuleResult.Deny("phone_invalid", "Phone must contain at least 11 digits.");
        return RuleResult.Allow();
    }

    public static RuleResult Password(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return RuleResult.Deny("password_short", "Password must be at least 8 characters.");
        return RuleResult.Allow();
    }
}

public static class TextGuard
{
    public static bool HasMarkup(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return value.Contains('<') || value.Contains('>');
    }
}

public static class PageRules
{
    public const int DefaultSize = 20;
    public const int MaxSize = 50;

    public static RuleResult Resolve(int? page, int? pageSize, out int resolvedPage, out int resolvedSize)
    {
        resolvedPage = page ?? 1;
        resolvedSize = pageSize ?? DefaultSize;
        if (page is < 1)
            return RuleResult.Deny("invalid_page", "Page must be at least 1.");
        if (pageSize is < 1 or > MaxSize)
            return RuleResult.Deny("invalid_page_size", "Page size must be from 1 to 50.");
        return RuleResult.Allow();
    }
}

public sealed record SubscriptionSlice(Guid SubscriptionId, int Count, DateTime ExpiresAt);

public sealed record Allocation(bool Ok, string? Code, string? Message, int Remaining, IReadOnlyList<SubscriptionSlice> Slices)
{
    public static Allocation Rejected(int remaining) =>
        new(false, "code_quota_exceeded", $"Only {remaining} codes remain.", remaining, []);

    public static Allocation Done(IReadOnlyList<SubscriptionSlice> slices, int remainingAfter) =>
        new(true, null, null, remainingAfter, slices);
}

public static class CodeQuota
{
    public const int MaxBatch = 100;

    public static int Available(int planCodesCount, int codesIssued) => planCodesCount - codesIssued;

    public static RuleResult Batch(int count)
    {
        if (count < 1 || count > MaxBatch)
            return RuleResult.Deny("code_batch_limit", "A single request can generate at most 100 codes.");
        return RuleResult.Allow();
    }

    public static Allocation Allocate(
        IEnumerable<(Guid Id, DateTime StartsAt, DateTime EndsAt, int PlanCodesCount, int CodesIssued)> subscriptions,
        int count,
        DateTime now)
    {
        var ordered = subscriptions
            .Where(s => s.EndsAt > now && Available(s.PlanCodesCount, s.CodesIssued) > 0)
            .OrderBy(s => s.StartsAt)
            .ThenBy(s => s.Id)
            .ToList();
        var remaining = ordered.Sum(s => Available(s.PlanCodesCount, s.CodesIssued));
        if (count > remaining)
            return Allocation.Rejected(remaining);

        var slices = new List<SubscriptionSlice>();
        var left = count;
        foreach (var subscription in ordered)
        {
            if (left == 0) break;
            var take = Math.Min(left, Available(subscription.PlanCodesCount, subscription.CodesIssued));
            if (take == 0) continue;
            slices.Add(new SubscriptionSlice(subscription.Id, take, subscription.EndsAt));
            left -= take;
        }

        return Allocation.Done(slices, remaining - count);
    }

    /// <summary>
    /// Used only when no active subscription applies.
    /// TODO UNVERIFIED: a null activation_code_limit is not documented as unlimited, unlike export_codes_limit.
    /// </summary>
    public static Allocation AllocateFallback(int? activationCodeLimit, int issuedWithoutSubscription, int count)
    {
        if (activationCodeLimit is null)
            return Allocation.Rejected(0);
        var remaining = Math.Max(0, activationCodeLimit.Value - issuedWithoutSubscription);
        if (count > remaining)
            return Allocation.Rejected(remaining);
        return Allocation.Done([], remaining - count);
    }

    public static int ShownBalance(
        IEnumerable<(DateTime EndsAt, int PlanCodesCount, int CodesIssued)> activeUnexpired,
        int? activationCodeLimit,
        int issuedWithoutSubscription,
        DateTime now)
    {
        var applicable = activeUnexpired.Where(s => s.EndsAt > now).ToList();
        if (applicable.Count == 0)
        {
            if (activationCodeLimit is null) return 0;
            return Math.Max(0, activationCodeLimit.Value - issuedWithoutSubscription);
        }

        return applicable.Sum(s => Math.Max(0, Available(s.PlanCodesCount, s.CodesIssued)));
    }
}

public static class CodeRules
{
    public static RuleResult CanRedeem(CodeStatus? status, DateTime? expiresAt, DateTime now)
    {
        if (status is null)
            return RuleResult.Deny("code_missing", "This code does not exist.", 404);
        if (status == CodeStatus.Active)
            return RuleResult.Deny("code_already_active", "This code is already active.");
        if (status == CodeStatus.Canceled)
            return RuleResult.Deny("code_canceled", "This code is canceled.");
        if (expiresAt is not null && expiresAt <= now)
            return RuleResult.Deny("code_expired", "This code is past its end date.");
        return RuleResult.Allow();
    }

    public static RuleResult CanCancel(CodeStatus status)
    {
        if (status == CodeStatus.Active)
            return RuleResult.Deny("code_active", "An active code cannot be canceled.");
        if (status == CodeStatus.Canceled)
            return RuleResult.Deny("code_canceled", "This code is already canceled.");
        return RuleResult.Allow();
    }
}

public static class ExportRules
{
    public static RuleResult CanExport(bool institute, bool teacherApproved, int? exportLimit, int requested, bool hasActiveSubscription)
    {
        if (requested < 1)
            return RuleResult.Deny("invalid_export", "Nothing to export.");
        if (institute)
        {
            if (!hasActiveSubscription)
                return RuleResult.Deny("subscription_required", "An institute needs an active subscription before exporting codes.", 403);
            return RuleResult.Allow();
        }

        if (!teacherApproved)
        {
            if (exportLimit is null || requested <= exportLimit)
                return RuleResult.Allow();
            return RuleResult.Deny("export_limit", $"Export is limited to {exportLimit} codes before approval.", 403);
        }

        if (!hasActiveSubscription)
            return RuleResult.Deny("subscription_required", "An approved teacher needs an active subscription before exporting codes.", 403);
        return RuleResult.Allow();
    }
}

public static class TrialRules
{
    public static bool WritesAllowed(DateTime? trialEndsAt, DateTime now, bool hasQualifyingSubscription) =>
        trialEndsAt is null || trialEndsAt > now || hasQualifyingSubscription;
}

public static class PlanRules
{
    public static RuleResult CanAssign(PlanAudience audience, UserType userType, bool samePlanStillHasCodes)
    {
        if (audience == PlanAudience.ForTeacher && userType != UserType.Teacher)
            return RuleResult.Deny("plan_audience", "A teacher plan can only be assigned to a teacher.");
        if (audience == PlanAudience.ForInstitute && userType != UserType.InstituteManager)
            return RuleResult.Deny("plan_audience", "An institute plan can only be assigned to an institute manager.");
        if (samePlanStillHasCodes)
            return RuleResult.Deny("plan_already_open", "This user already has this plan with codes remaining.", 409);
        return RuleResult.Allow();
    }

    public static DateTime EndsAt(DateTime now) => now.AddDays(365);
}

public static class HourRules
{
    public static readonly int[] AllowedDurations = [30, 45, 60, 90, 120];
    public const int DefaultDuration = 60;

    public static int MinutesFor(int monthlyHoursLimit) => monthlyHoursLimit * 60;

    public static decimal DisplayHours(int remainingMinutes) =>
        Math.Round(remainingMinutes / 60m, 2, MidpointRounding.AwayFromZero);

    public static RuleResult Package(int monthlyHoursLimit)
    {
        if (monthlyHoursLimit < 1)
            return RuleResult.Deny("hours_limit", "Monthly hours must be at least 1.");
        return RuleResult.Allow();
    }

    public static RuleResult CanRedeemPackage(HourCodeStatus status, DateTime? expiresAt, DateTime now)
    {
        if (status == HourCodeStatus.Used)
            return RuleResult.Deny("package_code_used", "This package code is already used.");
        if (status == HourCodeStatus.Expired || (expiresAt is not null && expiresAt <= now))
            return RuleResult.Deny("package_code_expired", "This package code is expired.");
        return RuleResult.Allow();
    }

    public static RuleResult CanOpenClassroom(bool active, int remainingMinutes, DateTime endsAt, DateTime now)
    {
        if (!active || endsAt <= now || remainingMinutes <= 0)
            return RuleResult.Deny("hour_subscription_required", "An active hour subscription with time remaining is required.", 403);
        return RuleResult.Allow();
    }

    public static RuleResult CanSchedule(int? duration, int remainingMinutes, bool overlaps, bool funded)
    {
        var minutes = duration ?? DefaultDuration;
        if (!AllowedDurations.Contains(minutes))
            return RuleResult.Deny("invalid_duration", "Duration must be 30, 45, 60, 90, or 120 minutes.");
        if (!funded)
            return RuleResult.Deny("hour_subscription_required", "There is no active unexpired hour balance.", 403);
        if (remainingMinutes < minutes)
            return RuleResult.Deny("not_enough_minutes", "The remaining minutes are below this duration.");
        if (overlaps)
            return RuleResult.Deny("session_overlap", "This teacher already has a session in that time.", 409);
        return RuleResult.Allow();
    }

    public static bool Overlaps(DateTime start, int minutes, DateTime otherStart, int otherMinutes)
    {
        var end = start.AddMinutes(minutes);
        var otherEnd = otherStart.AddMinutes(otherMinutes);
        return start < otherEnd && otherStart < end;
    }
}

public static class ExamRules
{
    public static RuleResult Mark(int mark, string field)
    {
        if (mark is < 0 or > 100)
            return RuleResult.Deny("mark_limit", $"{field} mark must be from 0 to 100.");
        return RuleResult.Allow();
    }

    public static RuleResult CanCorrect(bool alreadyCorrected, int questionMark, int awarded)
    {
        if (alreadyCorrected)
            return RuleResult.Deny("already_corrected", "This answer was already corrected.", 409);
        if (awarded < 0 || awarded > questionMark)
            return RuleResult.Deny("mark_above_question", "The mark cannot exceed the question mark.");
        return RuleResult.Allow();
    }

    public static ExamStatus? NextStatus(
        ExamStatus current,
        bool allAssignedHaveAttempt,
        bool endPassed,
        int attemptCount,
        bool everyAttemptFullyCorrected)
    {
        if (current == ExamStatus.New && (allAssignedHaveAttempt || endPassed))
            return ExamStatus.UnderCorrection;
        if (current == ExamStatus.UnderCorrection && attemptCount > 0 && everyAttemptFullyCorrected)
            return ExamStatus.Completed;
        return null;
    }
}

public static class MeetingRules
{
    public const int LobbyMinutes = 10;

    public static RuleResult CanJoin(DateTime startsAt, int durationMinutes, DateTime now, bool allowedParticipant, bool host = false)
    {
        if (!allowedParticipant)
            return RuleResult.Deny("not_enrolled", "Only authorized participants can enter this meeting.", 403);
        var end = startsAt.AddMinutes(durationMinutes);
        var opens = host ? startsAt.AddMinutes(-LobbyMinutes) : startsAt;
        if (now < opens || now >= end)
            return RuleResult.Deny("outside_window", "The session is outside its allowed time window.", 403);
        return RuleResult.Allow();
    }

    public static string Phase(DateTime startsAt, int durationMinutes, DateTime now)
    {
        if (now >= startsAt.AddMinutes(durationMinutes)) return "ended";
        if (now >= startsAt) return "live";
        if (now >= startsAt.AddMinutes(-LobbyMinutes)) return "lobby";
        return "upcoming";
    }

    public static string Room(string environment, Guid sessionId) => $"{environment}:session:{sessionId}";

    public static string ChatPath(string environment, Guid sessionId) => $"{environment}/meetings/{sessionId}/messages";
}

public static class LiveRules
{
    public static readonly string[] Reactions = ["clap", "heart", "idea", "laugh", "wow"];
    public const int MaxHeartbeatSeconds = 45;
    public const int ReactionGapMilliseconds = 800;

    public static RuleResult Reaction(string? kind) =>
        Reactions.Contains(kind ?? "")
            ? RuleResult.Allow()
            : RuleResult.Deny("reaction_unknown", "Choose one of the offered reactions.");

    public static RuleResult QuestionText(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0 || value.Length > 300)
            return RuleResult.Deny("question_length", "A question is 1 to 300 characters.");
        if (TextGuard.HasMarkup(value))
            return RuleResult.Deny("markup_rejected", "Plain text only.");
        return RuleResult.Allow();
    }

    public static RuleResult Poll(string? question, IReadOnlyList<string>? options)
    {
        var title = (question ?? "").Trim();
        if (title.Length == 0 || title.Length > 200)
            return RuleResult.Deny("poll_question_length", "A poll question is 1 to 200 characters.");
        var list = (options ?? []).Select(o => (o ?? "").Trim()).ToList();
        if (list.Count < 2 || list.Count > 5)
            return RuleResult.Deny("poll_options_count", "A poll has 2 to 5 options.");
        if (list.Any(o => o.Length == 0 || o.Length > 80 || o.Contains('\n')))
            return RuleResult.Deny("poll_option_length", "Each option is 1 to 80 characters on one line.");
        if (list.Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count)
            return RuleResult.Deny("poll_option_duplicate", "Options must differ.");
        if (TextGuard.HasMarkup(title) || list.Any(TextGuard.HasMarkup))
            return RuleResult.Deny("markup_rejected", "Plain text only.");
        return RuleResult.Allow();
    }

    public static int Credit(DateTime lastSeenAt, DateTime now)
    {
        var gap = (int)Math.Floor((now - lastSeenAt).TotalSeconds);
        return Math.Clamp(gap, 0, MaxHeartbeatSeconds);
    }

    public static int AttendanceSeconds(int durationMinutes) => Math.Max(60, durationMinutes * 60 / 2);

    public static bool Attended(int liveSeconds, int durationMinutes) => liveSeconds >= AttendanceSeconds(durationMinutes);
}

public static class ProgressRules
{
    public const string Lesson = "lesson";
    public const string Exam = "exam";
    public const string Live = "live";

    public static int Points(string kind) => kind switch
    {
        Lesson => 10,
        Exam => 20,
        Live => 30,
        _ => 0
    };

    public static string Day(DateTime utc)
    {
        var local = utc.AddHours(3);
        return local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static (int Current, int Best, bool Today) Streak(IEnumerable<string> days, string today)
    {
        var set = days.Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var now = DateOnly.ParseExact(today, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var best = 0;
        foreach (var day in set)
        {
            if (set.Contains(day.AddDays(-1))) continue;
            var run = 0;
            while (set.Contains(day.AddDays(run))) run++;
            best = Math.Max(best, run);
        }
        var active = set.Contains(now);
        var cursor = active ? now : now.AddDays(-1);
        var current = 0;
        while (set.Contains(cursor.AddDays(-current))) current++;
        return (current, best, active);
    }

    public sealed record Badge(string Key, string Kind, int Goal);

    public static readonly Badge[] Badges =
    [
        new("first_lesson", Lesson, 1),
        new("ten_lessons", Lesson, 10),
        new("first_exam", Exam, 1),
        new("five_exams", Exam, 5),
        new("first_live", Live, 1),
        new("five_lives", Live, 5),
        new("streak_3", "streak", 3),
        new("streak_7", "streak", 7),
        new("xp_100", "xp", 100),
        new("xp_500", "xp", 500),
    ];
}
