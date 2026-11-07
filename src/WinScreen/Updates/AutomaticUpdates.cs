using System.Reflection;
using Velopack;
using Velopack.Locators;
using WinScreen.Settings;

namespace WinScreen.Updates;

/// <summary>Facts about this copy of WinScreen.</summary>
internal static class AppInfo
{
    /// <summary>The version people see, without the build details added after a "+".</summary>
    internal static string Version
    {
        get
        {
            string? version = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            return string.IsNullOrEmpty(version) ? "?" : version.Split('+')[0];
        }
    }
}

internal interface IUpdateSession : IDisposable
{
    Task<bool> Download(CancellationToken cancellation);
    Task ApplyOnExit(CancellationToken cancellation);
}

/// <summary>One check at a time. Disabling invalidates the entire session, including downloaded updates.</summary>
internal sealed class AutomaticUpdates(Func<CancellationToken, IUpdateSession?> createSession, Action<string> status)
    : IDisposable
{
    private CancellationTokenSource? _enabled;
    private IUpdateSession? _pending;
    private bool _busy;
    private bool _disposed;
    private bool _lastCheckFailed;

    internal void SetEnabled(bool enabled)
    {
        if (_disposed || enabled == (_enabled is not null))
            return;
        if (enabled)
            _enabled = new CancellationTokenSource();
        else
        {
            _enabled?.Cancel();
            _enabled?.Dispose();
            _enabled = null;
            _pending?.Dispose();
            _pending = null;
        }
    }

    internal async Task Check()
    {
        if (_disposed || _enabled is null || _busy || _pending is not null)
            return;
        var generation = _enabled;
        _busy = true;
        _lastCheckFailed = false;
        IUpdateSession? session = null;
        try
        {
            session = createSession(generation.Token);
            if (session is null)
                return;
            if (await session.Download(generation.Token) && _enabled == generation &&
                !generation.IsCancellationRequested)
            {
                _pending = session;
                session = null;
                status(UiText.T("Update ready; it will install when WinScreen exits."));
            }
        }
        catch (OperationCanceledException) when (generation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _lastCheckFailed = true;
            if (_enabled == generation && !_disposed)
                status(UiText.T("Update check failed. The current version will keep running."));
        }
        finally
        {
            session?.Dispose();
            _busy = false;
        }
    }

    /// <summary>Checks at the person's request, and says how it went.</summary>
    internal async Task<string> CheckNow()
    {
        if (_busy)
            return UiText.T("Already checking for updates.");
        await Check();
        if (_pending is not null)
            return UiText.T("Update ready; it will install when WinScreen exits.");
        return _lastCheckFailed
            ? UiText.T("Update check failed. The current version will keep running.")
            : UiText.T("WinScreen is up to date.");
    }

    internal async Task ApplyOnExit()
    {
        if (!_disposed && _enabled is not null && _pending is not null)
        {
            try
            {
                await _pending.ApplyOnExit(_enabled.Token);
            }
            catch (Exception)
            {
                /* Never prevent window restoration or shutdown if an update fails. */
            }
        }
    }

    public void Dispose()
    {
        SetEnabled(false);
        _disposed = true;
    }
}

internal sealed class VelopackUpdateSession : IUpdateSession
{
    internal static bool IsConfigured => typeof(VelopackUpdateSession).Assembly
                                             .GetCustomAttributes<AssemblyMetadataAttribute>().Any(metadata =>
                                                 metadata.Key == "UpdateRepository" &&
                                                 !string.IsNullOrWhiteSpace(metadata.Value)) &&
                                         typeof(VelopackUpdateSession).Assembly.GetManifestResourceNames()
                                             .Contains("WinScreen.UpdatePublicKey");

    /// <summary>True for a copy put in place by the installer, the only kind that can update itself.</summary>
    internal static bool IsInstalled
    {
        get
        {
            string folder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(Path.GetFileName(folder), "current", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(Path.Combine(Path.GetDirectoryName(folder) ?? "", "Update.exe"));
        }
    }

    private readonly UpdateTransport _transport;
    private readonly UpdateManager _manager;
    private VelopackAsset? _pending;

    private VelopackUpdateSession(string repository, string publicKey, CancellationToken cancellation)
    {
        _transport = new UpdateTransport(repository);
        _manager = new UpdateManager(new SignedUpdateSource(repository, publicKey, _transport, cancellation),
            new UpdateOptions { AllowVersionDowngrade = false, ExplicitChannel = "win" });
    }

    internal static IUpdateSession? Create(CancellationToken cancellation)
    {
        var assembly = typeof(VelopackUpdateSession).Assembly;
        string? repository = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(metadata => metadata.Key == "UpdateRepository")?.Value;
        using Stream? key = assembly.GetManifestResourceStream("WinScreen.UpdatePublicKey");
        if (string.IsNullOrWhiteSpace(repository) || key is null)
            return null;
        using var reader = new StreamReader(key);
        string publicKey = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(publicKey))
            return null;
        return new VelopackUpdateSession(repository, publicKey, cancellation);
    }

    public async Task<bool> Download(CancellationToken cancellation)
    {
        if (!_manager.IsInstalled)
            return false;
        var update = await _manager.CheckForUpdatesAsync();
        cancellation.ThrowIfCancellationRequested();
        if (update is null)
            return false;
        VelopackAsset asset = update;
        UpdateSecurity.ValidateAsset(asset);
        if (asset.Version <= _manager.CurrentVersion)
            throw new InvalidDataException("Update must be newer than the installed version.");
        await _manager.DownloadUpdatesAsync(update, cancelToken: cancellation);
        await UpdateSecurity.VerifyPackage(PackagePath(asset), asset, cancellation);
        _pending = asset;
        return true;
    }

    public async Task ApplyOnExit(CancellationToken cancellation)
    {
        if (_pending is not { } asset)
            return;
        await UpdateSecurity.VerifyPackage(PackagePath(asset), asset, cancellation);
        cancellation.ThrowIfCancellationRequested();
        _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: false);
    }

    public void Dispose() => _transport.Dispose();

    private static string PackagePath(VelopackAsset asset)
    {
        UpdateSecurity.ValidateAsset(asset);
        return Path.Combine(VelopackLocator.Current.PackagesDir!, asset.FileName);
    }
}
