using Barista.Services;
using Xunit;

namespace Barista.Core.Tests;

/// <summary>
/// Every tool Barista starts is named by its full path in System32. A bare
/// name is looked up next to Barista.exe first, and that is usually a
/// Downloads folder.
/// </summary>
public class SystemToolsTests
{
    public static TheoryData<string> Tools() => new()
    {
        SystemTools.PowerShell,
        SystemTools.Reg,
        SystemTools.Cmd,
        SystemTools.Shutdown,
    };

    [Theory]
    [MemberData(nameof(Tools))]
    public void Each_tool_is_a_full_path_under_System32(string path)
    {
        // Environment.SystemDirectory is empty off Windows; the CI job that
        // runs these tests is on Windows.
        if (!OperatingSystem.IsWindows()) return;

        Assert.True(Path.IsPathFullyQualified(path), path);
        Assert.StartsWith(Environment.SystemDirectory + Path.DirectorySeparatorChar, path,
            StringComparison.OrdinalIgnoreCase);
    }
}
