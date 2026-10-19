using Microsoft.EntityFrameworkCore;
using Students.Api.Data;
using Students.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Locally Aspire injects ConnectionStrings__StudentsDb for the SQL Server container.
// In Azure the connection string uses "Authentication=Active Directory Default" (managed identity).
builder.Services.AddDbContext<StudentsDbContext>(options => options
    .UseSqlServer(
        builder.Configuration.GetConnectionString("StudentsDb")
            ?? throw new InvalidOperationException("Connection string 'StudentsDb' is not configured."),
        sql => sql.EnableRetryOnFailure())
    .UseAsyncSeeding((ctx, _, ct) => SeedData.SeedAsync(ctx, ct))
    .UseSeeding((ctx, _) => SeedData.SeedAsync(ctx, CancellationToken.None).GetAwaiter().GetResult()));

builder.Services.AddHealthChecks().AddDbContextCheck<StudentsDbContext>("database");
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

if (app.Configuration.GetValue("Database:InitializeOnStartup", true))
{
    // Creates the schema and seeds sample data on first run. For production schemas switch to
    // EF Core migrations: dotnet ef migrations add Initial && dotnet ef database update.
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<StudentsDbContext>();

    // Seeding runs in a transaction, which the retrying execution strategy only allows inside ExecuteAsync.
    await db.Database.CreateExecutionStrategy().ExecuteAsync(() => db.Database.EnsureCreatedAsync());
}

app.UseExceptionHandler();
app.MapOpenApi();
app.MapDefaultEndpoints();
app.MapStudentEndpoints();

app.Run();
