using Microsoft.EntityFrameworkCore;
using Students.Api.Models;

namespace Students.Api.Data;

/// <summary>Deterministic sample data so the MCP server and agents have something to query.</summary>
public static class SeedData
{
    private static readonly string[] FirstNames = ["Ava", "Liam", "Noah", "Emma", "Olivia", "Ethan", "Mia", "Lucas", "Sophia", "Arjun", "Priya", "Chen", "Fatima", "Diego", "Yuki", "Omar", "Zara", "Mateo", "Aisha", "Leo"];
    private static readonly string[] LastNames = ["Smith", "Patel", "Garcia", "Kim", "Nguyen", "Johnson", "Brown", "Chen", "Khan", "Rossi", "Silva", "Müller", "Okafor", "Tanaka", "Lopez"];
    private static readonly string[] Majors = ["Computer Science", "Data Science", "Mathematics", "Physics", "Business", "Biology", "Mechanical Engineering"];
    private static readonly string[] Grades = ["A", "A", "A-", "B+", "B", "B", "B-", "C+", "C"];
    private static readonly string[] Terms = ["2025-FA", "2026-SP"];

    public static async Task SeedAsync(DbContext context, CancellationToken cancellationToken)
    {
        var db = (StudentsDbContext)context;
        if (await db.Students.AnyAsync(cancellationToken))
        {
            return;
        }

        var courses = new List<Course>
        {
            new() { Code = "CS101", Title = "Introduction to Programming", Department = "Computer Science", Credits = 4, Instructor = "Dr. Ada Lovelace" },
            new() { Code = "CS201", Title = "Data Structures and Algorithms", Department = "Computer Science", Credits = 4, Instructor = "Dr. Alan Turing" },
            new() { Code = "CS340", Title = "Cloud Computing on Azure", Department = "Computer Science", Credits = 3, Instructor = "Dr. Grace Hopper" },
            new() { Code = "DS210", Title = "Machine Learning Foundations", Department = "Data Science", Credits = 3, Instructor = "Dr. Andrew Ng" },
            new() { Code = "DS330", Title = "Retrieval-Augmented Generation", Department = "Data Science", Credits = 3, Instructor = "Dr. Fei-Fei Li" },
            new() { Code = "MATH150", Title = "Calculus I", Department = "Mathematics", Credits = 4, Instructor = "Dr. Emmy Noether" },
            new() { Code = "MATH220", Title = "Linear Algebra", Department = "Mathematics", Credits = 3, Instructor = "Dr. Carl Gauss" },
            new() { Code = "PHYS101", Title = "Classical Mechanics", Department = "Physics", Credits = 4, Instructor = "Dr. Richard Feynman" },
            new() { Code = "BUS200", Title = "Principles of Management", Department = "Business", Credits = 3, Instructor = "Dr. Peter Drucker" },
            new() { Code = "BIO110", Title = "Cell Biology", Department = "Biology", Credits = 4, Instructor = "Dr. Rosalind Franklin" },
        };
        db.Courses.AddRange(courses);

        var random = new Random(42);
        var students = new List<Student>();
        for (var i = 0; i < 60; i++)
        {
            var first = FirstNames[i % FirstNames.Length];
            var last = LastNames[(i * 7) % LastNames.Length];
            var year = 1 + (i % 4);
            var enrolled = new DateOnly(2026 - year, 9, 1);
            var student = new Student
            {
                StudentNumber = $"S{2026_000 + i + 1}",
                FirstName = first,
                LastName = last,
                Email = $"{first.ToLowerInvariant()}.{last.ToLowerInvariant()}{i + 1}@university.edu".Replace("ü", "u"),
                DateOfBirth = enrolled.AddYears(-18).AddDays(-random.Next(0, 700)),
                Major = Majors[i % Majors.Length],
                Year = year,
                Gpa = Math.Round(2.2m + (decimal)random.NextDouble() * 1.8m, 2),
                EnrollmentDate = enrolled,
                Status = i % 17 == 0 ? "On Leave" : i % 23 == 0 ? "Graduated" : "Active",
            };

            foreach (var course in courses.OrderBy(_ => random.Next()).Take(3 + random.Next(0, 3)))
            {
                var term = Terms[random.Next(Terms.Length)];
                student.Enrollments.Add(new Enrollment
                {
                    Course = course,
                    Term = term,
                    Grade = term == "2026-SP" ? null : Grades[random.Next(Grades.Length)],
                });
            }

            students.Add(student);
        }

        db.Students.AddRange(students);
        await db.SaveChangesAsync(cancellationToken);
    }
}
