using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>同一台机器、同一 Windows 用户的私有通道。只有持有读取邀请的连接才收到元数据；无跨用户 ACL 扩权。</summary>
internal static class RoomFollowLocalTransport
{
    private const int ProofSize = 32;
    private const int EmptyState = -1;
    private const int KeepAlive = -2;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(45);

    internal static async Task PublishAsync(RoomFollowGrant grant, RoomFollowOutbox outbox, CancellationToken token)
    {
        // 一个授权只允许一个本机服务实例。读取端在该管道没有发布/控制方法。
        using var pipe = CreateLocalPipe(grant.Invitation);
        await pipe.WaitForConnectionAsync(token);
        await AuthenticateReaderAsync(pipe, grant.Invitation, token);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var peer = WatchPeerAsync(pipe, session);
        try
        {
            long sentRevision = -1;
            while (!session.IsCancellationRequested)
            {
                var current = outbox.Capture();
                if (current.Revision != sentRevision)
                {
                    using var budget = Budget(session.Token, WriteTimeout);
                    var bytes = current.State is null ? null : RoomFollowFormat.Utf8.GetBytes(current.State);
                    await WriteFrameAsync(pipe, bytes?.Length ?? EmptyState, bytes, budget.Token);
                    sentRevision = current.Revision;
                }

                // 空保活帧只用于确认连接仍活着，不查询或发送未变化的房间状态。
                using var idle = Budget(session.Token, KeepAliveInterval);
                try { await outbox.WaitAsync(idle.Token); }
                catch (OperationCanceledException) when (!session.IsCancellationRequested)
                {
                    using var budget = Budget(session.Token, WriteTimeout);
                    await WriteFrameAsync(pipe, KeepAlive, null, budget.Token);
                }
            }
        }
        finally
        {
            await session.CancelAsync();
            await peer;
        }
    }

    internal static async Task SubscribeAsync(RoomFollowInvitation invitation, Action<string?> deliver, CancellationToken token)
    {
        // CurrentUserOnly 同时检查服务端所有者；Identification 不允许对端使用客户端身份操作系统对象。
        using var pipe = new NamedPipeClientStream(".", PipeName(invitation), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync((int)HandshakeTimeout.TotalMilliseconds, token);
        await ProveReaderAsync(pipe, invitation, token);
        var header = new byte[sizeof(int)];
        while (!token.IsCancellationRequested)
        {
            using var budget = Budget(token, ReadTimeout);
            await pipe.ReadExactlyAsync(header, budget.Token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length == KeepAlive) continue;
            if (length == EmptyState) { deliver(null); continue; }
            if (length <= 0 || length > RoomFollowFormat.MaxStateBytes) throw new InvalidDataException("本机跟随帧长度无效。");
            var bytes = new byte[length];
            await pipe.ReadExactlyAsync(bytes, budget.Token);
            token.ThrowIfCancellationRequested();
            deliver(RoomFollowFormat.Utf8.GetString(bytes));
        }
    }

    // .NET 的 CurrentUserOnly 不公开 PIPE_REJECT_REMOTE_CLIENTS：用同样的单 SID 私有 DACL 创建新管道，
    // 额外要求内核拒绝远程 SMB 客户端、且本实例是唯一发布端。绝不修改现有管道或扩大访问权限。
    private static NamedPipeServerStream CreateLocalPipe(RoomFollowInvitation invitation)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new IOException("无法确定本机跟随的 Windows 用户。");
        var security = new PipeSecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        var descriptor = GCHandle.Alloc(security.GetSecurityDescriptorBinaryForm(), GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor.AddrOfPinnedObject()
            };
            const uint duplex = 0x00000003, overlapped = 0x40000000, firstInstance = 0x00080000;
            const uint rejectRemoteClients = 0x00000008; // Byte transmission, byte read, blocking mode.
            var handle = CreateNamedPipe("\\\\.\\pipe\\" + PipeName(invitation), duplex | overlapped | firstInstance,
                rejectRemoteClients, 1, 4096, 4096, 0, ref attributes);
            try
            {
                if (handle.IsInvalid) throw new IOException("无法创建本机私有跟随管道。");
                return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
            }
            catch { handle.Dispose(); throw; }
        }
        finally { descriptor.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;
        internal int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outputBufferSize, uint inputBufferSize, uint timeout, ref SecurityAttributes securityAttributes);

    private static string PipeName(RoomFollowInvitation invitation) => "nexusbgi-room-follow-v1-" + invitation.ChannelId;

    // 发布端的 OS 身份由 CurrentUserOnly 验证。本机发布进程必须持有匹配的写授权（构造时核验）；
    // 不用读取密钥伪造所谓的发布能力证明，也绝不把写密钥发送给读端。同一 Windows 用户属于同一个信任边界。
    // 读端用随机挑战证明持有邀请，管道内不传输读密钥明文；重连必须重新证明。
    private static async Task AuthenticateReaderAsync(PipeStream pipe, RoomFollowInvitation invitation, CancellationToken token)
    {
        using var budget = Budget(token, HandshakeTimeout);
        var nonce = RandomNumberGenerator.GetBytes(ProofSize);
        await pipe.WriteAsync(nonce, budget.Token);
        var received = new byte[ProofSize];
        await pipe.ReadExactlyAsync(received, budget.Token);
        if (!CryptographicOperations.FixedTimeEquals(received, ReaderProof(invitation, nonce)))
            throw new InvalidDataException("本机房间跟随邀请验证失败。");
    }

    private static async Task ProveReaderAsync(PipeStream pipe, RoomFollowInvitation invitation, CancellationToken token)
    {
        using var budget = Budget(token, HandshakeTimeout);
        var nonce = new byte[ProofSize];
        await pipe.ReadExactlyAsync(nonce, budget.Token);
        await pipe.WriteAsync(ReaderProof(invitation, nonce), budget.Token);
    }

    private static byte[] ReaderProof(RoomFollowInvitation invitation, byte[] nonce)
    {
        var prefix = Encoding.UTF8.GetBytes("NexusBGI.RoomFollow.Local.Reader.v1:" + invitation.ChannelId);
        var input = new byte[prefix.Length + ProofSize];
        prefix.CopyTo(input, 0);
        nonce.CopyTo(input, prefix.Length);
        var key = Convert.FromHexString(invitation.ReadSecret);
        try { return HMACSHA256.HashData(key, input); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task WriteFrameAsync(PipeStream pipe, int length, byte[]? bytes, CancellationToken token)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await pipe.WriteAsync(header, token);
        if (bytes is not null) await pipe.WriteAsync(bytes, token);
    }

    // 验证完成后读端没有任何业务调用；EOF 或任何额外写入都结束本次会话。
    private static async Task WatchPeerAsync(PipeStream pipe, CancellationTokenSource session)
    {
        try { _ = await pipe.ReadAsync(new byte[1], session.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { await session.CancelAsync(); }
    }

    private static CancellationTokenSource Budget(CancellationToken token, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(timeout);
        return source;
    }
}
