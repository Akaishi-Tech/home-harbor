using HomeHarbor.Api.Services;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class StorageOobePlannerTests
{
    [TestMethod]
    public void Recommend_Uses_Single_Disk_With_Backup_Warning()
    {
        var inventory = Inventory(Disk("/dev/sdb", tebibytes: 4));

        var recommendation = StorageOobePlanner.Recommend(inventory, DefaultProfile());

        Assert.AreEqual("single-disk-luks2-btrfs", recommendation.RecommendedLayout);
        Assert.AreEqual("single", recommendation.DataProfile);
        Assert.AreEqual("dup", recommendation.MetadataProfile);
        CollectionAssert.Contains(recommendation.Warnings.ToArray(), "Single-disk storage has no disk redundancy. Configure an external backup target.");
    }

    [TestMethod]
    public void Recommend_Uses_Raid1_For_Two_Disks()
    {
        var inventory = Inventory(Disk("/dev/sdb", tebibytes: 4), Disk("/dev/sdc", tebibytes: 4));

        var recommendation = StorageOobePlanner.Recommend(inventory, DefaultProfile());

        Assert.AreEqual("two-disk-luks2-btrfs-raid1", recommendation.RecommendedLayout);
        Assert.AreEqual("raid1", recommendation.DataProfile);
        Assert.AreEqual("raid1", recommendation.MetadataProfile);
        Assert.AreEqual(4L * 1024 * 1024 * 1024 * 1024, recommendation.UsableBytes);
    }

    [TestMethod]
    public void Recommend_Uses_Raid1c3_Metadata_For_Three_Disks()
    {
        var inventory = Inventory(Disk("/dev/sdb", tebibytes: 4), Disk("/dev/sdc", tebibytes: 4), Disk("/dev/sdd", tebibytes: 4));

        var recommendation = StorageOobePlanner.Recommend(inventory, DefaultProfile());

        Assert.AreEqual("multi-disk-luks2-btrfs-raid1-metadata-raid1c3", recommendation.RecommendedLayout);
        Assert.AreEqual("raid1", recommendation.DataProfile);
        Assert.AreEqual("raid1c3", recommendation.MetadataProfile);
    }

    [TestMethod]
    public void Recommend_Treats_Removable_Disks_As_Backup_Targets()
    {
        var inventory = Inventory(
            Disk("/dev/sdb", tebibytes: 4),
            Disk("/dev/sdc", tebibytes: 8, removable: true, transport: "usb"));

        var recommendation = StorageOobePlanner.Recommend(inventory, DefaultProfile());

        CollectionAssert.Contains(recommendation.SelectedDevices.ToArray(), "/dev/sdb");
        CollectionAssert.DoesNotContain(recommendation.SelectedDevices.ToArray(), "/dev/sdc");
        CollectionAssert.Contains(recommendation.BackupTargetDevices.ToArray(), "/dev/sdc");
    }

    [TestMethod]
    public void CreatePlan_Rejects_Protected_Or_Mounted_Disks()
    {
        var inventory = Inventory(
            Disk("/dev/sdb", tebibytes: 4, isProtected: true),
            Disk("/dev/sdc", tebibytes: 4, mountpoints: ["/mnt/data"]));

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(["/dev/sdb"])));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(["/dev/sdc"])));
    }

    [TestMethod]
    public void CreatePlan_Creates_Destructive_Dry_Run_Plan()
    {
        var inventory = Inventory(Disk("/dev/sdb", tebibytes: 4), Disk("/dev/sdc", tebibytes: 4));

        var plan = StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(["/dev/sdb", "/dev/sdc"]));

        Assert.AreEqual("passphrase", plan.UnlockMode);
        Assert.AreEqual("btrfs", plan.FileSystem);
        Assert.AreEqual("mirror", plan.RaidMode);
        Assert.AreEqual("raid1", plan.DataProfile);
        Assert.AreEqual("raid1", plan.MetadataProfile);
        CollectionAssert.AreEquivalent(new[] { "/dev/sdb", "/dev/sdc" }, plan.DestructiveDevices.ToArray());
        Assert.AreEqual("APPLY STORAGE PLAN " + plan.PlanId, plan.ConfirmPhrase);
    }

    [TestMethod]
    public void CreatePlan_Allows_Main_Reserved_Target_With_Tpm2_Metadata()
    {
        var target = new StorageTarget("/dev/vda10", "main-reserved", 2L * 1024L * 1024L * 1024L * 1024L, "System Disk", "vda", "virtio", Eligible: true, []);
        var inventory = InventoryWithTargets([target], Disk("/dev/vda", tebibytes: 4, isSystem: true));

        var plan = StorageOobePlanner.CreatePlan(
            inventory,
            new StoragePlanRequest(
                Targets: [new StoragePlanTargetRequest("/dev/vda10", "main-reserved")],
                SelectedDevices: null,
                Profile: DefaultProfile(),
                RedundancyPreference: null,
                FileSystem: null,
                RaidMode: null,
                DataProfile: "single",
                MetadataProfile: "dup",
                UnlockMode: "tpm2",
                AllowRemovable: false));

        Assert.AreEqual("main-reserved-luks2-btrfs-single", plan.Layout);
        Assert.AreEqual("tpm2", plan.UnlockMode);
        Assert.IsFalse(plan.RequiresBootloaderUnlock);
        Assert.AreEqual("main-reserved", plan.Devices[0].Kind);
    }

    [TestMethod]
    public void CreatePlan_Xfs_Allows_Exactly_One_Target()
    {
        var inventory = Inventory(Disk("/dev/sdb", tebibytes: 4), Disk("/dev/sdc", tebibytes: 4));

        var plan = StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(["/dev/sdb"], fileSystem: "xfs", raidMode: "recommended"));

        Assert.AreEqual("single-disk-luks2-xfs-single", plan.Layout);
        Assert.AreEqual("xfs", plan.FileSystem);
        Assert.AreEqual("single", plan.RaidMode);
        Assert.AreEqual("single", plan.DataProfile);
        Assert.AreEqual("single", plan.MetadataProfile);
        CollectionAssert.Contains(plan.Operations.ToArray(), "mkfs.xfs");
        Assert.AreEqual("xfs", plan.MountChanges[0].FileSystem);
    }

    [TestMethod]
    public void CreatePlan_Xfs_Rejects_Multiple_Targets()
    {
        var inventory = Inventory(Disk("/dev/sdb", tebibytes: 4), Disk("/dev/sdc", tebibytes: 4));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(["/dev/sdb", "/dev/sdc"], fileSystem: "xfs")));

        Assert.AreEqual("XFS storage plans require exactly one target unless RAID5 or RAID6 is selected.", ex.Message);
    }

    [TestMethod]
    public void CreatePlan_Btrfs_Raid5_Uses_Mdadm_Backend()
    {
        var inventory = Inventory(
            Disk("/dev/sdb", tebibytes: 4),
            Disk("/dev/sdc", tebibytes: 4),
            Disk("/dev/sdd", tebibytes: 4));

        var plan = StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(["/dev/sdb", "/dev/sdc", "/dev/sdd"], fileSystem: "btrfs", raidMode: "raid5"));

        Assert.AreEqual("multi-disk-luks2-mdadm-raid5-btrfs", plan.Layout);
        Assert.AreEqual("btrfs", plan.FileSystem);
        Assert.AreEqual("raid5", plan.RaidMode);
        Assert.AreEqual("mdadm", plan.RaidBackend);
        Assert.AreEqual("single", plan.DataProfile);
        Assert.AreEqual("dup", plan.MetadataProfile);
        Assert.AreEqual(8L * 1024 * 1024 * 1024 * 1024, plan.UsableBytes);
        CollectionAssert.Contains(plan.Operations.ToArray(), "mdadm --create /dev/md/homeharbor-data --level=5");
        Assert.Contains(warning => warning.Contains("mdadm", StringComparison.OrdinalIgnoreCase), plan.Warnings);
    }

    [TestMethod]
    public void CreatePlan_Xfs_Raid6_Uses_Mdadm_Backend()
    {
        var inventory = Inventory(
            Disk("/dev/sdb", tebibytes: 4),
            Disk("/dev/sdc", tebibytes: 4),
            Disk("/dev/sdd", tebibytes: 4),
            Disk("/dev/sde", tebibytes: 4));

        var plan = StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(["/dev/sdb", "/dev/sdc", "/dev/sdd", "/dev/sde"], fileSystem: "xfs", raidMode: "raid6"));

        Assert.AreEqual("multi-disk-luks2-mdadm-raid6-xfs", plan.Layout);
        Assert.AreEqual("xfs", plan.FileSystem);
        Assert.AreEqual("raid6", plan.RaidMode);
        Assert.AreEqual("mdadm", plan.RaidBackend);
        Assert.AreEqual(8L * 1024 * 1024 * 1024 * 1024, plan.UsableBytes);
        CollectionAssert.Contains(plan.Operations.ToArray(), "mdadm --create /dev/md/homeharbor-data --level=6");
        CollectionAssert.Contains(plan.Operations.ToArray(), "mkfs.xfs");
        Assert.Contains(warning => warning.Contains("mdadm", StringComparison.OrdinalIgnoreCase), plan.Warnings);
    }

    [TestMethod]
    public void CreatePlan_Xfs_Raid6_Rejects_Too_Few_Targets()
    {
        var inventory = Inventory(
            Disk("/dev/sdb", tebibytes: 4),
            Disk("/dev/sdc", tebibytes: 4),
            Disk("/dev/sdd", tebibytes: 4));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(["/dev/sdb", "/dev/sdc", "/dev/sdd"], fileSystem: "xfs", raidMode: "raid6")));

        Assert.AreEqual("RAID6 requires at least four targets.", ex.Message);
    }

    [TestMethod]
    public void CreatePlan_Zfs_Uses_Native_Layout()
    {
        var inventory = Inventory(
            Disk("/dev/sdb", tebibytes: 4),
            Disk("/dev/sdc", tebibytes: 4),
            Disk("/dev/sdd", tebibytes: 4));

        var plan = StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(["/dev/sdb", "/dev/sdc", "/dev/sdd"], fileSystem: "zfs", raidMode: "raidz1"));

        Assert.AreEqual("multi-disk-luks2-zfs-raidz1", plan.Layout);
        Assert.AreEqual("zfs", plan.FileSystem);
        Assert.AreEqual("raidz1", plan.RaidMode);
        Assert.AreEqual("raidz1", plan.DataProfile);
        Assert.AreEqual("zfs", plan.MetadataProfile);
        Assert.AreEqual(8L * 1024 * 1024 * 1024 * 1024, plan.UsableBytes);
        CollectionAssert.Contains(plan.Operations.ToArray(), "zpool create homeharbor-data raidz1");
    }

    [TestMethod]
    [DataRow("raid5", "raidz1", 3)]
    [DataRow("raid6", "raidz2", 4)]
    public void CreatePlan_Zfs_Raid5_And_Raid6_Map_To_Native_Raidz(string requestedRaid, string expectedRaid, int diskCount)
    {
        var inventory = Inventory(
            [.. Enumerable.Range(0, diskCount).Select(index => Disk("/dev/sd" + (char)('b' + index), tebibytes: 4))]);

        var plan = StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(inventory.Targets.Select(target => target.Path).ToArray(), fileSystem: "zfs", raidMode: requestedRaid));

        Assert.AreEqual("zfs", plan.FileSystem);
        Assert.AreEqual(expectedRaid, plan.RaidMode);
        Assert.AreEqual("filesystem", plan.RaidBackend);
        CollectionAssert.Contains(plan.Operations.ToArray(), "zpool create homeharbor-data " + expectedRaid);
        Assert.IsEmpty(plan.Warnings);
    }

    [TestMethod]
    public void CreatePlan_Zfs_Rejects_Invalid_Raid10_Target_Count()
    {
        var inventory = Inventory(
            Disk("/dev/sdb", tebibytes: 4),
            Disk("/dev/sdc", tebibytes: 4),
            Disk("/dev/sdd", tebibytes: 4));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(["/dev/sdb", "/dev/sdc", "/dev/sdd"], fileSystem: "zfs", raidMode: "raid10")));

        Assert.AreEqual("ZFS RAID10 requires an even number of at least four targets.", ex.Message);
    }

    [TestMethod]
    public void CreatePlan_Rejects_Unavailable_File_System()
    {
        var inventory = InventoryWithTargets(
            [
                new StorageTarget("/dev/sdb", "whole-disk", 4L * 1024L * 1024L * 1024L * 1024L, "Test Disk", "sdb", "sata", true, [])
            ],
            [
                new StorageFileSystemCapability("btrfs", true, null, ["recommended", "single", "mirror", "raid10", "raid5", "raid6"], false),
                new StorageFileSystemCapability("zfs", false, "zfs-utils kernel addon is not mounted; boot the zfs kernel channel with its signed addon.", ["recommended", "single"], false)
            ],
            Disk("/dev/sdb", tebibytes: 4));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => StorageOobePlanner.CreatePlan(
            inventory,
            PlanRequest(["/dev/sdb"], fileSystem: "zfs")));

        Assert.AreEqual("zfs is not available for storage OOBE: zfs-utils kernel addon is not mounted; boot the zfs kernel channel with its signed addon.", ex.Message);
    }

    private static StorageInventory Inventory(params StorageDevice[] devices)
        => InventoryWithTargets(
            devices
                .Where(d => d.Type == "disk")
                .Select(d => new StorageTarget(
                    d.Path!,
                    "whole-disk",
                    d.SizeBytes,
                    d.Model,
                    d.Serial,
                    d.Transport,
                    Eligible: !d.IsProtected && !d.IsSystem && !d.IsRemovable && (d.Mountpoints.Count == 0),
                    EligibilityReasons: (!d.IsProtected && !d.IsSystem && !d.IsRemovable && d.Mountpoints.Count == 0) ? [] : ["ineligible"]))
                .ToArray(),
            devices);

    private static StorageInventory InventoryWithTargets(IReadOnlyList<StorageTarget> targets, params StorageDevice[] devices)
        => InventoryWithTargets(
            targets,
            [
                new StorageFileSystemCapability("btrfs", true, null, ["recommended", "single", "mirror", "raid10", "raid5", "raid6"], false),
                new StorageFileSystemCapability("xfs", true, null, ["recommended", "single", "raid5", "raid6"], false),
                new StorageFileSystemCapability("zfs", true, null, ["recommended", "single", "mirror", "raid10", "raid5", "raid6"], true)
            ],
            devices);

    private static StorageInventory InventoryWithTargets(
        IReadOnlyList<StorageTarget> targets,
        IReadOnlyList<StorageFileSystemCapability> fileSystems,
        params StorageDevice[] devices)
        => new(
            devices,
            targets,
            [],
            devices.Where(d => d.IsProtected || d.IsSystem).Select(d => d.Path!).ToArray(),
            [],
            fileSystems);

    private static StoragePlanRequest PlanRequest(
        IReadOnlyList<string> paths,
        string? fileSystem = null,
        string? raidMode = null)
        => new(
            Targets: paths.Select(path => new StoragePlanTargetRequest(path, "whole-disk")).ToArray(),
            SelectedDevices: null,
            Profile: DefaultProfile(),
            RedundancyPreference: null,
            FileSystem: fileSystem,
            RaidMode: raidMode,
            DataProfile: null,
            MetadataProfile: null,
            UnlockMode: "passphrase",
            AllowRemovable: false);

    private static StorageUseProfile DefaultProfile()
        => new(
            FamilyMembers: 4,
            PhoneCount: 4,
            ComputerCount: 2,
            PhotoVideoIntensity: "normal",
            MediaLibraryTb: 1,
            Apps: 4,
            BackupTargetPreference: "external",
            RedundancyPreference: "conservative");

    private static StorageDevice Disk(
        string path,
        int tebibytes,
        bool removable = false,
        bool isProtected = false,
        bool isSystem = false,
        string? transport = "sata",
        IReadOnlyList<string>? mountpoints = null)
        => new(
            Name: Path.GetFileName(path),
            Path: path,
            SizeBytes: tebibytes * 1024L * 1024L * 1024L * 1024L,
            Type: "disk",
            Model: "Test Disk",
            Serial: path.Replace("/dev/", "", StringComparison.Ordinal),
            Transport: transport,
            IsRotational: true,
            IsRemovable: removable,
            Mountpoints: mountpoints ?? [],
            FileSystem: null,
            Label: null,
            Uuid: null,
            ParentKernelName: null,
            IsSystem: isSystem,
            IsProtected: isProtected,
            Smart: new SmartHealth(true, 0, "passed"),
            Warnings: [],
            Children: []);
}
