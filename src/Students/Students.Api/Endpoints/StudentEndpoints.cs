using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Students.Api.Data;
using Students.Api.Models;

namespace Students.Api.Endpoints;

public static class StudentEndpoints
{
    public static IEndpointRouteBuilder MapStudentEndpoints(this IEndpointRouteBuilder app)
    {
        var students = app.MapGroup("/api/students").WithTags("Students");

        students.MapGet("/", async (StudentsDbContext db, string? search, string? major, string? status, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = db.Students.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(s =>
                    s.FirstName.Contains(search) || s.LastName.Contains(search) ||
                    s.Email.Contains(search) || s.StudentNumber.Contains(search));
            }

            if (!string.IsNullOrWhiteSpace(major))
            {
                query = query.Where(s => s.Major == major);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(s => s.Status == status);
            }

            var total = await query.CountAsync(ct);
            var items = await query
                .OrderBy(s => s.LastName).ThenBy(s => s.FirstName)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(s => StudentDto.From(s))
                .ToListAsync(ct);

            return new PagedResult<StudentDto>(items, page, pageSize, total);
        }).WithSummary("Search students by name, email or student number; filter by major and status.");

        students.MapGet("/{id:int}", async Task<Results<Ok<StudentDto>, NotFound>> (int id, StudentsDbContext db, CancellationToken ct) =>
            await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) is { } s
                ? TypedResults.Ok(StudentDto.From(s))
                : TypedResults.NotFound());

        students.MapGet("/by-number/{studentNumber}", async Task<Results<Ok<StudentDto>, NotFound>> (string studentNumber, StudentsDbContext db, CancellationToken ct) =>
            await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.StudentNumber == studentNumber, ct) is { } s
                ? TypedResults.Ok(StudentDto.From(s))
                : TypedResults.NotFound());

        students.MapGet("/{id:int}/enrollments", async (int id, StudentsDbContext db, CancellationToken ct) =>
            await db.Enrollments.AsNoTracking()
                .Where(e => e.StudentId == id)
                .OrderBy(e => e.Term).ThenBy(e => e.Course.Code)
                .Select(e => new EnrollmentDto(e.Course.Code, e.Course.Title, e.Course.Credits, e.Term, e.Grade))
                .ToListAsync(ct))
            .WithSummary("Courses, terms and grades for a student.");

        students.MapPost("/", async (StudentUpsertDto dto, StudentsDbContext db, CancellationToken ct) =>
        {
            var student = new Student();
            Apply(dto, student);
            db.Students.Add(student);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/api/students/{student.Id}", StudentDto.From(student));
        });

        students.MapPut("/{id:int}", async Task<Results<Ok<StudentDto>, NotFound>> (int id, StudentUpsertDto dto, StudentsDbContext db, CancellationToken ct) =>
        {
            var student = await db.Students.FindAsync([id], ct);
            if (student is null)
            {
                return TypedResults.NotFound();
            }

            Apply(dto, student);
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(StudentDto.From(student));
        });

        students.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, StudentsDbContext db, CancellationToken ct) =>
            await db.Students.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0
                ? TypedResults.NoContent()
                : TypedResults.NotFound());

        var courses = app.MapGroup("/api/courses").WithTags("Courses");

        courses.MapGet("/", async (StudentsDbContext db, string? department, CancellationToken ct) =>
            await db.Courses.AsNoTracking()
                .Where(c => department == null || c.Department == department)
                .OrderBy(c => c.Code)
                .Select(c => CourseDto.From(c))
                .ToListAsync(ct));

        courses.MapGet("/{code}/students", async (string code, StudentsDbContext db, string? term, CancellationToken ct) =>
            await db.Enrollments.AsNoTracking()
                .Where(e => e.Course.Code == code && (term == null || e.Term == term))
                .Select(e => StudentDto.From(e.Student))
                .ToListAsync(ct))
            .WithSummary("Students enrolled in a course, optionally for one term (e.g. 2026-SP).");

        app.MapGet("/api/stats/majors", async (StudentsDbContext db, CancellationToken ct) =>
            (await db.Students.AsNoTracking()
                .GroupBy(s => s.Major)
                .Select(g => new { Major = g.Key, Students = g.Count(), AverageGpa = g.Average(s => s.Gpa) })
                .OrderByDescending(m => m.Students)
                .ToListAsync(ct))
                .Select(m => new MajorStatsDto(m.Major, m.Students, Math.Round(m.AverageGpa, 2))))
            .WithTags("Stats")
            .WithSummary("Student count and average GPA per major.");

        return app;
    }

    private static void Apply(StudentUpsertDto dto, Student s)
    {
        s.StudentNumber = dto.StudentNumber;
        s.FirstName = dto.FirstName;
        s.LastName = dto.LastName;
        s.Email = dto.Email;
        s.DateOfBirth = dto.DateOfBirth;
        s.Major = dto.Major;
        s.Year = dto.Year;
        s.Gpa = dto.Gpa;
        s.EnrollmentDate = dto.EnrollmentDate;
        s.Status = dto.Status;
    }
}
