using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

if (args.Length == 2 && args[0] == "init")
{
    string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinScreenSigning");
    var owner = WindowsIdentity.GetCurrent().User!;
    var directorySecurity = new DirectorySecurity();
    directorySecurity.SetAccessRuleProtection(true, false);
    directorySecurity.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    var folder = new DirectoryInfo(directory);
    folder.Create(directorySecurity);
    folder.SetAccessControl(directorySecurity);
    string privatePath = Path.Combine(directory, "update-private-key.pem");
    if (File.Exists(privatePath) || File.Exists(args[1]))
        throw new IOException("Signing keys already exist. Do not replace a published trust key.");
    using var rsa = RSA.Create(3072);
    var fileSecurity = new FileSecurity();
    fileSecurity.SetAccessRuleProtection(true, false);
    fileSecurity.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
    using (var file = new FileInfo(privatePath).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, fileSecurity))
    using (var writer = new StreamWriter(file))
        writer.Write(rsa.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(args[1], rsa.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine($"Public key written to {args[1]}. Private key is protected by your Windows user ACL at {privatePath}.");
}
else if (args.Length == 3 && args[0] == "sign")
{
    // CI supplies a repository secret. Local builds read the ACL-protected private file instead.
    string privateKey = Environment.GetEnvironmentVariable("WINSCREEN_UPDATE_SIGNING_KEY")
        ?? File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinScreenSigning", "update-private-key.pem"));
    using var rsa = RSA.Create();
    rsa.ImportFromPem(privateKey);
    using var expected = RSA.Create();
    expected.ImportFromPem(File.ReadAllText(args[2]));
    if (rsa.KeySize < 3072 || !rsa.ExportSubjectPublicKeyInfo().SequenceEqual(expected.ExportSubjectPublicKeyInfo()))
        throw new CryptographicException("Signing key does not match the embedded public key.");
    byte[] data = File.ReadAllBytes(args[1]);
    byte[] signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    File.WriteAllBytes(args[1] + ".sig", signature);
    Console.WriteLine("Update feed signed.");
}
else
    throw new ArgumentException("Usage: init <public-key.pem> | sign <releases.win.json> <public-key.pem>");
