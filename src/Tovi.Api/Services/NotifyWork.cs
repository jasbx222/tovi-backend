using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Domain;

namespace Tovi.Api.Services;

public sealed class NotifyWork(ToviDb db, IClock clock, IPushSender push, ILogger<NotifyWork> logger)
{
    public async Task<PushResult> SendAsync(ScheduledNotification notice, CancellationToken cancellationToken)
    {
        var users = await AudienceAsync(notice, cancellationToken);
        foreach (var user in users)
        {
            db.Notifications.Add(new UserNotification
            {
                Id = Guid.NewGuid(),
                UserId = user,
                Title = notice.Title,
                Body = notice.Body,
                Type = notice.Type,
                CreatedAt = clock.UtcNow
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        var ids = users.ToList();
        var tokens = await db.DeviceTokens.Where(t => ids.Contains(t.UserId)).Select(t => t.Token).ToListAsync(cancellationToken);
        var result = await push.SendAsync(tokens, notice.Title, notice.Body, cancellationToken);
        logger.LogInformation("Notification {Id} stored for {Users} users. Push {Reason}. Tokens {Tokens}", notice.Id, ids.Count, result.Reason, tokens.Count);
        return result;
    }

    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var due = await db.ScheduledNotifications.Where(n => n.Enabled && n.NextRunAt <= now).ToListAsync(cancellationToken);
        foreach (var notice in due)
        {
            await SendAsync(notice, cancellationToken);
            if (notice.Repeat is null)
            {
                notice.Enabled = false;
            }
            else
            {
                notice.NextRunAt = notice.Repeat switch
                {
                    RepeatInterval.Daily => notice.NextRunAt.AddDays(1),
                    RepeatInterval.Weekly => notice.NextRunAt.AddDays(7),
                    RepeatInterval.Monthly => notice.NextRunAt.AddMonths(1),
                    RepeatInterval.Quarterly => notice.NextRunAt.AddMonths(3),
                    _ => notice.NextRunAt.AddYears(1)
                };
            }
        }

        if (due.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
        return due.Count;
    }

    public async Task<int> TrialNoticesAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var users = await db.Users.Where(u => u.TrialEndsAt != null && u.TrialEndsAt <= now && !u.TrialNotified && u.Status == UserStatus.Active).ToListAsync(cancellationToken);
        foreach (var user in users)
        {
            db.Notifications.Add(new UserNotification
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Title = "انتهت الفترة التجريبية",
                Body = "الكتابة تحتاج اشتراك أكواد ما زال ساريًا وفيه رصيد.",
                Type = NotificationType.Warning,
                CreatedAt = now
            });
            user.TrialNotified = true;
        }

        if (users.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
        return users.Count;
    }

    public async Task<int> SessionRemindersAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var sessions = await db.SessionsSchedule
            .Where(s => s.Status == SessionStatus.Scheduled && !s.ReminderSent && s.StartsAt <= now)
            .ToListAsync(cancellationToken);
        var sent = 0;
        foreach (var session in sessions.Where(s => now < s.StartsAt.AddMinutes(s.DurationMinutes)))
        {
            var students = await db.Enrollments
                .Where(e => e.TargetType == AccessTarget.VirtualClassroom && e.TargetId == session.ClassroomId)
                .Select(e => e.StudentId)
                .ToListAsync(cancellationToken);
            foreach (var student in students.Distinct())
            {
                db.Notifications.Add(new UserNotification
                {
                    Id = Guid.NewGuid(),
                    UserId = student,
                    Title = "بدأت الجلسة",
                    Body = "يمكنك الدخول إلى الصف خلال نافذة الوقت المحددة.",
                    Type = NotificationType.Info,
                    CreatedAt = now,
                    TargetType = "session",
                    TargetId = session.Id
                });
            }

            var tokens = await db.DeviceTokens.Where(t => students.Contains(t.UserId)).Select(t => t.Token).ToListAsync(cancellationToken);
            var pushResult = await push.SendAsync(tokens, "بدأت الجلسة", "يمكنك الدخول إلى الصف خلال نافذة الوقت المحددة.", cancellationToken);
            logger.LogInformation("Session reminder {SessionId} stored. Push {Reason}", session.Id, pushResult.Reason);
            session.ReminderSent = true;
            sent++;
        }

        if (sent > 0)
            await db.SaveChangesAsync(cancellationToken);
        return sent;
    }

    async Task<List<Guid>> AudienceAsync(ScheduledNotification notice, CancellationToken cancellationToken)
    {
        IQueryable<User> query = db.Users.Where(u => u.Status == UserStatus.Active);
        query = notice.Audience switch
        {
            NotificationAudience.Teacher => query.Where(u => u.Type == UserType.Teacher),
            NotificationAudience.All => query,
            NotificationAudience.EducationalGrade => query.Where(u => u.Type == UserType.Student && u.GradeId == notice.AudienceRefId),
            NotificationAudience.Section => query.Where(u => u.Type == UserType.Student && u.SectionId == notice.AudienceRefId),
            NotificationAudience.Gender => query.Where(u => u.Gender == notice.Gender),
            _ => query.Where(u => u.Type == UserType.Student)
        };

        var ids = await query.Select(u => u.Id).ToListAsync(cancellationToken);
        if (notice.Audience != NotificationAudience.StudentsWithoutActiveCode)
            return ids;

        var withCode = await db.Codes
            .Where(c => c.Status == CodeStatus.Active && c.RedeemedByStudentId != null)
            .Select(c => c.RedeemedByStudentId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        return ids.Except(withCode).ToList();
    }
}
