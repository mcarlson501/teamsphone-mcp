using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace TeamsPhoneMcp.Audit;

/// <summary>Composition root for the file-backed audit pipeline (build spec §9).</summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the fail-safe null audit services with the JSONL implementation
    /// and starts the retention sweeper. Called by the host; unit tests keep the
    /// null defaults so they never touch the filesystem.
    /// </summary>
    public static IServiceCollection AddTeamsPhoneAudit(this IServiceCollection services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        services
            .AddOptions<AuditOptions>()
            .BindConfiguration(AuditOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuditOptions>, AuditOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;
            var root = Path.IsPathRooted(options.RootPath)
                ? options.RootPath
                : Path.Combine(environment.ContentRootPath, options.RootPath);
            return new AuditPathResolver(root);
        });

        services.RemoveAll<IAuditSink>();
        services.AddSingleton<IAuditSink, JsonlAuditSink>();
        services.RemoveAll<IAuditSnapshotStore>();
        services.AddSingleton<IAuditSnapshotStore, FileAuditSnapshotStore>();

        services.AddSingleton<AuditRetentionSweeper>();
        services.AddHostedService<AuditRetentionSweeperService>();

        return services;
    }
}
