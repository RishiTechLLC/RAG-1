using System.ComponentModel;
using System.Text.Json.Nodes;
using Mcp.Server.Sources;
using ModelContextProtocol.Server;

namespace Mcp.Server.Tools;

[McpServerToolType]
public sealed class StudentTools(StudentsApiClient students)
{
    [McpServerTool(Name = "search_students", ReadOnly = true), Description("Search students by name, email or student number, optionally filtered by major (e.g. 'Computer Science') and status ('Active', 'On Leave', 'Graduated'). Returns a paged list.")]
    public async Task<JsonNode?> SearchStudents(
        [Description("Free text matched against first name, last name, email and student number. Leave empty to list all.")] string? search = null,
        [Description("Exact major name to filter by.")] string? major = null,
        [Description("Status filter: Active, On Leave or Graduated.")] string? status = null,
        [Description("1-based page number.")] int page = 1,
        [Description("Page size (max 100).")] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await students.SearchStudentsAsync(search, major, status, page, pageSize, cancellationToken);

    [McpServerTool(Name = "get_student", ReadOnly = true), Description("Get one student's profile by numeric id or by student number such as 'S2026001'.")]
    public async Task<JsonNode?> GetStudent(
        [Description("Numeric student id or student number (S2026001).")] string idOrNumber,
        CancellationToken cancellationToken = default)
    {
        var student = int.TryParse(idOrNumber, out var id)
            ? await students.GetStudentAsync(id, cancellationToken)
            : await students.GetStudentByNumberAsync(idOrNumber, cancellationToken);
        return student ?? JsonValue.Create($"No student found for '{idOrNumber}'.");
    }

    [McpServerTool(Name = "get_student_enrollments", ReadOnly = true), Description("List the courses, terms and grades for a student (use the numeric id from search_students/get_student).")]
    public async Task<JsonNode?> GetStudentEnrollments(
        [Description("Numeric student id.")] int studentId,
        CancellationToken cancellationToken = default) =>
        await students.GetEnrollmentsAsync(studentId, cancellationToken);

    [McpServerTool(Name = "list_courses", ReadOnly = true), Description("List the course catalog, optionally for one department.")]
    public async Task<JsonNode?> ListCourses(
        [Description("Department name, e.g. 'Computer Science'.")] string? department = null,
        CancellationToken cancellationToken = default) =>
        await students.GetCoursesAsync(department, cancellationToken);

    [McpServerTool(Name = "get_course_roster", ReadOnly = true), Description("List the students enrolled in a course by course code (e.g. 'CS340'), optionally for a term like '2025-FA' or '2026-SP'.")]
    public async Task<JsonNode?> GetCourseRoster(
        [Description("Course code, e.g. CS340.")] string courseCode,
        [Description("Term code, e.g. 2026-SP.")] string? term = null,
        CancellationToken cancellationToken = default) =>
        await students.GetCourseRosterAsync(courseCode, term, cancellationToken);

    [McpServerTool(Name = "get_major_statistics", ReadOnly = true), Description("Student counts and average GPA per major.")]
    public async Task<JsonNode?> GetMajorStatistics(CancellationToken cancellationToken = default) =>
        await students.GetMajorStatsAsync(cancellationToken);
}
