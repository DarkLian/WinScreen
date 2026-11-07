using System.Net;
using System.Net.Http;

namespace WinScreen.Updates;

/// <summary>No automatic redirects: every hop must remain HTTPS on an allowed GitHub host.</summary>
internal sealed class UpdateTransport : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _repository;

    internal UpdateTransport(string repository, HttpMessageHandler? handler = null)
    {
        _repository = UpdateSecurity.ValidateRepository(repository);
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("WinScreen-Updater/1.0");
    }

    internal bool IsAllowed(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                                        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
                                        (uri.Host == "github.com" &&
                                         uri.AbsolutePath.StartsWith($"/{_repository}/releases/",
                                             StringComparison.Ordinal) ||
                                         uri.Host is "release-assets.githubusercontent.com"
                                             or "objects.githubusercontent.com");

    internal async Task Copy(Uri uri, Stream destination, long limit, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        for (int hop = 0; hop < 6; hop++)
        {
            if (!IsAllowed(uri))
                throw new InvalidDataException("Untrusted update download URL.");
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                uri = new Uri(uri,
                    response.Headers.Location ?? throw new InvalidDataException("Missing redirect URL."));
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit)
                throw new InvalidDataException("Update download exceeds its size limit.");
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            byte[] buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await body.ReadAsync(buffer, deadline.Token)) != 0)
            {
                total += count;
                if (total > limit)
                    throw new InvalidDataException("Update download exceeds its size limit.");
                await destination.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
            }

            return;
        }

        throw new InvalidDataException("Too many update redirects.");
    }

    internal async Task<byte[]> Read(Uri uri, int limit, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        await Copy(uri, buffer, limit, cancellation);
        return buffer.ToArray();
    }

    internal async Task<Uri> ResolveRelease(Uri uri, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        for (int hop = 0; hop < 6; hop++)
        {
            if (!IsAllowed(uri)) throw new InvalidDataException("Untrusted release URL.");
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response =
                await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                uri = new Uri(uri,
                    response.Headers.Location ?? throw new InvalidDataException("Missing release redirect."));
                continue;
            }

            response.EnsureSuccessStatusCode();
            return uri;
        }

        throw new InvalidDataException("Too many release redirects.");
    }

    public void Dispose() => _client.Dispose();
}
