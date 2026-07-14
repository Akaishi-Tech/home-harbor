using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class SelinuxDependencyPackageSetTests
{
    [TestMethod]
    public void Dependency_Input_Fingerprint_Covers_Only_Effective_Version_Independent_Build_Inputs()
    {
        using var fixture = new DependencyFixture(["policy"]);
        var baseline = SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root);
        Assert.HasCount(64, baseline);

        File.WriteAllText(Path.Combine(fixture.SelinuxRoot, "README.md"), "documentation changed\n");
        var generated = Directory.CreateDirectory(Path.Combine(fixture.RecipeDirectory, "src"));
        File.WriteAllText(Path.Combine(generated.FullName, "generated.c"), "generated source\n");
        File.WriteAllText(Path.Combine(fixture.RecipeDirectory, ".SRCINFO"), "generated metadata\n");
        File.WriteAllText(Path.Combine(fixture.Root, "outside.txt"), "unrelated source\n");
        Assert.AreEqual(
            baseline,
            SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root));

        File.AppendAllText(fixture.Pkgbuild, "pkgrel=2\n");
        Assert.AreNotEqual(
            baseline,
            SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root));
        File.WriteAllText(fixture.Pkgbuild, fixture.OriginalPkgbuild);

        File.AppendAllText(fixture.Manifest, "# build order explanation changed\n");
        Assert.AreNotEqual(
            baseline,
            SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root));
        File.WriteAllText(fixture.Manifest, fixture.OriginalManifest);

        if (!OperatingSystem.IsWindows())
        {
            var originalMode = File.GetUnixFileMode(fixture.Pkgbuild);
            File.SetUnixFileMode(fixture.Pkgbuild, originalMode | UnixFileMode.UserExecute);
            Assert.AreNotEqual(
                baseline,
                SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root));
            File.SetUnixFileMode(fixture.Pkgbuild, originalMode);

            var originalDirectoryMode = File.GetUnixFileMode(fixture.RecipeDirectory);
            File.SetUnixFileMode(fixture.RecipeDirectory, originalDirectoryMode ^ UnixFileMode.GroupWrite);
            Assert.AreNotEqual(
                baseline,
                SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root));
            File.SetUnixFileMode(fixture.RecipeDirectory, originalDirectoryMode);
        }

        File.AppendAllText(fixture.BuilderContract, "// dependency build behavior changed\n");
        Assert.AreNotEqual(
            baseline,
            SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root));
        File.WriteAllText(fixture.BuilderContract, fixture.OriginalBuilderContract);

        File.AppendAllText(fixture.SharedKey, "updated key material\n");
        Assert.AreNotEqual(
            baseline,
            SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root));
    }

    [TestMethod]
    public async Task Dependency_Provenance_Is_Version_Independent_And_Imports_Only_Verified_Packages()
    {
        using var fixture = new DependencyFixture(["policy"]);
        var cache = Directory.CreateDirectory(Path.Combine(fixture.Root, "cache"));
        var archive = fixture.CreateArchive(cache.FullName, "policy", "1.2.3-4", "x86_64");
        var runner = fixture.CreateRunner();
        var inputSha256 = SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root);

        await SelinuxDependencyPackageSetProvenance.WriteAsync(
            fixture.Root,
            cache.FullName,
            inputSha256,
            runner);
        await SelinuxDependencyPackageSetProvenance.VerifyAsync(fixture.Root, cache.FullName, runner);

        var provenance = await File.ReadAllTextAsync(Path.Combine(
            cache.FullName,
            SelinuxDependencyPackageSetProvenance.FileName));
        Assert.Contains("\"dependencyInputSha256\"", provenance);
        Assert.Contains("\"version\": \"1.2.3-4\"", provenance);
        Assert.DoesNotContain("homeHarborVersion", provenance, StringComparison.OrdinalIgnoreCase);

        var destination = Directory.CreateDirectory(Path.Combine(fixture.Root, "combined-packages"));
        await SelinuxDependencyPackageSetProvenance.ImportVerifiedAsync(
            fixture.Root,
            cache.FullName,
            destination.FullName,
            runner);
        Assert.IsTrue(File.Exists(Path.Combine(destination.FullName, Path.GetFileName(archive))));
        Assert.IsFalse(File.Exists(Path.Combine(
            destination.FullName,
            SelinuxDependencyPackageSetProvenance.FileName)));
    }

    [TestMethod]
    public async Task Dependency_Provenance_Rejects_Archive_Metadata_And_Manifest_Tampering()
    {
        using var fixture = new DependencyFixture(["policy"]);
        var cache = Directory.CreateDirectory(Path.Combine(fixture.Root, "cache"));
        var archive = fixture.CreateArchive(cache.FullName, "policy", "1.2.3-4", "x86_64");
        var runner = fixture.CreateRunner();
        await SelinuxDependencyPackageSetProvenance.WriteAsync(
            fixture.Root,
            cache.FullName,
            SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root),
            runner);

        await File.AppendAllTextAsync(archive, "tampered\n");
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SelinuxDependencyPackageSetProvenance.VerifyAsync(fixture.Root, cache.FullName, runner));
        await File.WriteAllTextAsync(archive, "policy package\n");
        await SelinuxDependencyPackageSetProvenance.VerifyAsync(fixture.Root, cache.FullName, runner);

        fixture.PackageInfo[Path.GetFileName(archive)] = PackageInfo("policy", "1.2.3-5", "x86_64");
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SelinuxDependencyPackageSetProvenance.VerifyAsync(fixture.Root, cache.FullName, runner));
        fixture.PackageInfo[Path.GetFileName(archive)] = PackageInfo("policy", "1.2.3-4", "x86_64");

        var provenancePath = Path.Combine(cache.FullName, SelinuxDependencyPackageSetProvenance.FileName);
        var provenance = await File.ReadAllTextAsync(provenancePath);
        await File.WriteAllTextAsync(
            provenancePath,
            provenance.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SelinuxDependencyPackageSetProvenance.VerifyAsync(fixture.Root, cache.FullName, runner));

        await File.WriteAllTextAsync(
            provenancePath,
            $$"""
            {
              "schemaVersion": 1,
              "targetArchitecture": "x86_64",
              "dependencyInputSha256": "{{SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root)}}",
              "packages": {
                "{{Path.GetFileName(archive)}}": null
              }
            }
            """);
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SelinuxDependencyPackageSetProvenance.VerifyAsync(fixture.Root, cache.FullName, runner));
    }

    [TestMethod]
    public async Task Dependency_Provenance_Rejects_Missing_Extra_Duplicate_Foreign_And_Linked_Archives()
    {
        using (var fixture = new DependencyFixture(["policy"]))
        {
            var cache = Directory.CreateDirectory(Path.Combine(fixture.Root, "missing"));
            var archive = fixture.CreateArchive(cache.FullName, "policy", "1-1", "x86_64");
            var runner = fixture.CreateRunner();
            await SelinuxDependencyPackageSetProvenance.WriteAsync(
                fixture.Root,
                cache.FullName,
                SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root),
                runner);

            File.Delete(archive);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                SelinuxDependencyPackageSetProvenance.VerifyAsync(fixture.Root, cache.FullName, runner));

            var external = Path.Combine(fixture.Root, "external.pkg.tar.zst");
            await File.WriteAllTextAsync(external, "external package\n");
            _ = File.CreateSymbolicLink(archive, external);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                SelinuxDependencyPackageSetProvenance.VerifyAsync(fixture.Root, cache.FullName, runner));
        }

        using (var fixture = new DependencyFixture(["policy"]))
        {
            var cache = Directory.CreateDirectory(Path.Combine(fixture.Root, "extra"));
            _ = fixture.CreateArchive(cache.FullName, "policy", "1-1", "x86_64");
            _ = fixture.CreateArchive(cache.FullName, "extra", "1-1", "x86_64");
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                SelinuxDependencyPackageSetProvenance.WriteAsync(
                    fixture.Root,
                    cache.FullName,
                    SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root),
                    fixture.CreateRunner()));
        }

        using (var fixture = new DependencyFixture(["policy", "helper"]))
        {
            var cache = Directory.CreateDirectory(Path.Combine(fixture.Root, "duplicate"));
            _ = fixture.CreateArchive(cache.FullName, "policy", "1-1", "x86_64");
            var duplicate = fixture.CreateArchive(cache.FullName, "helper", "1-1", "x86_64");
            fixture.PackageInfo[Path.GetFileName(duplicate)] = PackageInfo("policy", "1-1", "x86_64");
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                SelinuxDependencyPackageSetProvenance.WriteAsync(
                    fixture.Root,
                    cache.FullName,
                    SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root),
                    fixture.CreateRunner()));
        }

        using (var fixture = new DependencyFixture(["policy"]))
        {
            var cache = Directory.CreateDirectory(Path.Combine(fixture.Root, "foreign"));
            _ = fixture.CreateArchive(cache.FullName, "policy", "1-1", "aarch64");
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                SelinuxDependencyPackageSetProvenance.WriteAsync(
                    fixture.Root,
                    cache.FullName,
                    SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(fixture.Root),
                    fixture.CreateRunner()));
        }
    }

    [TestMethod]
    public void Release_Workflow_Uses_An_Exact_Version_Independent_Dependency_Cache_Stage()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "homeharbor-release.yml"));
        var pkgbuild = File.ReadAllText(Path.Combine(root, "packaging", "arch", "PKGBUILD"));

        Assert.Contains("  dependencies:\n", workflow);
        Assert.Contains("    needs: dependencies\n", workflow);
        Assert.Contains("selinux-dependency-key", workflow);
        Assert.Contains("selinux-dependency-build", workflow);
        Assert.Contains("selinux-dependency-verify", workflow);
        Assert.Contains("homeharbor-selinux-dependencies-v1-x86_64-${fingerprint}", workflow);
        Assert.Contains("uses: actions/cache/restore@v6", workflow);
        Assert.Contains("uses: actions/cache/save@v6", workflow);
        Assert.Contains("fail-on-cache-miss: true", workflow);
        Assert.DoesNotContain("restore-keys:", workflow, StringComparison.Ordinal);
        Assert.Contains("HOMEHARBOR_SELINUX_DEPENDENCY_CACHE", workflow);
        Assert.Contains("aspnet-targeting-pack", workflow);
        Assert.Contains("aspnet-targeting-pack", pkgbuild);
        Assert.DoesNotContain("AllowMissingPrunePackageData", workflow, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Dependency_Cache_Work_And_Output_Paths_Must_Not_Overlap()
    {
        var root = Path.Combine(Path.GetTempPath(), "homeharbor-dependency-paths");
        var cache = Path.Combine(root, "cache");
        var nested = Path.Combine(cache, "nested");
        var separate = Path.Combine(root, "output");

        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            BuildToolCommands.RequireSeparateDirectories(cache, cache, "test paths"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            BuildToolCommands.RequireSeparateDirectories(cache, nested, "test paths"));
        BuildToolCommands.RequireSeparateDirectories(cache, separate, "test paths");
    }

    private static string PackageInfo(string name, string version, string architecture)
        => $"pkgname = {name}\npkgver = {version}\narch = {architecture}\n";

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HomeHarbor.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class DependencyFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("homeharbor-selinux-dependency-");

        public DependencyFixture(IReadOnlyList<string> packages)
        {
            Root = _root.FullName;
            SelinuxRoot = Directory.CreateDirectory(
                Path.Combine(Root, "packaging", "arch", "selinux")).FullName;
            RecipeDirectory = Directory.CreateDirectory(Path.Combine(SelinuxRoot, "policy")).FullName;
            Pkgbuild = Path.Combine(RecipeDirectory, "PKGBUILD");
            OriginalPkgbuild = "pkgname=('" + string.Join("' '", packages) + "')\npkgver=1\npkgrel=1\n";
            File.WriteAllText(Pkgbuild, OriginalPkgbuild);
            Manifest = Path.Combine(SelinuxRoot, "manifest.yml");
            OriginalManifest =
                "schemaVersion: 1\n" +
                "upstreamRepository: https://example.com/upstream.git\n" +
                "upstreamRevision: 0123456789abcdef0123456789abcdef01234567\n" +
                "recipes:\n" +
                "  - name: policy\n" +
                "    path: packaging/arch/selinux/policy\n" +
                "    packages: [" + string.Join(", ", packages) + "]\n" +
                "    installPackages: []\n" +
                "buildOrder: [policy]\n";
            File.WriteAllText(Manifest, OriginalManifest);
            var tooling = Directory.CreateDirectory(Path.Combine(Root, "src", "HomeHarbor.Tooling"));
            BuilderContract = Path.Combine(tooling.FullName, "SelinuxPackageBuilder.cs");
            OriginalBuilderContract = "// dependency build contract\n";
            File.WriteAllText(BuilderContract, OriginalBuilderContract);
            var keys = Directory.CreateDirectory(Path.Combine(SelinuxRoot, "keys", "pgp"));
            SharedKey = Path.Combine(keys.FullName, "release.asc");
            File.WriteAllText(SharedKey, "key material\n");
        }

        public string Root { get; }

        public string SelinuxRoot { get; }

        public string RecipeDirectory { get; }

        public string Pkgbuild { get; }

        public string OriginalPkgbuild { get; }

        public string Manifest { get; }

        public string OriginalManifest { get; }

        public string SharedKey { get; }

        public string BuilderContract { get; }

        public string OriginalBuilderContract { get; }

        public Dictionary<string, string> PackageInfo { get; } = new(StringComparer.Ordinal);

        public string CreateArchive(string directory, string name, string version, string architecture)
        {
            var fileName = $"{name}-{version}-{architecture}.pkg.tar.zst";
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(path, name + " package\n");
            PackageInfo[fileName] = SelinuxDependencyPackageSetTests.PackageInfo(name, version, architecture);
            return path;
        }

        public ICommandRunner CreateRunner()
            => new PackageInfoRunner(PackageInfo);

        public void Dispose()
        {
            if (_root.Exists)
            {
                _root.Delete(recursive: true);
            }
        }
    }

    private sealed class PackageInfoRunner(IReadOnlyDictionary<string, string> packageInfo) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            CommandRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var materialized = arguments.ToArray();
            if (fileName != "bsdtar" || materialized.Length != 3 ||
                !packageInfo.TryGetValue(Path.GetFileName(materialized[1]), out var metadata))
            {
                return Task.FromResult(new CommandResult(1, string.Empty, "unexpected archive", fileName));
            }

            return Task.FromResult(new CommandResult(0, metadata, string.Empty, fileName));
        }
    }
}
