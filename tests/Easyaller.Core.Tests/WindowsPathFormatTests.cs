using Easyaller.Core;

namespace Easyaller.Core.Tests;

public sealed class WindowsPathFormatTests
{
    // These are exactly the cases where Path.IsPathRooted/IsPathFullyQualified silently disagree
    // with themselves depending on which OS runs the check — the bug this type exists to avoid.
    [Theory]
    [InlineData(@"D:\installers", true)]
    [InlineData("D:/installers", true)]
    [InlineData(@"\\server\share\installers", true)]
    [InlineData("//server/share/installers", true)]
    [InlineData("installers", false)]
    [InlineData(@"..\installers", false)]
    [InlineData("D:installers", false)]
    public void IsFullyQualified_RecognizesWindowsRootsRegardlessOfRunningPlatform(string path, bool expected) =>
        Assert.Equal(expected, WindowsPathFormat.IsFullyQualified(path));

    [Theory]
    [InlineData(@"C:\Windows\System32\evil.exe", true)]
    [InlineData("C:/Windows/System32/evil.exe", true)]
    [InlineData(@"\Windows\System32", true)]
    [InlineData("/etc/passwd", true)]
    [InlineData(@"\\server\share\file", true)]
    [InlineData("installers/example.msi", false)]
    [InlineData(@"installers\example.msi", false)]
    [InlineData("", false)]
    public void IsAbsolute_RejectsEveryWindowsRootFormRegardlessOfRunningPlatform(string path, bool expected) =>
        Assert.Equal(expected, WindowsPathFormat.IsAbsolute(path));
}
