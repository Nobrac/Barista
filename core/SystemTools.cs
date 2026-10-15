using System.IO;

namespace Barista.Services;

/// <summary>
/// The Windows tools Barista starts, by full path.
///
/// A bare "powershell.exe" is resolved by CreateProcess, and the first place
/// it looks is the folder the calling exe sits in. Barista.exe is usually run
/// from Downloads, which any process of the user can write to, and it runs
/// elevated. A powershell.exe dropped next to it would run as administrator
/// with the install scripts' arguments. System32 is writable only by
/// TrustedInstaller and administrators, so naming it removes that lookup.
/// </summary>
public static class SystemTools
{
    public static string PowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static string Reg => Path.Combine(Environment.SystemDirectory, "reg.exe");

    public static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public static string Shutdown => Path.Combine(Environment.SystemDirectory, "shutdown.exe");
}
