using System.Text.Json.Serialization;

namespace Tovi.Domain;

public class User
{
    public Guid Id { get; set; }
    public UserType Type { get; set; }
    public UserStatus Status { get; set; } = UserStatus.Active;
    public string Phone { get; set; } = "";
    public string? Phone2 { get; set; }
    public string? Email { get; set; }
    [JsonIgnore]
    public string PasswordHash { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? InstituteName { get; set; }
    public string? Address { get; set; }
    public string? Description { get; set; }
    public Gender Gender { get; set; } = Gender.Unspecified;
    public StudentKind? StudentKind { get; set; }
    public Guid? GradeId { get; set; }
    public Guid? SectionId { get; set; }
    public Guid? GovernorateId { get; set; }
    public Guid? SchoolId { get; set; }
    public bool TeacherApproved { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public bool TrialNotified { get; set; }
    public int? ActivationCodeLimit { get; set; }
    public int? ExportCodesLimit { get; set; }
    public Guid? EmployerUserId { get; set; }
    public EmployeeStyle? EmployeeStyle { get; set; }
    public Guid? LogoFileId { get; set; }
    /// <summary>Public profile photo shown to students on teacher cards and the teacher profile.</summary>
    public Guid? PhotoFileId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PermissionGrant
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Slug { get; set; } = "";
}

public class PermissionGroup
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = "";
}

public class PermissionGroupItem
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public string Slug { get; set; } = "";
}

public class UserPermissionGroup
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid GroupId { get; set; }
}

public interface INamed
{
    Guid Id { get; set; }
    string Name { get; set; }
}

public class LearningDomain : INamed
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class Section : INamed
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class EducationalGrade : INamed
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class Subject : INamed
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class Governorate : INamed
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class School
{
    public Guid Id { get; set; }
    public Guid InstituteId { get; set; }
    public string Name { get; set; } = "";
}

public class InstituteTeacher
{
    public Guid Id { get; set; }
    public Guid InstituteId { get; set; }
    public Guid TeacherId { get; set; }
}

public class Course
{
    public Guid Id { get; set; }
    public Guid TeacherId { get; set; }
    public Guid? InstituteId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public Guid? LearningDomainId { get; set; }
    public Guid? SectionId { get; set; }
    public Guid? GradeId { get; set; }
    public Guid? SubjectId { get; set; }
    /// <summary>Public cover image shown on course cards and the course page.</summary>
    public Guid? CoverFileId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Chapter
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public string Title { get; set; } = "";
    public int SortOrder { get; set; }
}

public class Lesson
{
    public Guid Id { get; set; }
    public Guid ChapterId { get; set; }
    public string Title { get; set; } = "";
    public int SortOrder { get; set; }
    public string? VideoReference { get; set; }
    /// <summary>A free lesson plays for any signed-in student, without a course code.</summary>
    public bool IsFree { get; set; }
}

public class Summary
{
    public Guid Id { get; set; }
    public Guid TeacherId { get; set; }
    public Guid? CourseId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}

public class LessonProgress
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public Guid LessonId { get; set; }
    public DateTime CompletedAt { get; set; }
}

public class Enrollment
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public AccessTarget TargetType { get; set; }
    public Guid TargetId { get; set; }
    public Guid CodeId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class StudentGroup
{
    public Guid Id { get; set; }
    public Guid TeacherId { get; set; }
    public string Name { get; set; } = "";
}

public class GroupMember
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid StudentId { get; set; }
}

public class Exam
{
    public Guid Id { get; set; }
    public Guid TeacherId { get; set; }
    public Guid? CourseId { get; set; }
    public Guid? ChapterId { get; set; }
    public Guid? LessonId { get; set; }
    public string Title { get; set; } = "";
    public int Mark { get; set; }
    public ExamStatus Status { get; set; } = ExamStatus.New;
    public DateTime EndDate { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ExamGroup
{
    public Guid Id { get; set; }
    public Guid ExamId { get; set; }
    public Guid GroupId { get; set; }
}

public class Question
{
    public Guid Id { get; set; }
    public Guid ExamId { get; set; }
    public QuestionType Type { get; set; }
    public string Text { get; set; } = "";
    public int Mark { get; set; }
    public bool? CorrectBool { get; set; }
    public int SortOrder { get; set; }
}

public class QuestionOption
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public string Text { get; set; } = "";
    public bool IsCorrect { get; set; }
}

public class ExamAttempt
{
    public Guid Id { get; set; }
    public Guid ExamId { get; set; }
    public Guid StudentId { get; set; }
    public int TotalMark { get; set; }
    public DateTime SubmittedAt { get; set; }
}

public class ExamAnswer
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public Guid QuestionId { get; set; }
    public string Response { get; set; } = "";
    public int? Mark { get; set; }
    public bool Corrected { get; set; }
}

public class ActivationCode
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public CodeStatus Status { get; set; } = CodeStatus.Inactive;
    public AccessTarget TargetType { get; set; }
    public Guid TargetId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public Guid OwnerUserId { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public Guid? RedeemedByStudentId { get; set; }
}

public class CodePlan
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public PlanAudience Audience { get; set; }
    public long PriceYearly { get; set; }
    public int CodesCount { get; set; }
}

public class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid PlanId { get; set; }
    public SubscriptionStatus Status { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public int PlanCodesCount { get; set; }
    public int CodesIssued { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Payment
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public long AmountIqd { get; set; }
    public PaymentMethod Method { get; set; }
    public PaymentStatus Status { get; set; }
    public string? ProviderReference { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Invoice
{
    public Guid Id { get; set; }
    public Guid PartyUserId { get; set; }
    public long AmountIqd { get; set; }
    public int ActivatedCodesCount { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class HourPackage
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int MonthlyHoursLimit { get; set; }
    public int MonthlyMinutesLimit { get; set; }
    public int ValidityDays { get; set; } = 30;
    public long PriceIqd { get; set; }
    public ReportLevel ReportLevel { get; set; } = ReportLevel.Basic;
    public bool AttendanceEnabled { get; set; } = true;
    public bool ExportEnabled { get; set; }
    public bool IsActive { get; set; } = true;
}

public class HourPackageCode
{
    public Guid Id { get; set; }
    public Guid PackageId { get; set; }
    public string Code { get; set; } = "";
    public HourCodeStatus Status { get; set; } = HourCodeStatus.Unused;
    public DateTime? ExpiresAt { get; set; }
}

public class HourSubscription
{
    public Guid Id { get; set; }
    public Guid TeacherId { get; set; }
    public Guid PackageId { get; set; }
    public int RemainingMinutes { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public bool Active { get; set; }
}

public class VirtualClassroom
{
    public Guid Id { get; set; }
    public Guid TeacherId { get; set; }
    public string Title { get; set; } = "";
    public ClassroomStatus Status { get; set; } = ClassroomStatus.Active;
    public DateTime CreatedAt { get; set; }
}

public class ClassSession
{
    public Guid Id { get; set; }
    public Guid ClassroomId { get; set; }
    public Guid TeacherId { get; set; }
    public Guid? InstituteId { get; set; }
    public SessionFunding Funding { get; set; }
    public DateTime StartsAt { get; set; }
    public int DurationMinutes { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Scheduled;
    public string? Title { get; set; }
    public bool ReminderSent { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class InstituteHourPool
{
    public Guid InstituteId { get; set; }
    public int RemainingMinutes { get; set; }
}

public class PoolCredit
{
    public Guid Id { get; set; }
    public Guid InstituteId { get; set; }
    public int Minutes { get; set; }
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class Advertisement
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? LinkUrl { get; set; }
    public Guid? ImageFileId { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public class FaqItem
{
    public Guid Id { get; set; }
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public int SortOrder { get; set; }
}

public class Guide
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}

public class SystemParameter
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class ScheduledNotification
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public NotificationType Type { get; set; }
    public NotificationAudience Audience { get; set; }
    public Guid? AudienceRefId { get; set; }
    public Gender? Gender { get; set; }
    public DateTime NextRunAt { get; set; }
    public RepeatInterval? Repeat { get; set; }
    public bool Enabled { get; set; } = true;
}

public class UserNotification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public NotificationType Type { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool Read { get; set; }
    /// <summary>What the notification opens in the app: session, course, exam, summary, or empty.</summary>
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public DateTime? ReadAt { get; set; }
}

public class DeviceToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Token { get; set; } = "";
    public string Platform { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class StoredFile
{
    public Guid Id { get; set; }
    public string OriginalName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool Public { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class TeacherFollow
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public Guid TeacherId { get; set; }
}

public class CourseLike
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public Guid CourseId { get; set; }
}

public class SavedCourse
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public Guid CourseId { get; set; }
}

public class OtpChallenge
{
    public Guid Id { get; set; }
    public string Phone { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public bool Consumed { get; set; }
}

public class Recording
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string Status { get; set; } = "";
    public string? PlaybackPath { get; set; }
    public string? EgressId { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SessionPresence
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public string Identity { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Host { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? JoinedLiveAt { get; set; }
    public int LiveSeconds { get; set; }
    public bool Attended { get; set; }
}

public class LiveReaction
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class LiveQuestion
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid AuthorId { get; set; }
    public string AuthorName { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public bool Hidden { get; set; }
}

public class LiveQuestionVote
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public Guid UserId { get; set; }
}

public class LivePoll
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid CreatedById { get; set; }
    public string Question { get; set; } = "";
    public string Options { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public class LivePollVote
{
    public Guid Id { get; set; }
    public Guid PollId { get; set; }
    public Guid UserId { get; set; }
    public int Option { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class XpEvent
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public string Kind { get; set; } = "";
    public Guid SourceId { get; set; }
    public int Points { get; set; }
    public string Day { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
