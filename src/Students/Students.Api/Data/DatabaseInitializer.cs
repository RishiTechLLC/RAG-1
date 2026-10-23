using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Students.Api.Data;

/// <summary>
/// Prepares the database on startup. Works against a fresh container, an existing on-prem SQL Server
/// database that already holds other tables, or Azure SQL:
///   1. creates the database if it does not exist (needs CREATE DATABASE permission; otherwise create it first),
///   2. creates the Students/Courses/Enrollments tables only if they are missing,
///   3. inserts sample data only if the Students table is empty.
/// EnsureCreated() is not used because it does nothing when the database already contains any table.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(StudentsDbContext db, bool seedSampleData, ILogger logger, CancellationToken cancellationToken = default)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();

        // The retrying execution strategy requires multi-step work to run inside ExecuteAsync.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (!await creator.ExistsAsync(cancellationToken))
            {
                logger.LogInformation("Database does not exist; creating it");
                await creator.CreateAsync(cancellationToken);
            }

            if (!await TableExistsAsync(db, "Students", cancellationToken))
            {
                logger.LogInformation("Creating tables Students, Courses and Enrollments");
                await creator.CreateTablesAsync(cancellationToken);
            }

            if (seedSampleData)
            {
                await SeedData.SeedAsync(db, cancellationToken);
            }
        });
    }

    private static async Task<bool> TableExistsAsync(StudentsDbContext db, string table, CancellationToken cancellationToken)
    {
        var schema = db.Model.FindEntityType(typeof(Models.Student))?.GetSchema() ?? "dbo";
        var count = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = {schema} AND TABLE_NAME = {table}")
            .SingleAsync(cancellationToken);
        return count > 0;
    }
}
