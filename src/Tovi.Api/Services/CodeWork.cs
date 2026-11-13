using System.Data;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Domain;

namespace Tovi.Api.Services;

public sealed class CodeWork(ToviDb db, IClock clock)
{
    public async Task<int> BalanceAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var subs = await db.Subscriptions
            .Where(s => s.UserId == ownerId && s.Status == SubscriptionStatus.Active && s.EndsAt > now)
            .Select(s => new { s.EndsAt, s.PlanCodesCount, s.CodesIssued })
            .ToListAsync(cancellationToken);
        var user = await db.Users.FirstAsync(u => u.Id == ownerId, cancellationToken);
        var fallbackIssued = await db.Codes.CountAsync(c => c.OwnerUserId == ownerId && c.SubscriptionId == null, cancellationToken);
        return CodeQuota.ShownBalance(
            subs.Select(s => (s.EndsAt!.Value, s.PlanCodesCount, s.CodesIssued)),
            user.ActivationCodeLimit,
            fallbackIssued,
            now);
    }

    public async Task<IReadOnlyList<ActivationCode>> GenerateAsync(Actor actor, AccessTarget target, Guid targetId, int count, CancellationToken cancellationToken)
    {
        RuleException.Require(CodeQuota.Batch(count));
        await Guards.EnsureTrialAsync(db, actor, clock, cancellationToken);
        await OwnAsync(actor, target, targetId, cancellationToken);
        var now = clock.UtcNow;
        var ownerId = actor.Owner.Id;

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var subs = await db.Subscriptions
            .Where(s => s.UserId == ownerId && s.Status == SubscriptionStatus.Active && s.EndsAt > now)
            .ToListAsync(cancellationToken);
        var projection = subs.Select(s => (s.Id, s.StartsAt!.Value, s.EndsAt!.Value, s.PlanCodesCount, s.CodesIssued));
        var activeBalance = subs.Where(s => s.EndsAt > now).Sum(s => Math.Max(0, CodeQuota.Available(s.PlanCodesCount, s.CodesIssued)));
        Allocation allocation;
        if (activeBalance == 0 && subs.All(s => CodeQuota.Available(s.PlanCodesCount, s.CodesIssued) <= 0 || s.EndsAt <= now))
        {
            var user = await db.Users.FirstAsync(u => u.Id == ownerId, cancellationToken);
            var issued = await db.Codes.CountAsync(c => c.OwnerUserId == ownerId && c.SubscriptionId == null, cancellationToken);
            allocation = CodeQuota.AllocateFallback(user.ActivationCodeLimit, issued, count);
        }
        else
        {
            allocation = CodeQuota.Allocate(projection, count, now);
        }

        if (!allocation.Ok)
            throw new RuleException(allocation.Code!, allocation.Message!, 409, allocation.Remaining);

        var created = new List<ActivationCode>();
        foreach (var slice in allocation.Slices)
        {
            var subscription = subs.First(s => s.Id == slice.SubscriptionId);
            for (var i = 0; i < slice.Count; i++)
            {
                var code = await NewCodeAsync(ownerId, target, targetId, subscription.Id, slice.ExpiresAt, now, cancellationToken);
                subscription.CodesIssued += 1;
                created.Add(code);
            }
        }

        if (allocation.Slices.Count == 0)
        {
            for (var i = 0; i < count; i++)
                created.Add(await NewCodeAsync(ownerId, target, targetId, null, null, now, cancellationToken));
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<byte[]> ExportAsync(Actor actor, AccessTarget target, Guid targetId, CancellationToken cancellationToken)
    {
        await OwnAsync(actor, target, targetId, cancellationToken);
        var codes = await db.Codes
            .Where(c => c.OwnerUserId == actor.Owner.Id && c.TargetType == target && c.TargetId == targetId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
        var now = clock.UtcNow;
        var active = await db.Subscriptions.AnyAsync(
            s => s.UserId == actor.Owner.Id && s.Status == SubscriptionStatus.Active && s.EndsAt > now,
            cancellationToken);
        var institute = actor.Portal == UserType.InstituteManager;
        RuleException.Require(ExportRules.CanExport(
            institute,
            actor.Owner.TeacherApproved,
            actor.Owner.ExportCodesLimit,
            codes.Count,
            active));

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("codes");
        sheet.Cell(1, 1).Value = "code";
        sheet.Cell(1, 2).Value = "status";
        sheet.Cell(1, 3).Value = "expires_at";
        for (var i = 0; i < codes.Count; i++)
        {
            sheet.Cell(i + 2, 1).Value = codes[i].Code;
            sheet.Cell(i + 2, 2).Value = codes[i].Status.ToString();
            sheet.Cell(i + 2, 3).Value = codes[i].ExpiresAt?.ToString("O") ?? "";
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<ActivationCode> RedeemAsync(Guid studentId, string rawCode, CancellationToken cancellationToken)
    {
        var normalized = AccountRules.Digits(rawCode);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var code = await db.Codes.FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);
        RuleException.Require(CodeRules.CanRedeem(code?.Status, code?.ExpiresAt, clock.UtcNow));
        var now = clock.UtcNow;
        var codeId = code!.Id;
        var targetType = code.TargetType;
        var targetId = code.TargetId;
        db.Entry(code).State = EntityState.Detached;
        var updated = await db.Codes
            .Where(c => c.Id == codeId && c.Status == CodeStatus.Inactive)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, CodeStatus.Active)
                .SetProperty(c => c.RedeemedAt, now)
                .SetProperty(c => c.RedeemedByStudentId, studentId), cancellationToken);
        if (updated != 1)
            throw new RuleException("code_already_active", "This code is already active.");
        code.Status = CodeStatus.Active;
        code.RedeemedAt = now;
        code.RedeemedByStudentId = studentId;
        code.TargetType = targetType;
        code.TargetId = targetId;
        var enrolled = await db.Enrollments.AnyAsync(
            e => e.StudentId == studentId && e.TargetType == code.TargetType && e.TargetId == code.TargetId,
            cancellationToken);
        if (!enrolled)
        {
            db.Enrollments.Add(new Enrollment
            {
                Id = Guid.NewGuid(),
                StudentId = studentId,
                TargetType = code.TargetType,
                TargetId = code.TargetId,
                CodeId = code.Id,
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return code;
    }

    public async Task CancelAsync(Actor actor, Guid codeId, CancellationToken cancellationToken)
    {
        var code = await db.Codes.FirstOrDefaultAsync(c => c.Id == codeId, cancellationToken)
            ?? throw new RuleException("code_missing", "This code does not exist.", 404);
        if (actor.Portal != UserType.Admin && code.OwnerUserId != actor.Owner.Id)
            throw new RuleException("forbidden", "You cannot cancel a code you do not own.", 403);
        var issued = await db.Subscriptions.Where(s => s.Id == code.SubscriptionId).Select(s => s.CodesIssued).FirstOrDefaultAsync(cancellationToken);
        RuleException.Require(CodeRules.CanCancel(code.Status));
        code.Status = CodeStatus.Canceled;
        await db.SaveChangesAsync(cancellationToken);
        if (code.SubscriptionId is not null)
        {
            var after = await db.Subscriptions.Where(s => s.Id == code.SubscriptionId).Select(s => s.CodesIssued).FirstAsync(cancellationToken);
            if (after != issued)
                throw new InvalidOperationException("Cancel must not change codes_issued.");
        }
    }

    async Task<ActivationCode> NewCodeAsync(Guid ownerId, AccessTarget target, Guid targetId, Guid? subscriptionId, DateTime? expiresAt, DateTime now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var value = CodeNumbers.New16();
            if (await db.Codes.AnyAsync(c => c.Code == value, cancellationToken)) continue;
            var code = new ActivationCode
            {
                Id = Guid.NewGuid(),
                Code = value,
                Status = CodeStatus.Inactive,
                TargetType = target,
                TargetId = targetId,
                SubscriptionId = subscriptionId,
                OwnerUserId = ownerId,
                ExpiresAt = expiresAt,
                CreatedAt = now
            };
            db.Codes.Add(code);
            return code;
        }

        throw new RuleException("code_collision", "Could not allocate a unique code.");
    }

    public async Task OwnAsync(Actor actor, AccessTarget target, Guid targetId, CancellationToken cancellationToken)
    {
        if (actor.Portal == UserType.Admin) return;
        Guid teacherId;
        Guid? instituteId = null;
        switch (target)
        {
            case AccessTarget.Course:
                var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == targetId, cancellationToken)
                    ?? throw new RuleException("not_found", "Course was not found.", 404);
                teacherId = course.TeacherId;
                instituteId = course.InstituteId;
                break;
            case AccessTarget.Summary:
                var summary = await db.Summaries.FirstOrDefaultAsync(c => c.Id == targetId, cancellationToken)
                    ?? throw new RuleException("not_found", "Summary was not found.", 404);
                teacherId = summary.TeacherId;
                break;
            case AccessTarget.Exam:
                var exam = await db.Exams.FirstOrDefaultAsync(c => c.Id == targetId, cancellationToken)
                    ?? throw new RuleException("not_found", "Exam was not found.", 404);
                teacherId = exam.TeacherId;
                break;
            default:
                var room = await db.Classrooms.FirstOrDefaultAsync(c => c.Id == targetId, cancellationToken)
                    ?? throw new RuleException("not_found", "Classroom was not found.", 404);
                teacherId = room.TeacherId;
                break;
        }

        if (actor.Portal == UserType.Teacher && teacherId != actor.Owner.Id)
            throw new RuleException("forbidden", "A teacher cannot generate codes for another teacher's content.", 403);
        if (actor.Portal == UserType.InstituteManager)
        {
            var linked = await db.InstituteTeachers.AnyAsync(x => x.InstituteId == actor.Owner.Id && x.TeacherId == teacherId, cancellationToken);
            if (!linked && instituteId != actor.Owner.Id)
                throw new RuleException("forbidden", "This content is outside the institute.", 403);
        }
    }
}

public static class Guards
{
    public static async Task EnsureTrialAsync(ToviDb db, Actor actor, IClock clock, CancellationToken cancellationToken)
    {
        if (actor.Portal is not (UserType.Teacher or UserType.InstituteManager)) return;
        var now = clock.UtcNow;
        var qualifying = await db.Subscriptions.AnyAsync(
            s => s.UserId == actor.Owner.Id && s.Status == SubscriptionStatus.Active && s.EndsAt > now && s.CodesIssued < s.PlanCodesCount,
            cancellationToken);
        if (!TrialRules.WritesAllowed(actor.Owner.TrialEndsAt, now, qualifying))
            throw new RuleException("trial_ended", "The trial has ended. An active code plan with remaining codes is required.", 403);
    }

    public static void Plain(object? body) => PlainText.Reject(body);
}
