using System.Net;
using System.Security.Cryptography;
using System.Text;
using Velopack;
using Velopack.Locators;
using WinScreen.Updates;
using Xunit;

namespace WinScreen.Tests;

public sealed class UpdateSecurityTests
{
    private static VelopackAsset Asset(byte[] bytes) => new()
    {
        PackageId = "WinScreen", Version = SemanticVersion.Parse("0.4.2"), Type = VelopackAssetType.Full,
        FileName = "WinScreen-0.4.2-full.nupkg", Size = bytes.Length,
        SHA256 = Convert.ToHexString(SHA256.HashData(bytes)),
    };

    private static byte[] Feed(VelopackAsset asset) => Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
        new
        {
            Assets = new[]
            {
                new
                {
                    asset.PackageId, Version = asset.Version.ToString(), Type = asset.Type.ToString(), asset.FileName,
                    asset.Size, asset.SHA256
                }
            },
        }));

    [Fact]
    public void Authentic_feed_passes_but_tampering_wrong_key_and_missing_signatures_fail()
    {
        using var key = RSA.Create(3072);
        using var impostor = RSA.Create(3072);
        byte[] feed = Feed(Asset([1, 2, 3]));
        byte[] signature = key.SignData(feed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        string publicKey = key.ExportSubjectPublicKeyInfoPem();
        Assert.Single(UpdateSecurity.VerifyFeed(feed, signature, publicKey).Assets);
        Assert.Throws<CryptographicException>(() =>
            UpdateSecurity.VerifyFeed(feed, signature, impostor.ExportSubjectPublicKeyInfoPem()));
        signature[0] ^= 1;
        Assert.Throws<CryptographicException>(() => UpdateSecurity.VerifyFeed(feed, signature, publicKey));
        Assert.Throws<InvalidDataException>(() => UpdateSecurity.VerifyFeed(feed, [], publicKey));
        signature = key.SignData(feed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        feed[^2] ^= 1;
        Assert.Throws<CryptographicException>(() => UpdateSecurity.VerifyFeed(feed, signature, publicKey));
    }

    [Theory]
    [InlineData("../evil.nupkg")]
    [InlineData("C:\\evil.nupkg")]
    [InlineData("https://evil.example/payload")]
    [InlineData("WinScreen-..\\payload-full.nupkg")]
    [InlineData("WinScreen-0.4.2-full.nupkg?token=foo")]
    public void Feed_cannot_inject_paths_or_urls(string filename)
    {
        var asset = Asset([1]);
        asset.FileName = filename;
        Assert.Throws<InvalidDataException>(() => UpdateSecurity.ValidateAsset(asset));
    }

    [Fact]
    public void Wrong_identity_prereleases_deltas_weak_hashes_and_oversized_packages_fail()
    {
        var asset = Asset([1]);
        asset.PackageId = "AnotherApp";
        Assert.Throws<InvalidDataException>(() => UpdateSecurity.ValidateAsset(asset));
        asset = Asset([1]);
        asset.Version = SemanticVersion.Parse("0.4.2-beta");
        Assert.Throws<InvalidDataException>(() => UpdateSecurity.ValidateAsset(asset));
        asset = Asset([1]);
        asset.Type = VelopackAssetType.Delta;
        Assert.Throws<InvalidDataException>(() => UpdateSecurity.ValidateAsset(asset));
        asset = Asset([1]);
        asset.SHA256 = "";
        Assert.Throws<InvalidDataException>(() => UpdateSecurity.ValidateAsset(asset));
        asset = Asset([1]);
        asset.Size = UpdateSecurity.MaxPackageSize + 1;
        Assert.Throws<InvalidDataException>(() => UpdateSecurity.ValidateAsset(asset));
    }

    [Fact]
    public async Task Modified_and_truncated_cached_packages_cannot_be_applied()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] bytes = [1, 2, 3];
            var asset = Asset(bytes);
            await File.WriteAllBytesAsync(path, bytes);
            await UpdateSecurity.VerifyPackage(path, asset, CancellationToken.None);
            await File.WriteAllBytesAsync(path, [3, 2, 1]);
            await Assert.ThrowsAsync<CryptographicException>(() =>
                UpdateSecurity.VerifyPackage(path, asset, CancellationToken.None));
            await File.WriteAllBytesAsync(path, [1]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                UpdateSecurity.VerifyPackage(path, asset, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("http://github.com/owner/repo/releases/latest/download/feed", false)]
    [InlineData("https://github.com.evil.example/owner/repo/releases/feed", false)]
    [InlineData("https://github.com/attacker/repo/releases/feed", false)]
    [InlineData("https://github.com/owner/repo/releases/feed", true)]
    [InlineData("https://release-assets.githubusercontent.com/file?signature=x", true)]
    [InlineData("https://github.com:444/owner/repo/releases/feed", false)]
    [InlineData("https://user@github.com/owner/repo/releases/feed", false)]
    [InlineData("file:///C:/payload", false)]
    public void Transport_restricts_hosts_scheme_port_and_credentials(string url, bool expected)
    {
        using var transport = new UpdateTransport("owner/repo");
        Assert.Equal(expected, transport.IsAllowed(new Uri(url)));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task Redirect_to_untrusted_host_is_never_requested()
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://attacker.example/payload");
            return response;
        });
        using var transport = new UpdateTransport("owner/repo", handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            transport.Read(new Uri("https://github.com/owner/repo/releases/feed"), 100, CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Download_limits_apply_even_without_content_length()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnknownLengthStream(new byte[101])),
        });
        using var transport = new UpdateTransport("owner/repo", handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            transport.Read(new Uri("https://github.com/owner/repo/releases/feed"), 100, CancellationToken.None));
    }

    [Fact]
    public async Task A_repository_with_no_releases_yet_means_no_update_rather_than_a_failure()
    {
        using var key = RSA.Create(3072);
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var transport = new UpdateTransport("owner/repo", handler);
        var source = new SignedUpdateSource("owner/repo", key.ExportSubjectPublicKeyInfoPem(), transport,
            CancellationToken.None);
        Assert.Empty((await source.GetReleaseFeed(null!, "WinScreen", "win")).Assets);
    }

    [Fact]
    public async Task Signed_source_downloads_only_verified_packages_and_deletes_corrupt_downloads()
    {
        using var key = RSA.Create(3072);
        byte[] package = [1, 2, 3];
        var asset = Asset(package);
        byte[] feed = Feed(asset);
        byte[] signature = key.SignData(feed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        bool corrupt = false;
        var requested = new List<string>();
        var handler = new Handler(request =>
        {
            requested.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/latest"))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://github.com/owner/repo/releases/tag/v0.4.2");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith(".sig") ? signature :
                    request.RequestUri.AbsolutePath.EndsWith(".json") ? feed :
                    corrupt ? [3, 2, 1] : package),
            };
        });
        using var transport = new UpdateTransport("owner/repo", handler);
        var source = new SignedUpdateSource("owner/repo", key.ExportSubjectPublicKeyInfoPem(), transport,
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetReleaseFeed(null!, "WrongApp", "win"));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetReleaseFeed(null!, "WinScreen", "beta"));
        string path = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                source.DownloadReleaseEntry(null!, asset, path, _ => { }, CancellationToken.None));
            var verified = await source.GetReleaseFeed(null!, "WinScreen", "win");
            Assert.Contains("/owner/repo/releases/download/v0.4.2/releases.win.json", requested);
            Assert.Contains("/owner/repo/releases/download/v0.4.2/releases.win.json.sig", requested);
            Assert.DoesNotContain(requested, path => path.Contains("latest/download"));
            asset = Assert.Single(verified.Assets);
            await source.DownloadReleaseEntry(null!, asset, path, _ => { }, CancellationToken.None);
            Assert.Equal(package, await File.ReadAllBytesAsync(path));
            corrupt = true;
            await Assert.ThrowsAsync<CryptographicException>(() =>
                source.DownloadReleaseEntry(null!, asset, path, _ => { }, CancellationToken.None));
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("0.4.0", false)]
    [InlineData("0.4.1", false)]
    [InlineData("0.4.2", true)]
    public async Task Velopack_selects_only_newer_signed_versions(string version, bool available)
    {
        using var key = RSA.Create(3072);
        var asset = Asset([1]);
        asset.Version = SemanticVersion.Parse(version);
        byte[] feed = Feed(asset);
        byte[] signature = key.SignData(feed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest"))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://github.com/owner/repo/releases/tag/v" + version);
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith(".sig") ? signature : feed) };
        });
        using var transport = new UpdateTransport("owner/repo", handler);
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            var locator = new TestVelopackLocator("WinScreen", "0.4.1", directory);
            var manager = new UpdateManager(
                new SignedUpdateSource("owner/repo", key.ExportSubjectPublicKeyInfoPem(), transport,
                    CancellationToken.None),
                new UpdateOptions { AllowVersionDowngrade = false, ExplicitChannel = "win" }, locator);
            Assert.Equal(available, await manager.CheckForUpdatesAsync() is not null);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
