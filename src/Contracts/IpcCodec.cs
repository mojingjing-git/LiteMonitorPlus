using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiteMonitor.Contracts;

/// <summary>协议违规（帧超限 / 非法 JSON / 头缺失 / 未知类型）：接收侧必须有界拒绝并断连，不得尝试重同步。</summary>
public sealed class IpcProtocolException : Exception
{
    public IpcProtocolException(string message) : base(message) { }
}

/// <summary>
/// 帧编解码：[int32 小端长度 N][N 字节 UTF-8 JSON]。读写共用，Host/主程序/测试三方同一实现，
/// 协议测试即真实产品路径（P-I2 §2.6「真实调用产品路径」纪律）。
/// </summary>
public static class IpcCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // 契约 DTO 为 record + 预定义字段，读写两侧同版本编译；不用宽松忽略策略掩盖主协议冲突（Plan A §5.1）。
        PropertyNameCaseInsensitive = false,
    };

    public static byte[] Encode(IpcEnvelope envelope)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        if (json.Length > IpcProtocol.MaxMessageBytes)
            throw new IpcProtocolException($"消息体 {json.Length} 字节超过上限 {IpcProtocol.MaxMessageBytes}");
        var frame = new byte[IpcProtocol.LengthPrefixBytes + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, IpcProtocol.LengthPrefixBytes);
        return frame;
    }

    /// <summary>解码单帧（测试/工具用；流式读取走 <see cref="IpcFrameReader"/>）。</summary>
    public static IpcEnvelope DecodeFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length <= IpcProtocol.LengthPrefixBytes)
            throw new IpcProtocolException("空帧");
        int length = BinaryPrimitives.ReadInt32LittleEndian(frame);
        if (length <= 0 || length > IpcProtocol.MaxMessageBytes)
            throw new IpcProtocolException($"非法帧长 {length}");
        if (frame.Length - IpcProtocol.LengthPrefixBytes < length)
            throw new IpcProtocolException($"帧不完整：声明 {length}，实际 {frame.Length - IpcProtocol.LengthPrefixBytes}");
        return DecodeJson(frame.Slice(IpcProtocol.LengthPrefixBytes, length));
    }

    public static IpcEnvelope DecodeJson(ReadOnlySpan<byte> json)
    {
        IpcEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<IpcEnvelope>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new IpcProtocolException($"非法 JSON：{ex.Message}");
        }
        if (envelope?.Header is null)
            throw new IpcProtocolException("信封缺少 Header");
        IpcHeader h = envelope.Header;
        if (string.IsNullOrEmpty(h.Type) || !IpcProtocol.KnownTypes.Contains(h.Type))
            throw new IpcProtocolException($"未知消息类型：{h.Type ?? "(null)"}");
        if (string.IsNullOrEmpty(h.SessionId))
            throw new IpcProtocolException("信封缺少 SessionId");
        // 主版本裁决在端点握手层（hello → welcome(Accepted=false, reason) 后断连，可给出
        // "组件版本不匹配"提示）；结构合法但版本不符的帧由端点按协议违规处理。
        return envelope;
    }

    public static T DeserializePayload<T>(IpcEnvelope envelope) =>
        envelope.Payload is JsonElement element
            ? element.Deserialize<T>(Options) ?? throw new IpcProtocolException("载荷反序列化为 null")
            : throw new IpcProtocolException("载荷不是 JSON 对象（本端构造的信封不含 JsonElement）");

    /// <summary>把具体 DTO 序列化为命令参数 JSON（IpcCommandMessage.ArgumentJson）。</summary>
    public static string SerializeArgument<T>(T argument) => JsonSerializer.Serialize(argument, Options);
}

/// <summary>
/// 流式帧读取：内部缓冲累积，天然容忍任意拆包（每次 Stream.Read 至少推进 1 字节即继续）。
/// 协议违规抛 <see cref="IpcProtocolException"/>；对端正常关闭返回 null。
/// </summary>
public sealed class IpcFrameReader
{
    private readonly Stream _stream;
    private readonly byte[] _lengthBuffer = new byte[IpcProtocol.LengthPrefixBytes];
    private byte[]? _payloadBuffer;

    public IpcFrameReader(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>阻塞读取一帧；流结束（对端关闭）返回 null。</summary>
    public async Task<IpcEnvelope?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await ReadExactAsync(_lengthBuffer, cancellationToken).ConfigureAwait(false))
            return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(_lengthBuffer);
        if (length <= 0 || length > IpcProtocol.MaxMessageBytes)
            throw new IpcProtocolException($"非法帧长 {length}（上限 {IpcProtocol.MaxMessageBytes}）");

        _payloadBuffer ??= new byte[IpcProtocol.MaxMessageBytes];
        // 只在长度校验通过后分配/复用缓冲（Plan A §5.1：接收长度先校验再分配内存）。
        if (!await ReadExactAsync(new Memory<byte>(_payloadBuffer, 0, length), cancellationToken).ConfigureAwait(false))
            return null;
        return IpcCodec.DecodeJson(new ReadOnlySpan<byte>(_payloadBuffer, 0, length));
    }

    private async Task<bool> ReadExactAsync(Memory<byte> target, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < target.Length)
        {
            int read = await _stream.ReadAsync(target.Slice(total), cancellationToken).ConfigureAwait(false);
            if (read <= 0) return false; // 对端关闭
            total += read;
        }
        return true;
    }
}

/// <summary>有界串行写入器：单写线程逐帧写出（不做并发写；控制响应优先/快照槽位在其上层实现）。</summary>
public sealed class IpcFrameWriter
{
    private readonly Stream _stream;

    public IpcFrameWriter(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    public async Task WriteAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        byte[] frame = IpcCodec.Encode(envelope);
        await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
