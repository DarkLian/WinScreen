using System.Net;
using System.Net.Sockets;

namespace WinScreen.Glass;

// The one place outside Updates/ that goes online (INV-12): a picture the person gave Glass as a link is fetched from
// that address, and nothing is sent but the request for it. A video link is opened by Windows' own player.
internal static class BackdropLink
{
    private const long Largest = 64L * 1024 * 1024;
    private const int MostRedirects = 5;

    // Redirects are followed here, not by HttpClient, so one can't lead from the internet into this PC or its network.
    private static readonly HttpClient Web = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    internal static async Task<byte[]> Fetch(string link, CancellationToken wanted)
    {
        // HttpClient's own limit ends with the headers; a body that trickles in is limited here.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(wanted);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        CancellationToken cancel = limit.Token;
        var address = new Uri(link);
        bool startedAtHome = await PointsHome(address, cancel);
        HttpResponseMessage answer = await Web.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancel);
        for (int hop = 0; IsRedirect(answer.StatusCode) && answer.Headers.Location is { } next; hop++)
        {
            answer.Dispose();
            address = next.IsAbsoluteUri ? next : new Uri(address, next);
            if (hop == MostRedirects || address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp ||
                !startedAtHome && await PointsHome(address, cancel))
                throw new InvalidDataException("The link leads somewhere it can't be followed.");
            answer = await Web.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancel);
        }
        using (answer)
        {
            answer.EnsureSuccessStatusCode();
            if (answer.Content.Headers.ContentLength > Largest) throw new InvalidDataException("The picture is too big.");
            var bytes = new MemoryStream();
            await using (Stream body = await answer.Content.ReadAsStreamAsync(cancel))
            {
                var part = new byte[81920];
                for (int read; (read = await body.ReadAsync(part, cancel)) > 0;)
                {
                    bytes.Write(part, 0, read);
                    if (bytes.Length > Largest) throw new InvalidDataException("The picture is too big.");
                }
            }
            return bytes.ToArray();
        }
    }

    private static bool IsRedirect(HttpStatusCode code) => (int)code is >= 300 and < 400 and not 304;

    private static async Task<bool> PointsHome(Uri address, CancellationToken cancel)
    {
        if (address.IsLoopback) return true;
        if (IPAddress.TryParse(address.DnsSafeHost, out IPAddress? literal)) return IsHome(literal);
        try { return (await Dns.GetHostAddressesAsync(address.DnsSafeHost, cancel)).Any(IsHome); }
        catch (SocketException unknown)
        {
            Diagnostics.Log.Swallowed(unknown);
            return false; // The request itself then fails.
        }
    }

    // This PC, its network, or a link-local address.
    internal static bool IsHome(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 ||
               b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127;
    }
}
