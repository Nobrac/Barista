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

        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        RefuseReparsePoints(parent);

        new DirectoryInfo(path).Create(Acl());
    }

    /// <summary>
    /// <see cref="Create"/> for a folder that may already exist, such as the
    /// state folder under ProgramData.
    ///
    /// ProgramData lets every user create folders, so somebody could make
    /// ...\TheAdminCafe\Barista before Barista ever runs, own it, and leave a
    /// pending-run record in it for the elevated wizard to act on. An existing
    /// folder gets the same ACL and owner as a new one, and every file in it
    /// that neither SYSTEM nor Administrators owns is deleted: Barista runs
    /// elevated, so what it wrote is owned by one of the two, and anything else
    /// was put there by someone else.
    /// </summary>
    public static void Lock(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
        {
            Create(path);
            return;
        }

        RefuseReparsePoints(path);
        dir.SetAccessControl(Acl());

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            var owner = entry is DirectoryInfo d
                ? d.GetAccessControl().GetOwner(typeof(SecurityIdentifier))
                : ((FileInfo)entry).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (owner == admins || owner == system) continue;

            if (entry is DirectoryInfo sub && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                sub.Delete(recursive: true);
            else
                entry.Delete();
        }
    }

    // SYSTEM and Administrators may change it, everyone else may read it. Owned
    // by Administrators, not inherited, so a loosened ACL further up the tree
    // does not carry down.
    private static DirectorySecurity Acl()
    {
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
        return acl;
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
