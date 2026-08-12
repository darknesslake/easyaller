using Easyaller.Core.Profiles;
using Easyaller.Deployment;

namespace Easyaller.Core.Tests;

public sealed class ApplicationInstallationServiceTests
{
    [Fact]
    public void CreatePlan_PackageApplications_ResolveInProfileOrderAndSkipManualOnes()
    {
        using var directory = new TemporaryDirectory();
        CreateInstaller(directory.Path, "installers/first.exe");
        CreateInstaller(directory.Path, "installers/second.msi");
        var profile = ProfileWith(
            PackageApplication("first", "First app", "installers/first.exe", ["/S"]),
            ManualApplication("manual", "Manual app"),
            PackageApplication("second", "Second app", "installers/second.msi", ["/qn"]));

        var plan = new ApplicationInstallationService().CreatePlan(profile, directory.Path);

        Assert.True(plan.CanRun);
        Assert.Equal(["First app", "Second app"], plan.Steps.Select(static step => step.DisplayName));
        Assert.Equal(["/S"], plan.Steps[0].Arguments);
        Assert.Single(plan.ManualApplications);
    }

    [Fact]
    public void CreatePlan_MissingInstallerFile_BlocksTheRun()
    {
        using var directory = new TemporaryDirectory();
        var profile = ProfileWith(PackageApplication("first", "First app", "installers/absent.exe", []));

        var plan = new ApplicationInstallationService().CreatePlan(profile, directory.Path);

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Errors, static error => error.Code == "applications.install.file.missing");
    }

    [Fact]
    public void CreatePlan_MissingRootDirectory_IsReported()
    {
        var profile = ProfileWith(PackageApplication("first", "First app", "installers/first.exe", []));

        var plan = new ApplicationInstallationService().CreatePlan(
            profile,
            Path.Combine(Path.GetTempPath(), "Easyaller.Tests", Guid.NewGuid().ToString("N")));

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Errors, static error => error.Code == "applications.packageRoot.missing");
    }

    [Fact]
    public void Run_FirstFailure_StopsAndLeavesRemainingInstallersUntouched()
    {
        using var directory = new TemporaryDirectory();
        CreateInstaller(directory.Path, "installers/first.exe");
        CreateInstaller(directory.Path, "installers/second.exe");
        CreateInstaller(directory.Path, "installers/third.exe");
        var profile = ProfileWith(
            PackageApplication("first", "First", "installers/first.exe", []),
            PackageApplication("second", "Second", "installers/second.exe", []),
            PackageApplication("third", "Third", "installers/third.exe", []));
        var service = new ApplicationInstallationService();
        var plan = service.CreatePlan(profile, directory.Path);
        var runner = new FakeRunner(new Dictionary<string, ApplicationProcessResult>
        {
            ["First"] = new(0, null),
            ["Second"] = new(1603, null),
            ["Third"] = new(0, null),
        });

        var report = service.Run(plan, runner);

        Assert.True(report.StoppedOnFailure);
        Assert.Equal(["First", "Second"], runner.StartedApplications);
        Assert.Equal(ApplicationInstallOutcome.Installed, report.Results[0].Outcome);
        Assert.Equal(ApplicationInstallOutcome.Failed, report.Results[1].Outcome);
        Assert.Equal(ApplicationInstallOutcome.NotRun, report.Results[2].Outcome);
        Assert.Equal(1, report.InstalledCount);
    }

    [Theory]
    [InlineData(0, ApplicationInstallOutcome.Installed, false)]
    [InlineData(3010, ApplicationInstallOutcome.InstalledRestartRequired, true)]
    [InlineData(1641, ApplicationInstallOutcome.InstalledRestartRequired, true)]
    [InlineData(1603, ApplicationInstallOutcome.Failed, false)]
    public void Run_WindowsExitCodes_AreTranslatedToOutcomes(int exitCode, ApplicationInstallOutcome expected, bool expectRestart)
    {
        using var directory = new TemporaryDirectory();
        CreateInstaller(directory.Path, "installers/only.exe");
        var profile = ProfileWith(PackageApplication("only", "Only", "installers/only.exe", []));
        var service = new ApplicationInstallationService();
        var plan = service.CreatePlan(profile, directory.Path);
        var runner = new FakeRunner(new Dictionary<string, ApplicationProcessResult>
        {
            ["Only"] = new(exitCode, null),
        });

        var report = service.Run(plan, runner);

        Assert.Equal(expected, report.Results[0].Outcome);
        Assert.Equal(expectRestart, report.RequiresRestart);
    }

    [Fact]
    public void Run_LaunchError_IsReportedAsFailureWithoutAnExitCode()
    {
        using var directory = new TemporaryDirectory();
        CreateInstaller(directory.Path, "installers/only.exe");
        var profile = ProfileWith(PackageApplication("only", "Only", "installers/only.exe", []));
        var service = new ApplicationInstallationService();
        var plan = service.CreatePlan(profile, directory.Path);
        var runner = new FakeRunner(new Dictionary<string, ApplicationProcessResult>
        {
            ["Only"] = new(null, "Отказано в доступе."),
        });

        var report = service.Run(plan, runner);

        Assert.Equal(ApplicationInstallOutcome.Failed, report.Results[0].Outcome);
        Assert.Equal("Отказано в доступе.", report.Results[0].ErrorMessage);
    }

    [Fact]
    public async Task RunPipelinedAsync_CopiesTheNextInstallerWhileTheCurrentOneInstalls()
    {
        using var source = new TemporaryDirectory();
        using var destination = new TemporaryDirectory();
        CreateInstaller(source.Path, "first.exe");
        CreateInstaller(source.Path, "second.exe");
        CreateInstaller(source.Path, "third.exe");
        var profile = ProfileWith(
            PackageApplication("first", "First", "first.exe", []),
            PackageApplication("second", "Second", "second.exe", []),
            PackageApplication("third", "Third", "third.exe", []));
        var service = new ApplicationInstallationService();
        var plan = service.CreatePlan(profile, source.Path);

        // Each install blocks until the test releases it, which is when later copies must already exist.
        var release = new SemaphoreSlim(0);
        var copiedWhileInstalling = new List<int>();
        var runner = new BlockingRunner(release, () =>
            copiedWhileInstalling.Add(Directory.GetFiles(destination.Path, "*.exe").Length));

        var run = service.RunPipelinedAsync(plan, destination.Path, runner);
        for (var i = 0; i < 3; i++)
        {
            await WaitUntilAsync(() => runner.StartedApplications.Count > i);
            release.Release();
        }

        var report = await run;

        Assert.False(report.StoppedOnFailure);
        Assert.Equal(["First", "Second", "Third"], runner.StartedApplications);
        // While the first installer was running, its own copy plus at least the next one existed.
        Assert.True(copiedWhileInstalling[0] >= 2, $"Expected a lookahead copy, saw {copiedWhileInstalling[0]} file(s).");
    }

    [Fact]
    public async Task RunPipelinedAsync_InstallFailure_StopsFurtherInstalls()
    {
        using var source = new TemporaryDirectory();
        using var destination = new TemporaryDirectory();
        CreateInstaller(source.Path, "first.exe");
        CreateInstaller(source.Path, "second.exe");
        var profile = ProfileWith(
            PackageApplication("first", "First", "first.exe", []),
            PackageApplication("second", "Second", "second.exe", []));
        var service = new ApplicationInstallationService();
        var plan = service.CreatePlan(profile, source.Path);
        var runner = new FakeRunner(new Dictionary<string, ApplicationProcessResult>
        {
            ["First"] = new(1603, null),
            ["Second"] = new(0, null),
        });

        var report = await service.RunPipelinedAsync(plan, destination.Path, runner);

        Assert.True(report.StoppedOnFailure);
        Assert.Equal(["First"], runner.StartedApplications);
        Assert.Equal(ApplicationInstallOutcome.NotRun, report.Results[1].Outcome);
    }

    [Theory]
    [InlineData("This installer was built with the Nullsoft Install System", InstallerFramework.Nsis, "/S")]
    [InlineData("Copyright (C) 1997-2024 Jordan Russell. All rights reserved. Inno Setup", InstallerFramework.InnoSetup, "/VERYSILENT")]
    [InlineData("Flexera InstallShield 2024 Setup Launcher", InstallerFramework.InstallShield, "/s")]
    [InlineData("random padding before the marker .wixburn random padding after", InstallerFramework.WixBurn, "/quiet")]
    [InlineData("just some ordinary executable with no known marker inside", InstallerFramework.Unknown, null)]
    public void DetectFromContent_RecognizesKnownFrameworkMarkers(string embeddedText, InstallerFramework expected, string? expectedFirstArgument)
    {
        var content = System.Text.Encoding.ASCII.GetBytes(embeddedText);

        var detection = InstallerFrameworkDetector.DetectFromContent(content);

        Assert.Equal(expected, detection.Framework);
        if (expectedFirstArgument is null)
        {
            Assert.Empty(detection.SuggestedArguments);
        }
        else
        {
            Assert.Equal(expectedFirstArgument, detection.SuggestedArguments[0]);
        }
    }

    [Fact]
    public void Detect_MsiExtension_SuggestsQuietWithoutReadingContent()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "package.msi");
        File.WriteAllBytes(path, "not a real msi but the extension is what matters"u8.ToArray());

        var detection = InstallerFrameworkDetector.Detect(path);

        Assert.Equal(InstallerFramework.Msi, detection.Framework);
        Assert.Equal(["/qn"], detection.SuggestedArguments);
    }

    [Fact]
    public void Detect_RealFileOnDisk_FindsMarkerNearTheStart()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "setup.exe");
        var content = new byte[1024];
        "This stub was produced by the Nullsoft Install System build tool"u8.ToArray().CopyTo(content, 200);
        File.WriteAllBytes(path, content);

        var detection = InstallerFrameworkDetector.Detect(path);

        Assert.Equal(InstallerFramework.Nsis, detection.Framework);
    }

    [Fact]
    public void DiscoverInstallers_TakesOneEntryPerProductFolder()
    {
        // Mirrors a real software share: loose installers plus product folders that ship a setup
        // file next to a large number of component packages.
        using var source = new TemporaryDirectory();
        CreateInstaller(source.Path, "winrar-x32-624ru.exe");
        CreateInstaller(source.Path, "java8-x64.exe");
        CreateInstaller(source.Path, "XenAppHosted.msi");
        CreateInstaller(source.Path, "unins000.exe");
        CreateInstaller(source.Path, Path.Combine("Office 2016 x32", "setup.exe"));
        CreateInstaller(source.Path, Path.Combine("Office 2016 x32", "accessmui.msi"));
        CreateInstaller(source.Path, Path.Combine("Office 2016 x32", "excelmui.msi"));
        CreateInstaller(source.Path, Path.Combine("Office 2016 x32", "wordmui.msi"));

        var discovered = ApplicationInstallationService.DiscoverInstallers(source.Path);

        Assert.Equal(4, discovered.Count);
        Assert.Contains(discovered, installer => installer.SuggestedName == "winrar-x32-624ru");
        Assert.Contains(discovered, installer => installer.SuggestedName == "java8-x64");
        Assert.Contains(discovered, installer => installer.SuggestedName == "XenAppHosted");
        // The Office folder collapses to its setup file, named after the product folder.
        Assert.Contains(discovered, installer =>
            installer.SuggestedName == "Office 2016 x32"
            && installer.RelativePath == Path.Combine("Office 2016 x32", "setup.exe"));
        Assert.DoesNotContain(discovered, installer => installer.RelativePath.Contains("accessmui", StringComparison.Ordinal));
        Assert.DoesNotContain(discovered, installer => installer.SuggestedName.StartsWith("unins", StringComparison.Ordinal));
    }

    [Fact]
    public void DiscoverInstallers_FolderWithoutSetupOffersEachInstaller()
    {
        using var source = new TemporaryDirectory();
        CreateInstaller(source.Path, Path.Combine("Drivers", "network.msi"));
        CreateInstaller(source.Path, Path.Combine("Drivers", "printer.msi"));

        var discovered = ApplicationInstallationService.DiscoverInstallers(source.Path);

        Assert.Equal(2, discovered.Count);
        Assert.All(discovered, installer => Assert.StartsWith("Drivers — ", installer.SuggestedName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ApplicationArchitecture.X64, "CryptoSocket x64")]
    [InlineData(ApplicationArchitecture.X86, "CryptoSocket x86")]
    public void CreatePlan_KeepsOnlyTheInstallerMatchingTheSystemArchitecture(
        ApplicationArchitecture systemArchitecture,
        string expectedName)
    {
        // The same product ships as two builds; only the one matching Windows may run.
        using var source = new TemporaryDirectory();
        CreateInstaller(source.Path, "crypto-x64.exe");
        CreateInstaller(source.Path, "crypto-x86.exe");
        var profile = ProfileWith(
            PackageApplication("crypto-x64", "CryptoSocket x64", "crypto-x64.exe", []) with
            {
                Architecture = ApplicationArchitecture.X64,
            },
            PackageApplication("crypto-x86", "CryptoSocket x86", "crypto-x86.exe", []) with
            {
                Architecture = ApplicationArchitecture.X86,
            });
        var service = new ApplicationInstallationService();

        var plan = service.CreatePlan(profile, source.Path, systemArchitecture);

        Assert.Equal(expectedName, Assert.Single(plan.Steps).DisplayName);
        Assert.Single(plan.SkippedByArchitecture);
        Assert.Empty(plan.Errors);
    }

    [Fact]
    public void CreatePlan_AnyArchitectureInstallerRunsOnBothSystems()
    {
        using var source = new TemporaryDirectory();
        CreateInstaller(source.Path, "winrar.exe");
        var profile = ProfileWith(PackageApplication("winrar", "WinRAR", "winrar.exe", []));
        var service = new ApplicationInstallationService();

        Assert.Single(service.CreatePlan(profile, source.Path, ApplicationArchitecture.X64).Steps);
        Assert.Single(service.CreatePlan(profile, source.Path, ApplicationArchitecture.X86).Steps);
    }

    [Fact]
    public async Task RunPipelinedAsync_KeepsSubfolderLayoutWhenCopying()
    {
        // Real installer shares mix loose files with per-product subfolders, such as Office.
        using var source = new TemporaryDirectory();
        using var destination = new TemporaryDirectory();
        CreateInstaller(source.Path, "winrar.exe");
        CreateInstaller(source.Path, Path.Combine("Office 2016 x32", "setup.exe"));
        var profile = ProfileWith(
            PackageApplication("winrar", "WinRAR", "winrar.exe", []),
            PackageApplication("office", "Office 2016 x32", Path.Combine("Office 2016 x32", "setup.exe"), []));
        var service = new ApplicationInstallationService();
        var plan = service.CreatePlan(profile, source.Path);
        var runner = new PathRecordingRunner();

        var report = await service.RunPipelinedAsync(plan, destination.Path, runner);

        Assert.False(report.StoppedOnFailure);
        Assert.True(File.Exists(Path.Combine(destination.Path, "winrar.exe")));
        Assert.True(File.Exists(Path.Combine(destination.Path, "Office 2016 x32", "setup.exe")));
    }

    [Fact]
    public async Task RunPipelinedAsync_InstallsFromTheLocalCopyNotTheSource()
    {
        using var source = new TemporaryDirectory();
        using var destination = new TemporaryDirectory();
        CreateInstaller(source.Path, "only.exe");
        var profile = ProfileWith(PackageApplication("only", "Only", "only.exe", []));
        var service = new ApplicationInstallationService();
        var plan = service.CreatePlan(profile, source.Path);
        var runner = new PathRecordingRunner();

        await service.RunPipelinedAsync(plan, destination.Path, runner);

        Assert.StartsWith(destination.Path, runner.ExecutedPaths.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(destination.Path, "only.exe")));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "Condition was not reached in time.");
    }

    private static void CreateInstaller(string root, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "test installer");
    }

    private static ApplicationProfile PackageApplication(string id, string displayName, string path, string[] arguments) =>
        new(id, displayName, ApplicationSourceKind.PackageRelative, path, arguments);

    private static ApplicationProfile ManualApplication(string id, string displayName) =>
        new(id, displayName, ApplicationSourceKind.ExternalManual, null, []);

    private static ProvisioningProfile ProfileWith(params ApplicationProfile[] applications) =>
        ProvisioningProfileFactory.CreateDefault() with { Applications = applications };

    private sealed class FakeRunner(Dictionary<string, ApplicationProcessResult> results) : IApplicationInstallerRunner
    {
        public List<string> StartedApplications { get; } = [];

        public ApplicationProcessResult Run(ApplicationInstallStep step)
        {
            StartedApplications.Add(step.DisplayName);
            return results[step.DisplayName];
        }
    }

    private sealed class BlockingRunner(SemaphoreSlim release, Action onStart) : IApplicationInstallerRunner
    {
        public List<string> StartedApplications { get; } = [];

        public ApplicationProcessResult Run(ApplicationInstallStep step)
        {
            onStart();
            lock (StartedApplications)
            {
                StartedApplications.Add(step.DisplayName);
            }

            release.Wait();
            return new ApplicationProcessResult(0, null);
        }
    }

    private sealed class PathRecordingRunner : IApplicationInstallerRunner
    {
        public List<string> ExecutedPaths { get; } = [];

        public ApplicationProcessResult Run(ApplicationInstallStep step)
        {
            ExecutedPaths.Add(step.ExecutablePath);
            return new ApplicationProcessResult(0, null);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Easyaller.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
