using LanShare.Core;

namespace LanShare.Server;

/// <summary>Composition root used by the WPF shell. Implemented by the LAN server project.</summary>
public static class LanShareServer
{
    /// <summary>Creates the runtime with the default data directory (%LOCALAPPDATA%\LanShare).</summary>
    public static Task<ILanShareRuntime> CreateRuntimeAsync(CancellationToken ct = default)
        => CreateRuntimeAsync(null, ct);

    /// <summary>Creates the runtime. A non-empty <paramref name="dataDirectory"/> isolates settings and storage (tests, portable mode).
    /// The returned runtime is not listening yet; call <see cref="IServerHost.StartAsync"/>.</summary>
    public static async Task<ILanShareRuntime> CreateRuntimeAsync(string? dataDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await LanShareRuntime.CreateAsync(dataDirectory, ct).ConfigureAwait(false);
    }

    public static string Version =>
        typeof(LanShareServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
