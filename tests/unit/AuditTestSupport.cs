using Microsoft.Extensions.Options;
using TeamsPhoneMcp.Audit;

namespace TeamsPhoneMcp.UnitTests;

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> for tests that need mutable options.</summary>
internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Creates and cleans up a throwaway audit root for filesystem-backed tests.</summary>
internal sealed class TempAuditRoot : IDisposable
{
    public TempAuditRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "teamsphone-audit-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
        Resolver = new AuditPathResolver(Path);
    }

    public string Path { get; }

    public AuditPathResolver Resolver { get; }

    public AuditOptions Options { get; } = new();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder must never fail a test run.
        }
    }
}
