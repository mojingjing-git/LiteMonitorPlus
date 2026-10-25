using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
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
