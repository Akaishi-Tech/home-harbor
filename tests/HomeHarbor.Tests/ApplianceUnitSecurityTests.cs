namespace HomeHarbor.Tests;

[TestClass]
public sealed class ApplianceUnitSecurityTests
{
    [TestMethod]
    public void Api_Socket_Uses_Dedicated_Group_Without_Changing_Api_Data_Group()
    {
        var root = RepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "os", "systemd", "homeharbor-api.service"));
        var sysusers = File.ReadAllText(Path.Combine(root, "packaging", "arch", "homeharbor.sysusers"));
        var tmpfiles = File.ReadAllText(Path.Combine(root, "packaging", "arch", "homeharbor.tmpfiles"));
        var manifest = File.ReadAllText(Path.Combine(root, "system", "x86_64", "system", "manifest.yml"));

        Assert.Contains("\nGroup=homeharbor\n", service);
        Assert.Contains("SupplementaryGroups=homeharbor-api valkey", service);
        Assert.DoesNotContain("homeharbor-apps", service);
        Assert.DoesNotContain("RuntimeDirectory=homeharbor-api", service);
        Assert.Contains("ExecStartPre=+/usr/bin/chgrp homeharbor-api /run/homeharbor-api", service);
        Assert.Contains("UnixSocketPath=/run/homeharbor-api/api.sock", service);
        Assert.IsFalse(service.Contains("\nGroup=homeharbor-api\n", StringComparison.Ordinal));
        Assert.Contains("m caddy homeharbor-api", sysusers);
        Assert.IsFalse(sysusers.Contains("m caddy homeharbor\n", StringComparison.Ordinal));
        Assert.DoesNotContain("homeharbor-apps", sysusers);
        Assert.DoesNotContain("homeharbor-apps", manifest);
        Assert.Contains("name: homeharbor-containers\n      start: 200000\n      count: 65536", manifest);
        Assert.Contains("d /run/homeharbor-api 2750 homeharbor homeharbor-api", tmpfiles);
        Assert.Contains("d /homeharbor-data/apps 0711 root root", tmpfiles);
    }

    [TestMethod]
    public void Runtime_Reconcile_Services_Run_After_Api_And_Retry_Failures()
    {
        var root = RepositoryRoot();
        var api = File.ReadAllText(Path.Combine(root, "os", "systemd", "homeharbor-api.service"));
        foreach (var name in new[] { "homeharbor-caddy-render", "homeharbor-smb-apply", "homeharbor-container-apply" })
        {
            var service = File.ReadAllText(Path.Combine(root, "os", "systemd", name + ".service"));
            Assert.Contains("After=network-online.target homeharbor-api.service", service);
            Assert.Contains("Restart=on-failure", service);
            Assert.Contains("StartLimitIntervalSec=5min", service);
            Assert.Contains("StartLimitBurst=6", service);
            Assert.DoesNotContain("StartLimitIntervalSec=0", service);
            Assert.Contains(name + ".service", api);
        }

        foreach (var name in new[] { "homeharbor-smb-apply", "homeharbor-container-apply" })
        {
            var service = File.ReadAllText(Path.Combine(root, "os", "systemd", name + ".service"));
            Assert.Contains("[Install]\nWantedBy=multi-user.target", service);
        }
    }

    [TestMethod]
    public void Caddy_Admin_Api_Uses_Permissioned_Unix_Socket()
    {
        var root = RepositoryRoot();
        var dropIn = File.ReadAllText(Path.Combine(root, "os", "systemd", "caddy.service.d", "homeharbor-config.conf"));
        var apiConfigService = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Api", "Services", "ReverseProxyConfigService.cs"));
        var agentProgram = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Agent", "Program.cs"));

        Assert.Contains("RuntimeDirectory=caddy", dropIn);
        Assert.Contains("RuntimeDirectoryMode=0750", dropIn);
        Assert.Contains("ExecReload=/usr/bin/caddy reload --address unix//run/caddy/admin.sock", dropIn);
        Assert.Contains("admin unix//run/caddy/admin.sock", apiConfigService);
        Assert.Contains("admin unix//run/caddy/admin.sock", agentProgram);
        Assert.DoesNotContain("admin off", apiConfigService);
        Assert.DoesNotContain("admin off", agentProgram);
    }

    [TestMethod]
    public void Tls_Trust_Bootstrap_Is_Packaged_And_Runs_After_Caddy()
    {
        var root = RepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "os", "systemd", "homeharbor-tls-trust.service"));
        var package = File.ReadAllText(Path.Combine(root, "packaging", "arch", "PKGBUILD"));
        var manifest = File.ReadAllText(Path.Combine(root, "system", "x86_64", "system", "manifest.yml"));

        Assert.Contains("Requires=caddy.service", service);
        Assert.Contains("After=caddy.service", service);
        Assert.Contains("ExecStart=/usr/lib/homeharbor/agent/HomeHarbor.Agent display-tls-trust", service);
        Assert.Contains("Restart=on-failure", service);
        Assert.Contains("StartLimitBurst=12", service);
        Assert.Contains("RestartSec=5", service);
        Assert.DoesNotContain("StartLimitIntervalSec=0", service);
        Assert.Contains("homeharbor-tls-trust.service", package);
        Assert.Contains("- homeharbor-tls-trust", manifest);
    }

    [TestMethod]
    public void Storage_Health_Job_Drops_Root_And_Is_Read_Only()
    {
        var service = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "os",
            "systemd",
            "homeharbor-storage-health.service"));

        Assert.Contains("User=homeharbor", service);
        Assert.Contains("Group=homeharbor", service);
        Assert.Contains("NoNewPrivileges=true", service);
        Assert.Contains("ProtectSystem=strict", service);
        Assert.Contains("ProtectHome=true", service);
        Assert.Contains("RestrictAddressFamilies=AF_UNIX", service);
        Assert.DoesNotContain("MemoryDenyWriteExecute=true", service);
        Assert.DoesNotContain("ReadWritePaths=", service);
    }

    [TestMethod]
    public void Recovery_Action_Preserves_The_Default_Slot_And_Has_No_Esp_Write_Access()
    {
        var root = RepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "os", "systemd", "homeharbor-recovery-action.service"));
        var tmpfiles = File.ReadAllText(Path.Combine(root, "packaging", "arch", "homeharbor-recovery.tmpfiles"));
        var manifest = File.ReadAllText(Path.Combine(root, "system", "x86_64", "system", "manifest.yml"));
        var kernelBuilder = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Tooling", "KernelPackageBuilder.cs"));

        Assert.Contains("ProtectSystem=strict", service);
        Assert.DoesNotContain("ProtectSystem=full", service);
        Assert.Contains("ReadWritePaths=/run/homeharbor-recovery", service);
        Assert.DoesNotContain("ReadWritePaths=/efi", service);
        Assert.Contains("NoNewPrivileges=yes", service);
        Assert.Contains("d /var/lib/homeharbor 0711 root root", tmpfiles);
        Assert.Contains("d /var/lib/homeharbor/recovery 0750 recovery recovery", tmpfiles);
        Assert.Contains("- homeharbor-recovery-action.path", manifest);
        Assert.Contains(".. systemPlan.Recovery.SystemdUnits", kernelBuilder);
        Assert.DoesNotContain("systemctl\", [\"enable\", \"systemd-networkd\"", kernelBuilder);
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
