using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TeamsPhoneMcp.Core.Sessions;

internal sealed class TenantSessionCleanupService(
    TenantSessionManager sessionManager,
    IOptions<TenantSessionOptions> options,
    TimeProvider timeProvider,
    ILogger<TenantSessionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.Value.CleanupInterval, timeProvider, stoppingToken);
                await sessionManager.EvictIdleSessionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                logger.LogError("Tenant session idle cleanup failed.");
            }
        }
    }
}