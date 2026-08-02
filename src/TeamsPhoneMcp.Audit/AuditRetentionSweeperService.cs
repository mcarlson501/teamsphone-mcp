using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TeamsPhoneMcp.Audit;

/// <summary>
/// Runs the retention sweeper on startup and then on the configured interval.
/// A sweep failure is logged and retried on the next tick rather than taking the
/// server down — audit hygiene must never block tenant operations.
/// </summary>
public sealed class AuditRetentionSweeperService : BackgroundService
{
    private readonly AuditRetentionSweeper _sweeper;
    private readonly IOptionsMonitor<AuditOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditRetentionSweeperService> _logger;

    public AuditRetentionSweeperService(
        AuditRetentionSweeper sweeper,
        IOptionsMonitor<AuditOptions> options,
        TimeProvider timeProvider,
        ILogger<AuditRetentionSweeperService> logger)
    {
        ArgumentNullException.ThrowIfNull(sweeper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sweeper = sweeper;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _sweeper.SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The audit retention sweep failed; it will be retried on the next interval.");
            }

            var interval = TimeSpan.FromHours(Math.Max(1, _options.CurrentValue.SweepIntervalHours));
            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
