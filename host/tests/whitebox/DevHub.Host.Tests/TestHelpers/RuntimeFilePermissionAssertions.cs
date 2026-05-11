namespace DevHub.Host.Tests.TestHelpers;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

/// <summary>
/// 运行时安全文件权限断言。
/// </summary>
internal static class RuntimeFilePermissionAssertions
{
    /// <summary>
    /// 断言文件仅允许当前 OS 用户访问。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    public static void AssertCurrentUserOnlyAccess(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            AssertWindowsUserOnlyAcl(filePath);
            return;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var mode = File.GetUnixFileMode(filePath);
        var effective = mode &
            (UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, effective);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsUserOnlyAcl(string filePath)
    {
        var security = new FileInfo(filePath).GetAccessControl(AccessControlSections.Access);
        var rules = security
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToList();

        var currentUserSid = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(currentUserSid);
        Assert.Contains(rules, rule => Equals(rule.IdentityReference, currentUserSid));
        Assert.DoesNotContain(rules, rule => !Equals(rule.IdentityReference, currentUserSid));
    }
}
