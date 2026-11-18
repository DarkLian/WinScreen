using System.Text.RegularExpressions;
using Velopack;

namespace WinScreen.Updates;

// Reads the same signed feed through the same checks as an update does, and nothing else.
internal static partial class UpdateNews
{
    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersion();

    internal static async Task<string?> NewerVersion(SignedUpdateSource source, string current)
    {
        var feed = await source.GetReleaseFeed(null!, "WinScreen", UpdateChannel.Current);
        var newest = feed.Assets.MaxBy(asset => asset.Version);
        if (newest is null || !SemanticVersion.TryParse(current, out SemanticVersion? mine)) return null;
        UpdateSecurity.ValidateAsset(newest);
        return newest.Version > mine ? newest.Version.ToString() : null;
    }

    // Made from the built-in repository and a version the feed check passed; never a link from the feed itself.
    internal static Uri ReleasePage(string repository, string version)
    {
        if (!StableVersion().IsMatch(version)) throw new InvalidDataException("Only a stable version has a release page here.");
        return new Uri($"https://github.com/{UpdateSecurity.ValidateRepository(repository)}/releases/tag/v{version}");
    }
}
