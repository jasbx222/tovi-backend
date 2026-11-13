using Microsoft.EntityFrameworkCore;
using Tovi.Domain;

namespace Tovi.Api.Data;

public sealed class ToviDb : DbContext
{
    public ToviDb(DbContextOptions<ToviDb> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<PermissionGrant> PermissionGrants => Set<PermissionGrant>();
    public DbSet<PermissionGroup> PermissionGroups => Set<PermissionGroup>();
    public DbSet<PermissionGroupItem> PermissionGroupItems => Set<PermissionGroupItem>();
    public DbSet<UserPermissionGroup> UserPermissionGroups => Set<UserPermissionGroup>();
    public DbSet<LearningDomain> LearningDomains => Set<LearningDomain>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<EducationalGrade> Grades => Set<EducationalGrade>();
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<Governorate> Governorates => Set<Governorate>();
    public DbSet<School> Schools => Set<School>();
    public DbSet<InstituteTeacher> InstituteTeachers => Set<InstituteTeacher>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<Summary> Summaries => Set<Summary>();
    public DbSet<LessonProgress> LessonProgress => Set<LessonProgress>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<StudentGroup> Groups => Set<StudentGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Exam> Exams => Set<Exam>();
    public DbSet<ExamGroup> ExamGroups => Set<ExamGroup>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();
    public DbSet<ExamAttempt> Attempts => Set<ExamAttempt>();
    public DbSet<ExamAnswer> Answers => Set<ExamAnswer>();
    public DbSet<ActivationCode> Codes => Set<ActivationCode>();
    public DbSet<CodePlan> Plans => Set<CodePlan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<HourPackage> HourPackages => Set<HourPackage>();
    public DbSet<HourPackageCode> HourPackageCodes => Set<HourPackageCode>();
    public DbSet<HourSubscription> HourSubscriptions => Set<HourSubscription>();
    public DbSet<VirtualClassroom> Classrooms => Set<VirtualClassroom>();
    public DbSet<ClassSession> SessionsSchedule => Set<ClassSession>();
    public DbSet<InstituteHourPool> HourPools => Set<InstituteHourPool>();
    public DbSet<PoolCredit> PoolCredits => Set<PoolCredit>();
    public DbSet<Advertisement> Advertisements => Set<Advertisement>();
    public DbSet<FaqItem> Faqs => Set<FaqItem>();
    public DbSet<Guide> Guides => Set<Guide>();
    public DbSet<SystemParameter> Parameters => Set<SystemParameter>();
    public DbSet<ScheduledNotification> ScheduledNotifications => Set<ScheduledNotification>();
    public DbSet<UserNotification> Notifications => Set<UserNotification>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<TeacherFollow> Follows => Set<TeacherFollow>();
    public DbSet<CourseLike> Likes => Set<CourseLike>();
    public DbSet<SavedCourse> SavedCourses => Set<SavedCourse>();
    public DbSet<OtpChallenge> Otps => Set<OtpChallenge>();
    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<SessionPresence> Presences => Set<SessionPresence>();
    public DbSet<LiveReaction> Reactions => Set<LiveReaction>();
    public DbSet<LiveQuestion> LiveQuestions => Set<LiveQuestion>();
    public DbSet<LiveQuestionVote> LiveQuestionVotes => Set<LiveQuestionVote>();
    public DbSet<LivePoll> Polls => Set<LivePoll>();
    public DbSet<LivePollVote> PollVotes => Set<LivePollVote>();
    public DbSet<XpEvent> XpEvents => Set<XpEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasIndex(x => x.Phone).IsUnique();
        modelBuilder.Entity<User>().Property(x => x.Phone).HasMaxLength(20);
        modelBuilder.Entity<ActivationCode>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<ActivationCode>().Property(x => x.Code).HasMaxLength(16);
        modelBuilder.Entity<HourPackageCode>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<CodePlan>().HasIndex(x => x.Title).IsUnique();
        modelBuilder.Entity<SystemParameter>().HasKey(x => x.Key);
        modelBuilder.Entity<InstituteHourPool>().HasKey(x => x.InstituteId);
        modelBuilder.Entity<DeviceToken>().HasIndex(x => x.Token).IsUnique();
        modelBuilder.Entity<Enrollment>().HasIndex(x => new { x.StudentId, x.TargetType, x.TargetId }).IsUnique();
        modelBuilder.Entity<LessonProgress>().HasIndex(x => new { x.StudentId, x.LessonId }).IsUnique();
        modelBuilder.Entity<InstituteTeacher>().HasIndex(x => new { x.InstituteId, x.TeacherId }).IsUnique();
        modelBuilder.Entity<TeacherFollow>().HasIndex(x => new { x.StudentId, x.TeacherId }).IsUnique();
        modelBuilder.Entity<CourseLike>().HasIndex(x => new { x.StudentId, x.CourseId }).IsUnique();
        modelBuilder.Entity<SavedCourse>().HasIndex(x => new { x.StudentId, x.CourseId }).IsUnique();
        modelBuilder.Entity<GroupMember>().HasIndex(x => new { x.GroupId, x.StudentId }).IsUnique();
        modelBuilder.Entity<ExamGroup>().HasIndex(x => new { x.ExamId, x.GroupId }).IsUnique();
        modelBuilder.Entity<ClassSession>().Property(x => x.Title).HasMaxLength(120);
        modelBuilder.Entity<SessionPresence>().HasIndex(x => new { x.SessionId, x.UserId }).IsUnique();
        modelBuilder.Entity<SessionPresence>().Property(x => x.Identity).HasMaxLength(80);
        modelBuilder.Entity<SessionPresence>().Property(x => x.Name).HasMaxLength(120);
        modelBuilder.Entity<LiveReaction>().HasIndex(x => new { x.SessionId, x.UserId, x.CreatedAt });
        modelBuilder.Entity<LiveReaction>().Property(x => x.Kind).HasMaxLength(20);
        modelBuilder.Entity<LiveQuestion>().HasIndex(x => x.SessionId);
        modelBuilder.Entity<LiveQuestion>().Property(x => x.Text).HasMaxLength(300);
        modelBuilder.Entity<LiveQuestion>().Property(x => x.AuthorName).HasMaxLength(120);
        modelBuilder.Entity<LiveQuestionVote>().HasIndex(x => new { x.QuestionId, x.UserId }).IsUnique();
        modelBuilder.Entity<LivePoll>().HasIndex(x => x.SessionId);
        modelBuilder.Entity<LivePoll>().Property(x => x.Question).HasMaxLength(200);
        modelBuilder.Entity<LivePoll>().Property(x => x.Options).HasMaxLength(500);
        modelBuilder.Entity<LivePollVote>().HasIndex(x => new { x.PollId, x.UserId }).IsUnique();
        modelBuilder.Entity<XpEvent>().HasIndex(x => new { x.StudentId, x.Kind, x.SourceId }).IsUnique();
        modelBuilder.Entity<XpEvent>().Property(x => x.Kind).HasMaxLength(20);
        modelBuilder.Entity<XpEvent>().Property(x => x.Day).HasMaxLength(10);
        modelBuilder.Entity<Recording>().Property(x => x.EgressId).HasMaxLength(80);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType.IsEnum)
                    property.SetMaxLength(40);
            }
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        configurationBuilder.Properties<UserType>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<UserStatus>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<EmployeeStyle>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<Gender>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<StudentKind>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<CodeStatus>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<AccessTarget>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<PlanAudience>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<SubscriptionStatus>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<PaymentMethod>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<PaymentStatus>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<HourCodeStatus>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<ReportLevel>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<ClassroomStatus>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<SessionFunding>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<SessionStatus>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<QuestionType>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<ExamStatus>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<NotificationType>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<NotificationAudience>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<RepeatInterval>().HaveConversion<string>().HaveMaxLength(40);
    }
}

sealed class UtcDateTimeConverter : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter() : base(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc)) { }
}

sealed class NullableUtcDateTimeConverter : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>
{
    public NullableUtcDateTimeConverter() : base(
        value => value,
        value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value) { }
}
