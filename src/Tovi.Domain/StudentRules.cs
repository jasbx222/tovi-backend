namespace Tovi.Domain;

/// <summary>Rules behind the student exam list, the leaderboard and account changes.</summary>
public static class StudentRules
{
    /// <summary>
    /// The state a student sees on an exam card.
    /// locked: no exam code yet. done: submitted and fully corrected. pending: submitted with open answers still uncorrected.
    /// closed: the exam is completed or past its end date without an attempt. open: can be taken now.
    /// </summary>
    public static string ExamState(bool unlocked, bool attempted, bool pendingCorrection, ExamStatus status, DateTime endDate, DateTime now)
    {
        if (attempted) return pendingCorrection ? "pending" : "done";
        if (!unlocked) return "locked";
        if (status == ExamStatus.Completed) return "closed";
        if (endDate != default && endDate < now) return "closed";
        return "open";
    }

    /// <summary>A public name on the leaderboard: first name plus the first letter of the last name.</summary>
    public static string PublicName(string firstName, string? lastName)
    {
        var first = (firstName ?? "").Trim();
        var last = (lastName ?? "").Trim();
        if (last.Length == 0) return first;
        return $"{first} {last[..1]}.";
    }

    public sealed record Standing(Guid StudentId, int Xp, int Rank);

    /// <summary>Orders students by points. Ties share a rank; the next rank skips (1, 2, 2, 4).</summary>
    public static List<Standing> Rank(IEnumerable<(Guid StudentId, int Xp)> totals)
    {
        var ordered = totals.Where(t => t.Xp > 0).OrderByDescending(t => t.Xp).ThenBy(t => t.StudentId).ToList();
        var result = new List<Standing>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var rank = i > 0 && ordered[i].Xp == ordered[i - 1].Xp ? result[i - 1].Rank : i + 1;
            result.Add(new Standing(ordered[i].StudentId, ordered[i].Xp, rank));
        }
        return result;
    }

    /// <summary>The first day (Saturday, Baghdad week) of the week that contains the given progress day.</summary>
    public static string WeekStart(string day)
    {
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var back = ((int)date.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
        return date.AddDays(-back).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static RuleResult ChangePassword(bool currentMatches, string? next, string? current)
    {
        if (!currentMatches) return RuleResult.Deny("password_wrong", "The current password is not correct.");
        var shape = AccountRules.Password(next);
        if (!shape.Ok) return shape;
        if (next == current) return RuleResult.Deny("password_same", "Choose a password different from the current one.");
        return RuleResult.Allow();
    }

    public static RuleResult Name(string? firstName)
    {
        var value = (firstName ?? "").Trim();
        if (value.Length is < 2 or > 60) return RuleResult.Deny("name_required", "Write a first name of 2 to 60 letters.");
        return RuleResult.Allow();
    }

    public static readonly string[] NotificationTargets = ["session", "course", "exam", "summary", "teacher", "institute"];

    /// <summary>Only known targets with an id become a deep link; anything else opens nothing.</summary>
    public static (string? Type, Guid? Id) Link(string? type, Guid? id)
    {
        if (string.IsNullOrWhiteSpace(type) || id is null || id == Guid.Empty) return (null, null);
        return NotificationTargets.Contains(type) ? (type, id) : (null, null);
    }
}
