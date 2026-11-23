using System.Reflection;
using Velopack;
using Velopack.Locators;
using WinScreen.Localization;
using WinScreen.Settings;

namespace WinScreen.Updates;

internal static class AppInfo
{
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

internal enum UpdateStage { Checking, Downloading, Verifying }

// Percent is the download's, 0 to 100.
internal readonly record struct UpdateProgress(UpdateStage Stage, string? Version = null, int Percent = 0);

internal interface IUpdateSession : IDisposable
{
    Task<string?> NewerVersion(CancellationToken cancellation);
    Task<bool> Download(CancellationToken cancellation, Action<UpdateProgress>? progress = null);
    Task ApplyOnExit(CancellationToken cancellation, bool restart);
}

// One check at a time. Turning automatic updates off cancels a check in progress and discards a downloaded update.
internal sealed class AutomaticUpdates(Func<CancellationToken, IUpdateSession?> createSession, Action<string> status,
    Action<string>? available = null) : IDisposable
{
    private CancellationTokenSource? _generation;
    private bool _automatic;
    private bool _telling;
    private string? _told;
    private IUpdateSession? _pending;
    private bool _busy;
    private TaskCompletionSource? _idle;
    private bool _disposed;
    private bool _lastCheckFailed;
    private string? _downloading;

    // Raised on whatever thread the download reports from.
    internal event Action<UpdateProgress>? Progress;

    // The version downloaded and waiting to install, when it is known.
    internal string? ReadyVersion => Ready ? _downloading : null;

    private void Begin()
    {
        _busy = true;
        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void End()
    {
        _busy = false;
        _idle?.TrySetResult();
    }

    internal void SetEnabled(bool enabled)
    {
        if (_disposed || enabled == _automatic)
            return;
        _automatic = enabled;
        if (enabled)
            _generation ??= new CancellationTokenSource();
        else
            Discard();
    }

    private void Discard()
    {
        _generation?.Cancel();
        _generation?.Dispose();
        _generation = null;
        _pending?.Dispose();
        _pending = null;
    }

    // Also what a copy that can't install (a portable one) does with Install for me.
    internal void SetTelling(bool telling)
    {
        if (_disposed) return;
        _telling = telling;
    }

    internal string? Available => _told;

    // No network at all when updates are off.
    internal Task Check() => _automatic ? Run(quiet: false) : _telling ? Tell() : Task.CompletedTask;

    private async Task Tell()
    {
        if (_disposed || _busy || _pending is not null)
            return;
        Begin();
        IUpdateSession? session = null;
        var generation = _generation ??= new CancellationTokenSource();
        try
        {
            session = createSession(generation.Token);
            if (session is null) return;
            string? version = await session.NewerVersion(generation.Token);
            if (version is not null && version != _told && !_disposed && _generation == generation)
            {
                _told = version;
                available?.Invoke(version);
            }
        }
        catch (Exception caught)
        {
            // Quiet: a look nobody asked for right now isn't worth a message when it fails.
            Diagnostics.Log.Swallowed(caught);
        }
        finally
        {
            session?.Dispose();
            End();
        }
    }

    private async Task Run(bool quiet)
    {
        if (_disposed || _busy || _pending is not null)
            return;
        var generation = _generation ??= new CancellationTokenSource();
        Begin();
        _lastCheckFailed = false;
        IUpdateSession? session = null;
        try
        {
            session = createSession(generation.Token);
            if (session is null)
                return;
            _downloading = null;
            Progress?.Invoke(new UpdateProgress(UpdateStage.Checking));
            if (await session.Download(generation.Token, step =>
                {
                    if (step.Version is not null) _downloading = step.Version;
                    Progress?.Invoke(step);
                }) && _generation == generation &&
                !generation.IsCancellationRequested)
            {
                _pending = session;
                session = null;
                if (!quiet) status(UiText.T("updates.ready"));
            }
        }
        catch (OperationCanceledException caught) when (generation.IsCancellationRequested)
        {
            Diagnostics.Log.Swallowed(caught);
        }
        catch (Exception)
        {
            _lastCheckFailed = true;
            if (_generation == generation && !_disposed && !quiet)
                status(UiText.T("updates.checkFailed"));
        }
        finally
        {
            session?.Dispose();
            End();
        }
    }

    // Works whatever the setting, since the person asked. A check already under way is waited for first, so the person
    // gets the answer to their own look.
    internal async Task<(string? Version, bool Failed)> LookNow()
    {
        while (_busy && !_disposed && _idle is { } running)
            await running.Task;
        if (_disposed) return (null, true);
        Begin();
        IUpdateSession? session = null;
        try
        {
            session = createSession(CancellationToken.None);
            if (session is null) return (null, true);
            string? version = await session.NewerVersion(CancellationToken.None);
            if (version is not null) _told = version;
            return (version, false);
        }
        catch (Exception caught)
        {
            Diagnostics.Log.Swallowed(caught);
            return (null, true);
        }
        finally
        {
            session?.Dispose();
            End();
        }
    }

    // A check already under way, the timer's or one asked for before, is waited for, then this one gives the answer.
    internal async Task<string> CheckNow()
    {
        while (_busy && !_disposed && _idle is { } running)
            await running.Task;
        await Run(quiet: true);
        if (_pending is not null)
            return UiText.T("updates.ready");
        return _lastCheckFailed
            ? UiText.T("updates.checkFailed")
            : UiText.T("updates.upToDate");
    }

    internal bool Ready => !_disposed && _generation is not null && _pending is not null;

    internal async Task ApplyOnExit(bool restart = false)
    {
        if (!_disposed && _generation is not null && _pending is not null)
        {
            try
            {
                await _pending.ApplyOnExit(_generation.Token, restart);
            }
            catch (Exception caught)
            {
                /* Never prevent window restoration or shutdown if an update fails. */
                Diagnostics.Log.Swallowed(caught);
            }
        }
    }

    public void Dispose()
    {
        Discard();
        _automatic = false;
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
    private readonly SignedUpdateSource _source;
    private readonly UpdateManager _manager;
    private VelopackAsset? _pending;

    private VelopackUpdateSession(string repository, string publicKey, CancellationToken cancellation)
    {
        _transport = new UpdateTransport(repository);
        _source = new SignedUpdateSource(repository, publicKey, _transport, cancellation);
        _manager = new UpdateManager(_source,
            new UpdateOptions { AllowVersionDowngrade = false, ExplicitChannel = UpdateChannel.Current });
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

    public async Task<string?> NewerVersion(CancellationToken cancellation)
    {
        string? version = await UpdateNews.NewerVersion(_source, AppInfo.Version);
        cancellation.ThrowIfCancellationRequested();
        return version;
    }

    internal static string? Repository => typeof(VelopackUpdateSession).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(metadata => metadata.Key == "UpdateRepository")?.Value is { Length: > 0 } repository ? repository : null;

    public async Task<bool> Download(CancellationToken cancellation, Action<UpdateProgress>? progress = null)
    {
        if (!_manager.IsInstalled)
            return false;
        progress?.Invoke(new UpdateProgress(UpdateStage.Checking));
        var update = await _manager.CheckForUpdatesAsync();
        cancellation.ThrowIfCancellationRequested();
        if (update is null)
            return false;
        VelopackAsset asset = update;
        UpdateSecurity.ValidateAsset(asset);
        if (asset.Version <= _manager.CurrentVersion)
            throw new InvalidDataException("Update must be newer than the installed version.");
        string version = asset.Version.ToString();
        progress?.Invoke(new UpdateProgress(UpdateStage.Downloading, version));
        await _manager.DownloadUpdatesAsync(update, percent => progress?.Invoke(new UpdateProgress(UpdateStage.Downloading, version, percent)), cancellation);
        progress?.Invoke(new UpdateProgress(UpdateStage.Verifying, version));
        await UpdateSecurity.VerifyPackage(PackagePath(asset), asset, cancellation);
        _pending = asset;
        return true;
    }

    public async Task ApplyOnExit(CancellationToken cancellation, bool restart)
    {
        if (_pending is not { } asset)
            return;
        await UpdateSecurity.VerifyPackage(PackagePath(asset), asset, cancellation);
        cancellation.ThrowIfCancellationRequested();
        _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: restart);
    }

    public void Dispose() => _transport.Dispose();

    private static string PackagePath(VelopackAsset asset)
    {
        UpdateSecurity.ValidateAsset(asset);
        return Path.Combine(VelopackLocator.Current.PackagesDir!, asset.FileName);
    }
}
