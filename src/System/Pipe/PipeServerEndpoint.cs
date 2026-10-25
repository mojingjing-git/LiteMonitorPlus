using System;
using System.IO;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;

namespace LiteMonitor.SystemServices.Pipe;

/// <summary>
/// P-I2 / Plan A §5.2：主程序侧管道服务端端点。主程序创建**随机会话名**的管道服务端并
/// 生成随机会话令牌，随后启动 HardwareHost（由监督器负责），Host 作为客户端回连。
/// 安全约束：管道名位于会话本地命名空间（无 Global 前缀 ⇒ 不可远程连接）；ACL 仅授予
/// 当前用户；握手核验连接对端真实 PID（GetNamedPipeClientProcessId，不信任载荷）与会话令牌。
/// </summary>
public sealed class PipeServerEndpoint : IDisposable
{
    private const int PipeBufferSize = 64 * 1024;

    private NamedPipeServerStream? _listener;

    public string PipeName { get; }
    public string SessionToken { get; }

    private PipeServerEndpoint(string pipeName, string sessionToken)
    {
        PipeName = pipeName;
        SessionToken = sessionToken;
    }

    public static PipeServerEndpoint Create()
    {
        // 会话本地命名空间（省略 Global\）+ 随机后缀：禁止远程、避免撞名。
        return new PipeServerEndpoint(
            $"LiteMonitor.HardwareHost.{Guid.NewGuid():N}",
            Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 等待真正的 Host 连接：逐一接受连接并核验（PID / 会话令牌 / 协议主版本），
    /// 未通过者收到可解释拒绝后被断开，继续等待直到 <paramref name="timeout"/> 到期。
    /// 到期未见合格 Host 返回 null（监督器按握手超时处理）。
    /// </summary>
    public async Task<ConnectedHost?> WaitForHostAsync(int expectedProcessId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return null;

            NamedPipeServerStream server = CreateListenerInstance();
            _listener = server;
            ConnectedHost? host = null;
            try
            {
                long capped = Math.Min(remaining, (long)timeout.TotalMilliseconds);
                using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                acceptCts.CancelAfter(TimeSpan.FromMilliseconds(capped));
                try
                {
                    await server.WaitForConnectionAsync(acceptCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return null; // 总期限到
                }

                // 身份核验一：连接对端真实 PID（管道 API，不信任 hello 载荷）。
                if (!TryGetClientProcessId(server, out int clientPid) || clientPid != expectedProcessId)
                {
                    await TryRejectAsync(server, "pid-mismatch").ConfigureAwait(false);
                    continue;
                }

                // 读取 hello（期限取剩余时间）。
                var reader = new IpcFrameReader(server);
                var writer = new IpcFrameWriter(server);
                IpcEnvelope? hello;
                using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    helloCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(remaining, (long)timeout.TotalMilliseconds)));
                    try
                    {
                        hello = await reader.ReadAsync(helloCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }
                    catch (IpcProtocolException)
                    {
                        await TryRejectAsync(server, "invalid-hello").ConfigureAwait(false);
                        continue;
                    }
                }

                if (hello is null || hello.Header.Type != IpcProtocol.TypeHello)
                {
                    await TryRejectAsync(server, "expected-hello").ConfigureAwait(false);
                    continue;
                }

                // 身份核验二：会话令牌（随机会话，伪造连接拿不到）。
                if (!string.Equals(hello.Header.SessionId, SessionToken, StringComparison.Ordinal))
                {
                    await TryRejectAsync(server, "session-mismatch").ConfigureAwait(false);
                    continue;
                }

                // 身份核验三：协议主版本（不兼容即拒，可解释；不靠 JSON 忽略未知字段掩盖）。
                if (hello.Header.Major != IpcProtocol.MajorVersion)
                {
                    await TryRejectAsync(server,
                        $"version-mismatch: host {hello.Header.Major}, main {IpcProtocol.MajorVersion}").ConfigureAwait(false);
                    continue;
                }

                IpcHelloMessage identity = hello.PayloadAs<IpcHelloMessage>();
                await writer.WriteAsync(new IpcEnvelope(
                    new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeWelcome,
                        SessionToken, 0, 1),
                    new IpcWelcomeMessage(true, null)), cancellationToken).ConfigureAwait(false);

                host = new ConnectedHost(server, reader, writer, clientPid,
                    hello.Header.Major, hello.Header.Minor, identity.HostStartTickMs, identity.HostVersion);
                return host;
            }
            finally
            {
                _listener = null;
                if (host is null) server.Dispose();
            }
        }
    }

    private NamedPipeServerStream CreateListenerInstance()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法取得当前用户 SID"),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            // ACL 已显式限当前用户（CurrentUserOnly 与显式 PipeSecurity 二选一，避免双重指定）。
            PipeOptions.Asynchronous,
            inBufferSize: PipeBufferSize,
            outBufferSize: PipeBufferSize,
            pipeSecurity: security);
    }

    private static async Task TryRejectAsync(NamedPipeServerStream server, string reason)
    {
        try
        {
            var writer = new IpcFrameWriter(server);
            await writer.WriteAsync(new IpcEnvelope(
                new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeWelcome,
                    "rejected", 0, 1),
                new IpcWelcomeMessage(false, reason)), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 对端可能已断开；拒绝失败不影响继续等待真正的 Host。
        }
    }

    private static bool TryGetClientProcessId(NamedPipeServerStream server, out int clientPid)
    {
        server = server ?? throw new ArgumentNullException(nameof(server));
        return GetNamedPipeClientProcessId(server.SafePipeHandle, out clientPid);
    }

    public void Dispose() => _listener?.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out int clientProcessId);
}

/// <summary>握手通过的 Host 连接：暴露读/写与对端身份。协议违规在读取侧抛 <see cref="IpcProtocolException"/>。</summary>
public sealed class ConnectedHost : IDisposable
{
    private readonly NamedPipeServerStream _stream;
    private readonly IpcFrameReader _reader;
    private readonly IpcFrameWriter _writer;

    public int PeerProcessId { get; }
    public int PeerMajorVersion { get; }
    public int PeerMinorVersion { get; }
    public long PeerStartTickMs { get; }
    public string PeerVersion { get; }

    internal ConnectedHost(NamedPipeServerStream stream, IpcFrameReader reader, IpcFrameWriter writer,
        int peerPid, int major, int minor, long startTick, string version)
    {
        _stream = stream;
        _reader = reader;
        _writer = writer;
        PeerProcessId = peerPid;
        PeerMajorVersion = major;
        PeerMinorVersion = minor;
        PeerStartTickMs = startTick;
        PeerVersion = version;
    }

    /// <summary>读一帧；对端关闭返回 null；协议违规抛 IpcProtocolException（调用方断连处理）。</summary>
    public Task<IpcEnvelope?> ReadAsync(CancellationToken cancellationToken) => _reader.ReadAsync(cancellationToken);

    public Task SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken) =>
        _writer.WriteAsync(envelope, cancellationToken);

    public bool IsConnected => _stream.IsConnected;

    public void Dispose() => _stream.Dispose();
}
