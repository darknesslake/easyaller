using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class UsbDestructiveConfirmationStateMachineTests
{
    [Fact]
    public void Begin_EligibleSelection_ShowsIdentityFactsAndExactPhrase()
    {
        var disk = UsbDisk("usb-001", 3) with { Vendor = "Test Vendor", SizeBytes = 32L * 1024 * 1024 * 1024 };
        var machine = new UsbDestructiveConfirmationStateMachine();

        var begin = machine.Begin(new RemovableDiskSafetyService().Select(disk));

        Assert.True(begin.IsAwaitingTypedPhrase);
        Assert.Equal("Test Vendor", begin.Confirmation!.Prompt.Vendor);
        Assert.Equal("serial-usb-001", begin.Confirmation.Prompt.SerialOrDeviceId);
        Assert.Equal(3, begin.Confirmation.Prompt.DiskNumber);
        Assert.Equal(disk.SizeBytes, begin.Confirmation.Prompt.SizeBytes);
        Assert.Equal("ERASE", begin.Confirmation.Prompt.RequiredPhrase);
    }

    [Fact]
    public void Submit_WrongCaseOrWhitespace_DoesNotConfirm()
    {
        var machine = new UsbDestructiveConfirmationStateMachine();
        var confirmation = Begin(machine);

        var wrongCase = machine.Submit(confirmation, "erase");
        var whitespace = machine.Submit(confirmation, "ERASE ");

        Assert.False(wrongCase.IsConfirmed);
        Assert.False(whitespace.IsConfirmed);
        Assert.Equal(UsbDestructiveConfirmationStatus.AwaitingTypedPhrase, confirmation.Status);
        Assert.Contains(wrongCase.Errors, error => error.Code == "usb.confirmation.phrase.invalid");
    }

    [Fact]
    public void AuthorizeFirstWrite_RequiresPhraseAndConsumesOneTimeConfirmation()
    {
        var disk = UsbDisk("usb-001", 3);
        var machine = new UsbDestructiveConfirmationStateMachine();
        var confirmation = Begin(machine, disk);

        var beforePhrase = machine.AuthorizeFirstWrite(confirmation, [disk]);
        var confirmed = machine.Submit(confirmation, "ERASE");
        var authorized = machine.AuthorizeFirstWrite(confirmation, [disk]);
        var replay = machine.AuthorizeFirstWrite(confirmation, [disk]);

        Assert.Contains(beforePhrase.Errors, error => error.Code == "usb.confirmation.notConfirmed");
        Assert.True(confirmed.IsConfirmed);
        Assert.True(authorized.IsAuthorizedForFirstWrite);
        Assert.Equal(UsbDestructiveConfirmationStatus.Consumed, confirmation.Status);
        Assert.Contains(replay.Errors, error => error.Code == "usb.confirmation.notConfirmed");
    }

    [Fact]
    public void AuthorizeFirstWrite_HotSwapBlocksAndDoesNotAuthorizeReplacement()
    {
        var original = UsbDisk("usb-original", 3);
        var replacement = UsbDisk("usb-replacement", 3);
        var machine = new UsbDestructiveConfirmationStateMachine();
        var confirmation = Begin(machine, original);
        machine.Submit(confirmation, "ERASE");

        var result = machine.AuthorizeFirstWrite(confirmation, [replacement]);

        Assert.False(result.IsAuthorizedForFirstWrite);
        Assert.Equal(UsbDestructiveConfirmationStatus.Blocked, confirmation.Status);
        Assert.Contains(result.Errors, error => error.Code == "usb.disk.selection.changed");
    }

    [Fact]
    public void Submit_ExpiredConfirmation_IsBlocked()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-09T10:00:00Z"));
        var machine = new UsbDestructiveConfirmationStateMachine(timeProvider: clock, lifetime: TimeSpan.FromMinutes(5));
        var confirmation = Begin(machine);
        clock.Advance(TimeSpan.FromMinutes(5));

        var result = machine.Submit(confirmation, "ERASE");

        Assert.False(result.IsConfirmed);
        Assert.Equal(UsbDestructiveConfirmationStatus.Expired, confirmation.Status);
        Assert.Contains(result.Errors, error => error.Code == "usb.confirmation.expired");
    }

    private static UsbDestructiveConfirmation Begin(UsbDestructiveConfirmationStateMachine machine, DiskInventoryItem? disk = null) =>
        machine.Begin(new RemovableDiskSafetyService().Select(disk ?? UsbDisk("usb-001", 3))).Confirmation!;

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

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan interval) => _now = _now.Add(interval);
    }
}
