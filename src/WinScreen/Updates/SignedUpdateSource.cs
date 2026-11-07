using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace WinScreen.Updates;

/// <summary>Only a signed stable Windows feed and full packages from the built-in repository are accepted.</summary>
internal sealed class SignedUpdateSource(string repository, string publicKey, UpdateTransport transport,
    CancellationToken cancellation) : IUpdateSource
{
    private readonly string _repository = UpdateSecurity.ValidateRepository(repository);
    private readonly Dictionary<string, VelopackAsset> _verified = [];

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (appId != "WinScreen" || channel != "win")
            throw new InvalidDataException("Unexpected update identity or channel.");
        string baseUrl = $"https://github.com/{_repository}/releases/latest/download/";
        byte[] feedBytes = await transport.Read(new Uri(baseUrl + "releases.win.json"), 1048576, cancellation);
        byte[] signature = await transport.Read(new Uri(baseUrl + "releases.win.json.sig"), 1024, cancellation);
        var feed = UpdateSecurity.VerifyFeed(feedBytes, signature, publicKey);
        _verified.Clear();
        foreach (var asset in feed.Assets)
            _verified.Add(asset.FileName, asset);
        return feed;
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, cancelToken);
        if (!_verified.TryGetValue(releaseEntry.FileName, out var trusted) ||
            trusted.SHA256 != releaseEntry.SHA256 || trusted.Size != releaseEntry.Size || trusted.Version != releaseEntry.Version)
            throw new InvalidDataException("The update was not in the verified feed.");
        UpdateSecurity.ValidateAsset(releaseEntry);
        var uri = new Uri($"https://github.com/{_repository}/releases/download/v{releaseEntry.Version}/{releaseEntry.FileName}");
        try
        {
            await using (var output = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None))
                await transport.Copy(uri, output, releaseEntry.Size, linked.Token);
            await UpdateSecurity.VerifyPackage(localFile, releaseEntry, linked.Token);
            progress(100);
        }
        catch
        {
            File.Delete(localFile);
            throw;
        }
    }
}
