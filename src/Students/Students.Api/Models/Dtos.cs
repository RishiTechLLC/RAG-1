namespace Students.Api.Models;

public sealed record StudentDto(
    int Id, string StudentNumber, string FirstName, string LastName, string Email,
    DateOnly DateOfBirth, string Major, int Year, decimal Gpa, DateOnly EnrollmentDate, string Status)
{
    public static StudentDto From(Student s) =>
        new(s.Id, s.StudentNumber, s.FirstName, s.LastName, s.Email, s.DateOfBirth, s.Major, s.Year, s.Gpa, s.EnrollmentDate, s.Status);
}

public sealed record StudentUpsertDto(
    string StudentNumber, string FirstName, string LastName, string Email,
    DateOnly DateOfBirth, string Major, int Year, decimal Gpa, DateOnly EnrollmentDate, string Status = "Active");

public sealed record CourseDto(int Id, string Code, string Title, string Department, int Credits, string Instructor)
{
    public static CourseDto From(Course c) => new(c.Id, c.Code, c.Title, c.Department, c.Credits, c.Instructor);
}

public sealed record EnrollmentDto(string CourseCode, string CourseTitle, int Credits, string Term, string? Grade);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record MajorStatsDto(string Major, int Students, decimal AverageGpa);
