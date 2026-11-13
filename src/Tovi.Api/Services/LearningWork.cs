using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Domain;

namespace Tovi.Api.Services;

public sealed class LearningWork(ToviDb db, IClock clock)
{
    public async Task FormatCourseAsync(Guid courseId, CancellationToken cancellationToken)
    {
        var lessonIds = await (
            from lesson in db.Lessons
            join chapter in db.Chapters on lesson.ChapterId equals chapter.Id
            where chapter.CourseId == courseId
            select lesson.Id).ToListAsync(cancellationToken);
        var progress = await db.LessonProgress.Where(p => lessonIds.Contains(p.LessonId)).ToListAsync(cancellationToken);
        var enrollments = await db.Enrollments.Where(e => e.TargetType == AccessTarget.Course && e.TargetId == courseId).ToListAsync(cancellationToken);
        db.LessonProgress.RemoveRange(progress);
        db.Enrollments.RemoveRange(enrollments);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task FormatSummaryAsync(Guid summaryId, CancellationToken cancellationToken)
    {
        var links = await db.Enrollments.Where(e => e.TargetType == AccessTarget.Summary && e.TargetId == summaryId).ToListAsync(cancellationToken);
        db.Enrollments.RemoveRange(links);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReorderLessonsAsync(Guid chapterId, IReadOnlyList<Guid> lessonIds, CancellationToken cancellationToken)
    {
        var lessons = await db.Lessons.Where(l => l.ChapterId == chapterId).ToListAsync(cancellationToken);
        if (lessons.Count != lessonIds.Count || lessons.Select(l => l.Id).Except(lessonIds).Any())
            throw new RuleException("invalid_order", "The lesson list must contain each lesson of the chapter once.");
        for (var i = 0; i < lessonIds.Count; i++)
            lessons.First(l => l.Id == lessonIds[i]).SortOrder = i;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ExamAttempt> SubmitAsync(Guid studentId, Guid examId, IReadOnlyList<(Guid QuestionId, string Response)> responses, CancellationToken cancellationToken)
    {
        var exam = await db.Exams.FirstOrDefaultAsync(e => e.Id == examId, cancellationToken)
            ?? throw new RuleException("not_found", "Exam was not found.", 404);
        if (exam.Status == ExamStatus.Completed)
            throw new RuleException("exam_closed", "This exam is completed.");
        var enrolled = await db.Enrollments.AnyAsync(
            e => e.StudentId == studentId && e.TargetType == AccessTarget.Exam && e.TargetId == examId,
            cancellationToken);
        if (!enrolled)
            throw new RuleException("not_enrolled", "Redeem an exam code before taking it.", 403);
        if (await db.Attempts.AnyAsync(a => a.ExamId == examId && a.StudentId == studentId, cancellationToken))
            throw new RuleException("attempt_exists", "This exam already has an attempt.");

        var questions = await db.Questions.Where(q => q.ExamId == examId).ToListAsync(cancellationToken);
        if (responses.Count != questions.Count || questions.Select(q => q.Id).Except(responses.Select(r => r.QuestionId)).Any())
            throw new RuleException("incomplete_attempt", "Answer every question once.");

        var options = await db.QuestionOptions.Where(o => questions.Select(q => q.Id).Contains(o.QuestionId)).ToListAsync(cancellationToken);
        var attempt = new ExamAttempt
        {
            Id = Guid.NewGuid(),
            ExamId = examId,
            StudentId = studentId,
            SubmittedAt = clock.UtcNow
        };
        var total = 0;
        foreach (var response in responses)
        {
            var question = questions.First(q => q.Id == response.QuestionId);
            var answer = new ExamAnswer
            {
                Id = Guid.NewGuid(),
                AttemptId = attempt.Id,
                QuestionId = question.Id,
                Response = response.Response
            };
            if (question.Type == QuestionType.OpenEnded)
            {
                answer.Corrected = false;
                answer.Mark = null;
            }
            else if (question.Type == QuestionType.TrueFalse)
            {
                var correct = bool.TryParse(response.Response, out var value) && question.CorrectBool == value;
                answer.Corrected = true;
                answer.Mark = correct ? question.Mark : 0;
                total += answer.Mark.Value;
            }
            else
            {
                var chosen = options.FirstOrDefault(o => o.QuestionId == question.Id && o.Id.ToString() == response.Response);
                answer.Corrected = true;
                answer.Mark = chosen?.IsCorrect == true ? question.Mark : 0;
                total += answer.Mark.Value;
            }

            db.Answers.Add(answer);
        }

        attempt.TotalMark = total;
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    public async Task CorrectAsync(Actor actor, Guid answerId, int awarded, CancellationToken cancellationToken)
    {
        if (actor.User.Type == UserType.Employee)
            actor.Require(PermissionCatalog.CorrectExams);
        else if (actor.Portal != UserType.Teacher && actor.Portal != UserType.Admin)
            throw new RuleException("forbidden", "Only a teacher or a permitted assistant can correct an answer.", 403);

        var answer = await db.Answers.FirstOrDefaultAsync(a => a.Id == answerId, cancellationToken)
            ?? throw new RuleException("not_found", "Answer was not found.", 404);
        var question = await db.Questions.FirstAsync(q => q.Id == answer.QuestionId, cancellationToken);
        var attempt = await db.Attempts.FirstAsync(a => a.Id == answer.AttemptId, cancellationToken);
        var exam = await db.Exams.FirstAsync(e => e.Id == attempt.ExamId, cancellationToken);
        if (actor.Portal == UserType.Teacher && exam.TeacherId != actor.Owner.Id)
            throw new RuleException("forbidden", "This exam belongs to another teacher.", 403);
        if (question.Type != QuestionType.OpenEnded)
            throw new RuleException("auto_scored", "Only open-ended answers are corrected by hand.");
        RuleException.Require(ExamRules.CanCorrect(answer.Corrected, question.Mark, awarded));
        db.Entry(answer).State = EntityState.Detached;
        var updated = await db.Answers
            .Where(a => a.Id == answerId && !a.Corrected)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.Corrected, true)
                .SetProperty(a => a.Mark, awarded), cancellationToken);
        if (updated != 1)
            throw new RuleException("already_corrected", "This answer was already corrected.", 409);
        attempt.TotalMark += awarded;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> TransitionExamsAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var exams = await db.Exams.Where(e => e.Status != ExamStatus.Completed).ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var exam in exams)
        {
            var groupStudents = await (
                from link in db.ExamGroups
                join member in db.GroupMembers on link.GroupId equals member.GroupId
                where link.ExamId == exam.Id
                select member.StudentId).ToListAsync(cancellationToken);
            var enrolled = await db.Enrollments
                .Where(e => e.TargetType == AccessTarget.Exam && e.TargetId == exam.Id)
                .Select(e => e.StudentId)
                .ToListAsync(cancellationToken);
            var assigned = groupStudents.Concat(enrolled).Distinct().ToList();
            var attempts = await db.Attempts.Where(a => a.ExamId == exam.Id).ToListAsync(cancellationToken);
            var questionCount = await db.Questions.CountAsync(q => q.ExamId == exam.Id, cancellationToken);
            var attemptIds = attempts.Select(a => a.Id).ToList();
            var answers = await db.Answers.Where(a => attemptIds.Contains(a.AttemptId)).ToListAsync(cancellationToken);
            var allAssigned = assigned.Count > 0 && assigned.All(id => attempts.Any(a => a.StudentId == id));
            var fully = attempts.Count > 0 && attempts.All(attempt =>
            {
                var rows = answers.Where(a => a.AttemptId == attempt.Id).ToList();
                return questionCount > 0 && rows.Count == questionCount && rows.All(a => a.Corrected);
            });
            var next = ExamRules.NextStatus(exam.Status, allAssigned, exam.EndDate <= now, attempts.Count, fully);
            if (next is null || next == exam.Status) continue;
            exam.Status = next.Value;
            changed++;
        }

        if (changed > 0)
            await db.SaveChangesAsync(cancellationToken);
        return changed;
    }
}
