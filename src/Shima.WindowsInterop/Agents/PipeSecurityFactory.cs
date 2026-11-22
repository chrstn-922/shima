using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Shima.WindowsInterop.Agents;

internal static class PipeSecurityFactory
{
    /// <summary>
    /// Creates a Windows PipeSecurity descriptor permitting ONLY the current user's SID
    /// (denying NT AUTHORITY\SYSTEM, Everyone, and all other local users).
    /// </summary>
    public static PipeSecurity CreateCurrentUserOnlyPipeSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUserSid = identity.User
            ?? throw new InvalidOperationException("Current Windows user SID could not be resolved.");

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            currentUserSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}
