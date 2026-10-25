using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.SystemServices.Pipe;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// P-I2 §2.6 协议健壮性（Plan A §8「协议健壮性」行）：拆包、超大长度、非法 JSON、
/// 错误版本、伪造连接、迟到响应。本文件先覆盖编解码层；端点层（管道握手/ACL/PID 核验）
/// 的集成用例见同文件后段 I2PipeEndpointTests。
/// </summary>
public sealed class I2PipeProtocolTests
{
    private static IpcEnvelope MakeSnapshotEnvelope(long batch, string session = "s1", long seq = 1)
    {
        var snapshot = new HardwareSnapshot(
            BatchId: batch, CatalogVersion: 1, ConfigRevision: -1,
            Samples: new[]
            {
                new MetricSample("CPU.Load", null, 42.5f, null, SampleQuality.Good, null, 0),
                new MetricSample("CPU.Temp", null, null, null, SampleQuality.Missing, "no-sensor", 0),
            },
            SamplingDuration: TimeSpan.FromMilliseconds(3.5), TimestampMonoMs: 1000);
        return new IpcEnvelope(
            new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeSnapshot, session, 0, seq),
            new IpcSnapshotMessage(snapshot, PublishTickMs: 1001));
    }

    [Fact]
    public void 帧编解码往返_头与载荷不丢失()
    {
        IpcEnvelope envelope = MakeSnapshotEnvelope(7, session: "sess-abc", seq: 12);

        byte[] frame = IpcCodec.Encode(envelope);
        IpcEnvelope decoded = IpcCodec.DecodeFrame(frame);

        Assert.Equal(IpcProtocol.TypeSnapshot, decoded.Header.Type);
        Assert.Equal("sess-abc", decoded.Header.SessionId);
        Assert.Equal(12, decoded.Header.Sequence);
        IpcSnapshotMessage payload = decoded.PayloadAs<IpcSnapshotMessage>();
        Assert.Equal(7, payload.Snapshot.BatchId);
        MetricSample load = payload.Snapshot.Samples.Single(s => s.Key == "CPU.Load");
        Assert.Equal(42.5f, load.Value);          // 真实 0/有效值语义保留
        Assert.Equal(SampleQuality.Good, load.Quality);
        MetricSample missing = payload.Snapshot.Samples.Single(s => s.Key == "CPU.Temp");
        Assert.Null(missing.Value);
        Assert.Equal(SampleQuality.Missing, missing.Quality);
        Assert.Equal("no-sensor", missing.MissingReason);
    }

    /// <summary>拆包：每次 Read 只给 1 字节，读取器必须完整重组帧。</summary>
    [Fact]
    public async Task 拆包_单字节流也能完整重组()
    {
        byte[] frame = IpcCodec.Encode(MakeSnapshotEnvelope(3));
        using var dribble = new DribbleStream(frame, maxBytesPerRead: 1);
        var reader = new IpcFrameReader(dribble);

        IpcEnvelope? decoded = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(decoded);
        Assert.Equal(3, decoded!.PayloadAs<IpcSnapshotMessage>().Snapshot.BatchId);
        Assert.Equal(frame.Length, dribble.TotalRead);
    }

    [Fact]
    public async Task 对端关闭_读取器返回null而非抛错()
    {
        using var empty = new MemoryStream(Array.Empty<byte>());
        var reader = new IpcFrameReader(empty);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    /// <summary>超大长度：先校验再分配——读取器在 4 字节前缀处即拒绝，不缓冲任何载荷。</summary>
    [Theory]
    [InlineData(IpcProtocol.MaxMessageBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task 超大帧长_有界拒绝(int badLength)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, badLength);
        // 尾随 1MiB 垃圾：若实现先读后验，这里会放大内存/时间，断言守住"先验后读"。
        using var stream = new PipedPrefixStream(prefix, trailingGarbageBytes: 1_100_000);
        var reader = new IpcFrameReader(stream);
        await Assert.ThrowsAsync<IpcProtocolException>(() => reader.ReadAsync(CancellationToken.None));
        Assert.True(stream.BytesRead <= IpcProtocol.LengthPrefixBytes,
            $"读取器不得在长度校验失败后继续读载荷（实际读了 {stream.BytesRead} 字节）");
    }

    [Fact]
    public void 编码侧超限_同样拒绝()
    {
        var big = new IpcEnvelope(
            new IpcHeader(1, 0, IpcProtocol.TypeSnapshot, "s", 0, 1),
            new IpcSnapshotMessage(
                new HardwareSnapshot(1, 1, -1,
                    Enumerable.Range(0, 200_000).Select(i =>
                        new MetricSample($"K{i}", null, i, null, SampleQuality.Good, null, 0)).ToList(),
                    TimeSpan.Zero, 0), 0));
        Assert.Throws<IpcProtocolException>(() => IpcCodec.Encode(big));
    }

    [Fact]
    public async Task 非法JSON_有界拒绝()
    {
        byte[] garbage = Encoding.UTF8.GetBytes("{\"not\":\"a valid envelope\"");
        var frame = new byte[4 + garbage.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, garbage.Length);
        garbage.CopyTo(frame, 4);
        using var stream = new MemoryStream(frame);
        var reader = new IpcFrameReader(stream);
        await Assert.ThrowsAsync<IpcProtocolException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void 未知消息类型_拒绝()
    {
        var envelope = new IpcEnvelope(new IpcHeader(1, 0, "definitely-not-a-type", "s", 0, 1), null);
        byte[] frame = IpcTestEncode.RawFrame(envelope);
        Assert.Throws<IpcProtocolException>(() => IpcCodec.DecodeFrame(frame));
    }

    [Fact]
    public void 缺失会话令牌_拒绝()
    {
        var envelope = new IpcEnvelope(new IpcHeader(1, 0, IpcProtocol.TypeHeartbeat, "", 0, 1), null);
        byte[] frame = IpcTestEncode.RawFrame(envelope);
        Assert.Throws<IpcProtocolException>(() => IpcCodec.DecodeFrame(frame));
    }

    /// <summary>错误版本：编解码层放行（可给出可解释拒绝），主版本裁决由端点握手负责（见端点用例）。</summary>
    [Fact]
    public void 版本不匹配_编解码层可读出对端版本供端点裁决()
    {
        var envelope = new IpcEnvelope(new IpcHeader(99, 7, IpcProtocol.TypeHello, "s", 0, 1),
            new IpcHelloMessage(123, 0, "test"));
        byte[] frame = IpcTestEncode.RawFrame(envelope);
        IpcEnvelope decoded = IpcCodec.DecodeFrame(frame);
        Assert.Equal(99, decoded.Header.Major);
        Assert.Equal(123, decoded.PayloadAs<IpcHelloMessage>().HostProcessId);
    }

    // ── 测试用流 ─────────────────────────────────────────────────────────────

    /// <summary>限流流：每次 Read 最多吐 maxBytesPerRead 字节（模拟任意拆包）。</summary>
    private sealed class DribbleStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _max;
        private int _pos;
        public int TotalRead { get; private set; }

        public DribbleStream(byte[] data, int maxBytesPerRead)
        {
            _data = data; _max = maxBytesPerRead;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, _max), _data.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n; TotalRead += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>前缀流：只提供 4 字节前缀，读前缀失败后若再读载荷即计入 BytesRead（守住先验后读）。</summary>
    private sealed class PipedPrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly int _trailing;
        private int _pos;
        public int BytesRead { get; private set; }

        public PipedPrefixStream(byte[] prefix, int trailingGarbageBytes)
        {
            _prefix = prefix; _trailing = trailingGarbageBytes;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int fromPrefix = Math.Min(count, _prefix.Length - _pos);
            if (fromPrefix > 0)
            {
                Array.Copy(_prefix, _pos, buffer, offset, fromPrefix);
                _pos += fromPrefix; BytesRead += fromPrefix;
                return fromPrefix;
            }
            BytesRead += Math.Min(count, _trailing); // 模拟继续供垃圾（读取器不应到达这里）
            return Math.Min(count, 1);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>测试辅助：编码结构非法但 JSON 合法的信封（未知类型/空会话/错误版本用例）。</summary>
internal static class IpcTestEncode
{
    public static byte[] RawFrame(IpcEnvelope envelope)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        return frame;
    }
}

/// <summary>
/// P-I2 步骤 3 端点级集成：管道服务端握手核验（PID/会话/版本）、伪造连接有界拒绝、
/// 与真实 Host 进程的完整会话（握手 → 快照 → 命令应答 → 受控关闭）。
/// </summary>
public sealed class I2PipeEndpointTests
{
    private static IpcEnvelope HelloEnvelope(string session, int major = 1) => new(
        new IpcHeader(major, 0, IpcProtocol.TypeHello, session, 0, 1),
        new IpcHelloMessage(Environment.ProcessId, Environment.TickCount64, "test"));

    private static async Task<IpcWelcomeMessage> ConnectAndSpeakAsync(
        PipeServerEndpoint endpoint, Func<NamedPipeClientStream, Task> speak, TimeSpan readTimeout)
    {
        using var client = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut);
        await client.ConnectAsync(2000);
        await speak(client);
        using var cts = new CancellationTokenSource(readTimeout);
        IpcEnvelope? welcome = await new IpcFrameReader(client).ReadAsync(cts.Token);
        Assert.NotNull(welcome);
        return welcome!.PayloadAs<IpcWelcomeMessage>();
    }

    [Fact]
    public async Task 伪造连接_会话令牌不匹配被拒并继续有界等待()
    {
        using var endpoint = PipeServerEndpoint.Create();
        var wait = endpoint.WaitForHostAsync(expectedProcessId: Environment.ProcessId, TimeSpan.FromSeconds(2));

        IpcWelcomeMessage welcome = await ConnectAndSpeakAsync(endpoint,
            client => new IpcFrameWriter(client).WriteAsync(HelloEnvelope(endpoint.SessionToken + "-forged"), CancellationToken.None),
            TimeSpan.FromSeconds(2));

        Assert.False(welcome.Accepted);
        Assert.Contains("session-mismatch", welcome.Reason);
        Assert.Null(await wait); // 期限内无合格 Host ⇒ null（不抛、不挂死）
    }

    [Fact]
    public async Task 伪造连接_错误PID被拒_端点继续有界等待()
    {
        using var endpoint = PipeServerEndpoint.Create();
        var wait = endpoint.WaitForHostAsync(expectedProcessId: Environment.ProcessId + 40_000, TimeSpan.FromSeconds(2));

        // PID 核验发生在读取 hello 之前：伪造端可能收到拒绝 welcome，也可能因服务端
        // 立即断开而遇到 broken pipe——两者都证明"连接被拒"；关键断言是端点不挂死、
        // 期限内返回 null。
        try
        {
            using var client = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut);
            await client.ConnectAsync(2000);
            var writer = new IpcFrameWriter(client);
            await writer.WriteAsync(HelloEnvelope(endpoint.SessionToken), CancellationToken.None);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            IpcEnvelope? welcome = await new IpcFrameReader(client).ReadAsync(cts.Token);
            if (welcome is not null)
            {
                IpcWelcomeMessage verdict = welcome.PayloadAs<IpcWelcomeMessage>();
                Assert.False(verdict.Accepted);
                Assert.Contains("pid-mismatch", verdict.Reason);
            }
        }
        catch (IOException)
        {
            // broken pipe：连接级拒绝的另一种可观察面
        }

        Assert.Null(await wait);
    }

    [Fact]
    public async Task 错误版本_有解释拒绝不误判会话()
    {
        using var endpoint = PipeServerEndpoint.Create();
        var wait = endpoint.WaitForHostAsync(expectedProcessId: Environment.ProcessId, TimeSpan.FromSeconds(2));

        IpcWelcomeMessage welcome = await ConnectAndSpeakAsync(endpoint,
            client => new IpcFrameWriter(client).WriteAsync(HelloEnvelope(endpoint.SessionToken, major: 99), CancellationToken.None),
            TimeSpan.FromSeconds(2));

        Assert.False(welcome.Accepted);
        Assert.Contains("version-mismatch", welcome.Reason);
        Assert.Null(await wait);
    }

    [Fact]
    public async Task 握手阶段非法JSON_有界拒绝后继续等待()
    {
        using var endpoint = PipeServerEndpoint.Create();
        var wait = endpoint.WaitForHostAsync(expectedProcessId: Environment.ProcessId, TimeSpan.FromSeconds(2));

        IpcWelcomeMessage welcome = await ConnectAndSpeakAsync(endpoint, async client =>
        {
            byte[] garbage = Encoding.UTF8.GetBytes("not-json-at-all{{{");
            var frame = new byte[4 + garbage.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, garbage.Length);
            garbage.CopyTo(frame, 4);
            await client.WriteAsync(frame);
            await client.FlushAsync();
        }, TimeSpan.FromSeconds(2));

        Assert.False(welcome.Accepted);
        Assert.Contains("invalid-hello", welcome.Reason);
        Assert.Null(await wait);
    }

    private static Process StartHost(params string[] args)
    {
        var psi = new ProcessStartInfo(I2HostBinaries.ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task 握手超时_delay连接的Host等不到_有界返回null()
    {
        using var endpoint = PipeServerEndpoint.Create();
        // 真实 Host 8 秒后才连接；端点 1.5 秒期限先到。
        using var host = StartHost("--pipe", endpoint.PipeName, "--session", endpoint.SessionToken, "--fault", "delay-handshake");
        try
        {
            long t0 = Environment.TickCount64;
            ConnectedHost? connected = await endpoint.WaitForHostAsync(host.Id, TimeSpan.FromMilliseconds(1500));
            long elapsed = Environment.TickCount64 - t0;
            Assert.Null(connected);
            Assert.InRange(elapsed, 1000, 4000); // 到期返回，不无限等待
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            host.WaitForExit(10000);
        }
    }

    [Fact]
    public async Task 真实Host_完整会话_握手快照命令与受控关闭()
    {
        using var endpoint = PipeServerEndpoint.Create();
        using var host = StartHost(
            "--pipe", endpoint.PipeName,
            "--session", endpoint.SessionToken,
            "--parent-pid", Environment.ProcessId.ToString(),
            "--refresh-ms", "100",
            "--test-pattern");
        try
        {
            using ConnectedHost? connected = await endpoint.WaitForHostAsync(host.Id, TimeSpan.FromSeconds(5));
            Assert.NotNull(connected);
            Assert.Equal(host.Id, connected!.PeerProcessId);
            Assert.Equal(1, connected.PeerMajorVersion);

            // 读取真实快照（合成样本模式：12 个样本）。
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            IpcEnvelope? first = null;
            while (first is null || first.Header.Type != IpcProtocol.TypeSnapshot)
            {
                IpcEnvelope? envelope = await connected.ReadAsync(cts.Token);
                Assert.NotNull(envelope);
                first = envelope;
            }
            IpcSnapshotMessage snapshot = first!.PayloadAs<IpcSnapshotMessage>();
            Assert.Equal(12, snapshot.Snapshot.Samples.Count);
            Assert.True(snapshot.Snapshot.BatchId > 0);

            // 命令 → 应答（请求 ID 对得上）。
            await connected.SendAsync(new IpcEnvelope(
                new IpcHeader(1, 0, IpcProtocol.TypeCommand, endpoint.SessionToken, 77, 1),
                new IpcCommandMessage(IpcProtocol.OpRescan, null)), cts.Token);
            bool sawResponse = false;
            while (!sawResponse)
            {
                IpcEnvelope? envelope = await connected.ReadAsync(cts.Token);
                Assert.NotNull(envelope);
                if (envelope!.Header.Type == IpcProtocol.TypeResponse)
                {
                    Assert.Equal(77, envelope.Header.RequestId);
                    Assert.True(envelope.PayloadAs<IpcResponseMessage>().Ok);
                    sawResponse = true;
                }
            }

            // 受控关闭：shutdown → 应答 + goodbye → Host 退出码 0。
            await connected.SendAsync(new IpcEnvelope(
                new IpcHeader(1, 0, IpcProtocol.TypeCommand, endpoint.SessionToken, 78, 2),
                new IpcCommandMessage(IpcProtocol.OpShutdown, null)), cts.Token);
            bool sawGoodbye = false;
            while (!sawGoodbye)
            {
                IpcEnvelope? envelope = await connected.ReadAsync(cts.Token);
                if (envelope is null) break;
                if (envelope.Header.Type == IpcProtocol.TypeGoodbye) sawGoodbye = true;
            }
            Assert.True(sawGoodbye);
            Assert.True(host.WaitForExit(8000));
            Assert.Equal(0, host.ExitCode);
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            host.WaitForExit(10000);
        }
    }

    [Fact]
    public async Task Host故障注入_badSession与badVersion_握手被拒后自退()
    {
        foreach (string fault in new[] { "bad-session", "bad-version" })
        {
            using var endpoint = PipeServerEndpoint.Create();
            using var host = StartHost("--pipe", endpoint.PipeName, "--session", endpoint.SessionToken, "--fault", fault);
            ConnectedHost? connected = await endpoint.WaitForHostAsync(host.Id, TimeSpan.FromSeconds(3));
            Assert.Null(connected); // 拒绝后继续等待 → 期限到 null
            Assert.True(host.WaitForExit(8000), $"{fault}: Host 应在握手被拒后自行退出");
            Assert.Equal(13, host.ExitCode); // 13 = 服务端拒绝
        }
    }
}
