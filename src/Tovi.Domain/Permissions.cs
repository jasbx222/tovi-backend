namespace Tovi.Domain;

/// <summary>
/// Permission catalog assembled from the modules named in the product brief.
/// TODO UNVERIFIED: file 06 was not available on this machine, so these slugs are the brief's
/// can-view / can-create / can-update / can-delete / reset-password / activate pattern.
/// The named legacy gates are fixed in <see cref="AccessMap"/>.
/// </summary>
public static class PermissionCatalog
{
    public static readonly string[] Modules =
    [
        "teachers", "institutes", "students", "employees", "sections", "grades", "subjects",
        "learning-domains", "governorates", "schools", "plans", "subscriptions", "invoices",
        "advertisements", "notifications", "guides", "faq", "parameters", "reports",
        "courses", "chapters", "lessons", "summaries", "exams", "questions", "groups",
        "classrooms", "sessions", "hour-packages", "codes", "meetings", "hour-pool"
    ];

    public static string View(string module) => $"can-view-{module}";
    public static string Create(string module) => $"can-create-{module}";
    public static string Update(string module) => $"can-update-{module}";
    public static string Delete(string module) => $"can-delete-{module}";

    public const string FormatCourse = "can-format-course";
    public const string FormatSummary = "can-format-summary";
    public const string CorrectExams = "can-correct-exams";
    public const string ManageMeeting = "can-manage-meeting";

    public static IReadOnlyList<string> All { get; } = Build();

    public static readonly HashSet<string> Teacher = BuildTeacher();

    public static readonly HashSet<string> Institute = BuildInstitute();

    static List<string> Build()
    {
        var list = new List<string>();
        foreach (var module in Modules)
        {
            list.Add(View(module));
            list.Add(Create(module));
            list.Add(Update(module));
            list.Add(Delete(module));
        }

        foreach (var module in new[] { "teachers", "students", "employees", "institutes" })
        {
            list.Add($"can-reset-password-{module}");
            list.Add($"can-activate-{module}");
        }

        list.Add(FormatCourse);
        list.Add(FormatSummary);
        list.Add(CorrectExams);
        list.Add(ManageMeeting);
        return list;
    }

    static HashSet<string> BuildSet(params string[] modules)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            set.Add(View(module));
            set.Add(Create(module));
            set.Add(Update(module));
            set.Add(Delete(module));
        }

        set.Add(FormatCourse);
        set.Add(FormatSummary);
        set.Add(CorrectExams);
        set.Add(ManageMeeting);
        return set;
    }

    static HashSet<string> BuildTeacher()
    {
        var set = BuildSet(
            "courses", "chapters", "lessons", "summaries", "exams", "questions", "groups",
            "classrooms", "sessions", "codes", "meetings");
        set.Add(View("students"));
        set.Add(View("employees"));
        set.Add(Create("employees"));
        set.Add(Update("employees"));
        set.Add(Delete("employees"));
        set.Add(View("reports"));
        set.Add(View("plans"));
        set.Add(View("subscriptions"));
        set.Add(Create("subscriptions"));
        return set;
    }

    static HashSet<string> BuildInstitute()
    {
        var set = BuildSet(
            "teachers", "courses", "chapters", "lessons", "summaries", "students", "employees",
            "schools", "exams", "questions", "advertisements", "codes", "sessions", "classrooms", "faq");
        set.Add(View("reports"));
        set.Add(View("plans"));
        set.Add(View("subscriptions"));
        set.Add(Create("subscriptions"));
        set.Add(View("hour-pool"));
        set.Add(View("meetings"));
        set.Remove(Create("hour-pool"));
        return set;
    }
}

public static class AccessMap
{
    public const string Institutes = "can-view-institutes";
    public const string Teachers = "can-view-teachers";
    public const string Guides = "can-view-guides";
    public const string Advertisements = "can-view-advertisements";
    public const string Employees = "can-view-employees";
    public const string Students = "can-view-students";
    public const string Exams = "can-view-exams";
    public const string DeleteChapter = "can-delete-chapter";
    public const string Questions = "can-view-questions";
    public const string UpdateQuestions = "can-update-questions";
    public const string DeleteQuestions = "can-delete-questions";
    public const string DeleteCourse = "can-delete-course";
    public const string Invoices = "can-view-invoices";
    public const string Codes = "can-view-codes";
}
