namespace Students.Api.Data;

/// <summary>
/// Runs <see cref="DatabaseInitializer"/> in the background and retries until SQL Server is reachable,
/// so the API starts even when the database is not yet available (e.g. the on-prem Hybrid Connection
/// Manager is not connected yet). Until it succeeds, /health reports the database as unhealthy.
/// </summary>
public sealed class DatabaseInitializerService(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<DatabaseInitializerService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Database:InitializeOnStartup", true))
        {
            return;
        }

        var seed = configuration.GetValue("Database:SeedSampleData", true);
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                await DatabaseInitializer.InitializeAsync(
                    scope.ServiceProvider.GetRequiredService<StudentsDbContext>(), seed, logger, stoppingToken);
                logger.LogInformation("Database ready");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Database not reachable yet (attempt {Attempt}); retrying in {Interval}", attempt, RetryInterval);
                await Task.Delay(RetryInterval, stoppingToken);
            }
        }
    }
}
