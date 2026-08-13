namespace Easyaller.Core;

/// <summary>
/// Recognizes Windows path shapes by their literal text, deliberately independent of the OS
/// actually running the check. <c>Path.IsPathRooted</c>/<c>Path.IsPathFullyQualified</c> follow
/// the current platform's own rules, so a Windows path like <c>D:\installers</c> or
/// <c>C:\Windows\System32</c> silently stops being "rooted" or "qualified" when validated on
/// Linux — exactly where this solution's cross-platform test suite runs. Every place that checks
/// a portable path value (profile fields, manifest entries) must use these instead of the
/// platform-native <c>Path</c> members, or the check quietly loses its meaning off Windows.
/// </summary>
public static class WindowsPathFormat
{
    /// <summary>True for a UNC root (<c>\\server\share</c>) or a drive-qualified root (<c>D:\</c>).</summary>
    public static bool IsFullyQualified(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith("//", StringComparison.Ordinal)
        || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

    /// <summary>True for any absolute form: UNC, drive-qualified, or rooted to the current drive.</summary>
    public static bool IsAbsolute(string path) =>
        path.Length > 0 && (path[0] is '\\' or '/' || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'));
}
