using System.Data;
using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Domain;

namespace Tovi.Api.Services;

public sealed class CommerceWork(ToviDb db, IClock clock, IPaymentGateway gateway)
{
    public async Task<(Subscription Subscription, Payment Payment, string? PaymentUrl)> AssignAsync(
        Guid userId, Guid planId, bool cash, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new RuleException("not_found", "User was not found.", 404);
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken)
            ?? throw new RuleException("not_found", "Plan was not found.", 404);
        var open = await db.Subscriptions.AnyAsync(
            s => s.UserId == userId && s.PlanId == planId &&
                 (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Waiting) &&
                 s.PlanCodesCount - s.CodesIssued > 0,
            cancellationToken);
        RuleException.Require(PlanRules.CanAssign(plan.Audience, user.Type, open));
        var now = clock.UtcNow;
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanId = plan.Id,
            PlanCodesCount = plan.CodesCount,
            CodesIssued = 0,
            CreatedAt = now
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscription.Id,
            AmountIqd = plan.PriceYearly,
            CreatedAt = now
        };

        string? url = null;
        if (cash)
        {
            subscription.Status = SubscriptionStatus.Active;
            subscription.StartsAt = now;
            subscription.EndsAt = PlanRules.EndsAt(now);
            payment.Method = PaymentMethod.Cash;
            payment.Status = PaymentStatus.Succeeded;
        }
        else
        {
            subscription.Status = SubscriptionStatus.Waiting;
            payment.Method = PaymentMethod.Gateway;
            payment.Status = PaymentStatus.Pending;
            url = await gateway.StartAsync(payment.Id, payment.AmountIqd, cancellationToken);
            payment.ProviderReference = payment.Id.ToString();
        }

        db.Subscriptions.Add(subscription);
        db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);
        return (subscription, payment, url);
    }

    public async Task ApplyPaymentAsync(Guid paymentId, bool succeeded, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
            ?? throw new RuleException("not_found", "Payment was not found.", 404);
        if (payment.Method != PaymentMethod.Gateway)
            throw new RuleException("not_gateway", "Cash payments are not confirmed by the gateway.");
        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == payment.SubscriptionId, cancellationToken);
        if (subscription.Status == SubscriptionStatus.Active && payment.Status == PaymentStatus.Succeeded)
            return;
        if (!succeeded)
        {
            payment.Status = PaymentStatus.Failed;
            subscription.Status = SubscriptionStatus.Failed;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var now = clock.UtcNow;
        payment.Status = PaymentStatus.Succeeded;
        subscription.Status = SubscriptionStatus.Active;
        subscription.StartsAt = now;
        subscription.EndsAt = PlanRules.EndsAt(now);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> ExpireAndConsumeAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var rows = await db.Subscriptions.Where(s => s.Status == SubscriptionStatus.Active).ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var row in rows)
        {
            if (row.CodesIssued >= row.PlanCodesCount)
            {
                row.Status = SubscriptionStatus.Consumed;
                changed++;
            }
            else if (row.EndsAt is not null && row.EndsAt <= now)
            {
                row.Status = SubscriptionStatus.Expired;
                changed++;
            }
        }

        if (changed > 0)
            await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    public async Task<int> ReconcilePaymentsAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddMinutes(-2);
        var pending = await db.Payments
            .Where(p => p.Status == PaymentStatus.Pending && p.Method == PaymentMethod.Gateway && p.CreatedAt <= cutoff)
            .ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var payment in pending)
        {
            var remote = await gateway.GetStatusAsync(payment.Id, cancellationToken);
            if (remote is RemotePaymentStatus.Unknown or RemotePaymentStatus.Pending) continue;
            await ApplyPaymentAsync(payment.Id, remote == RemotePaymentStatus.Succeeded, cancellationToken);
            changed++;
        }

        return changed;
    }
}

public sealed class HourWork(ToviDb db, IClock clock)
{
    public HourPackage CreatePackage(string name, int monthlyHours, int validityDays, long priceIqd, ReportLevel level, bool attendanceEnabled, bool exportEnabled, bool isActive)
    {
        RuleException.Require(HourRules.Package(monthlyHours));
        if (validityDays < 1) validityDays = 30;
        var package = new HourPackage
        {
            Id = Guid.NewGuid(),
            Name = name,
            MonthlyHoursLimit = monthlyHours,
            MonthlyMinutesLimit = HourRules.MinutesFor(monthlyHours),
            ValidityDays = validityDays,
            PriceIqd = priceIqd,
            ReportLevel = level,
            AttendanceEnabled = attendanceEnabled,
            ExportEnabled = exportEnabled,
            IsActive = isActive
        };
        db.HourPackages.Add(package);
        return package;
    }

    public async Task<HourSubscription> RedeemPackageAsync(Guid teacherId, string rawCode, CancellationToken cancellationToken)
    {
        var code = await db.HourPackageCodes.FirstOrDefaultAsync(c => c.Code == AccountRules.Digits(rawCode), cancellationToken)
            ?? throw new RuleException("package_code_missing", "This package code does not exist.", 404);
        RuleException.Require(HourRules.CanRedeemPackage(code.Status, code.ExpiresAt, clock.UtcNow));
        var package = await db.HourPackages.FirstAsync(p => p.Id == code.PackageId, cancellationToken);
        if (!package.IsActive)
            throw new RuleException("package_inactive", "This hour package is inactive.");
        var current = await db.HourSubscriptions.Where(s => s.TeacherId == teacherId && s.Active).ToListAsync(cancellationToken);
        foreach (var row in current)
            row.Active = false;
        var now = clock.UtcNow;
        var subscription = new HourSubscription
        {
            Id = Guid.NewGuid(),
            TeacherId = teacherId,
            PackageId = package.Id,
            RemainingMinutes = package.MonthlyMinutesLimit,
            StartsAt = now,
            EndsAt = now.AddDays(package.ValidityDays),
            Active = true
        };
        code.Status = HourCodeStatus.Used;
        db.HourSubscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);
        return subscription;
    }

    public async Task<VirtualClassroom> OpenClassroomAsync(Guid teacherId, string title, CancellationToken cancellationToken)
    {
        var hour = await ActiveHourAsync(teacherId, cancellationToken);
        RuleException.Require(HourRules.CanOpenClassroom(hour is not null, hour?.RemainingMinutes ?? 0, hour?.EndsAt ?? DateTime.MinValue, clock.UtcNow));
        var room = new VirtualClassroom
        {
            Id = Guid.NewGuid(),
            TeacherId = teacherId,
            Title = title,
            Status = ClassroomStatus.Active,
            CreatedAt = clock.UtcNow
        };
        db.Classrooms.Add(room);
        await db.SaveChangesAsync(cancellationToken);
        return room;
    }

    public async Task<ClassSession> ScheduleTeacherAsync(Guid teacherId, Guid classroomId, DateTime startsAt, int? duration, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var room = await db.Classrooms.FirstOrDefaultAsync(c => c.Id == classroomId && c.TeacherId == teacherId, cancellationToken)
            ?? throw new RuleException("not_found", "Classroom was not found.", 404);
        if (room.Status != ClassroomStatus.Active)
            throw new RuleException("classroom_archived", "This classroom is archived.");
        var hour = await ActiveHourAsync(teacherId, cancellationToken);
        var minutes = duration ?? HourRules.DefaultDuration;
        var overlaps = await OverlapsAsync(teacherId, startsAt, minutes, cancellationToken);
        var funded = hour is not null && hour.EndsAt > clock.UtcNow;
        RuleException.Require(HourRules.CanSchedule(duration, hour?.RemainingMinutes ?? 0, overlaps, funded));
        hour!.RemainingMinutes -= minutes;
        var session = NewSession(room.Id, teacherId, null, SessionFunding.TeacherPackage, startsAt, minutes);
        db.SessionsSchedule.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<ClassSession> ScheduleInstituteAsync(Guid instituteId, Guid classroomId, DateTime startsAt, int? duration, CancellationToken cancellationToken)
    {
        if (classroomId == Guid.Empty)
            throw new RuleException("classroom_required", "The classroom id is required.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var room = await db.Classrooms.FirstOrDefaultAsync(c => c.Id == classroomId, cancellationToken)
            ?? throw new RuleException("not_found", "Classroom was not found.", 404);
        var linked = await db.InstituteTeachers.AnyAsync(x => x.InstituteId == instituteId && x.TeacherId == room.TeacherId, cancellationToken);
        if (!linked && room.TeacherId != instituteId)
            throw new RuleException("forbidden", "This classroom is outside the institute.", 403);
        var pool = await db.HourPools.FirstOrDefaultAsync(p => p.InstituteId == instituteId, cancellationToken)
            ?? throw new RuleException("pool_missing", "The institute hour pool has no balance.", 403);
        var minutes = duration ?? HourRules.DefaultDuration;
        var overlaps = await OverlapsAsync(room.TeacherId, startsAt, minutes, cancellationToken);
        RuleException.Require(HourRules.CanSchedule(duration, pool.RemainingMinutes, overlaps, true));
        pool.RemainingMinutes -= minutes;
        var session = NewSession(room.Id, room.TeacherId, instituteId, SessionFunding.InstitutePool, startsAt, minutes);
        db.SessionsSchedule.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return session;
    }

    public async Task CancelAsync(Guid sessionId, Guid? teacherId, Guid? instituteId, CancellationToken cancellationToken)
    {
        var session = await db.SessionsSchedule.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new RuleException("not_found", "Session was not found.", 404);
        if (teacherId is not null && session.TeacherId != teacherId)
            throw new RuleException("forbidden", "This session belongs to another teacher.", 403);
        if (instituteId is not null && session.InstituteId != instituteId)
            throw new RuleException("forbidden", "This session belongs to another institute.", 403);
        var before = session.Funding == SessionFunding.TeacherPackage
            ? await db.HourSubscriptions.Where(s => s.TeacherId == session.TeacherId && s.Active).Select(s => s.RemainingMinutes).FirstOrDefaultAsync(cancellationToken)
            : await db.HourPools.Where(p => p.InstituteId == session.InstituteId).Select(p => p.RemainingMinutes).FirstOrDefaultAsync(cancellationToken);
        session.Status = SessionStatus.Canceled;
        await db.SaveChangesAsync(cancellationToken);
        var after = session.Funding == SessionFunding.TeacherPackage
            ? await db.HourSubscriptions.Where(s => s.TeacherId == session.TeacherId && s.Active).Select(s => s.RemainingMinutes).FirstOrDefaultAsync(cancellationToken)
            : await db.HourPools.Where(p => p.InstituteId == session.InstituteId).Select(p => p.RemainingMinutes).FirstOrDefaultAsync(cancellationToken);
        if (after != before)
            throw new InvalidOperationException("Cancel must not refund minutes.");
    }

    public async Task CreditPoolAsync(Guid instituteId, int minutes, string note, CancellationToken cancellationToken)
    {
        if (minutes < 1)
            throw new RuleException("invalid_minutes", "Minutes must be at least 1.");
        var pool = await db.HourPools.FirstOrDefaultAsync(p => p.InstituteId == instituteId, cancellationToken);
        if (pool is null)
        {
            pool = new InstituteHourPool { InstituteId = instituteId, RemainingMinutes = 0 };
            db.HourPools.Add(pool);
        }

        pool.RemainingMinutes += minutes;
        db.PoolCredits.Add(new PoolCredit
        {
            Id = Guid.NewGuid(),
            InstituteId = instituteId,
            Minutes = minutes,
            Note = note,
            CreatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    async Task<HourSubscription?> ActiveHourAsync(Guid teacherId, CancellationToken cancellationToken) =>
        await db.HourSubscriptions.FirstOrDefaultAsync(
            s => s.TeacherId == teacherId && s.Active && s.EndsAt > clock.UtcNow,
            cancellationToken);

    async Task<bool> OverlapsAsync(Guid teacherId, DateTime startsAt, int minutes, CancellationToken cancellationToken)
    {
        var rows = await db.SessionsSchedule
            .Where(s => s.TeacherId == teacherId && s.Status == SessionStatus.Scheduled)
            .ToListAsync(cancellationToken);
        return rows.Any(s => HourRules.Overlaps(startsAt, minutes, s.StartsAt, s.DurationMinutes));
    }

    ClassSession NewSession(Guid classroomId, Guid teacherId, Guid? instituteId, SessionFunding funding, DateTime startsAt, int minutes) =>
        new()
        {
            Id = Guid.NewGuid(),
            ClassroomId = classroomId,
            TeacherId = teacherId,
            InstituteId = instituteId,
            Funding = funding,
            StartsAt = startsAt,
            DurationMinutes = minutes,
            Status = SessionStatus.Scheduled,
            CreatedAt = clock.UtcNow
        };
}
