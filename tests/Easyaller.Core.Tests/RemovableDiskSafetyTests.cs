using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class RemovableDiskSafetyTests
{
    [Fact]
    public void Assess_EligibleUsbDisk_RequiresExplicitSelectionAndNeverProvidesADefault()
    {
        var service = new RemovableDiskSafetyService();
        var assessments = service.Assess([UsbDisk("usb-001", 3)]);

        Assert.Single(assessments);
        Assert.True(assessments[0].IsEligibleForConfirmation);
        Assert.DoesNotContain(typeof(RemovableDiskSafetyService).GetMethods(), method => method.Name.Contains("Default", StringComparison.Ordinal));
    }

    [Fact]
    public void Assess_SystemOrFixedDisk_IsAlwaysBlocked()
    {
        var service = new RemovableDiskSafetyService();
        var fixedSystemDisk = UsbDisk("internal-001", 0) with
        {
            BusType = DiskBusType.Other,
            IsRemovable = false,
            IsSystem = true,
            IsBoot = true,
        };

        var assessment = service.Assess([fixedSystemDisk]).Single();

        Assert.False(assessment.IsEligibleForConfirmation);
        Assert.Contains(assessment.Errors, error => error.Code == "usb.disk.system.prohibited");
        Assert.Contains(assessment.Errors, error => error.Code == "usb.disk.removable.required");
    }

    [Fact]
    public void Select_MissingImmutableIdentity_IsBlocked()
    {
        var service = new RemovableDiskSafetyService();
        var selection = service.Select(UsbDisk(string.Empty, 3));

        Assert.False(selection.IsStillEligible);
        Assert.Contains(selection.Errors, error => error.Code == "usb.disk.identity.missing");
    }

    [Fact]
    public void Recheck_HotSwapAtTheSameDiskNumber_IsBlocked()
    {
        var service = new RemovableDiskSafetyService();
        var original = UsbDisk("usb-original", 3);
        var selection = service.Select(original).Selection;
        var replacement = UsbDisk("usb-replacement", 3);

        var recheck = service.Recheck(selection, [replacement]);

        Assert.False(recheck.IsStillEligible);
        Assert.Contains(recheck.Errors, error => error.Code == "usb.disk.selection.changed");
    }

    [Fact]
    public void Recheck_SameImmutableIdentityAfterDiskNumberChange_RemainsEligible()
    {
        var service = new RemovableDiskSafetyService();
        var original = UsbDisk("usb-001", 3);
        var selection = service.Select(original).Selection;
        var renumbered = UsbDisk("usb-001", 5);

        var recheck = service.Recheck(selection, [renumbered]);

        Assert.True(recheck.IsStillEligible);
        Assert.Equal(5, recheck.Assessment!.Disk.DiskNumber);
    }

    private static DiskInventoryItem UsbDisk(string uniqueId, int number) => new(
        new DiskIdentity(uniqueId, "serial-" + uniqueId),
        number,
        "Test USB",
        "Easyaller Tests",
        DiskBusType.Usb,
        IsRemovable: true,
        SizeBytes: 64L * 1024 * 1024 * 1024,
        IsSystem: false,
        IsBoot: false,
        IsReadOnly: false,
        IsOffline: false);
}
