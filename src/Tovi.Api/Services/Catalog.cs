using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Endpoints;
using Tovi.Domain;

namespace Tovi.Api.Services;

public record TeacherCard(Guid Id, string Name, string FirstName, string? PhotoUrl, string? Subject, List<string> Subjects, string? Institute, int Followers, int Courses, int FreeLessons, bool Following);

public record CourseCard(Guid Id, string Title, string? Description, string? CoverUrl, Guid TeacherId, string TeacherName, string? TeacherPhotoUrl, string? Subject, string? Grade, string? Section, int Chapters, int Lessons, int FreeLessons);

/// <summary>Card projections shared by the student home, lists and profiles.</summary>
public static class Catalog
{
    public static async Task<List<TeacherCard>> TeacherCardsAsync(ToviDb db, Guid studentId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var list = ids.ToList();
        var users = await db.Users.Where(u => list.Contains(u.Id)).ToListAsync(ct);
        var followers = await db.Follows.Where(f => list.Contains(f.TeacherId)).GroupBy(f => f.TeacherId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var mine = await db.Follows.Where(f => f.StudentId == studentId && list.Contains(f.TeacherId)).Select(f => f.TeacherId).ToListAsync(ct);
        var courses = await db.Courses.Where(c => list.Contains(c.TeacherId)).Select(c => new { c.Id, c.TeacherId, c.SubjectId }).ToListAsync(ct);
        var subjectIds = courses.Where(c => c.SubjectId != null).Select(c => c.SubjectId!.Value).Distinct().ToList();
        var subjects = await db.Subjects.Where(s => subjectIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var free = await (
            from l in db.Lessons
            join ch in db.Chapters on l.ChapterId equals ch.Id
            join c in db.Courses on ch.CourseId equals c.Id
            where l.IsFree && list.Contains(c.TeacherId)
            group l by c.TeacherId into g
            select new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var links = await (
            from link in db.InstituteTeachers
            join i in db.Users on link.InstituteId equals i.Id
            where list.Contains(link.TeacherId)
            select new { link.TeacherId, i.InstituteName }).ToListAsync(ct);
        return list.Select(id => users.FirstOrDefault(u => u.Id == id)).Where(u => u is not null).Select(u =>
        {
            var own = courses.Where(c => c.TeacherId == u!.Id).ToList();
            var names = own.Where(c => c.SubjectId != null && subjects.ContainsKey(c.SubjectId.Value))
                .GroupBy(c => subjects[c.SubjectId!.Value]).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            return new TeacherCard(
                u!.Id,
                $"{u.FirstName} {u.LastName}".Trim(),
                u.FirstName,
                Media.Url(u.PhotoFileId),
                names.FirstOrDefault(),
                names,
                links.FirstOrDefault(l => l.TeacherId == u.Id)?.InstituteName,
                followers.GetValueOrDefault(u.Id),
                own.Count,
                free.GetValueOrDefault(u.Id),
                mine.Contains(u.Id));
        }).ToList();
    }

    public static async Task<List<CourseCard>> CourseCardsAsync(ToviDb db, IReadOnlyCollection<Course> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(c => c.Id).ToList();
        var teacherIds = rows.Select(c => c.TeacherId).Distinct().ToList();
        var teachers = await db.Users.Where(u => teacherIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var subjectIds = rows.Where(c => c.SubjectId != null).Select(c => c.SubjectId!.Value).Distinct().ToList();
        var gradeIds = rows.Where(c => c.GradeId != null).Select(c => c.GradeId!.Value).Distinct().ToList();
        var sectionIds = rows.Where(c => c.SectionId != null).Select(c => c.SectionId!.Value).Distinct().ToList();
        var subjects = await db.Subjects.Where(s => subjectIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var grades = await db.Grades.Where(s => gradeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var sections = await db.Sections.Where(s => sectionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var chapters = await db.Chapters.Where(c => ids.Contains(c.CourseId)).Select(c => new { c.Id, c.CourseId }).ToListAsync(ct);
        var chapterIds = chapters.Select(c => c.Id).ToList();
        var lessons = await db.Lessons.Where(l => chapterIds.Contains(l.ChapterId)).Select(l => new { l.ChapterId, l.IsFree }).ToListAsync(ct);
        string? Name(Dictionary<Guid, string> map, Guid? id) => id is not null && map.TryGetValue(id.Value, out var name) ? name : null;
        return rows.Select(c =>
        {
            var own = chapters.Where(ch => ch.CourseId == c.Id).Select(ch => ch.Id).ToHashSet();
            var inCourse = lessons.Where(l => own.Contains(l.ChapterId)).ToList();
            teachers.TryGetValue(c.TeacherId, out var teacher);
            return new CourseCard(
                c.Id, c.Title, c.Description, Media.Url(c.CoverFileId), c.TeacherId,
                teacher is null ? "" : $"{teacher.FirstName} {teacher.LastName}".Trim(),
                Media.Url(teacher?.PhotoFileId),
                Name(subjects, c.SubjectId), Name(grades, c.GradeId), Name(sections, c.SectionId),
                own.Count, inCourse.Count, inCourse.Count(l => l.IsFree));
        }).ToList();
    }
}
