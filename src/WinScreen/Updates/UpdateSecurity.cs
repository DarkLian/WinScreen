using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Velopack;

namespace WinScreen.Updates;

internal static partial class UpdateSecurity
{
    internal const long MaxPackageSize = 512L * 1024 * 1024;

    [GeneratedRegex(@"^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    internal static string ValidateRepository(string repository)
    {
        if (!RepositoryPattern().IsMatch(repository) || repository.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException("Invalid update repository.");
        return repository;
    }

    internal static VelopackAssetFeed VerifyFeed(byte[] data, byte[] signature, string publicKey)
    {
        if (data.Length is 0 or > 1048576 || signature.Length is 0 or > 1024)
            throw new InvalidDataException("Invalid update feed size.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        if (rsa.KeySize < 3072 || !rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("Update feed signature verification failed.");
        var feed = VelopackAssetFeed.FromJson(new UTF8Encoding(false, true).GetString(data));
        foreach (var asset in feed.Assets)
            ValidateAsset(asset);
        return feed;
    }

    internal static void ValidateAsset(VelopackAsset asset)
    {
        if (asset.PackageId != "WinScreen" || asset.Type != VelopackAssetType.Full ||
            asset.Version is null || asset.Version.IsPrerelease || asset.Size <= 0 || asset.Size > MaxPackageSize ||
            !Regex.IsMatch(asset.SHA256 ?? "", "\\A[0-9a-fA-F]{64}\\z") ||
            !Regex.IsMatch(asset.FileName ?? "", @"\AWinScreen-[0-9A-Za-z.+_-]+-full\.nupkg\z"))
            throw new InvalidDataException("Invalid update package metadata.");
    }

    internal static async Task VerifyPackage(string path, VelopackAsset asset, CancellationToken cancellation)
    {
        ValidateAsset(asset);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != asset.Size)
            throw new InvalidDataException("Update package size mismatch.");
        byte[] digest = await SHA256.HashDataAsync(file, cancellation);
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(asset.SHA256)))
            throw new CryptographicException("Update package hash mismatch.");
    }
}
