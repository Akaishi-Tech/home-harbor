using System.Security.Cryptography;
using System.Text;

namespace HomeHarbor.Tooling;

public sealed record SelinuxPolicyStoreSynchronizationResult(
    SelinuxPolicyEpoch Epoch,
    bool StoreReplaced);

internal sealed record SelinuxPolicyStoreSynchronizationCoreResult(
    string StoreSha256,
    bool StoreReplaced);

public static class SelinuxPolicyStoreSynchronizer
{
    public const string PolicyType = "refpolicy-arch";
    public const string ImmutableSeedPath = "/usr/lib/homeharbor/selinux-store/refpolicy-arch";
    public const string RuntimeStorePath = "/var/lib/selinux/refpolicy-arch";
    public const string DigestFileName = ".homeharbor-store-sha256";

    private const string NewSuffix = ".homeharbor-new";
    private const string OldSuffix = ".homeharbor-old";

    public static bool SynchronizeDefault()
        => SynchronizeDefaultDetailed().StoreReplaced;

    public static SelinuxPolicyStoreSynchronizationResult SynchronizeDefaultDetailed()
    {
        if (!OperatingSystem.IsLinux() || !string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SELinux policy store synchronization must run as root on Linux");
        }

        return SelinuxRelabelCoordinator.SynchronizeDefaultDetailed();
    }

    internal static string PrepareImmutableSeed(string rootfs)
    {
        var root = Path.GetFullPath(rootfs);
        var source = UnderRoot(root, RuntimeStorePath);
        var destination = UnderRoot(root, ImmutableSeedPath);
        if (!Directory.Exists(source) || !Directory.EnumerateFileSystemEntries(source).Any())
        {
            throw new InvalidOperationException("SELinux policy module store is unavailable before image sealing: " + source);
        }

        DeleteEntry(destination);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        FileTreeCopier.CopyDirectory(source, destination);
        var digest = ComputeStoreDigest(destination);
        var digestFile = Path.Combine(destination, DigestFileName);
        File.WriteAllText(digestFile, digest + "\n", new UTF8Encoding(false));
        File.SetUnixFileMode(
            digestFile,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        RequireValidSeed(destination);

        Directory.Delete(source, recursive: true);
        return digest;
    }

    internal static bool Synchronize(string immutableSeed, string runtimeStore)
        => SynchronizeDetailed(immutableSeed, runtimeStore).StoreReplaced;

    internal static SelinuxPolicyStoreSynchronizationCoreResult SynchronizeDetailed(
        string immutableSeed,
        string runtimeStore,
        Action<string>? beforeStoreReplacement = null)
    {
        var seed = Path.GetFullPath(immutableSeed);
        var destination = Path.GetFullPath(runtimeStore);
        if (IsSameOrChild(destination, seed) || IsSameOrChild(seed, destination))
        {
            throw new InvalidOperationException("SELinux seed and runtime stores must not overlap");
        }

        var expectedDigest = RequireValidSeed(seed);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("SELinux runtime store has no parent directory");
        EnsureRealDirectory(parent);

        var pending = destination + NewSuffix;
        var backup = destination + OldSuffix;
        RefuseSymbolicLink(destination, "SELinux runtime store");
        RefuseSymbolicLink(pending, "SELinux pending store");
        RefuseSymbolicLink(backup, "SELinux backup store");

        var storeReplaced = false;
        var replacementPrepared = false;

        if (!EntryExists(destination) && Directory.Exists(backup))
        {
            beforeStoreReplacement?.Invoke(expectedDigest);
            replacementPrepared = true;
            Directory.Move(backup, destination);
            storeReplaced = true;
        }

        if (StoreMatches(destination, expectedDigest))
        {
            DeleteEntry(pending);
            DeleteEntry(backup);
            return new SelinuxPolicyStoreSynchronizationCoreResult(expectedDigest, storeReplaced);
        }

        DeleteEntry(pending);
        FileTreeCopier.CopyDirectory(seed, pending);
        if (!StoreMatches(pending, expectedDigest))
        {
            DeleteEntry(pending);
            throw new InvalidOperationException("copied SELinux policy store does not match its immutable seed");
        }

        DeleteEntry(backup);
        if (Directory.Exists(destination))
        {
            if (!replacementPrepared)
            {
                beforeStoreReplacement?.Invoke(expectedDigest);
                replacementPrepared = true;
            }

            Directory.Move(destination, backup);
            storeReplaced = true;
        }
        else if (EntryExists(destination))
        {
            throw new InvalidOperationException("SELinux runtime store is not a directory: " + destination);
        }
        else if (!replacementPrepared)
        {
            beforeStoreReplacement?.Invoke(expectedDigest);
            replacementPrepared = true;
            storeReplaced = true;
        }

        try
        {
            Directory.Move(pending, destination);
        }
        catch
        {
            if (!EntryExists(destination) && Directory.Exists(backup))
            {
                Directory.Move(backup, destination);
            }

            throw;
        }

        DeleteEntry(backup);
        if (!StoreMatches(destination, expectedDigest))
        {
            throw new InvalidOperationException("installed SELinux policy store does not match its immutable seed");
        }

        return new SelinuxPolicyStoreSynchronizationCoreResult(expectedDigest, storeReplaced);
    }

    internal static string ComputeStoreDigest(string store)
    {
        var root = Path.GetFullPath(store);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("SELinux policy store not found: " + root);
        }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(digest, "homeharbor-selinux-store-v1\n");
        AppendDirectory(digest, root, root, ".");
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    internal static string RequireValidSeed(string seed)
    {
        if (!Directory.Exists(seed))
        {
            throw new DirectoryNotFoundException("immutable SELinux policy store seed not found: " + seed);
        }

        RefuseSymbolicLink(seed, "immutable SELinux policy store seed");
        var digestFile = Path.Combine(seed, DigestFileName);
        var expected = File.Exists(digestFile) ? File.ReadAllText(digestFile).Trim() : string.Empty;
        if (!IsSha256(expected))
        {
            throw new InvalidOperationException("immutable SELinux policy store seed has no valid digest: " + digestFile);
        }

        var actual = ComputeStoreDigest(seed);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("immutable SELinux policy store seed digest mismatch");
        }

        return expected.ToLowerInvariant();
    }

    internal static bool StoreMatches(string store, string expectedDigest)
    {
        if (!Directory.Exists(store))
        {
            return false;
        }

        RefuseSymbolicLink(store, "SELinux policy store");
        var marker = Path.Combine(store, DigestFileName);
        if (!File.Exists(marker) ||
            !string.Equals(File.ReadAllText(marker).Trim(), expectedDigest, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return string.Equals(ComputeStoreDigest(store), expectedDigest, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void AppendDirectory(IncrementalHash digest, string root, string directory, string relative)
    {
        AppendText(digest, $"D\0{relative}\0{UnixMode(directory):X}\n");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var entryRelative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(entryRelative, DigestFileName, StringComparison.Ordinal))
            {
                continue;
            }

            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "SELinux policy stores must not contain symbolic links: " + entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                AppendDirectory(digest, root, entry, entryRelative);
            }
            else
            {
                var info = new FileInfo(entry);
                AppendText(digest, $"F\0{entryRelative}\0{UnixMode(entry):X}\0{info.Length}\n");
                using var stream = File.OpenRead(entry);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    digest.AppendData(buffer.AsSpan(0, read));
                }
            }
        }
    }

    private static int UnixMode(string path)
        => OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(path);

    private static void AppendText(IncrementalHash digest, string value)
        => digest.AppendData(Encoding.UTF8.GetBytes(value));

    private static void EnsureRealDirectory(string path)
    {
        if (EntryExists(path) && !Directory.Exists(path))
        {
            throw new InvalidOperationException("SELinux store parent is not a directory: " + path);
        }

        RefuseSymbolicLink(path, "SELinux store parent");
        if (!Directory.Exists(path))
        {
            _ = Directory.CreateDirectory(path);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void RefuseSymbolicLink(string path, string label)
    {
        if (new DirectoryInfo(path).LinkTarget is not null || new FileInfo(path).LinkTarget is not null)
        {
            throw new InvalidOperationException(label + " must not be a symbolic link: " + path);
        }
    }

    private static bool EntryExists(string path)
        => File.Exists(path) || Directory.Exists(path) ||
           new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;

    private static void DeleteEntry(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null)
        {
            Directory.Delete(path);
            return;
        }

        var file = new FileInfo(path);
        if (file.LinkTarget is not null || File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string UnderRoot(string root, string absolutePath)
        => Path.Combine(root, absolutePath.TrimStart(Path.DirectorySeparatorChar));

    private static bool IsSameOrChild(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
               (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);
}
