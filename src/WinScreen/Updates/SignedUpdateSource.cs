using System.Net;
using System.Net.Http;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace WinScreen.Updates;

/// <summary>Only a signed stable Windows feed and full packages from the built-in repository are accepted.</summary>
internal sealed class SignedUpdateSource(
    string repository,
    string publicKey,
    UpdateTransport transport,
    CancellationToken cancellation) : IUpdateSource
{
    private readonly string _repository = UpdateSecurity.ValidateRepository(repository);
    private readonly Dictionary<string, VelopackAsset> _verified = [];
    private string? _releaseBase;

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (appId != "WinScreen" || channel != UpdateChannel.Current)
            throw new InvalidDataException("Unexpected update identity or channel.");
        Uri release;
        try
        {
            release = await transport.ResolveRelease(new Uri($"https://github.com/{_repository}/releases/latest"),
                cancellation);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return new VelopackAssetFeed(); // Nothing has been published yet, which isn't a failure.
        }

        string prefix = $"/{_repository}/releases/tag/";
        if (release.Host != "github.com" || !release.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) ||
            release.Query.Length != 0)
            throw new InvalidDataException("The latest release did not resolve to this repository's stable tag.");
        string tag = Uri.UnescapeDataString(release.AbsolutePath[prefix.Length..]);
        if (!System.Text.RegularExpressions.Regex.IsMatch(tag,
                @"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z"))
            throw new InvalidDataException("Only stable vMAJOR.MINOR.PATCH release tags are accepted.");
        string baseUrl = $"https://github.com/{_repository}/releases/download/{tag}/";
        byte[] feedBytes = await transport.Read(new Uri(baseUrl + $"releases.{channel}.json"), 1048576, cancellation);
        byte[] signature = await transport.Read(new Uri(baseUrl + $"releases.{channel}.json.sig"), 1024, cancellation);
        var feed = UpdateSecurity.VerifyFeed(feedBytes, signature, publicKey);
        if (feed.Assets.Any(asset => asset.Version.ToString() != tag[1..]))
            throw new InvalidDataException("The signed feed does not match its release tag.");
        _releaseBase = baseUrl;
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
            trusted.SHA256 != releaseEntry.SHA256 || trusted.Size != releaseEntry.Size ||
            trusted.Version != releaseEntry.Version)
            throw new InvalidDataException("The update was not in the verified feed.");
        UpdateSecurity.ValidateAsset(releaseEntry);
        var uri = new Uri((_releaseBase ?? throw new InvalidDataException("No verified release.")) +
                          releaseEntry.FileName);
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
