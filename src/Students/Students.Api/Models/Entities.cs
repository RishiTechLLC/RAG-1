namespace Students.Api.Models;

public sealed class Student
{
    public int Id { get; set; }
    public string StudentNumber { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public string Major { get; set; } = string.Empty;
    public int Year { get; set; }
    public decimal Gpa { get; set; }
    public DateOnly EnrollmentDate { get; set; }
    public string Status { get; set; } = "Active";
    public List<Enrollment> Enrollments { get; set; } = [];
}

public sealed class Course
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public int Credits { get; set; }
    public string Instructor { get; set; } = string.Empty;
    public List<Enrollment> Enrollments { get; set; } = [];
}

public sealed class Enrollment
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public string Term { get; set; } = string.Empty;
    public string? Grade { get; set; }
}
