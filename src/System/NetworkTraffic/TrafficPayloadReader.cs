using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace LiteMonitor.src.SystemServices.NetworkTraffic;

/// <summary>
/// 载荷读取器抽象。
/// <para>目的：让解码逻辑**只有一份**，同时支持</para>
/// <list type="bullet">
/// <item>零拷贝路径：直接读 ETW 缓冲区的原生内存（不复制、不分配）；</item>
/// <item>回退路径：读 <c>TraceEvent.EventData()</c> 复制出来的 byte[]。</item>
/// </list>
/// <para>两条路径的解码结果必须**逐字节一致**（有单元测试守着）。</para>
/// </summary>
internal abstract class TrafficPayloadReader
{
    /// <summary>载荷长度（字节）。</summary>
    public abstract int Length { get; }

    /// <summary>按指定字节序读取一个 32 位无符号整数。</summary>
    public abstract uint ReadUInt32(int offset, bool littleEndian);

    /// <summary>按指定字节序读取一个 64 位无符号整数。</summary>
    public abstract ulong ReadUInt64(int offset, bool littleEndian);

    /// <summary>按指定字节序读取一个 16 位无符号整数。</summary>
    public abstract ushort ReadUInt16(int offset, bool littleEndian);
}

/// <summary>原生内存读取器（零拷贝路径）。</summary>
internal sealed class NativePayloadReader : TrafficPayloadReader
{
    private readonly IntPtr _pointer;
    private readonly int _length;

    public NativePayloadReader(IntPtr pointer, int length)
    {
        _pointer = pointer;
        _length = length;
    }

    public override int Length => _length;

    public override uint ReadUInt32(int offset, bool littleEndian)
    {
        uint raw = unchecked((uint)Marshal.ReadInt32(_pointer, offset));
        return littleEndian == BitConverter.IsLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
    }

    public override ulong ReadUInt64(int offset, bool littleEndian)
    {
        ulong raw = unchecked((ulong)Marshal.ReadInt64(_pointer, offset));
        return littleEndian == BitConverter.IsLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
    }

    public override ushort ReadUInt16(int offset, bool littleEndian)
    {
        ushort raw = unchecked((ushort)Marshal.ReadInt16(_pointer, offset));
        return littleEndian == BitConverter.IsLittleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
    }
}

/// <summary>托管字节数组读取器（回退路径 / 单元测试）。</summary>
internal sealed class ArrayPayloadReader : TrafficPayloadReader
{
    private readonly byte[] _data;

    public ArrayPayloadReader(byte[] data)
    {
        _data = data;
    }

    public override int Length => _data.Length;

    public override uint ReadUInt32(int offset, bool littleEndian)
    {
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset, 4));
        return littleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
    }

    public override ulong ReadUInt64(int offset, bool littleEndian)
    {
        ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(offset, 8));
        return littleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
    }

    public override ushort ReadUInt16(int offset, bool littleEndian)
    {
        ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset, 2));
        return littleEndian ? raw : BinaryPrimitives.ReverseEndianness(raw);
    }
}
