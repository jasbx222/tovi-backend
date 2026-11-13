namespace Tovi.Domain;

public enum UserType
{
    Admin,
    Teacher,
    Student,
    Employee,
    InstituteManager
}

public enum UserStatus
{
    Active,
    Disabled
}

public enum EmployeeStyle
{
    Assistant,
    Staff
}

public enum Gender
{
    Unspecified,
    Male,
    Female
}

public enum StudentKind
{
    Internal,
    External
}

public enum CodeStatus
{
    Inactive,
    Active,
    Canceled
}

public enum AccessTarget
{
    Course,
    Summary,
    Exam,
    VirtualClassroom
}

public enum PlanAudience
{
    ForTeacher,
    ForInstitute
}

public enum SubscriptionStatus
{
    Waiting,
    Active,
    Expired,
    Canceled,
    Failed,
    Consumed
}

public enum PaymentMethod
{
    Cash,
    Gateway
}

public enum PaymentStatus
{
    Pending,
    Succeeded,
    Failed
}

public enum HourCodeStatus
{
    Unused,
    Used,
    Expired
}

public enum ReportLevel
{
    Basic,
    Detailed,
    Advanced
}

public enum ClassroomStatus
{
    Active,
    Archived
}

public enum SessionFunding
{
    TeacherPackage,
    InstitutePool
}

public enum SessionStatus
{
    Scheduled,
    Canceled
}

public enum QuestionType
{
    OpenEnded,
    MultipleChoice,
    TrueFalse
}

public enum ExamStatus
{
    New,
    UnderCorrection,
    Completed
}

public enum NotificationType
{
    Info,
    Ads,
    Warning
}

public enum NotificationAudience
{
    Teacher,
    StudentsWithoutActiveCode,
    EducationalGrade,
    Gender,
    All,
    Section
}

public enum RepeatInterval
{
    Daily,
    Weekly,
    Monthly,
    Quarterly,
    Yearly
}
