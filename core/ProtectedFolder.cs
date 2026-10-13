using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Barista.Services;

/// <summary>
/// A folder only SYSTEM and Administrators can change.
///
/// The scripts the install runs are started elevated with $PSScriptRoot
/// pointing into the payload folder. If an ordinary user could write there,
/// they could swap install-doppio.ps1 between the hash check and the run and
/// have it executed as administrator. Program Files already denies that by
/// inheritance; this sets it explicitly and stops inheriting, so a loosened
/// ACL further up the tree does not carry down.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProtectedFolder
{
    public static void Create(string path)
    {
        RefuseReparsePoints(path);

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var acl = new DirectorySecurity();
        acl.SetOwner(admins);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, both,
            PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, both,
            PropagationFlags.None, AccessControlType.Allow));
        // Read is enough for everyone else, and is what lets the user look at
        // the files the integrity page lists without elevating Explorer.
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, both,
            PropagationFlags.None, AccessControlType.Allow));

        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        RefuseReparsePoints(parent);

        new DirectoryInfo(path).Create(acl);
    }

    /// <summary>
    /// Throws if <paramref name="path"/> or any folder above it is a junction
    /// or symbolic link. A junction planted at ...\Barista\payload pointing
    /// somewhere user-writable would undo everything the ACL above is for.
    /// </summary>
    public static void RefuseReparsePoints(string path)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException(
                    $"{dir.FullName} is a junction or symbolic link. Refusing to unpack through it.");
        }
    }
}
