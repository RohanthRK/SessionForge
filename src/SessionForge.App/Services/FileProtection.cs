using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SessionForge.App.Services;

public static class FileProtection
{
    /// <summary>Removes inherited permissions and grants access to the current user only (like chmod 600), as OpenSSH for Windows expects for private keys.</summary>
    public static void RestrictToCurrentUser(string path)
    {
        var fi = new FileInfo(path);
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User!;
        sec.SetOwner(user);
        sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        fi.SetAccessControl(sec);
    }
}
