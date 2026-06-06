namespace Plexus.Sidecar.Routing;

// Keeps model metadata current without code changes: loads on startup, then
// refreshes from models.dev daily.
public sealed class RegistryRefreshService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private readonly ModelRegistry _registry;

    public RegistryRefreshService(ModelRegistry registry) => _registry = registry;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _registry.EnsureLoadedAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                await _registry.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
