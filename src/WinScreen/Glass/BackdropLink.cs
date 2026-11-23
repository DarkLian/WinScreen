namespace WinScreen.Glass;

// The one place outside Updates/ that goes online (INV-12): a picture the person gave Glass as a link is fetched from
// that address, and nothing is sent but the request for it. A video link is opened by Windows' own player.
internal static class BackdropLink
{
    private const long Largest = 64L * 1024 * 1024;
    private static readonly HttpClient Web = new() { Timeout = TimeSpan.FromSeconds(30) };

    internal static async Task<byte[]> Fetch(string link, CancellationToken cancel)
    {
        using HttpResponseMessage answer = await Web.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, cancel);
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
