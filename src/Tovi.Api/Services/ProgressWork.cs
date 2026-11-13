using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Domain;

namespace Tovi.Api.Services;

public sealed class ProgressWork(ToviDb db, IClock clock)
{
    /// <summary>Adds the points for one finished activity once. A repeated award for the same source is ignored.</summary>
    public async Task<int> AwardAsync(Guid studentId, string kind, Guid sourceId, CancellationToken cancellationToken)
    {
        var points = ProgressRules.Points(kind);
        if (points <= 0) return 0;
        if (await db.XpEvents.AnyAsync(e => e.StudentId == studentId && e.Kind == kind && e.SourceId == sourceId, cancellationToken))
            return 0;
        db.XpEvents.Add(new XpEvent
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            Kind = kind,
            SourceId = sourceId,
            Points = points,
            Day = ProgressRules.Day(clock.UtcNow),
            CreatedAt = clock.UtcNow
        });
        return points;
    }

    public async Task<Dictionary<Guid, (int Xp, int Streak)>> TotalsAsync(IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken)
    {
        var rows = await db.XpEvents.Where(e => studentIds.Contains(e.StudentId)).Select(e => new { e.StudentId, e.Points, e.Day }).ToListAsync(cancellationToken);
        var today = ProgressRules.Day(clock.UtcNow);
        return rows.GroupBy(r => r.StudentId).ToDictionary(
            g => g.Key,
            g => (g.Sum(r => r.Points), ProgressRules.Streak(g.Select(r => r.Day).Distinct(), today).Current));
    }

    public async Task<object> SummaryAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var events = await db.XpEvents.Where(e => e.StudentId == studentId).OrderBy(e => e.CreatedAt).ToListAsync(cancellationToken);
        var today = ProgressRules.Day(clock.UtcNow);
        var todayDate = DateOnly.ParseExact(today, "yyyy-MM-dd");
        var streak = ProgressRules.Streak(events.Select(e => e.Day).Distinct(), today);
        var week = Enumerable.Range(0, 7).Select(i => todayDate.AddDays(i - 6)).Select(day =>
        {
            var key = day.ToString("yyyy-MM-dd");
            return new { day = key, xp = events.Where(e => e.Day == key).Sum(e => e.Points) };
        }).ToList();
        var total = events.Sum(e => e.Points);
        var badges = new List<object>();
        foreach (var badge in ProgressRules.Badges)
        {
            int progress;
            DateTime? unlockedAt = null;
            switch (badge.Kind)
            {
                case "streak":
                    progress = Math.Max(streak.Best, streak.Current);
                    if (progress >= badge.Goal) unlockedAt = StreakReachedAt(events, badge.Goal);
                    break;
                case "xp":
                    progress = total;
                    var running = 0;
                    foreach (var item in events)
                    {
                        running += item.Points;
                        if (running >= badge.Goal) { unlockedAt = item.CreatedAt; break; }
                    }
                    break;
                default:
                    var matching = events.Where(e => e.Kind == badge.Kind).ToList();
                    progress = matching.Count;
                    if (matching.Count >= badge.Goal) unlockedAt = matching[badge.Goal - 1].CreatedAt;
                    break;
            }
            badges.Add(new { key = badge.Key, kind = badge.Kind, goal = badge.Goal, progress = Math.Min(progress, badge.Goal), unlocked = unlockedAt is not null, unlockedAt });
        }
        return new
        {
            xp = total,
            todayXp = events.Where(e => e.Day == today).Sum(e => e.Points),
            weekXp = week.Sum(d => d.xp),
            streak = new { current = streak.Current, best = streak.Best, activeToday = streak.Today },
            week,
            counts = new
            {
                lessons = events.Count(e => e.Kind == ProgressRules.Lesson),
                exams = events.Count(e => e.Kind == ProgressRules.Exam),
                lives = events.Count(e => e.Kind == ProgressRules.Live)
            },
            achievements = badges,
            recent = events.OrderByDescending(e => e.CreatedAt).Take(10).Select(e => new { e.Kind, e.Points, e.SourceId, e.CreatedAt })
        };
    }

    static DateTime? StreakReachedAt(List<XpEvent> events, int goal)
    {
        var days = events.Select(e => e.Day).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        var run = 0;
        DateOnly? previous = null;
        foreach (var key in days)
        {
            var day = DateOnly.ParseExact(key, "yyyy-MM-dd");
            run = previous is not null && previous.Value.AddDays(1) == day ? run + 1 : 1;
            previous = day;
            if (run >= goal) return events.First(e => e.Day == key).CreatedAt;
        }
        return null;
    }
}
