using System.Runtime.InteropServices;
using LiteMonitor.src.SystemServices.NetworkTraffic;
using LiteMonitor.NetworkExclusionProbe.Support;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace LiteMonitor.NetworkExclusionProbe.Probes;

/// <summary>
/// 载荷偏移标定：找出 ETW 记录里**用户数据**相对 <c>TraceEvent.DataStart</c> 的字节偏移。
///
/// <para>为什么需要它：N0 用 <c>TraceEvent.EventData()</c> 解码，<b>每个事件复制一次载荷</b>，
/// 这是分配的主因（227~423 B/事件）。产品的零拷贝解码路径要直接读原生内存，
/// 就必须知道用户数据在 <c>DataStart</c> 之后的哪个位置。</para>
///
/// <para>做法：拿到第一个数据事件后，取 <c>EventData()</c>（= 用户数据的权威副本），
/// 再在 <c>DataStart</c> 起的 128 字节窗口里按 4 字节步进比对 PID+size，
/// 命中即偏移量；再用产品解码器复核一次。</para>
/// </summary>
internal static class CalibrationProbe
{
    private const int ScanWindowBytes = 128;

    public static async Task<int> Run(Evidence ev, string outDir)
    {
        string sessionName = "LiteMonitor.NetExclusion.Calibrate." + Environment.ProcessId + "." +
                             DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");

        int? offset = null;
        int? payloadLength = null;
        int? eventDataLengthProperty = null;
        byte[]? copy = null;
        string? decodeCheck = null;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        TraceEventSession? session = null;
        Task? pump = null;
        try
        {
            session = new TraceEventSession(sessionName);
            bool enabled = session.EnableProvider(EtwNetworkTrafficEventSource.ProviderName, TraceEventLevel.Verbose, 0x30);
            ev.Set("enableProviderByNameReturned", enabled);
            bool enabledByGuid = enabled;
            if (!enabled)
            {
                // 按 GUID 再试一次（N0 的探针忽略返回值，只看事件是否到达）
                enabledByGuid = session.EnableProvider(EtwNetworkTrafficEventSource.ProviderGuid, TraceEventLevel.Verbose, 0x30);
                ev.Set("enableProviderByGuidReturned", enabledByGuid);
            }
            ev.Log("EnableProvider(name)=" + enabled + " EnableProvider(guid)=" + enabledByGuid);

            session.Source.AllEvents += (TraceEvent e) =>
            {
                if (offset != null || copy != null) { return; }
                int task = Convert.ToInt32(e.Task);
                int opcode = Convert.ToInt32(e.Opcode);
                if (!EtwNetworkTrafficEventSource.IsDataEvent(task, opcode)) { return; }

                byte[] data;
                try { data = e.EventData(); }
                catch { return; }
                if (data == null || data.Length == 0) { return; }

                copy = data;
                payloadLength = data.Length;
                eventDataLengthProperty = e.EventDataLength;

                IntPtr dataStart = e.DataStart;
                uint wantPid = BitConverter.ToUInt32(data, 0);
                uint wantSize = BitConverter.ToUInt32(data, 4);

                for (int o = 0; o + 8 <= ScanWindowBytes; o += 4)
                {
                    uint gotPid = unchecked((uint)Marshal.ReadInt32(dataStart, o));
                    if (gotPid != wantPid) { continue; }
                    uint gotSize = unchecked((uint)Marshal.ReadInt32(dataStart, o + 4));
                    if (gotSize != wantSize) { continue; }
                    offset = o;
                    break;
                }

                // 用产品解码器复核载荷本身
                if (EtwNetworkTrafficEventSource.TryDecodePayload(data, task, opcode, DateTime.UnixEpoch, out var decoded))
                {
                    decodeCheck = "task=" + task + " opcode=" + opcode +
                                 " proto=" + decoded.Protocol +
                                 " dir=" + decoded.Direction +
                                 " kind=" + decoded.Kind +
                                 " remote=" + decoded.RemoteAddress + ":" + decoded.RemotePort +
                                 " local=" + decoded.LocalAddress + ":" + decoded.LocalPort +
                                 " size=" + decoded.SizeBytes +
                                 " pid=" + decoded.ProcessId;
                }
                else
                {
                    decodeCheck = "product decoder rejected the payload";
                }

                ready.TrySetResult(true);
            };

            pump = Task.Run(() => session.Source.Process());

            // Provider 只在**有流量**时才发事件 —— 必须同时造一点回环流量。
            using (var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
            {
                var payload = new byte[200];
                var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 47900);
                var traffic = Task.Run(async () =>
                {
                    while (!ready.Task.IsCompleted)
                    {
                        try { await udp.SendAsync(payload, payload.Length, remote); } catch { /* ignore */ }
                        await Task.Delay(20);
                    }
                });
                await Task.WhenAny(ready.Task, Task.Delay(20000));
                await traffic;
            }
        }
        catch (Exception ex)
        {
            ev.Log("calibration failed: " + ProbeSupport.DescribeException(ex));
            ev.Set("calibrationError", ProbeSupport.DescribeException(ex));
            return 1;
        }
        finally
        {
            try { session?.Source.StopProcessing(); } catch { /* ignore */ }
            try { session?.Dispose(); } catch { /* ignore */ }
            try { pump?.Wait(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        }

        ev.Set("calibration", new
        {
            payloadOffsetFromDataStart = offset,
            payloadLength,
            eventDataLengthProperty,
            scanWindowBytes = ScanWindowBytes,
            decodeCheck,
            note = "payloadOffsetFromDataStart is the byte offset of the provider user data inside the ETW record, " +
                   "measured from TraceEvent.DataStart. The product uses it for the zero-copy decode path.",
        });
        ev.Log("calibration result: offset=" + (offset?.ToString() ?? "not found") +
               " payloadLength=" + (payloadLength?.ToString() ?? "?"));

        return offset.HasValue ? 0 : 1;
    }
}
