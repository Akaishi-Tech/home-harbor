using HomeHarbor.Tooling;
using System.Text.RegularExpressions;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class SelinuxBuildTests
{
    [TestMethod]
    public void Default_Package_Plan_Covers_Every_Maintained_Recipe_Without_A_Binary_Hardened_Repository()
    {
        var root = RepositoryRoot();
        var plan = SelinuxPackageBuildDescriptor.LoadDefaultPlan(root);
        var recipeRoot = Path.Combine(root, "packaging", "arch", "selinux");
        var recipeDirectories = Directory.GetDirectories(recipeRoot)
            .Where(directory => File.Exists(Path.Combine(directory, "PKGBUILD")))
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.IsTrue(recipeDirectories.SetEquals(plan.Recipes.Keys));
        Assert.AreEqual(plan.Recipes.Count, plan.BuildOrder.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual("e65a9de9c1820c4ac48e4ac6c69cf740d59ffc71", plan.UpstreamRevision);

        foreach (var recipe in plan.Recipes.Values)
        {
            var pkgbuild = File.ReadAllText(Path.Combine(recipe.Directory, "PKGBUILD"));
            Assert.DoesNotContain("archlinuxhardened.org", pkgbuild, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[archlinuxhardened]", pkgbuild, StringComparison.OrdinalIgnoreCase);
        }
    }

    [TestMethod]
    public void Systemd_SELinux_Variant_Fixes_The_Live_Overlay_Root_After_Policy_Load()
    {
        var root = RepositoryRoot();
        var plan = SelinuxPackageBuildDescriptor.LoadDefaultPlan(root);
        const string recipeName = "systemd-selinux";
        var recipe = plan.Recipes[recipeName];
        Assert.IsGreaterThanOrEqualTo(1, plan.BuildOrder.Count(name => name == recipeName));

        var pkgbuild = File.ReadAllText(Path.Combine(recipe.Directory, "PKGBUILD"));
        const string patchName = "0002-homeharbor-fix-live-overlay-root-context.patch";
        Assert.Contains("pkgrel=5", pkgbuild);
        Assert.Contains($"'{patchName}'", pkgbuild);
        Assert.Contains($"patch -Np1 -i ../{patchName}", pkgbuild);

        var patch = File.ReadAllText(Path.Combine(recipe.Directory, patchName));
        Assert.Contains("HOMEHARBOR_ROOT_CONTEXT \"system_u:object_r:root_t:s0\"", patch);
        Assert.Contains("HOMEHARBOR_TMPFS_CONTEXT \"system_u:object_r:tmpfs_t:s0\"", patch);
        Assert.Contains("sym_getfilecon_raw(\"/\", &context)", patch);
        Assert.Contains("streq_ptr(context, HOMEHARBOR_ROOT_CONTEXT)", patch);
        Assert.Contains("streq_ptr(context, HOMEHARBOR_TMPFS_CONTEXT)", patch);
        Assert.Contains("access(\"/run/archiso\", F_OK)", patch);
        Assert.Contains("path_is_fs_type(\"/\", OVERLAYFS_SUPER_MAGIC)", patch);
        Assert.Contains("sym_setfilecon_raw(\"/\", HOMEHARBOR_ROOT_CONTEXT)", patch);
        Assert.Contains("sym_getfilecon_raw(\"/\", &verified)", patch);
        Assert.Contains("streq_ptr(verified, HOMEHARBOR_ROOT_CONTEXT)", patch);
        Assert.IsGreaterThanOrEqualTo(2, Regex.Count(patch, "return -ENOTRECOVERABLE;", RegexOptions.CultureInvariant));
        Assert.Contains("return log_struct_errno(LOG_EMERG", patch);
        Assert.DoesNotContain("mac_selinux_init()", patch, StringComparison.Ordinal);
        var policyLoadIndex = patch.IndexOf("sym_selinux_init_load_policy", StringComparison.Ordinal);
        var fixIndex = patch.IndexOf("r = homeharbor_fix_root_context();", StringComparison.Ordinal);
        var transitionIndex = patch.IndexOf("/* Transition to the new context */", StringComparison.Ordinal);
        Assert.IsTrue(policyLoadIndex >= 0 && fixIndex > policyLoadIndex && transitionIndex > fixIndex);

        var policy = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy",
            "homeharbor.te"));
        Assert.Contains("allow kernel_t tmpfs_t:dir relabelfrom;", policy);
        Assert.Contains("allow kernel_t root_t:dir relabelto;", policy);
    }

    [TestMethod]
    public void Systemd_SELinux_Variant_Labels_Dynamically_Created_Cgroup_Pressure_Files()
    {
        var root = RepositoryRoot();
        var plan = SelinuxPackageBuildDescriptor.LoadDefaultPlan(root);
        var recipe = plan.Recipes["systemd-selinux"];
        var pkgbuild = File.ReadAllText(Path.Combine(recipe.Directory, "PKGBUILD"));
        const string patchName = "0003-homeharbor-label-dynamic-cgroup-memory-pressure.patch";

        Assert.Contains("pkgrel=5", pkgbuild);
        Assert.Contains($"'{patchName}'", pkgbuild);
        Assert.Contains($"patch -Np1 -i ../{patchName}", pkgbuild);

        var patch = File.ReadAllText(Path.Combine(recipe.Directory, patchName));
        Assert.Contains("#include \"label-util.h\"", patch);
        Assert.Contains("#include \"selinux-util.h\"", patch);
        Assert.Contains("static int cg_fix_memory_pressure_labels(const char *path)", patch);
        Assert.Contains("path_startswith(path, cgroup_root)", patch);
        Assert.Contains("r = mac_selinux_init_lazy();", patch);
        Assert.Contains("path_join(current, \"memory.pressure\")", patch);
        Assert.Contains("label_fix(pressure, LABEL_IGNORE_ENOENT)", patch);
        Assert.Contains("path_equal(current, cgroup_root)", patch);
        Assert.Contains("r = cg_fix_memory_pressure_labels(fs);", patch);
        Assert.Contains("if (r < 0)\n+                return r;", patch);
        Assert.DoesNotContain("r = mac_init_lazy();", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("cgroup_t:file { getattr open read write }", patch, StringComparison.Ordinal);

        var policy = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy",
            "homeharbor.te"));
        const string creatorDomains = "{ init_t systemd_user_session_type systemd_nspawn_t }";
        Assert.Contains("domain_obj_id_change_exemption(systemd_user_session_type)", policy);
        Assert.DoesNotContain(
            "domain_obj_id_change_exemption(systemd_nspawn_t)",
            policy,
            StringComparison.Ordinal);
        Assert.Contains($"allow {creatorDomains} cgroup_t:file relabelfrom;", policy);
        Assert.Contains($"allow {creatorDomains} memory_pressure_t:file relabelto;", policy);
        Assert.Contains("seutil_read_file_contexts(systemd_user_session_type)", policy);
        Assert.Contains("selinux_use_status_page(systemd_user_session_type)", policy);
        Assert.Contains("seutil_read_config(systemd_nspawn_t)", policy);
        Assert.Contains("seutil_read_file_contexts(systemd_nspawn_t)", policy);
        Assert.Contains("selinux_use_status_page(systemd_nspawn_t)", policy);
        Assert.DoesNotContain("seutil_read_config(systemd_user_session_type)", policy, StringComparison.Ordinal);
        Assert.Contains("attribute systemd_user_session_type;", policy);
    }

    [TestMethod]
    public void Live_Sysadm_Login_Transitions_The_Installer_Into_The_HomeHarbor_Domain()
    {
        var policy = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy",
            "homeharbor.te"));

        Assert.Contains("type sysadm_t;", policy);
        Assert.Contains("role sysadm_r;", policy);
        Assert.Contains("domtrans_pattern(sysadm_t, homeharbor_exec_t, homeharbor_t)", policy);
        Assert.Contains("role sysadm_r types homeharbor_t;", policy);
        Assert.Contains("unconfined_run_to(homeharbor_t, homeharbor_exec_t)", policy);
        Assert.DoesNotContain(
            "allow sysadm_t homeharbor_exec_t:file execute_no_trans;",
            policy,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void System_And_Recovery_Select_Only_Locally_Built_SELinux_Variants()
    {
        var plan = SystemImageBuildDescriptor.LoadDefaultPlan(RepositoryRoot(), "1.0.0");
        var required = new[]
        {
            "coreutils-selinux",
            "findutils-selinux",
            "iproute2-selinux",
            "pambase-selinux",
            "pam-selinux",
            "psmisc-selinux",
            "shadow-selinux",
            "util-linux-selinux",
            "util-linux-libs-selinux",
            "systemd-selinux",
            "systemd-libs-selinux",
            "systemd-resolvconf-selinux",
            "systemd-sysvcompat-selinux",
            "dbus-selinux",
            "dbus-broker-selinux",
            "device-mapper-selinux",
            "erofs-utils-selinux",
            "selinux-refpolicy-arch",
            "homeharbor-selinux-policy"
        };
        foreach (var package in required)
        {
            Assert.Contains(package, plan.Packages.Rootfs);
            Assert.Contains(package, plan.Packages.Recovery);
        }

        foreach (var package in new[]
                 {
                     "networkmanager-selinux",
                     "libnm-selinux",
                     "openssh-selinux",
                     "sudo-selinux",
                     "crun-selinux"
                 })
        {
            Assert.Contains(package, plan.Packages.Rootfs);
        }

        foreach (var package in new[] { "systemd", "systemd-resolvconf", "device-mapper", "erofs-utils" })
        {
            Assert.DoesNotContain(package, plan.Packages.Rootfs);
            Assert.DoesNotContain(package, plan.Packages.Recovery);
        }
        foreach (var package in new[] { "networkmanager", "openssh", "sudo", "crun" })
        {
            Assert.DoesNotContain(package, plan.Packages.Rootfs);
        }

        Assert.Contains("homeharbor-control", plan.Packages.Rootfs);
        Assert.Contains("homeharbor-recovery", plan.Packages.Recovery);
        Assert.Contains("audit", plan.Packages.Rootfs);
        Assert.Contains("audit", plan.Packages.Recovery);
        Assert.Contains("auditd", plan.Rootfs.SystemdUnits);
        Assert.Contains("auditd", plan.Recovery.SystemdUnits);
        Assert.Contains("homeharbor-selinux-ready.target", plan.Rootfs.SystemdUnits);
        Assert.Contains("homeharbor-selinux-ready.target", plan.Recovery.SystemdUnits);
        Assert.Contains("lsm=landlock,lockdown,yama,integrity,selinux,bpf", plan.KernelArgs);
        Assert.Contains("enforcing=1", plan.KernelArgs);
        Assert.Contains("audit_backlog_limit=8192", plan.KernelArgs);
    }

    [TestMethod]
    public void HomeHarbor_Packages_Depend_Explicitly_On_SELinux_Variants()
    {
        var pkgbuild = File.ReadAllText(Path.Combine(RepositoryRoot(), "packaging", "arch", "PKGBUILD"));

        AssertPackageDependencies(
            pkgbuild,
            "homeharbor-control",
            ["crun-selinux", "device-mapper-selinux", "homeharbor-selinux-policy", "systemd-selinux"],
            ["crun", "device-mapper", "systemd"]);
        AssertPackageDependencies(
            pkgbuild,
            "homeharbor-recovery",
            ["device-mapper-selinux", "homeharbor-selinux-policy", "systemd-selinux"],
            ["device-mapper", "systemd"]);
        AssertPackageDependencies(
            pkgbuild,
            "homeharbor-installer",
            [
                "device-mapper-selinux",
                "erofs-utils-selinux",
                "homeharbor-selinux-policy",
                "networkmanager-selinux",
                "policycoreutils",
                "systemd-resolvconf-selinux",
                "systemd-selinux"
            ],
            ["device-mapper", "erofs-utils", "networkmanager", "systemd-resolvconf", "systemd"]);
    }

    [TestMethod]
    public async Task Generated_Pacman_Config_Prioritizes_The_Controlled_Local_Repository()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-selinux-pacman-");
        try
        {
            var packages = Directory.CreateDirectory(Path.Combine(temp.FullName, "packages"));
            var config = Path.Combine(temp.FullName, "pacman.conf");

            await ArchLocalPackageRepositoryBuilder.WritePacmanConfigAsync(config, packages.FullName);

            var contents = await File.ReadAllTextAsync(config);
            var localIndex = contents.IndexOf("[homeharbor-local]", StringComparison.Ordinal);
            var coreIndex = contents.IndexOf("[core]", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, localIndex);
            Assert.IsGreaterThan(localIndex, coreIndex);
            Assert.Contains(new Uri(packages.FullName + Path.DirectorySeparatorChar).AbsoluteUri, contents);
            Assert.DoesNotContain("archlinuxhardened", contents, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Pacstrap_Uses_The_Controlled_Config_Before_The_Target_Root()
    {
        var arguments = RootlessBuildExecutor.PacstrapArguments("/build/root", ["base"], "/build/pacman.conf");

        CollectionAssert.AreEqual(
            new[] { "-N", "-C", "/build/pacman.conf", "/build/root", "base" },
            arguments.ToArray());
        Assert.DoesNotContain("-c", arguments);
    }

    [TestMethod]
    public void Pacstrap_Result_Rejects_A_Masked_Package_Installation_Failure()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-pacstrap-result-");
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(
                temp.FullName,
                "var",
                "lib",
                "pacman",
                "local",
                "filesystem-1-1"));
            var result = new CommandResult(
                0,
                string.Empty,
                "==> ERROR: Failed to install packages to new root\n",
                "pacstrap");

            var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                RootlessBuildExecutor.ValidatePacstrapResult(temp.FullName, result));
            Assert.Contains("despite returning success", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Failed to install packages to new root", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Pacstrap_Result_Requires_A_Populated_Local_Package_Database()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-pacstrap-result-");
        try
        {
            var result = new CommandResult(0, string.Empty, string.Empty, "pacstrap");
            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                RootlessBuildExecutor.ValidatePacstrapResult(temp.FullName, result));

            _ = Directory.CreateDirectory(Path.Combine(
                temp.FullName,
                "var",
                "lib",
                "pacman",
                "local",
                "filesystem-1-1"));

            Assert.AreSame(result, RootlessBuildExecutor.ValidatePacstrapResult(temp.FullName, result));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Pacstrap_Requires_A_Disposable_Root_And_Clears_The_Target_Package_Cache()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-pacstrap-cache-");
        try
        {
            RootlessBuildExecutor.RequireEmptyPacstrapRoot(temp.FullName);

            var localPackage = Path.Combine(
                temp.FullName,
                "var",
                "lib",
                "pacman",
                "local",
                "filesystem-1-1");
            _ = Directory.CreateDirectory(localPackage);
            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                RootlessBuildExecutor.RequireEmptyPacstrapRoot(temp.FullName));

            var cache = Path.Combine(temp.FullName, "var", "cache", "pacman", "pkg");
            _ = Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "base-1-1-x86_64.pkg.tar.zst"), "package");
            File.WriteAllText(Path.Combine(cache, "download.part"), "partial");

            RootlessBuildExecutor.ClearPacstrapPackageCache(temp.FullName);

            Assert.IsTrue(Directory.Exists(cache));
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(cache).Any());
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Rootless_Chroot_Uses_The_No_Bind_Mount_Root_Option()
    {
        var arguments = RootlessBuildExecutor.MappedChrootArguments(
            "/build/root",
            "pacman",
            ["-S", "libselinux"]);

        CollectionAssert.AreEqual(
            new[] { "-N", "-r", "/build/root", "pacman", "-S", "libselinux" },
            arguments.ToArray());
    }

    [TestMethod]
    public void Rootless_Chroot_Restores_The_Configured_Systemd_Resolved_Link_After_Each_Command()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-resolver-");
        try
        {
            var source = Path.Combine(temp.FullName, "host-resolv.conf");
            File.WriteAllText(source, "nameserver 192.0.2.53\n");
            var etc = Directory.CreateDirectory(Path.Combine(temp.FullName, "root", "etc"));
            var destination = Path.Combine(etc.FullName, "resolv.conf");
            File.WriteAllText(destination, "# filesystem package placeholder\n");
            RootlessBuildExecutor.ConfigureSystemdResolved(Path.Combine(temp.FullName, "root"));
            Assert.AreEqual(RootlessBuildExecutor.SystemdResolvedTarget, new FileInfo(destination).LinkTarget);

            using (RootlessBuildExecutor.StageResolverConfiguration(
                       Path.Combine(temp.FullName, "root"),
                       source))
            {
                Assert.IsNull(new FileInfo(destination).LinkTarget);
                Assert.AreEqual("nameserver 192.0.2.53\n", File.ReadAllText(destination));
            }

            Assert.AreEqual(RootlessBuildExecutor.SystemdResolvedTarget, new FileInfo(destination).LinkTarget);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Rootless_Build_Commands_Clear_The_Desktop_And_Credential_Environment()
    {
        var options = RootlessBuildExecutor.IsolatedOptions(new CommandRunOptions(
            EnvironmentOverride: new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = "/tool/lib" }));

        Assert.IsTrue(options.ClearEnvironment);
        Assert.AreEqual("/tmp", options.Environment["HOME"]);
        Assert.AreEqual("C.UTF-8", options.Environment["LC_ALL"]);
        Assert.AreEqual("/tool/lib", options.Environment["LD_LIBRARY_PATH"]);
        Assert.IsFalse(options.Environment.ContainsKey("DISPLAY"));
        Assert.IsFalse(options.Environment.ContainsKey("DBUS_SESSION_BUS_ADDRESS"));
    }

    [TestMethod]
    public void Erofs_Labeling_Requires_A_Safe_Configured_Policy_And_Compiled_File_Contexts()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-selinux-contexts-");
        try
        {
            var selinux = Directory.CreateDirectory(Path.Combine(temp.FullName, "etc", "selinux"));
            File.WriteAllText(Path.Combine(selinux.FullName, "config"), "SELINUX=enforcing\nSELINUXTYPE=refpolicy-arch\n");
            var contexts = Path.Combine(selinux.FullName, "refpolicy-arch", "contexts", "files", "file_contexts");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(contexts)!);
            File.WriteAllText(contexts, "/.* system_u:object_r:root_t:s0\n");

            Assert.AreEqual(contexts, SelinuxErofsTool.RequireFileContexts(temp.FullName));

            File.WriteAllText(Path.Combine(selinux.FullName, "config"), "SELINUXTYPE=../escape\n");
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => SelinuxErofsTool.RequireFileContexts(temp.FullName));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Erofs_Tool_Wrappers_Safely_Quote_Local_Package_Paths()
    {
        Assert.AreEqual("'/tmp/HomeHarbor'", SelinuxErofsTool.BashSingleQuote("/tmp/HomeHarbor"));
        Assert.AreEqual("'/tmp/Home'\"'\"'Harbor'", SelinuxErofsTool.BashSingleQuote("/tmp/Home'Harbor"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => SelinuxErofsTool.BashSingleQuote("/tmp/bad\npath"));
    }

    [TestMethod]
    public void Package_Dependency_Parsing_Recognizes_Versioned_Local_Capabilities()
    {
        const string sourceInfo = """
            pkgname = systemd-libs-selinux
            provides = systemd-libs=261.1-1
            provides = libsystemd.so=0-64
            depends = libselinux>=3.10
            makedepends = util-linux-selinux
            checkdepends = python-pytest
            """;

        CollectionAssert.AreEquivalent(
            new[] { "systemd-libs-selinux", "systemd-libs", "libsystemd.so" },
            SelinuxPackageBuilder.ParseCapabilities(sourceInfo).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "libselinux", "util-linux-selinux", "python-pytest" },
            SelinuxPackageBuilder.ParseDependencies(sourceInfo).ToArray());
    }

    [TestMethod]
    public void Clean_Package_Build_Path_Includes_Arch_Perl_Command_Directories()
    {
        var entries = SelinuxPackageBuilder.CleanBuildPath.Split(':');

        CollectionAssert.Contains(entries, "/usr/bin/site_perl");
        CollectionAssert.Contains(entries, "/usr/bin/vendor_perl");
        CollectionAssert.Contains(entries, "/usr/bin/core_perl");
        CollectionAssert.Contains(entries, "/usr/bin");
    }

    [TestMethod]
    public void Coreutils_Uses_The_Official_Github_Mirror_With_A_Signed_Tag()
    {
        var pkgbuild = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "packaging",
            "arch",
            "selinux",
            "coreutils-selinux",
            "PKGBUILD"));

        Assert.Contains(
            "git+https://github.com/coreutils/${_pkgname}.git?signed#tag=v${pkgver}",
            pkgbuild);
        Assert.DoesNotContain("git+https://git.savannah.gnu.org", pkgbuild, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Findutils_Uses_The_Official_Gnu_Release_Tarball_With_A_Detached_Signature()
    {
        var pkgbuild = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "packaging",
            "arch",
            "selinux",
            "findutils-selinux",
            "PKGBUILD"));

        Assert.Contains("https://ftp.gnu.org/gnu/${_pkgname}/${_pkgname}-${pkgver}.tar.xz", pkgbuild);
        Assert.Contains("https://ftp.gnu.org/gnu/${_pkgname}/${_pkgname}-${pkgver}.tar.xz.sig", pkgbuild);
        Assert.Contains("validpgpkeys=(", pkgbuild);
        Assert.DoesNotContain("git.savannah.gnu.org", pkgbuild, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git+", pkgbuild, StringComparison.Ordinal);
    }

    [TestMethod]
    public void UtilLinux_Excludes_Only_The_Nondeterministic_Fincore_Count_Test()
    {
        var pkgbuild = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "packaging",
            "arch",
            "selinux",
            "util-linux-selinux",
            "PKGBUILD"));

        Assert.Contains(
            "../util-linux/tests/run.sh --show-diff --exclude=fincore/count",
            pkgbuild);
        Assert.AreEqual(1, Regex.Count(pkgbuild, "--exclude=", RegexOptions.CultureInvariant));
        Assert.DoesNotContain("--nocheck", pkgbuild, StringComparison.Ordinal);
        Assert.DoesNotContain("|| true", pkgbuild, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Makepkg_Retries_Only_Recognized_Transient_Git_Source_Failures()
    {
        var interruptedGitClone = new CommandResult(
            1,
            string.Empty,
            """
            ==> ERROR: Failure while downloading pam git repo
            error: RPC failed; curl 56 OpenSSL SSL_read: unexpected eof while reading
            fetch-pack: unexpected disconnect while reading sideband packet
            fatal: early EOF
            """,
            "makepkg");
        Assert.IsTrue(SelinuxPackageBuilder.IsTransientGitSourceFetchFailure(interruptedGitClone));

        var badChecksum = new CommandResult(
            1,
            string.Empty,
            "==> ERROR: One or more files did not pass the validity check!",
            "makepkg");
        Assert.IsFalse(SelinuxPackageBuilder.IsTransientGitSourceFetchFailure(badChecksum));

        var failedTest = new CommandResult(
            1,
            string.Empty,
            "FAILED (fincore/count) after an unexpected disconnect assertion",
            "makepkg");
        Assert.IsFalse(SelinuxPackageBuilder.IsTransientGitSourceFetchFailure(failedTest));
        Assert.IsFalse(SelinuxPackageBuilder.IsTransientGitSourceFetchFailure(
            interruptedGitClone with { ExitCode = 0 }));

        var permanentGitFailure = interruptedGitClone with
        {
            Stderr = """
                ==> ERROR: Failure while downloading pam git repo
                fatal: couldn't find remote ref refs/tags/v1.7.2
                """
        };
        Assert.IsFalse(SelinuxPackageBuilder.IsTransientGitSourceFetchFailure(permanentGitFailure));

        var results = new Queue<CommandResult>(
        [
            interruptedGitClone,
            interruptedGitClone,
            interruptedGitClone with { ExitCode = 0, Stderr = string.Empty }
        ]);
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var recovered = await SelinuxPackageBuilder.RunWithTransientGitSourceFetchRetryAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(results.Dequeue());
            },
            "pam-selinux",
            (value, _) =>
            {
                delays.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.AreEqual(0, recovered.ExitCode);
        Assert.AreEqual(3, attempts);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) },
            delays);

        attempts = 0;
        delays.Clear();
        var permanentCheckFailure = new CommandResult(
            1,
            string.Empty,
            """
            fatal: early EOF
            ==> ERROR: A failure occurred in check().
            """,
            "makepkg");
        var notRetried = await SelinuxPackageBuilder.RunWithTransientGitSourceFetchRetryAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(permanentCheckFailure);
            },
            "util-linux-selinux",
            (value, _) =>
            {
                delays.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.AreSame(permanentCheckFailure, notRetried);
        Assert.AreEqual(1, attempts);
        Assert.IsEmpty(delays);

        attempts = 0;
        delays.Clear();
        var exhausted = await SelinuxPackageBuilder.RunWithTransientGitSourceFetchRetryAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(interruptedGitClone);
            },
            "pam-selinux",
            (value, _) =>
            {
                delays.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.AreSame(interruptedGitClone, exhausted);
        Assert.AreEqual(3, attempts);
        Assert.HasCount(2, delays);
    }

    [TestMethod]
    public void Makepkg_Runs_With_Private_Api_Filesystems_Before_Dropping_To_The_Build_User()
    {
        var arguments = SelinuxPackageBuilder.MakepkgNamespaceArguments(
            "util-linux-selinux",
            ["--force", "--cleanbuild"]);

        CollectionAssert.AreEqual(
            new[]
            {
                "--mount",
                "--pid",
                "--fork",
                "--kill-child",
                "sh",
                "-ceu",
                SelinuxPackageBuilder.PrivateBuildMountScript,
                "sh",
                "runuser",
                "-u",
                "builder",
                "--",
                "env",
                "-i",
                "HOME=/home/builder",
                "LANG=C.UTF-8",
                "LC_ALL=C.UTF-8",
                "LOGNAME=builder",
                "PATH=" + SelinuxPackageBuilder.CleanBuildPath,
                "SHELL=/bin/bash",
                "TERM=dumb",
                "USER=builder",
                "GIT_TERMINAL_PROMPT=0",
                "PKGDEST=/packages",
                "SRCDEST=/sources",
                "BUILDDIR=/build/util-linux-selinux",
                "LOGDEST=/logs",
                "makepkg",
                "--dir",
                "/recipes/util-linux-selinux",
                "--force",
                "--cleanbuild"
            },
            arguments.ToArray());
        Assert.Contains("umount /proc", SelinuxPackageBuilder.PrivateBuildMountScript);
        Assert.Contains("mount -t proc -o nosuid,nodev,noexec proc /proc", SelinuxPackageBuilder.PrivateBuildMountScript);
        Assert.Contains("mount -t devpts -o nosuid,noexec,newinstance,ptmxmode=0666,mode=0620,gid=5", SelinuxPackageBuilder.PrivateBuildMountScript);
        Assert.Contains("mount -t tmpfs -o mode=1777,nosuid,nodev tmpfs /dev/shm", SelinuxPackageBuilder.PrivateBuildMountScript);
        Assert.Contains("exec \"$@\"", SelinuxPackageBuilder.PrivateBuildMountScript);
    }

    [TestMethod]
    public void Relabel_Units_Fail_Closed_And_Bracket_Tmpfiles_Without_Overwriting_Container_Mcs_Labels()
    {
        var root = RepositoryRoot();
        var early = File.ReadAllText(Path.Combine(root, "os", "systemd", "homeharbor-selinux-relabel.service"));
        var late = File.ReadAllText(Path.Combine(root, "os", "systemd", "homeharbor-selinux-relabel-late.service"));
        var storeSyncPath = Path.Combine(root, "os", "systemd", "homeharbor-selinux-store-sync.service");
        var storeSync = File.ReadAllText(storeSyncPath);
        var storeSyncLines = File.ReadAllLines(storeSyncPath);
        var ready = File.ReadAllText(Path.Combine(root, "os", "systemd", "homeharbor-selinux-ready.target"));

        Assert.Contains("AssertSecurity=selinux", storeSync);
        Assert.Contains("AssertSecurity=selinux", early);
        Assert.Contains("AssertSecurity=selinux", late);
        Assert.Contains("AssertPathExists=/usr/bin/restorecon", early);
        Assert.Contains("AssertPathExists=/usr/bin/restorecon", late);
        Assert.Contains("Requires=homeharbor-selinux-store-sync.service", early);
        Assert.Contains("After=local-fs.target homeharbor-selinux-store-sync.service", early);
        Assert.Contains("Before=systemd-tmpfiles-setup.service", early);
        Assert.Contains(
            "Before=systemd-tpm2-setup-early.service systemd-tpm2-setup.service systemd-pcrproduct.service",
            early);
        Assert.Contains("Requires=homeharbor-selinux-relabel.service systemd-tmpfiles-setup.service", late);
        Assert.Contains("After=homeharbor-selinux-relabel.service systemd-tmpfiles-setup.service", late);
        Assert.Contains(
            "ExecStart=/usr/lib/homeharbor/selinux-store-sync selinux-relabel persistent",
            early);
        Assert.Contains("TimeoutStartSec=6h", early);
        Assert.Contains(
            "ExecStart=/usr/lib/homeharbor/selinux-store-sync selinux-relabel managed",
            late);
        Assert.DoesNotContain("ExecStart=/usr/bin/restorecon -Rx /var", early, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecStart=/usr/bin/restorecon -Rx /var", late, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecStart=/usr/bin/restorecon -Rx /homeharbor-data", early, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecStart=/usr/bin/restorecon -Rx /homeharbor-data", late, StringComparison.Ordinal);
        Assert.Contains(
            "ExecStartPost=/usr/lib/homeharbor/selinux-store-sync selinux-ready-check",
            late);
        Assert.DoesNotContain("restorecon -F", early, StringComparison.Ordinal);
        Assert.DoesNotContain("restorecon -F", late, StringComparison.Ordinal);
        Assert.Contains("Requires=homeharbor-selinux-relabel-late.service", ready);
        Assert.Contains("After=homeharbor-selinux-relabel-late.service", ready);
        Assert.Contains("Before=sysinit.target shutdown.target", ready);
        Assert.Contains("RequiredBy=sysinit.target", ready);
        Assert.Contains(
            "Requires=homeharbor-selinux-relabel-late.service homeharbor-selinux-cgroup-relabel.service",
            ready);
        Assert.Contains(
            "After=homeharbor-selinux-relabel-late.service homeharbor-selinux-cgroup-relabel.service",
            ready);
        Assert.DoesNotContain("WantedBy=sysinit.target", early);
        Assert.DoesNotContain("WantedBy=sysinit.target", late);
        Assert.DoesNotContain("WantedBy=sysinit.target", storeSync);
        Assert.Contains("RequiresMountsFor=/var", storeSync);
        Assert.Contains("Before=homeharbor-selinux-relabel.service systemd-tmpfiles-setup.service", storeSync);
        Assert.Contains("AssertPathIsSymbolicLink=!/var/lib", storeSyncLines);
        Assert.Contains("AssertPathIsSymbolicLink=!/var/lib/selinux", storeSyncLines);
        Assert.Contains(
            "AssertPathIsSymbolicLink=!/var/lib/selinux/.homeharbor-store-sync",
            storeSyncLines);
        Assert.Contains("StateDirectory=selinux/.homeharbor-store-sync", storeSyncLines);
        Assert.DoesNotContain("StateDirectory=selinux", storeSyncLines);
        Assert.Contains("StateDirectoryMode=0700", storeSyncLines);
        Assert.Contains("ExecStart=/usr/lib/homeharbor/selinux-store-sync selinux-store-sync", storeSync);

        var storageApply = File.ReadAllText(Path.Combine(root, "os", "systemd", "homeharbor-storage-apply.service"));
        Assert.Contains(
            "ExecStartPost=/usr/lib/homeharbor/agent/HomeHarbor.Agent storage-postapply",
            storageApply);
        Assert.Contains("TimeoutStartSec=6h", storageApply);
        Assert.DoesNotContain("ExecStartPost=-", storageApply, StringComparison.Ordinal);

        var pkgbuild = File.ReadAllText(Path.Combine(root, "packaging", "arch", "PKGBUILD"));
        Assert.Contains("sysinit.target.requires/homeharbor-selinux-ready.target", pkgbuild);
        Assert.DoesNotContain("sysinit.target.wants/homeharbor-selinux", pkgbuild);

        var imageBuilder = File.ReadAllText(Path.Combine(
            root,
            "src",
            "HomeHarbor.Tooling",
            "SystemImageBuilder.cs"));
        Assert.AreEqual(
            2,
            Regex.Count(
                imageBuilder,
                "\\\"/usr/lib/systemd/system/homeharbor-selinux-cgroup-relabel\\.service\\\"",
                RegexOptions.CultureInvariant));
        Assert.AreEqual(
            2,
            Regex.Count(
                imageBuilder,
                "\\\"/usr/lib/systemd/system/sysinit\\.target\\.requires/homeharbor-selinux-cgroup-relabel\\.service\\\"",
                RegexOptions.CultureInvariant));
        Assert.AreEqual(
            2,
            Regex.Count(
                imageBuilder,
                "\\\"/usr/lib/systemd/system/homeharbor-selinux-ready\\.target\\\"",
                RegexOptions.CultureInvariant));
        Assert.AreEqual(
            2,
            Regex.Count(
                imageBuilder,
                "\\\"/usr/lib/systemd/system/sysinit\\.target\\.requires/homeharbor-selinux-ready\\.target\\\"",
                RegexOptions.CultureInvariant));

        foreach (var serviceName in new[]
        {
            "homeharbor-api.service",
            "homeharbor-boot-attempt.service",
            "homeharbor-boot-success.service",
            "homeharbor-fastbootd.service",
            "homeharbor-nmbd.service",
            "homeharbor-recovery-action.service",
            "homeharbor-smbd.service",
            "homeharbor-storage-apply.service",
            "homeharbor-tls-trust.service"
        })
        {
            var service = File.ReadAllText(Path.Combine(root, "os", "systemd", serviceName));
            Assert.Contains("Requires=homeharbor-selinux-ready.target", service);
            Assert.Contains("After=homeharbor-selinux-ready.target", service);
        }

        var bootSuccess = File.ReadAllText(Path.Combine(root, "os", "systemd", "homeharbor-boot-success.service"));
        Assert.Contains("Requires=homeharbor-selinux-ready.target homeharbor-api.service", bootSuccess);
    }

    [TestMethod]
    public void Runtime_Label_Repair_Is_Bounded_Outside_The_Epoch_Gated_Root_Scans()
    {
        var root = RepositoryRoot();
        var agent = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Agent", "Program.cs"));
        var ota = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Agent", "OtaApplyCommand.cs"));

        Assert.Contains("[\"selinux-relabel\", \"managed\"]", agent);
        Assert.Contains("[\"selinux-relabel\", \"data\"]", agent);
        Assert.Contains("[root, activeRoot, stagedRoot, versionsRoot]", agent);
        Assert.AreEqual(2, Regex.Count(agent, @"RestoreconTreeAsync\("));
        Assert.Contains("await RestoreconPathsAsync(runner, [directory, path], cancellationToken);", agent);
        Assert.DoesNotContain("RestoreconTreeAsync(runner, \"/var", agent, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreconTreeAsync(runner, \"/homeharbor-data", agent, StringComparison.Ordinal);

        Assert.Contains("failed to label the bounded OTA work directories", ota);
        Assert.DoesNotContain("restorecon\",\n            [\"-R", ota, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Runtime_Readiness_Requires_Enforcing_And_All_System_Tmpfiles_Directories()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-selinux-ready-system-");
        try
        {
            CreateReadinessRoot(
                temp.FullName,
                "/usr/lib/homeharbor/api/HomeHarbor.Api",
                SelinuxRuntimeReadiness.SystemDirectories,
                enforcing: false);

            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => SelinuxRuntimeReadiness.RequireRoot(temp.FullName));

            File.WriteAllText(
                Path.Combine(temp.FullName, SelinuxRuntimeReadiness.EnforcePath.TrimStart('/')),
                "1\n");
            SelinuxRuntimeReadiness.RequireRoot(temp.FullName);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Runtime_Readiness_Rejects_Wrong_Missing_And_Symbolic_Tmpfiles_Directories()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-selinux-ready-invalid-");
        try
        {
            CreateReadinessRoot(
                temp.FullName,
                "/usr/lib/homeharbor/api/HomeHarbor.Api",
                SelinuxRuntimeReadiness.SystemDirectories,
                enforcing: true);

            var requirement = SelinuxRuntimeReadiness.SystemDirectories[^1];
            var requiredPath = Path.Combine(temp.FullName, requirement.Path.TrimStart('/'));
            File.SetUnixFileMode(
                requiredPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => SelinuxRuntimeReadiness.RequireRoot(temp.FullName));

            Directory.Delete(requiredPath);
            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => SelinuxRuntimeReadiness.RequireRoot(temp.FullName));

            var symlinkTarget = Directory.CreateDirectory(Path.Combine(temp.FullName, "symbolic-target"));
            Directory.CreateSymbolicLink(requiredPath, symlinkTarget.FullName);
            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => SelinuxRuntimeReadiness.RequireRoot(temp.FullName));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Runtime_Readiness_Selects_And_Validates_The_Recovery_Profile()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-selinux-ready-recovery-");
        try
        {
            CreateReadinessRoot(
                temp.FullName,
                "/usr/lib/homeharbor/recovery/HomeHarbor.Recovery",
                SelinuxRuntimeReadiness.RecoveryDirectories,
                enforcing: true);

            SelinuxRuntimeReadiness.RequireRoot(temp.FullName);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Runtime_Readiness_Rejects_An_Incorrect_Tmpfiles_Owner()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-selinux-ready-owner-");
        try
        {
            CreateReadinessRoot(
                temp.FullName,
                "/usr/lib/homeharbor/api/HomeHarbor.Api",
                SelinuxRuntimeReadiness.SystemDirectories,
                enforcing: true);

            var requirement = SelinuxRuntimeReadiness.SystemDirectories[^1];
            var requiredPath = Path.Combine(temp.FullName, requirement.Path.TrimStart('/'));
            var metadata = SelinuxRuntimeReadiness.ReadDirectoryMetadata(requiredPath);
            var passwdPath = Path.Combine(temp.FullName, "etc", "passwd");
            var passwd = File.ReadAllLines(passwdPath);
            var ownerIndex = Array.FindIndex(
                passwd,
                line => line.StartsWith(requirement.Owner + ":", StringComparison.Ordinal));
            Assert.IsGreaterThanOrEqualTo(0, ownerIndex);
            var fields = passwd[ownerIndex].Split(':');
            fields[2] = (metadata.Uid == uint.MaxValue ? metadata.Uid - 1 : metadata.Uid + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            passwd[ownerIndex] = string.Join(':', fields);
            File.WriteAllLines(passwdPath, passwd);

            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => SelinuxRuntimeReadiness.RequireRoot(temp.FullName));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Runtime_Readiness_Rejects_A_Noncanonical_SELinux_Context()
    {
        SelinuxRuntimeReadiness.RequireMatchingSelinuxContext(
            "/run/homeharbor",
            "system_u:object_r:homeharbor_runtime_t:s0",
            "system_u:object_r:homeharbor_runtime_t:s0");

        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            SelinuxRuntimeReadiness.RequireMatchingSelinuxContext(
                "/run/homeharbor",
                "system_u:object_r:var_run_t:s0",
                "system_u:object_r:homeharbor_runtime_t:s0"));
    }

    [TestMethod]
    public void Runtime_Readiness_Profiles_Exactly_Match_Maintained_Tmpfiles_Directories_And_Modes()
    {
        var root = RepositoryRoot();
        AssertTmpfilesRequirementsMatch(
            Path.Combine(root, "packaging", "arch", "homeharbor.tmpfiles"),
            SelinuxRuntimeReadiness.SystemDirectories);
        AssertTmpfilesRequirementsMatch(
            Path.Combine(root, "packaging", "arch", "homeharbor-recovery.tmpfiles"),
            SelinuxRuntimeReadiness.RecoveryDirectories);

        var pkgbuild = File.ReadAllText(Path.Combine(root, "packaging", "arch", "PKGBUILD"));
        var recoveryPackage = Regex.Match(
            pkgbuild,
            @"^package_homeharbor-recovery\(\) \{(?<body>.*?)^\}",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.IsTrue(recoveryPackage.Success);
        Assert.Contains("install -dm750 \"${pkgdir}/homeharbor-data\"", recoveryPackage.Groups["body"].Value);

        var imageBuilder = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Tooling", "SystemImageBuilder.cs"));
        Assert.Contains("EnsureDirectory(Path.Combine(recoveryRootfs, \"homeharbor-data\"), 0750);", imageBuilder);

        var kernelBuilder = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Tooling", "KernelPackageBuilder.cs"));
        Assert.Contains("var recoveryDataMountPoint = Directory.CreateDirectory", kernelBuilder);
        Assert.Contains("File.SetUnixFileMode(\n            recoveryDataMountPoint.FullName", kernelBuilder);
    }

    [TestMethod]
    public void Data_Volume_Directories_Are_Not_Created_By_Early_Rootfs_Tmpfiles()
    {
        var root = RepositoryRoot();
        var tmpfiles = File.ReadAllLines(Path.Combine(root, "packaging", "arch", "homeharbor.tmpfiles"));
        Assert.IsFalse(tmpfiles.Any(line => line.StartsWith("d /homeharbor-data", StringComparison.Ordinal)));
        Assert.Contains(
            "d /var/lib/homeharbor-containers/.cache/containers 0750 homeharbor-containers homeharbor-containers -",
            tmpfiles);
        Assert.Contains(
            "d /var/lib/homeharbor-containers/.config/containers/runtime 0700 homeharbor-containers homeharbor-containers -",
            tmpfiles);

        var policyContexts = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy",
            "homeharbor.fc"));
        Assert.Contains("container_cache_home_t", policyContexts);
        Assert.Contains(
            "/var/lib/homeharbor-containers/\\.config/containers(/.*)?        gen_context(system_u:object_r:container_conf_home_t,s0)",
            policyContexts);
    }

    [TestMethod]
    public void Rootless_Podman_Config_Preserves_Confined_Identity_Without_Exposing_Root_Managed_Quadlets()
    {
        var root = RepositoryRoot();
        var tmpfiles = File.ReadAllText(Path.Combine(root, "packaging", "arch", "homeharbor.tmpfiles"));
        var agent = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Agent", "Program.cs"));
        var recipe = Path.Combine(root, "packaging", "arch", "selinux", "homeharbor-selinux-policy");
        var pkgbuild = File.ReadAllText(Path.Combine(recipe, "PKGBUILD"));
        var homeHarborPkgbuild = File.ReadAllText(Path.Combine(root, "packaging", "arch", "PKGBUILD"));
        var podmanConfig = File.ReadAllText(Path.Combine(recipe, "homeharbor-containers.conf"));
        var moduleConfig = File.ReadAllLines(Path.Combine(root, "packaging", "arch", "homeharbor-containers.modules"));
        var imageBuilder = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Tooling", "SystemImageBuilder.cs"));

        Assert.Contains(
            "d /var/lib/homeharbor-containers/.config 0750 root homeharbor-containers -",
            tmpfiles);
        Assert.Contains(
            "d /var/lib/homeharbor-containers/.config/containers/systemd 0750 root homeharbor-containers -",
            tmpfiles);
        Assert.Contains(
            "d /var/lib/homeharbor-containers/.config/containers/runtime 0700 homeharbor-containers homeharbor-containers -",
            tmpfiles);
        Assert.DoesNotContain(
            "d /var/lib/homeharbor-containers/.config 0750 homeharbor-containers",
            tmpfiles,
            StringComparison.Ordinal);
        Assert.Contains("ContainerRuntimePaths.PodmanConfigHome, 0700", agent);
        Assert.Contains("podmanConfigHome, 0700, uid, gid", agent);
        Assert.Contains("homeharbor-containers.conf", pkgbuild);
        Assert.Contains(
            "/usr/share/containers/containers.rootless.conf.d/10-homeharbor-selinux.conf",
            pkgbuild);
        Assert.Contains("[containers]", podmanConfig);
        Assert.Contains("label_users = true", podmanConfig);
        Assert.DoesNotContain("label_users = false", podmanConfig, StringComparison.Ordinal);
        CollectionAssert.AreEqual(
            new[] { "overlay" },
            moduleConfig.Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#')).ToArray());
        Assert.DoesNotContain("homeharbor-containers.modules", pkgbuild, StringComparison.Ordinal);
        Assert.Contains("homeharbor-containers.modules", homeHarborPkgbuild);
        Assert.Contains("/usr/lib/modules-load.d/homeharbor-containers.conf", homeHarborPkgbuild);
        Assert.Contains("\"/usr/lib/modules-load.d/homeharbor-containers.conf\"", imageBuilder);
    }

    [TestMethod]
    public void Samba_Private_Directory_Uses_Runtime_Type_While_Secret_Files_Stay_Restricted()
    {
        var root = RepositoryRoot();
        var policyContexts = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy",
            "homeharbor.fc"));

        Assert.Contains("/var/lib/homeharbor/samba(/.*)?", policyContexts);
        Assert.Contains("samba_var_t", policyContexts);
        Assert.Contains("private/(passdb\\.tdb|secrets\\.tdb|smbpasswd|MACHINE\\.SID)", policyContexts);
        Assert.Contains("samba_secrets_t", policyContexts);
        Assert.DoesNotContain("private(/.*)?", policyContexts, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Tmpfiles_Can_Inspect_Only_The_Systemd_Manager_Runtime_Socket()
    {
        var root = RepositoryRoot();
        var policy = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy",
            "homeharbor.te"));

        Assert.Contains("type init_runtime_t;", policy);
        Assert.Contains("allow systemd_tmpfiles_t init_runtime_t:sock_file getattr;", policy);
        Assert.DoesNotContain("allow systemd_tmpfiles_t init_runtime_t:sock_file {", policy, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Device_Mapper_Can_Complete_Only_HomeHarbor_Udev_Synchronization_Semaphores()
    {
        var root = RepositoryRoot();
        var policy = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy",
            "homeharbor.te"));

        Assert.Contains("type lvm_t;", policy);
        Assert.Contains("allow lvm_t homeharbor_t:sem { associate read write unix_read unix_write };", policy);
    }

    [TestMethod]
    public void Audit_Log_Directory_Is_Recreated_On_Both_Writable_Var_Layouts()
    {
        var root = RepositoryRoot();
        const string expected = "d /var/log/audit 0700 root root -";

        Assert.Contains(expected, File.ReadAllLines(Path.Combine(root, "packaging", "arch", "homeharbor.tmpfiles")));
        Assert.Contains(expected, File.ReadAllLines(Path.Combine(root, "packaging", "arch", "homeharbor-recovery.tmpfiles")));
    }

    [TestMethod]
    public void Audit_Rules_Load_From_The_Immutable_Image_Without_Writing_Etc()
    {
        var root = RepositoryRoot();
        var rules = File.ReadAllText(Path.Combine(root, "os", "audit", "homeharbor.rules"));
        var dropIn = File.ReadAllText(Path.Combine(
            root,
            "os",
            "systemd",
            "audit-rules.service.d",
            "homeharbor.conf"));
        var tmpfiles = File.ReadAllText(Path.Combine(root, "packaging", "arch", "homeharbor-audit.tmpfiles"));

        Assert.Contains("-b 8192", rules);
        Assert.Contains("-D", rules);
        Assert.Contains("-f 1", rules);
        Assert.Contains("-e 1", rules);
        Assert.Contains("ExecStart=\nExecStart=/usr/bin/auditctl -R /etc/audit/homeharbor.rules", dropIn);
        Assert.Contains("d /var/log/audit 0700 root root -", tmpfiles);
        Assert.Contains("z /var/log/audit 0700 root root -", tmpfiles);
        Assert.DoesNotContain("/etc/audit", tmpfiles, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Tmpfiles_Has_Only_The_Concrete_Persistent_Types_It_Must_Create()
    {
        var root = RepositoryRoot();
        var policy = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy",
            "homeharbor.te"));

        foreach (var type in new[]
                 {
                     "auditd_log_t",
                     "postgresql_db_t",
                     "systemd_networkd_var_lib_t",
                     "init_var_lib_t",
                     "uuidd_var_lib_t",
                     "var_spool_t"
                 })
        {
            Assert.Contains($"systemd_tmpfilesd_managed({type})", policy);
        }

        Assert.Contains("allow systemd_tmpfiles_t ssh_home_t:dir getattr;", policy);
        Assert.Contains("fs_list_tmpfs(getty_t)", policy);
        Assert.Contains("logging_search_logs(systemd_journal_init_t)", policy);
        Assert.Contains("files_read_etc_runtime_files(systemd_journal_init_t)", policy);
        Assert.Contains("auth_use_nsswitch(systemd_journal_init_t)", policy);
        Assert.Contains("allow systemd_journal_init_t systemd_journal_t:file map;", policy);
        Assert.Contains("kernel_read_vm_sysctls(container_engine_t)", policy);
        Assert.Contains("kernel_read_vm_sysctls(podman_user_t)", policy);
        var policyRules = string.Join(
            '\n',
            policy.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));
        Assert.DoesNotContain("systemd_tmpfiles_manage_all", policyRules, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Systemd_261_Compatibility_Grants_Only_Observed_Selinux_Operations()
    {
        var root = RepositoryRoot();
        var policy = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy",
            "homeharbor.te"));

        foreach (var rule in new[]
                 {
                     "allow systemd_generator_t selinux_config_t:lnk_file read;",
                     "fs_list_tmpfs(systemd_networkd_t)",
                     "fs_search_tmpfs(syslogd_t)",
                     "fs_getattr_nsfs_files(systemd_pcrphase_t)",
                     "fs_list_cgroup_dirs(systemd_networkd_t)",
                     "allow systemd_modules_load_t cgroup_t:file { getattr ioctl open read };",
                     "allowxperm systemd_modules_load_t cgroup_t:file ioctl 0x542a;",
                     "mount_read_runtime_files(systemd_networkd_t)",
                     "systemd_stream_connect_networkd(systemd_resolved_t)",
                     "fs_watch_memory_pressure(systemd_networkd_t)",
                     "fs_watch_memory_pressure(systemd_nsresourced_t)",
                     "fs_getattr_memory_pressure(ntpd_t)",
                     "allow systemd_nsresourced_t self:capability sys_admin;",
                     "allow systemd_nsresourced_t self:capability2 perfmon;",
                     "fs_getattr_xattr_fs(systemd_nsresourced_t)",
                     "allow systemd_machine_id_setup_t self:process getcap;",
                     "fs_getattr_xattr_fs(systemd_machine_id_setup_t)",
                     "fs_getattr_nsfs(systemd_machine_id_setup_t)",
                     "type_transition systemd_logind_t init_runtime_t:sock_file systemd_logind_runtime_t \"io.systemd.Login\";",
                     "allow systemd_logind_t init_runtime_t:dir { search write add_name remove_name };",
                     "allow systemd_logind_t systemd_logind_runtime_t:sock_file { create unlink };",
                     "allow NetworkManager_t self:capability2 bpf;",
                     "networkmanager_status(system_dbusd_t)",
                     "logging_send_audit_msgs(systemd_sysusers_t)",
                     "files_read_etc_runtime_files(systemd_user_runtime_dir_t)",
                     "fs_getattr_nsfs_files(staff_t)",
                     "allow staff_systemd_t systemd_user_runtime_notify_t:sock_file unlink;",
                     "allow local_login_t systemd_logind_runtime_t:sock_file write;",
                     "allow local_login_t systemd_logind_t:unix_stream_socket connectto;",
                     "fs_rw_tmpfs_files(systemd_networkd_t)",
                     "dontaudit systemd_networkd_t self:bpf prog_load;",
                     "dontaudit systemd_networkd_t self:capability sys_admin;",
                     "dontaudit systemd_networkd_t self:capability2 bpf;",
                     "dontaudit staff_systemd_t virtio_device_t:chr_file getattr;",
                     "fs_rw_tmpfs_files(loadkeys_t)",
                     "allow systemd_pcrphase_t var_run_t:dir { add_name search write };",
                     "type_transition systemd_pcrphase_t var_run_t:dir syslogd_tmp_t \"log\";",
                     "allow systemd_pcrphase_t syslogd_tmp_t:dir { add_name create getattr open read search setattr write };",
                     "type_transition systemd_pcrphase_t syslogd_tmp_t:dir systemd_log_t \"systemd\";",
                     "allow systemd_pcrphase_t systemd_log_t:dir { add_name create getattr open read search setattr write };",
                     "allow systemd_pcrphase_t systemd_log_t:file manage_file_perms;",
                     "init_create_runtime_dirs(systemd_pcrphase_t)",
                     "auth_use_nsswitch(systemd_pcrphase_t)",
                     "dev_write_urand(systemd_pcrphase_t)",
                     "dev_getattr_sysfs(systemd_pcrphase_t)",
                     "create_dirs_pattern(systemd_pcrphase_t, init_var_lib_t, init_var_lib_t)",
                     "files_list_boot(systemd_pcrphase_t)",
                     "fs_getattr_dos_fs(systemd_pcrphase_t)",
                     "fs_list_dos(systemd_pcrphase_t)",
                     "storage_raw_read_fixed_disk(systemd_pcrphase_t)",
                     "fs_manage_dos_dirs(systemd_pcrphase_t)",
                     "fs_manage_dos_files(systemd_pcrphase_t)",
                     "udev_read_runtime_files(systemd_pcrphase_t)",
                     "auth_use_nsswitch(systemd_user_runtime_dir_t)",
                     "fs_manage_dos_dirs(bootloader_t)",
                     "fs_manage_tmpfs_dirs(systemd_tmpfiles_t)",
                     "fs_manage_tmpfs_files(systemd_tmpfiles_t)",
                     "auth_getattr_shadow(systemd_tmpfiles_t)",
                     "allow systemd_tmpfiles_t security_t:file setattr;",
                     "allow systemd_tmpfiles_t { samba_runtime_t syslogd_runtime_t }:sock_file getattr;",
                     "allow systemd_tmpfiles_t root_t:dir setattr;",
                     "logging_dontaudit_search_audit_config(systemd_tmpfiles_t)",
                     "init_get_system_status(system_dbusd_t)",
                     "policykit_get_unit_status(system_dbusd_t)",
                     "policykit_dbus_chat(systemd_hostnamed_t)",
                     "systemd_status_networkd(policykit_t)"
                 })
        {
            Assert.Contains(rule, policy);
        }

        var modulesLoadCgroupRules = policy
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains(
                "systemd_modules_load_t cgroup_t:file",
                StringComparison.Ordinal))
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "allow systemd_modules_load_t cgroup_t:file { getattr ioctl open read };",
                "allowxperm systemd_modules_load_t cgroup_t:file ioctl 0x542a;"
            },
            modulesLoadCgroupRules);

        var notifyRules = policy
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains(
                "staff_systemd_t systemd_user_runtime_notify_t:sock_file",
                StringComparison.Ordinal))
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "allow staff_systemd_t systemd_user_runtime_notify_t:sock_file unlink;" },
            notifyRules);
        var joinedNotifyRules = string.Join('\n', notifyRules);
        Assert.DoesNotContain("manage_sock_file_perms", joinedNotifyRules, StringComparison.Ordinal);
        Assert.DoesNotContain("delete_sock_file_perms", joinedNotifyRules, StringComparison.Ordinal);

        Assert.DoesNotContain("unlabeled_t", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("fs_getattr_cgroup_files(", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("fs_read_cgroup_files(systemd_modules_load_t)", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("cgroup_t:file { getattr open read write }", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("systemd_modules_load_t cgroup_t:file { getattr ioctl lock", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("systemd_modules_load_t cgroup_t:file { getattr ioctl map", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("systemd_modules_load_t cgroup_t:file { getattr ioctl open read write", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("init_runtime_filetrans(systemd_logind_t", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("manage_sock_files_pattern(systemd_logind_t", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("init_manage_all_units(system_dbusd_t)", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("term_use_virtio_console(staff_systemd_t)", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("fs_read_nsfs_files(staff_t)", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("allow staff_systemd_t virtio_device_t:chr_file", policy, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "allow staff_systemd_t systemd_user_runtime_notify_t:sock_file manage_sock_file_perms",
            policy,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "allow systemd_user_session_type systemd_user_runtime_notify_t:sock_file",
            policy,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "allow staff_t systemd_user_runtime_notify_t:sock_file",
            policy,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void Runtime_Compatibility_Uses_Specific_Labels_And_Masks_Unused_User_Activation()
    {
        var root = RepositoryRoot();
        var recipe = Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy");
        var policy = File.ReadAllText(Path.Combine(recipe, "homeharbor.te"));
        var contexts = File.ReadAllText(Path.Combine(recipe, "homeharbor.fc"));
        var pkgbuild = File.ReadAllText(Path.Combine(recipe, "PKGBUILD"));
        var cgroupUnit = File.ReadAllText(Path.Combine(
            recipe,
            "homeharbor-selinux-cgroup-relabel.service"));

        Assert.Contains("/usr/lib/nm-daemon-helper", contexts);
        Assert.Contains("NetworkManager_exec_t", contexts);
        Assert.Contains("/home", contexts);
        Assert.Contains("home_root_t", contexts);
        foreach (var runtimeDirectory in new[]
                 {
                     "/run/homeharbor",
                     "/run/homeharbor-api",
                     "/run/homeharbor-recovery",
                     "/run/homeharbor-smb-credentials"
                 })
        {
            Assert.Contains(runtimeDirectory, contexts);
        }

        Assert.AreEqual(4, Regex.Count(contexts, @"/run/homeharbor[^\r\n]*-d gen_context\(system_u:object_r:var_run_t,s0\)"));
        Assert.Contains("/run/systemd/io\\.systemd\\.Login", contexts);
        Assert.Contains("systemd_logind_runtime_t", contexts);
        Assert.Contains("/sys/fs/cgroup/memory\\.pressure", contexts);
        Assert.Contains("/sys/fs/cgroup(/.*)?/memory\\.pressure", contexts);
        Assert.Contains("memory_pressure_t", contexts);
        Assert.Contains("/var/lib/lastlog(/.*)?", contexts);
        Assert.Contains("lastlog_t", contexts);

        Assert.Contains("systemd_tmpfilesd_managed(lastlog_t)", policy);
        Assert.Contains("allow local_login_t lastlog_t:dir manage_dir_perms;", policy);
        Assert.Contains("allow local_login_t lastlog_t:file manage_file_perms;", policy);
        Assert.DoesNotContain("allow NetworkManager_t lib_t:file", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("allow local_login_t var_lib_t", policy, StringComparison.Ordinal);

        Assert.Contains("policy_module(homeharbor, 1.0.40)", policy);
        Assert.Contains("container_search_config(sysadm_systemd_t)", policy);
        Assert.DoesNotContain("container_read_config(sysadm_systemd_t)", policy, StringComparison.Ordinal);
        Assert.Contains("pkgrel=42", pkgbuild);
        Assert.Contains("homeharbor-selinux-cgroup-relabel.service", pkgbuild);
        Assert.Contains("systemd-homed.service", pkgbuild);
        Assert.Contains("dirmngr.socket", pkgbuild);
        Assert.Contains("gpg-agent-ssh.socket", pkgbuild);
        Assert.Contains("keyboxd.socket", pkgbuild);
        Assert.Contains("sysinit.target.requires/homeharbor-selinux-cgroup-relabel.service", pkgbuild);
        Assert.Contains("ExecStart=/usr/bin/restorecon -i -RF /sys/fs/cgroup", cgroupUnit);
        Assert.DoesNotContain("ExecStart=/usr/bin/restorecon -RF /sys/fs/cgroup", cgroupUnit);
        Assert.Contains("Before=systemd-journald.service", cgroupUnit);
        Assert.Contains("Before=homeharbor-selinux-ready.target", cgroupUnit);
    }

    [TestMethod]
    public void Samba_Daemon_Can_Map_But_Not_Relabel_Secret_Databases()
    {
        var root = RepositoryRoot();
        var policy = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy",
            "homeharbor.te"));

        Assert.Contains("type smbd_t;", policy);
        Assert.Contains("allow smbd_t samba_secrets_t:file map;", policy);
        Assert.DoesNotContain("allow smbd_t samba_secrets_t:file relabel", policy, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Agent_Service_Control_Is_Limited_To_Appliance_And_Rootless_Unit_Actions()
    {
        var root = RepositoryRoot();
        var policy = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "homeharbor-selinux-policy",
            "homeharbor.te"));

        Assert.Contains("init_start_generic_units(homeharbor_t)", policy);
        Assert.Contains("init_get_generic_units_status(homeharbor_t)", policy);
        Assert.Contains("systemd_start_user_manager_units(homeharbor_t)", policy);
        Assert.Contains("systemd_get_user_manager_units_status(homeharbor_t)", policy);
        Assert.Contains("systemd_start_user_runtime_units(homeharbor_t)", policy);
        Assert.Contains("systemd_stop_user_runtime_units(homeharbor_t)", policy);
        Assert.Contains("systemd_get_user_runtime_units_status(homeharbor_t)", policy);
        Assert.Contains("init_get_generic_units_status(staff_t)", policy);
        Assert.Contains(
            "allow homeharbor_t staff_systemd_t:system { start stop status reload };",
            policy);
        Assert.Contains("allow staff_systemd_t homeharbor_t:dbus send_msg;", policy);
        Assert.Contains("allow staff_dbusd_t staff_systemd_t:system status;", policy);
        var staffDbusSystemPermissions = Regex.Matches(
                policy,
                @"(?m)^\s*allow\s+staff_dbusd_t\s+staff_systemd_t\s*:\s*system\s+(?<permissions>\{[^}]*\}|[A-Za-z_][A-Za-z0-9_]*)\s*;",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .SelectMany(match => match.Groups["permissions"].Value
                .Trim('{', '}')
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
        CollectionAssert.AreEqual(new[] { "status" }, staffDbusSystemPermissions);
        Assert.Contains("ps_process_pattern(staff_systemd_t, homeharbor_t)", policy);
        Assert.Contains("dev_read_urand(podman_user_t)", policy);
        Assert.Contains("fs_getattr_bpf(podman_user_t)", policy);
        Assert.Contains("dbus_spec_session_bus_client(staff, podman_user_t)", policy);
        Assert.Contains("allow podman_user_conmon_t podman_user_t:process noatsecure;", policy);
        CollectionAssert.AreEqual(
            new[] { "allow podman_user_conmon_t podman_user_t:process noatsecure;" },
            policy.Split('\n')
                .Select(line => line.Trim())
                .Where(line => Regex.IsMatch(line, @"\bnoatsecure\b", RegexOptions.CultureInvariant))
                .ToArray());
        Assert.Contains("systemd_user_send_systemd_notify(staff, podman_user_t)", policy);
        Assert.DoesNotContain("kernel_request_load_module(podman_user_t)", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("kernel_load_module(podman_user_t)", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("init_dbus_chat(podman_user_t)", policy, StringComparison.Ordinal);
        Assert.IsFalse(Regex.IsMatch(
            policy,
            @"(?m)^\s*allow\s+podman_user_t\s+kernel_t\s*:\s*system\s+(?:\{[^}]*\b(?:module_request|module_load)\b[^}]*\}|(?:module_request|module_load))\s*;",
            RegexOptions.CultureInvariant));
        Assert.IsFalse(Regex.IsMatch(
            policy,
            @"(?m)^\s*allow\s+podman_user_t\s+init_t\s*:\s*dbus\s+(?:\{[^}]*\bsend_msg\b[^}]*\}|send_msg)\s*;",
            RegexOptions.CultureInvariant));
        Assert.Contains("allow staff_systemd_t container_user_runtime_t:dir { getattr search };", policy);
        Assert.Contains("init_reboot_system(homeharbor_t)", policy);
        Assert.DoesNotContain("init_admin(homeharbor_t)", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("init_manage_all_units(homeharbor_t)", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("init_get_all_units_status(homeharbor_t)", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("init_get_system_status(homeharbor_t)", policy, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Refpolicy_Uses_Mcs_And_Maps_Only_Interactive_Appliance_Users()
    {
        var root = RepositoryRoot();
        var recipe = Path.Combine(root, "packaging", "arch", "selinux", "selinux-refpolicy-arch");
        var pkgbuild = File.ReadAllText(Path.Combine(recipe, "PKGBUILD"));
        var seusers = File.ReadAllText(Path.Combine(recipe, "seusers"));
        var semanageConfig = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "arch",
            "selinux",
            "libsemanage",
            "semanage.conf"));

        Assert.Contains("TYPE = mcs", pkgbuild);
        Assert.Contains("config/appconfig-mcs/seusers", pkgbuild);
        Assert.Contains("semodule-utils", pkgbuild);
        Assert.Contains("store-root = /var/lib/selinux", semanageConfig);
        Assert.DoesNotContain("$pkgdir/var/lib/selinux", pkgbuild, StringComparison.Ordinal);
        Assert.Contains("homeharbor-containers:staff_u:s0", seusers);
        Assert.Contains("recovery:unconfined_u:s0", seusers);
        Assert.DoesNotContain("homeharbor:staff_u", seusers, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Package_Set_Provenance_Rejects_A_Stale_Version_Or_Modified_Archive()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-package-provenance-");
        try
        {
            var recipeRoot = Directory.CreateDirectory(Path.Combine(temp.FullName, "packaging", "arch", "selinux", "policy"));
            await File.WriteAllTextAsync(Path.Combine(recipeRoot.FullName, "PKGBUILD"), "pkgname=policy\n");
            var packages = Directory.CreateDirectory(Path.Combine(temp.FullName, "packages"));
            var archive = Path.Combine(packages.FullName, "policy-1-1-any.pkg.tar.zst");
            await File.WriteAllTextAsync(archive, "first build");

            var sourceDigest = ArchPackageSetProvenance.ComputeSelinuxSourceSha256(temp.FullName);
            await ArchPackageSetProvenance.WriteAsync(temp.FullName, "1.2.3", packages.FullName, sourceDigest);
            await ArchPackageSetProvenance.VerifyAsync(temp.FullName, "1.2.3", packages.FullName);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => ArchPackageSetProvenance.VerifyAsync(temp.FullName, "1.2.4", packages.FullName));

            var pkgbuild = Path.Combine(recipeRoot.FullName, "PKGBUILD");
            await File.AppendAllTextAsync(pkgbuild, "pkgver=2\n");
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => ArchPackageSetProvenance.WriteAsync(
                    temp.FullName,
                    "1.2.3",
                    packages.FullName,
                    sourceDigest));
            await File.WriteAllTextAsync(pkgbuild, "pkgname=policy\n");

            var originalMode = File.GetUnixFileMode(pkgbuild);
            File.SetUnixFileMode(pkgbuild, originalMode | UnixFileMode.UserExecute);
            Assert.AreNotEqual(sourceDigest, ArchPackageSetProvenance.ComputeSelinuxSourceSha256(temp.FullName));
            File.SetUnixFileMode(pkgbuild, originalMode);

            await File.WriteAllTextAsync(archive, "modified build");
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => ArchPackageSetProvenance.VerifyAsync(temp.FullName, "1.2.3", packages.FullName));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Maintained_Recipe_Copy_And_Provenance_Ignore_Makepkg_Build_And_Vcs_Caches()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-package-source-cache-");
        try
        {
            var sourceRoot = Directory.CreateDirectory(Path.Combine(temp.FullName, "packaging", "arch", "selinux"));
            var recipe = Directory.CreateDirectory(Path.Combine(sourceRoot.FullName, "systemd-selinux"));
            File.WriteAllText(Path.Combine(recipe.FullName, "PKGBUILD"), "pkgname=systemd-selinux\n");
            File.WriteAllText(Path.Combine(recipe.FullName, "fix.patch"), "maintained patch\n");

            var sourceDigest = ArchPackageSetProvenance.ComputeSelinuxSourceSha256(temp.FullName);
            var src = Directory.CreateDirectory(Path.Combine(recipe.FullName, "src"));
            File.WriteAllText(Path.Combine(src.FullName, "generated.c"), "generated source\n");
            var pkg = Directory.CreateDirectory(Path.Combine(recipe.FullName, "pkg"));
            File.WriteAllText(Path.Combine(pkg.FullName, "generated-package"), "generated package\n");
            var vcsCache = Directory.CreateDirectory(Path.Combine(recipe.FullName, "systemd"));
            File.WriteAllText(Path.Combine(vcsCache.FullName, "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(vcsCache.FullName, "config"), "[core]\n\tbare = true\n");
            _ = Directory.CreateDirectory(Path.Combine(vcsCache.FullName, "objects"));
            _ = Directory.CreateDirectory(Path.Combine(vcsCache.FullName, "refs"));
            File.WriteAllText(Path.Combine(vcsCache.FullName, "objects", "pack"), "generated VCS cache\n");

            Assert.AreEqual(sourceDigest, ArchPackageSetProvenance.ComputeSelinuxSourceSha256(temp.FullName));
            Assert.IsFalse(ArchPackageSetProvenance.IsMaintainedSource(
                sourceRoot.FullName,
                Path.Combine(vcsCache.FullName, "objects", "pack")));

            var destination = Path.Combine(temp.FullName, "copied-recipe");
            FileTreeCopier.CopyDirectory(
                recipe.FullName,
                destination,
                path => ArchPackageSetProvenance.IsMaintainedSource(recipe.FullName, path));
            Assert.IsTrue(File.Exists(Path.Combine(destination, "PKGBUILD")));
            Assert.IsTrue(File.Exists(Path.Combine(destination, "fix.patch")));
            Assert.IsFalse(Directory.Exists(Path.Combine(destination, "src")));
            Assert.IsFalse(Directory.Exists(Path.Combine(destination, "pkg")));
            Assert.IsFalse(Directory.Exists(Path.Combine(destination, "systemd")));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Clean_Source_Input_Selection_Excludes_Makepkg_Caches_Inside_Selinux_Recipes()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-clean-source-input-");
        try
        {
            var recipe = Directory.CreateDirectory(Path.Combine(
                temp.FullName,
                "packaging",
                "arch",
                "selinux",
                "systemd-selinux"));
            var pkgbuild = Path.Combine(recipe.FullName, "PKGBUILD");
            File.WriteAllText(pkgbuild, "pkgname=systemd-selinux\n");
            var patch = Path.Combine(recipe.FullName, "fix.patch");
            File.WriteAllText(patch, "maintained patch\n");
            var generated = Directory.CreateDirectory(Path.Combine(recipe.FullName, "src"));
            var generatedFile = Path.Combine(generated.FullName, "generated.c");
            File.WriteAllText(generatedFile, "generated source\n");
            var vcsCache = Directory.CreateDirectory(Path.Combine(recipe.FullName, "systemd"));
            File.WriteAllText(Path.Combine(vcsCache.FullName, "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(vcsCache.FullName, "config"), "[core]\n\tbare = true\n");
            _ = Directory.CreateDirectory(Path.Combine(vcsCache.FullName, "objects"));
            _ = Directory.CreateDirectory(Path.Combine(vcsCache.FullName, "refs"));
            var vcsObject = Path.Combine(vcsCache.FullName, "objects", "pack");
            File.WriteAllText(vcsObject, "generated VCS cache\n");

            var selected = BuildToolCommands.SelectCleanSourcePaths(
                temp.FullName,
                [
                    Path.GetRelativePath(temp.FullName, pkgbuild),
                    Path.GetRelativePath(temp.FullName, patch),
                    Path.GetRelativePath(temp.FullName, generatedFile),
                    Path.GetRelativePath(temp.FullName, vcsObject)
                ]);

            CollectionAssert.AreEquivalent(new[] { pkgbuild, patch }, selected.ToArray());
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Selinux_Source_Provenance_Hashes_Link_Targets_Without_Following_Directory_Links()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-selinux-source-link-");
        try
        {
            var recipe = Directory.CreateDirectory(Path.Combine(
                temp.FullName,
                "packaging",
                "arch",
                "selinux",
                "policy"));
            File.WriteAllText(Path.Combine(recipe.FullName, "PKGBUILD"), "pkgname=policy\n");
            File.WriteAllText(Path.Combine(recipe.FullName, "first.patch"), "first\n");
            File.WriteAllText(Path.Combine(recipe.FullName, "second.patch"), "second\n");
            var sourceLink = Path.Combine(recipe.FullName, "selected.patch");
            _ = File.CreateSymbolicLink(sourceLink, "first.patch");

            var external = Directory.CreateDirectory(Path.Combine(temp.FullName, "external"));
            var externalFile = Path.Combine(external.FullName, "not-maintained.txt");
            File.WriteAllText(externalFile, "outside version one\n");
            _ = Directory.CreateSymbolicLink(Path.Combine(recipe.FullName, "external-link"), external.FullName);

            var firstDigest = ArchPackageSetProvenance.ComputeSelinuxSourceSha256(temp.FullName);
            File.WriteAllText(externalFile, "outside version two\n");
            Assert.AreEqual(firstDigest, ArchPackageSetProvenance.ComputeSelinuxSourceSha256(temp.FullName));

            File.Delete(sourceLink);
            _ = File.CreateSymbolicLink(sourceLink, "second.patch");
            Assert.AreNotEqual(firstDigest, ArchPackageSetProvenance.ComputeSelinuxSourceSha256(temp.FullName));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Offline_Policy_Validation_Requires_Compiled_Policy_Modules_And_Exact_Seusers()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-selinux-policy-");
        try
        {
            var policyRoot = Path.Combine(temp.FullName, "etc", "selinux", "refpolicy-arch");
            _ = Directory.CreateDirectory(Path.Combine(policyRoot, "contexts", "files"));
            _ = Directory.CreateDirectory(Path.Combine(policyRoot, "policy"));
            var modules = Directory.CreateDirectory(Path.Combine(
                temp.FullName,
                "var",
                "lib",
                "selinux",
                "refpolicy-arch",
                "active",
                "modules",
                "100",
                "base"));
            File.WriteAllText(Path.Combine(modules.FullName, "cil"), "compiled module");
            File.WriteAllText(
                Path.Combine(temp.FullName, "etc", "selinux", "semanage.conf"),
                "module-store = direct\nstore-root = /var/lib/selinux\n");
            File.WriteAllText(
                Path.Combine(temp.FullName, "etc", "selinux", "config"),
                "SELINUX=enforcing\nSELINUXTYPE=refpolicy-arch\n");
            File.WriteAllText(Path.Combine(policyRoot, "contexts", "files", "file_contexts"), "/.* root_t\n");
            File.WriteAllText(Path.Combine(policyRoot, "policy", "policy.34"), "compiled");
            File.WriteAllText(
                Path.Combine(policyRoot, "seusers"),
                "root:root:s0-s0:c0.c1023\n" +
                "homeharbor-containers:staff_u:s0-s0\n" +
                "recovery:unconfined_u:s0-s0\n" +
                "__default__:user_u:s0-s0\n");

            Assert.AreEqual("refpolicy-arch", SelinuxPolicyValidator.ValidateFileSystem(temp.FullName, "test rootfs"));
            SelinuxPolicyValidator.ValidateModuleListing("base 1.0\ncontainer 1.0\nhomeharbor 1.0\n", "test rootfs");

            File.AppendAllText(Path.Combine(policyRoot, "seusers"), "homeharbor:staff_u:s0\n");
            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => SelinuxPolicyValidator.ValidateFileSystem(temp.FullName, "test rootfs"));
            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => SelinuxPolicyValidator.ValidateModuleListing("base 1.0\ncontainer 1.0\n", "test rootfs"));

            File.WriteAllText(
                Path.Combine(policyRoot, "seusers"),
                "root:root:s0-s0:c0.c1023\n" +
                "homeharbor-containers:staff_u:s0-s0\n" +
                "recovery:unconfined_u:s0-s0\n" +
                "__default__:user_u:s0-s0\n");
            File.WriteAllText(
                Path.Combine(temp.FullName, "etc", "selinux", "semanage.conf"),
                "module-store = direct\nstore-root = /etc/selinux\n");
            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => SelinuxPolicyValidator.ValidateFileSystem(temp.FullName, "test rootfs"));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Policy_Store_Seed_Is_Hashed_And_Atomically_Replaces_Stale_State()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-selinux-store-");
        try
        {
            var rootfs = Directory.CreateDirectory(Path.Combine(temp.FullName, "rootfs"));
            var source = Path.Combine(
                rootfs.FullName,
                SelinuxPolicyStoreSynchronizer.RuntimeStorePath.TrimStart('/'));
            var module = Path.Combine(source, "active", "modules", "100", "base", "cil");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(module)!);
            File.WriteAllText(module, "base policy\n");
            File.SetUnixFileMode(module, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(Path.Combine(source, "semanage.read.LOCK"), string.Empty);

            var digest = SelinuxPolicyStoreSynchronizer.PrepareImmutableSeed(rootfs.FullName);
            var seed = Path.Combine(
                rootfs.FullName,
                SelinuxPolicyStoreSynchronizer.ImmutableSeedPath.TrimStart('/'));
            Assert.IsFalse(Directory.Exists(source));
            Assert.AreEqual(
                digest,
                File.ReadAllText(Path.Combine(seed, SelinuxPolicyStoreSynchronizer.DigestFileName)).Trim());

            var runtime = Path.Combine(temp.FullName, "state", "var", "lib", "selinux", "refpolicy-arch");
            Assert.IsTrue(SelinuxPolicyStoreSynchronizer.Synchronize(seed, runtime));
            Assert.IsFalse(SelinuxPolicyStoreSynchronizer.Synchronize(seed, runtime));
            Assert.AreEqual("base policy\n", File.ReadAllText(Path.Combine(runtime, "active", "modules", "100", "base", "cil")));

            File.WriteAllText(Path.Combine(runtime, "active", "modules", "100", "base", "cil"), "tampered\n");
            File.WriteAllText(Path.Combine(runtime, "stale-module"), "must disappear\n");
            Assert.IsTrue(SelinuxPolicyStoreSynchronizer.Synchronize(seed, runtime));
            Assert.AreEqual("base policy\n", File.ReadAllText(Path.Combine(runtime, "active", "modules", "100", "base", "cil")));
            Assert.IsFalse(File.Exists(Path.Combine(runtime, "stale-module")));

            var storeLink = Path.Combine(seed, "external-policy-link");
            _ = File.CreateSymbolicLink(storeLink, module);
            var linkError = Assert.ThrowsExactly<InvalidOperationException>(
                () => SelinuxPolicyStoreSynchronizer.Synchronize(seed, runtime));
            Assert.Contains("must not contain symbolic links", linkError.Message);
            File.Delete(storeLink);

            File.WriteAllText(Path.Combine(seed, "active", "modules", "100", "base", "cil"), "corrupt seed\n");
            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => SelinuxPolicyStoreSynchronizer.Synchronize(seed, runtime));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Relabel_Label_Contract_Rejects_Symbolic_Links()
    {
        using var fixture = SelinuxRelabelFixture.Create();
        var link = Path.Combine(fixture.Paths.Contexts, "linked-contexts");
        _ = File.CreateSymbolicLink(link, fixture.ContextFile);

        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths));
        Assert.Contains("must not contain symbolic links", error.Message);
    }

    [TestMethod]
    public void Store_Synchronization_Rejects_A_Symbolic_State_Parent_Before_Creating_Lock_Or_Store()
    {
        using var fixture = SelinuxRelabelFixture.Create();
        var state = Path.GetDirectoryName(fixture.Paths.Lock)!;
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root.FullName, "external-state"));
        Directory.Delete(state, recursive: true);
        _ = Directory.CreateSymbolicLink(state, external.FullName);

        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths));

        Assert.Contains("must not contain symbolic links", error.Message);
        Assert.IsFalse(File.Exists(Path.Combine(external.FullName, Path.GetFileName(fixture.Paths.Lock))));
        Assert.IsFalse(Directory.Exists(Path.Combine(
            external.FullName,
            Path.GetFileName(fixture.Paths.RuntimeStore))));
    }

    [TestMethod]
    public void Relabel_Epoch_Is_Persisted_Before_Store_Replacement_And_Rotates_On_Repair()
    {
        using var fixture = SelinuxRelabelFixture.Create();
        SelinuxPolicyEpoch? persisted = null;

        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            SelinuxRelabelCoordinator.SynchronizeDetailed(
                fixture.Paths,
                epoch =>
                {
                    persisted = epoch;
                    Assert.IsTrue(File.Exists(fixture.Paths.Epoch));
                    Assert.IsFalse(Directory.Exists(fixture.Paths.RuntimeStore));
                    throw new InvalidOperationException("simulated interruption after epoch persistence");
                }));

        Assert.IsNotNull(persisted);
        Assert.IsFalse(Directory.Exists(fixture.Paths.RuntimeStore));

        var installed = SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths);
        Assert.IsTrue(installed.StoreReplaced);
        Assert.AreNotEqual(persisted.Generation, installed.Epoch.Generation);

        var current = SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths);
        Assert.IsFalse(current.StoreReplaced);
        Assert.AreEqual(installed.Epoch, current.Epoch);

        File.WriteAllText(fixture.RuntimePayload, "tampered runtime policy\n");
        var repaired = SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths);
        Assert.IsTrue(repaired.StoreReplaced);
        Assert.AreNotEqual(current.Epoch.Generation, repaired.Epoch.Generation);
        Assert.AreEqual("base policy\n", File.ReadAllText(fixture.RuntimePayload));
    }

    [TestMethod]
    public async Task Persistent_And_Data_Relabel_Exactly_Once_Per_Policy_Epoch()
    {
        using var fixture = SelinuxRelabelFixture.Create();
        _ = SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths);
        var runner = new SelinuxRelabelRecordingRunner();
        var apps = Directory.CreateDirectory(Path.Combine(fixture.Paths.DataRoot, "apps")).FullName;
        var active = Directory.CreateDirectory(Path.Combine(
            fixture.Paths.DataRoot,
            "system-apps",
            "active")).FullName;
        var postgresData = Directory.CreateDirectory(Path.Combine(
            fixture.Paths.DataRoot,
            "postgresql",
            "data")).FullName;

        Assert.IsTrue(await SelinuxRelabelCoordinator.RelabelPersistentAsync(
            runner,
            fixture.Paths,
            CancellationToken.None));
        Assert.IsFalse(await SelinuxRelabelCoordinator.RelabelPersistentAsync(
            runner,
            fixture.Paths,
            CancellationToken.None));
        var beforeFirstData = runner.Calls.Count;
        Assert.IsTrue(await SelinuxRelabelCoordinator.RelabelDataAsync(
            runner,
            fixture.Paths,
            requireMountPoint: false,
            CancellationToken.None));
        var beforeSecondData = runner.Calls.Count;
        Assert.IsFalse(await SelinuxRelabelCoordinator.RelabelDataAsync(
            runner,
            fixture.Paths,
            requireMountPoint: false,
            CancellationToken.None));

        CollectionAssert.AreEqual(
            new[] { "-Rx", fixture.Paths.PersistentRoot },
            runner.Calls[0].Arguments);
        CollectionAssert.AreEqual(
            new[] { "-Rx", fixture.Paths.DataRoot },
            runner.Calls[beforeFirstData].Arguments);

        var secondDataCalls = runner.Calls.Skip(beforeSecondData).ToArray();
        Assert.IsNotEmpty(secondDataCalls);
        Assert.IsTrue(secondDataCalls.All(call => call.Arguments.Length == 1));
        Assert.IsTrue(secondDataCalls.All(call => !call.Arguments.Contains("-Rx", StringComparer.Ordinal)));
        CollectionAssert.Contains(secondDataCalls.SelectMany(call => call.Arguments).ToArray(), fixture.Paths.DataRoot);
        CollectionAssert.Contains(secondDataCalls.SelectMany(call => call.Arguments).ToArray(), apps);
        CollectionAssert.Contains(secondDataCalls.SelectMany(call => call.Arguments).ToArray(), active);
        CollectionAssert.Contains(secondDataCalls.SelectMany(call => call.Arguments).ToArray(), postgresData);
    }

    [TestMethod]
    public async Task Label_Contract_Change_Rotates_Epoch_Without_Replacing_Store_And_Relabels_Both_Roots()
    {
        using var fixture = SelinuxRelabelFixture.Create();
        var initial = SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths);
        var runner = new SelinuxRelabelRecordingRunner();
        _ = await SelinuxRelabelCoordinator.RelabelPersistentAsync(
            runner,
            fixture.Paths,
            CancellationToken.None);
        _ = await SelinuxRelabelCoordinator.RelabelDataAsync(
            runner,
            fixture.Paths,
            requireMountPoint: false,
            CancellationToken.None);

        File.AppendAllText(fixture.ContextFile, "/new(/.*)? system_u:object_r:var_t:s0\n");
        var updated = SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths);
        Assert.IsFalse(updated.StoreReplaced);
        Assert.AreEqual(initial.Epoch.StoreSha256, updated.Epoch.StoreSha256);
        Assert.AreNotEqual(initial.Epoch.LabelContractSha256, updated.Epoch.LabelContractSha256);
        Assert.AreNotEqual(initial.Epoch.Generation, updated.Epoch.Generation);

        Assert.IsTrue(await SelinuxRelabelCoordinator.RelabelPersistentAsync(
            runner,
            fixture.Paths,
            CancellationToken.None));
        Assert.IsTrue(await SelinuxRelabelCoordinator.RelabelDataAsync(
            runner,
            fixture.Paths,
            requireMountPoint: false,
            CancellationToken.None));
        Assert.AreEqual(
            4,
            runner.Calls.Count(call => call.Arguments.Contains("-Rx", StringComparer.Ordinal)));
    }

    [TestMethod]
    public async Task Failed_Relabel_Does_Not_Commit_Marker_And_Symbolic_Marker_Is_Rejected()
    {
        using var fixture = SelinuxRelabelFixture.Create();
        _ = SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths);
        var failing = new SelinuxRelabelRecordingRunner((fileName, _, _) =>
            Task.FromResult(new CommandResult(1, string.Empty, "simulated failure", fileName)));

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SelinuxRelabelCoordinator.RelabelPersistentAsync(
                failing,
                fixture.Paths,
                CancellationToken.None));
        Assert.IsFalse(File.Exists(fixture.Paths.PersistentMarker));

        var succeeding = new SelinuxRelabelRecordingRunner();
        Assert.IsTrue(await SelinuxRelabelCoordinator.RelabelPersistentAsync(
            succeeding,
            fixture.Paths,
            CancellationToken.None));
        Assert.IsTrue(File.Exists(fixture.Paths.PersistentMarker));

        File.Delete(fixture.Paths.PersistentMarker);
        var target = Path.Combine(fixture.Root.FullName, "marker-target");
        File.WriteAllText(target, string.Empty);
        File.CreateSymbolicLink(fixture.Paths.PersistentMarker, target);
        var rejected = new SelinuxRelabelRecordingRunner();
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SelinuxRelabelCoordinator.RelabelPersistentAsync(
                rejected,
                fixture.Paths,
                CancellationToken.None));
        Assert.IsEmpty(rejected.Calls);
    }

    [TestMethod]
    public async Task Data_Marker_Is_Always_Relabeled_Exactly_And_A_Failed_Label_Is_Retried()
    {
        using var fixture = SelinuxRelabelFixture.Create();
        _ = SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths);
        var failing = new SelinuxRelabelRecordingRunner((fileName, arguments, _) =>
            Task.FromResult(
                arguments.SequenceEqual([fixture.Paths.DataMarker], StringComparer.Ordinal)
                    ? new CommandResult(1, string.Empty, "simulated marker label failure", fileName)
                    : new CommandResult(0, string.Empty, string.Empty, fileName)));

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SelinuxRelabelCoordinator.RelabelDataAsync(
                failing,
                fixture.Paths,
                requireMountPoint: false,
                CancellationToken.None));
        Assert.IsTrue(File.Exists(fixture.Paths.DataMarker));
        Assert.IsTrue(failing.Calls.Any(call =>
            call.Arguments.SequenceEqual([fixture.Paths.DataMarker], StringComparer.Ordinal)));

        var retry = new SelinuxRelabelRecordingRunner();
        Assert.IsFalse(await SelinuxRelabelCoordinator.RelabelDataAsync(
            retry,
            fixture.Paths,
            requireMountPoint: false,
            CancellationToken.None));
        Assert.IsFalse(retry.Calls.Any(call => call.Arguments.Contains("-Rx", StringComparer.Ordinal)));
        Assert.IsTrue(retry.Calls.Any(call =>
            call.Arguments.SequenceEqual([fixture.Paths.DataMarker], StringComparer.Ordinal)));
    }

    [TestMethod]
    public async Task Managed_Path_Relabel_Is_Nonrecursive_And_Rejects_Symbolic_Paths()
    {
        using var fixture = SelinuxRelabelFixture.Create();
        _ = SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths);
        _ = await SelinuxRelabelCoordinator.RelabelPersistentAsync(
            new SelinuxRelabelRecordingRunner(),
            fixture.Paths,
            CancellationToken.None);

        var first = Directory.CreateDirectory(Path.Combine(fixture.Root.FullName, "managed-a")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(fixture.Root.FullName, "managed-b")).FullName;
        var runner = new SelinuxRelabelRecordingRunner();
        await SelinuxRelabelCoordinator.RelabelManagedPathsAsync(
            runner,
            fixture.Paths,
            [second, first],
            CancellationToken.None);

        Assert.HasCount(2, runner.Calls);
        CollectionAssert.AreEqual(new[] { first }, runner.Calls[0].Arguments);
        CollectionAssert.AreEqual(new[] { second }, runner.Calls[1].Arguments);
        Assert.IsFalse(runner.Calls
            .SelectMany(call => call.Arguments)
            .Any(argument => argument.StartsWith("-R", StringComparison.Ordinal)));

        var missingOptional = Path.Combine(fixture.Root.FullName, "optional-linger");
        var optional = new SelinuxRelabelRecordingRunner();
        await SelinuxRelabelCoordinator.RelabelManagedPathsAsync(
            optional,
            fixture.Paths,
            [
                new SelinuxRelabelCoordinator.ManagedPath(missingOptional, Required: false),
                new SelinuxRelabelCoordinator.ManagedPath(first, Required: true)
            ],
            CancellationToken.None);
        Assert.HasCount(1, optional.Calls);
        CollectionAssert.AreEqual(new[] { first }, optional.Calls[0].Arguments);

        var link = Path.Combine(fixture.Root.FullName, "managed-link");
        Directory.CreateSymbolicLink(link, first);
        var rejected = new SelinuxRelabelRecordingRunner();
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SelinuxRelabelCoordinator.RelabelManagedPathsAsync(
                rejected,
                fixture.Paths,
                [link],
                CancellationToken.None));
        Assert.IsEmpty(rejected.Calls);
    }

    [TestMethod]
    public void Default_Managed_Profile_Keeps_Readiness_Paths_Required_And_Firstboot_Paths_Optional()
    {
        foreach (var requirement in SelinuxRuntimeReadiness.SystemDirectories)
        {
            Assert.IsTrue(SelinuxRelabelCoordinator.SystemManagedPaths.Any(path =>
                path.Required && string.Equals(path.Path, requirement.Path, StringComparison.Ordinal)));
        }

        foreach (var optional in new[]
        {
            "/var/lib/homeharbor/storage",
            "/var/lib/homeharbor/ota/channel",
            "/var/lib/homeharbor/ota/kernel-channel",
            "/var/lib/systemd/linger",
            "/var/lib/systemd/linger/homeharbor-containers"
        })
        {
            Assert.IsTrue(SelinuxRelabelCoordinator.SystemManagedPaths.Any(path =>
                !path.Required && string.Equals(path.Path, optional, StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public async Task Concurrent_Relabel_Requests_Share_One_Successful_Scan()
    {
        using var fixture = SelinuxRelabelFixture.Create();
        _ = SelinuxRelabelCoordinator.SynchronizeDetailed(fixture.Paths);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new SelinuxRelabelRecordingRunner(async (fileName, _, _) =>
        {
            _ = entered.TrySetResult(true);
            _ = await release.Task;
            return new CommandResult(0, string.Empty, string.Empty, fileName);
        });

        var first = SelinuxRelabelCoordinator.RelabelPersistentAsync(
            runner,
            fixture.Paths,
            CancellationToken.None);
        _ = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = SelinuxRelabelCoordinator.RelabelPersistentAsync(
            runner,
            fixture.Paths,
            CancellationToken.None);
        _ = release.TrySetResult(true);

        var results = await Task.WhenAll(first, second);
        CollectionAssert.AreEqual(new[] { true, false }, results);
        Assert.HasCount(1, runner.Calls);
    }

    [TestMethod]
    public void Kernel_Config_Validation_Requires_Enforcing_And_Labeled_Filesystem_Prerequisites()
    {
        const string valid = """
            CONFIG_SECURITY=y
            CONFIG_SECURITYFS=y
            CONFIG_SECURITY_NETWORK=y
            CONFIG_SECURITY_SELINUX=y
            CONFIG_SECURITY_SELINUX_BOOTPARAM=y
            CONFIG_AUDIT=y
            CONFIG_AUDITSYSCALL=y
            CONFIG_EROFS_FS_XATTR=y
            CONFIG_EROFS_FS_SECURITY=y
            CONFIG_EXT4_FS_SECURITY=y
            CONFIG_TMPFS=y
            CONFIG_TMPFS_XATTR=y
            """;

        KernelConfigValidator.ValidateConfig(valid, "test kernel");
        _ = Assert.ThrowsExactly<InvalidOperationException>(
            () => KernelConfigValidator.ValidateConfig(valid.Replace("CONFIG_SECURITY_SELINUX=y\n", string.Empty), "test kernel"));

        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(
                   compressed,
                   System.IO.Compression.CompressionMode.Compress,
                   leaveOpen: true))
        {
            gzip.Write(System.Text.Encoding.UTF8.GetBytes(valid));
        }

        var image = "prefixIKCFG_ST"u8.ToArray().Concat(compressed.ToArray()).ToArray();
        Assert.AreEqual(valid, KernelConfigValidator.ExtractEmbeddedConfig(image));
    }

    [TestMethod]
    public void HomeHarbor_Archive_Metadata_Must_Match_The_Requested_Version_And_Pkgrel()
    {
        var packages = new[]
        {
            ArchPackageArchiveValidator.ParsePackageInfo("pkgname = homeharbor-control\npkgver = 1.2.3_dev-1\n", "control"),
            new ArchPackageMetadata("homeharbor-recovery", "1.2.3_dev-1"),
            new ArchPackageMetadata("homeharbor-installer", "1.2.3_dev-1")
        };

        ArchPackageArchiveValidator.ValidateHomeHarborMetadata(packages, "1.2.3-dev");
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ArchPackageArchiveValidator.ValidateHomeHarborMetadata(
                packages.Select(package => package.Name == "homeharbor-recovery"
                    ? package with { Version = "1.2.3_dev-2" }
                    : package),
                "1.2.3-dev"));
    }

    private static void AssertPackageDependencies(
        string pkgbuild,
        string packageName,
        IEnumerable<string> required,
        IEnumerable<string> forbidden)
    {
        var function = Regex.Match(
            pkgbuild,
            "^package_" + Regex.Escape(packageName) + @"\(\) \{(?<body>.*?)^\}",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.IsTrue(function.Success, "package function not found: " + packageName);
        var depends = Regex.Match(
            function.Groups["body"].Value,
            @"^  depends=\((?<items>.*?)^  \)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.IsTrue(depends.Success, "depends array not found: " + packageName);
        var items = depends.Groups["items"].Value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Trim('\'', '"'))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var dependency in required)
        {
            Assert.IsTrue(items.Contains(dependency), $"{packageName} must depend on {dependency}");
        }
        foreach (var dependency in forbidden)
        {
            Assert.IsFalse(items.Contains(dependency), $"{packageName} must not depend on generic {dependency}");
        }
    }

    private static void CreateReadinessRoot(
        string root,
        string profileMarker,
        IReadOnlyList<SelinuxRuntimeReadiness.RequiredDirectory> requirements,
        bool enforcing)
    {
        var enforcePath = Path.Combine(root, SelinuxRuntimeReadiness.EnforcePath.TrimStart('/'));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(enforcePath)!);
        File.WriteAllText(enforcePath, enforcing ? "1\n" : "0\n");

        var markerPath = Path.Combine(root, profileMarker.TrimStart('/'));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, string.Empty);

        foreach (var requirement in requirements)
        {
            var path = Directory.CreateDirectory(Path.Combine(root, requirement.Path.TrimStart('/')));
            File.SetUnixFileMode(path.FullName, (UnixFileMode)Convert.ToInt32(requirement.Mode, 8));
        }

        var metadata = SelinuxRuntimeReadiness.ReadDirectoryMetadata(root);
        var etc = Directory.CreateDirectory(Path.Combine(root, "etc"));
        var users = requirements.Select(requirement => requirement.Owner).Distinct(StringComparer.Ordinal);
        var groups = requirements.Select(requirement => requirement.Group).Distinct(StringComparer.Ordinal);
        File.WriteAllLines(
            Path.Combine(etc.FullName, "passwd"),
            users.Select(name =>
                $"{name}:x:{metadata.Uid}:{metadata.Gid}:{name}:/nonexistent:/usr/bin/nologin"));
        File.WriteAllLines(
            Path.Combine(etc.FullName, "group"),
            groups.Select(name => $"{name}:x:{metadata.Gid}:"));
    }

    private static void AssertTmpfilesRequirementsMatch(
        string tmpfilesPath,
        IReadOnlyList<SelinuxRuntimeReadiness.RequiredDirectory> requirements)
    {
        var tmpfilesDirectories = File.ReadAllLines(tmpfilesPath)
            .Where(line => line.StartsWith("d ", StringComparison.Ordinal))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(parts => string.Join(' ', parts[1], parts[2], parts[3], parts[4]))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var readinessDirectories = requirements
            .Select(requirement => string.Join(
                ' ',
                requirement.Path,
                requirement.Mode,
                requirement.Owner,
                requirement.Group))
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(tmpfilesDirectories, readinessDirectories);
    }

    private sealed class SelinuxRelabelFixture : IDisposable
    {
        private SelinuxRelabelFixture(
            DirectoryInfo root,
            SelinuxRelabelCoordinator.Paths paths,
            string runtimePayload,
            string contextFile)
        {
            Root = root;
            Paths = paths;
            RuntimePayload = runtimePayload;
            ContextFile = contextFile;
        }

        public DirectoryInfo Root { get; }

        public SelinuxRelabelCoordinator.Paths Paths { get; }

        public string RuntimePayload { get; }

        public string ContextFile { get; }

        public static SelinuxRelabelFixture Create()
        {
            var root = Directory.CreateTempSubdirectory("homeharbor-selinux-relabel-");
            var seed = Directory.CreateDirectory(Path.Combine(root.FullName, "seed"));
            var seedPayload = Path.Combine(seed.FullName, "active", "modules", "400", "base", "cil");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(seedPayload)!);
            File.WriteAllText(seedPayload, "base policy\n");
            File.WriteAllText(Path.Combine(seed.FullName, "active", "policy.kern"), "compiled store policy\n");
            File.WriteAllText(Path.Combine(seed.FullName, "active", "file_contexts"), "/var var_t\n");
            var storeDigest = SelinuxPolicyStoreSynchronizer.ComputeStoreDigest(seed.FullName);
            File.WriteAllText(
                Path.Combine(seed.FullName, SelinuxPolicyStoreSynchronizer.DigestFileName),
                storeDigest + "\n");

            var contexts = Directory.CreateDirectory(Path.Combine(root.FullName, "contexts"));
            var contextFile = Path.Combine(contexts.FullName, "file_contexts");
            File.WriteAllText(contextFile, "/var(/.*)? system_u:object_r:var_t:s0\n");
            var policy = Directory.CreateDirectory(Path.Combine(root.FullName, "policy"));
            File.WriteAllText(Path.Combine(policy.FullName, "policy.35"), "compiled image policy\n");

            var state = Directory.CreateDirectory(Path.Combine(root.FullName, "state"));
            var persistent = Directory.CreateDirectory(Path.Combine(root.FullName, "persistent"));
            var data = Directory.CreateDirectory(Path.Combine(root.FullName, "data"));
            var mountInfo = Path.Combine(root.FullName, "mountinfo");
            File.WriteAllText(mountInfo, string.Empty);
            var runtime = Path.Combine(state.FullName, "runtime-store");
            var paths = new SelinuxRelabelCoordinator.Paths(
                seed.FullName,
                runtime,
                contexts.FullName,
                policy.FullName,
                Path.Combine(state.FullName, "epoch"),
                Path.Combine(state.FullName, "persistent-marker"),
                persistent.FullName,
                Path.Combine(data.FullName, ".relabel-marker"),
                data.FullName,
                Path.Combine(state.FullName, "relabel.lock"),
                mountInfo);
            return new SelinuxRelabelFixture(
                root,
                paths,
                Path.Combine(runtime, "active", "modules", "400", "base", "cil"),
                contextFile);
        }

        public void Dispose()
        {
            if (Root.Exists)
            {
                Root.Delete(recursive: true);
            }
        }
    }

    private sealed record SelinuxRelabelCall(string FileName, string[] Arguments);

    private sealed class SelinuxRelabelRecordingRunner : ICommandRunner
    {
        private readonly Func<string, string[], CommandRunOptions?, Task<CommandResult>>? _handler;
        private readonly List<SelinuxRelabelCall> _calls = [];
        private readonly object _gate = new();

        public SelinuxRelabelRecordingRunner(
            Func<string, string[], CommandRunOptions?, Task<CommandResult>>? handler = null)
        {
            _handler = handler;
        }

        public IReadOnlyList<SelinuxRelabelCall> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        public async Task<CommandResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            CommandRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var materialized = arguments.ToArray();
            lock (_gate)
            {
                _calls.Add(new SelinuxRelabelCall(fileName, materialized));
            }

            return _handler is null
                ? new CommandResult(0, string.Empty, string.Empty, fileName)
                : await _handler(fileName, materialized, options);
        }
    }

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
}
