using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BetterGenshinImpact.Service.Instance;

// Kept independent of bootstrap so isolated tests can exercise the production ACL
// with a unique fixture pipe name, without discovering or contacting a BGI instance.
internal static class InstancePipeFactory
{
    internal static NamedPipeServerStream CreateServer(string pipeName, bool firstPipeInstance)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User
                       ?? throw new InvalidOperationException("无法取得当前 Windows 用户 SID。");
        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(ownerSid);
        security.AddAccessRule(new PipeAccessRule(networkSid, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(ownerSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        var options = PipeOptions.Asynchronous | PipeOptions.WriteThrough;
        if (firstPipeInstance) options |= PipeOptions.FirstPipeInstance;
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options,
            inBufferSize: 16 * 1024, outBufferSize: 16 * 1024, security);
    }
}
