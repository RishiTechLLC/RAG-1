using Microsoft.EntityFrameworkCore;
using Students.Api.Models;

namespace Students.Api.Data;

public sealed class StudentsDbContext(DbContextOptions<StudentsDbContext> options) : DbContext(options)
{
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Student>(e =>
        {
            e.HasIndex(s => s.StudentNumber).IsUnique();
            e.HasIndex(s => s.Email).IsUnique();
            e.Property(s => s.StudentNumber).HasMaxLength(20);
            e.Property(s => s.FirstName).HasMaxLength(100);
            e.Property(s => s.LastName).HasMaxLength(100);
            e.Property(s => s.Email).HasMaxLength(200);
            e.Property(s => s.Major).HasMaxLength(100);
            e.Property(s => s.Status).HasMaxLength(20);
            e.Property(s => s.Gpa).HasPrecision(3, 2);
        });

        modelBuilder.Entity<Course>(e =>
        {
            e.HasIndex(c => c.Code).IsUnique();
            e.Property(c => c.Code).HasMaxLength(20);
            e.Property(c => c.Title).HasMaxLength(200);
            e.Property(c => c.Department).HasMaxLength(100);
            e.Property(c => c.Instructor).HasMaxLength(100);
        });

        modelBuilder.Entity<Enrollment>(e =>
        {
            e.HasIndex(x => new { x.StudentId, x.CourseId, x.Term }).IsUnique();
            e.Property(x => x.Term).HasMaxLength(20);
            e.Property(x => x.Grade).HasMaxLength(2);
        });
    }
}
