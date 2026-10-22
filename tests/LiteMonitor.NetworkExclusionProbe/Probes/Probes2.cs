using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LiteMonitor.src.SystemServices.NetworkTraffic;
using LiteMonitor.NetworkExclusionProbe.Support;

namespace LiteMonitor.NetworkExclusionProbe.Probes;

/// <summary>
/// Item 3: which layer does the ETW "size" field live on, and how far is it from
/// the native NIC byte counters. Both numbers must come from comparable windows.
/// </summary>
internal static class SizeLayerProbe
{
    public static async Task<int> Run(Evidence ev, string outDir)
    {
        var adapters = NetworkAdapterResolver.Build();
        IPAddress lanAddress = TrafficProbe.FindLanAddress(adapters);
        int lanIfIndex = -1;
        foreach (NetworkAdapterInfo a in adapters.Adapters)
        {
            foreach (IpKey k in a.Addresses)
            {
                if (k.Equals(IpKey.FromIPAddress(lanAddress))) { lanIfIndex = a.IfIndex; }
            }
        }

        var captured = new List<NetworkTrafficEvent>();
        using var source = new EtwNetworkTrafficEventSource(ev.Log);
        source.EventReceived += e => { lock (captured) { captured.Add(e); } };
        source.Start();
        await Task.Delay(700);

        // Warm-up: the real-time consumer does not necessarily deliver anything during
        // the first seconds of a session (the kernel flushes the first buffers late).
        // N0 measured 0 events in the first ~3 s of a fresh session and normal delivery
        // afterwards, so the probe waits for the first event before measuring.
        long warmupStart = source.EventsSeen;
        long warmupBegin = Environment.TickCount64;
        await TrafficGenerator.SendUdpAsync("warmup", IPAddress.Loopback, IPAddress.Loopback, 45999, 200, 20);
        long warmupDeadline = Environment.TickCount64 + 15000;
        while (source.EventsSeen == warmupStart && Environment.TickCount64 < warmupDeadline)
        {
            await Task.Delay(200);
        }
        ev.Set("sessionWarmUp", new
        {
            firstEventAfterMs = Environment.TickCount64 - warmupBegin,
            eventsSeenAfterWarmUp = source.EventsSeen,
            note = "A freshly started real-time session may deliver no events for the first seconds; the first measurement window must be discarded.",
        });

        // ---------- A. segmentation: one 3000 byte TCP write over the LAN NIC ----------
        var (sink, sinkTask) = TrafficGenerator.StartTcpSink(lanAddress, 0);
        int sinkPort = ((IPEndPoint)sink.LocalEndpoint).Port;
        int beforeCount;
        lock (captured) { beforeCount = captured.Count; }
        DateTime tStart = DateTime.Now;
        TrafficRecord tcpRec = await TrafficGenerator.SendTcpAsync("tcp-lan-3000", lanAddress, lanAddress, sinkPort, 3000, 1);
        await ProbeSupport.DrainAsync(source);
        await Task.Delay(5000);
        await ProbeSupport.DrainAsync(source);
        DateTime tEnd = DateTime.Now;
        List<NetworkTrafficEvent> segEvents;
        lock (captured) { segEvents = captured.Skip(beforeCount).ToList(); }
        sink.Stop();

        var segSend = captured
            .Where(e => e.Protocol == NetworkTrafficProtocol.Tcp
                     && e.Direction == NetworkTrafficDirection.Send
                     && e.RemotePort == sinkPort)
            .ToList();
        ev.Set("tcpSegmentation", new
        {
            wroteBytes = tcpRec.BytesSent,
            writeCount = tcpRec.PacketsSent,
            etwSendEventCount = segSend.Count,
            etwSizes = segSend.Select(x => x.SizeBytes).ToList(),
            etwTotalBytes = segSend.Sum(x => x.SizeBytes),
            localAddresses = segSend.Select(x => x.LocalAddress.ToString()).Distinct().ToList(),
            remoteAddresses = segSend.Select(x => x.RemoteAddress.ToString()).Distinct().ToList(),
            note = "A write to the host's own LAN address is delivered locally and is not MSS segmented. Background internet TCP SEND events report PRE-segmentation (LSO) buffer sizes, while the matching CopyUp events report the on-wire MSS sized segments. The size field is a transport payload size at the stack layer, never a link layer size, and CopyUp must never be counted as new data.",
            backgroundTcpSendSizes = captured
                .Where(e => e.Protocol == NetworkTrafficProtocol.Tcp
                         && e.Direction == NetworkTrafficDirection.Send
                         && e.RemotePort != sinkPort)
                .GroupBy(e => e.SizeBytes)
                .Select(g => new { size = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
                .Take(6)
                .ToArray(),
        });

        // ---------- B. offload: a single 8000 byte UDP datagram over the LAN NIC ----------
        beforeCount = captured.Count;
        tStart = DateTime.Now;
        TrafficRecord udpRec = await TrafficGenerator.SendUdpAsync("udp-lan-8000", lanAddress, lanAddress, 45010, 8000, 5);
        await ProbeSupport.DrainAsync(source);
        await Task.Delay(5000);
        await ProbeSupport.DrainAsync(source);
        tEnd = DateTime.Now;
        var offloadSend = captured
            .Where(e => e.Protocol == NetworkTrafficProtocol.Udp
                     && e.Direction == NetworkTrafficDirection.Send
                     && e.RemotePort == 45010)
            .ToList();
        ev.Set("udpOffload", new
        {
            datagramBytes = udpRec.BytesSent / udpRec.PacketsSent,
            datagramCount = udpRec.PacketsSent,
            etwSendEventCount = offloadSend.Count,
            etwSizes = offloadSend.Select(x => x.SizeBytes).ToList(),
            note = "ETW reports the datagram payload size, i.e. it is captured above the NIC/LSO/GRO offload layer.",
        });

        // ---------- C. same-window ETW vs native NIC counters ----------
        // Traffic to the host's own LAN address is served by the local stack and never
        // crosses the NIC, so the local router (default gateway) is used as the
        // controlled off-host endpoint to make the NIC counters really move.
        IPAddress? gateway = FindGateway();
        IpKey lanKey = IpKey.FromIPAddress(lanAddress);
        const int DurationSeconds = 10;
        const int PayloadSize = 1000;
        (long nicSentBefore, long nicRecvBefore) = ProbeSupport.NicByteCounters(lanIfIndex);
        DateTime winStart = DateTime.Now;
        long swStart = Stopwatch.GetTimestamp();
        TrafficRecord rtp = gateway != null
            ? await TrafficGenerator.SendUdpRateAsync("rtp-gateway-200kbs", lanAddress, gateway, 45020, PayloadSize, 200_000, DurationSeconds)
            : await TrafficGenerator.SendUdpRateAsync("rtp-lan-200kbs", lanAddress, lanAddress, 45020, PayloadSize, 200_000, DurationSeconds);
        long swEnd = Stopwatch.GetTimestamp();
        DateTime winEnd = DateTime.Now;
        (long nicSentAfter, long nicRecvAfter) = ProbeSupport.NicByteCounters(lanIfIndex);
        await ProbeSupport.DrainAsync(source);
        await Task.Delay(300);

        List<NetworkTrafficEvent> windowEvents;
        lock (captured) { windowEvents = captured.ToList(); }

        var inWindow = windowEvents.Where(e => e.Timestamp >= winStart && e.Timestamp <= winEnd.AddSeconds(1)).ToList();
        var lanEvents = inWindow.Where(e => e.LocalAddress.Equals(lanKey)).ToList();
        long etwSend = lanEvents.Where(e => e.Direction == NetworkTrafficDirection.Send).Sum(e => e.SizeBytes);
        long etwRecv = lanEvents.Where(e => e.Direction == NetworkTrafficDirection.Receive).Sum(e => e.SizeBytes);
        long nicSendDelta = nicSentAfter - nicSentBefore;
        long nicRecvDelta = nicRecvAfter - nicRecvBefore;
        double seconds = (swEnd - swStart) / (double)Stopwatch.Frequency;

        ev.Set("sameWindowComparison", new
        {
            adapter = new { ifIndex = lanIfIndex, address = lanAddress.ToString() },
            offHostEndpoint = gateway?.ToString(),
            windowSeconds = Math.Round(seconds, 3),
            generatorBytes = rtp.BytesSent,
            generatorPackets = rtp.PacketsSent,
            etwBytesOnAdapter = new { send = etwSend, receive = etwRecv },
            etwEventCount = lanEvents.Count,
            nicByteCounters = new { sendDelta = nicSendDelta, receiveDelta = nicRecvDelta },
            ratioEtwSendOverNicSend = nicSendDelta == 0 ? (double?)null : Math.Round((double)etwSend / nicSendDelta, 4),
            ratioEtwRecvOverNicRecv = nicRecvDelta == 0 ? (double?)null : Math.Round((double)etwRecv / nicRecvDelta, 4),
            note = "Both figures cover the same window. The gap is the protocol header / offload / non-TCP-UDP layer difference, which is why the excluded amount must be produced by the SAME event source as the total.",
        });

        source.Stop();
        return 0;
    }

    private static IPAddress? FindGateway()
    {
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) { continue; }
            foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
            {
                if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(g.Address))
                {
                    return g.Address;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Self describing census of a window: what was actually captured, grouped by
    /// protocol / direction / local / remote / port / size.
    /// </summary>
    private static object[] Census(IEnumerable<NetworkTrafficEvent> events, int top = 12)
    {
        return events
            .GroupBy(e => e.Protocol + "/" + e.Direction + "/" + e.Kind + " local=" + e.LocalAddress +
                          " remote=" + e.RemoteAddress + ":" + e.RemotePort + " size=" + e.SizeBytes)
            .Select(g => new
            {
                flow = g.Key,
                count = g.Count(),
                bytes = g.Sum(x => x.SizeBytes),
            })
            .OrderByDescending(x => x.count)
            .Take(top)
            .ToArray();
    }
}

/// <summary>Item 7: CPU, allocations, memory and event loss under four load shapes.</summary>
internal static class PerformanceProbe
{
    private sealed record PhaseResult(
        string Phase,
        double Seconds,
        double TrafficSeconds,
        long CpuMs,
        double CpuPercentOfMachine,
        long AllocatedBytes,
        long AllocatedBytesPerEvent,
        long ManagedHeapBytes,
        long WorkingSetBytes,
        long EventsProcessed,
        long EventsLost,
        long DuplicateSuppressed,
        long BytesSent,
        long ZeroCopyDecoded,
        long CopyFallbackDecoded,
        long CopyFallbackFailures)
    {
        /// <summary>事件速率（事件/秒）。</summary>
        public double EventsPerSecond => Seconds > 0 ? EventsProcessed / Seconds : 0;

        /// <summary>每事件消耗的 CPU（微秒）。</summary>
        public double CpuUsPerEvent => EventsProcessed > 0 ? (CpuMs * 1000.0) / EventsProcessed : 0;
    }

    public static async Task<int> Run(Evidence ev, string outDir, bool withEtw = true)
    {
        var adapters = NetworkAdapterResolver.Build();
        IPAddress lanAddress = TrafficProbe.FindLanAddress(adapters);

        var captured = new List<NetworkTrafficEvent>();
        EtwNetworkTrafficEventSource? source = null;
        NetworkTrafficFilter? filter = null;
        if (withEtw)
        {
            source = new EtwNetworkTrafficEventSource(ev.Log);
            filter = new NetworkTrafficFilter(adapters);
            var rules = IpExclusionRules.Compile(new[] { "127.0.0.1" }, out _);
            filter.SetRules(rules, -1, RetransmitPolicy.ExcludeRetransmitFromTotal);
            EtwNetworkTrafficEventSource s = source;
            s.EventReceived += e =>
            {
                lock (captured) { captured.Add(e); }
                filter!.Apply(e, s.Generation);
            };

            s.Start();
            await Task.Delay(700);
        }
        else
        {
            // ★ 对照组：**不创建 ETW 会话**，同样的流量形状，用来把 CPU 归因到
            //   「流量发生器」还是「ETW 采集+解码」本身。
            ev.Log("control run: ETW session NOT created");
        }

        var results = new List<PhaseResult>();

        results.Add(await Measure(ev, source, filter, "idle", 5, async () =>
        {
            await Task.Delay(5000);
            return 0;
        }));

        results.Add(await Measure(ev, source, filter, "rtp-200kBps", 10, async () =>
        {
            // 200 byte RTP-ish payloads paced to 200 000 bytes/s for 10 s
            var rec = await TrafficGenerator.SendUdpRateAsync("rtp", IPAddress.Loopback, IPAddress.Loopback, 46000, 200, 200_000, 10);
            return rec.BytesSent;
        }));

        results.Add(await Measure(ev, source, filter, "tcp-high-rate", 10, async () =>
        {
            var (sink, _) = TrafficGenerator.StartTcpSink(IPAddress.Loopback, 0);
            int port = ((IPEndPoint)sink.LocalEndpoint).Port;
            var sw = Stopwatch.StartNew();
            long total = 0;
            var chunk = new byte[64000];
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            NetworkStream ns = client.GetStream();            while (sw.Elapsed.TotalSeconds < 10)
            {
                await ns.WriteAsync(chunk);
                total += chunk.Length;
            }
            client.Dispose();
            sink.Stop();
            return total;
        }));

        results.Add(await Measure(ev, source, filter, "high-packet-rate", 8, async () =>
        {
            // 100 byte payloads as fast as possible -> maximum event rate
            var sw = Stopwatch.StartNew();
            using var udp = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var payload = new byte[100];
            var remote = new IPEndPoint(IPAddress.Loopback, 47000);
            long total = 0;
            while (sw.Elapsed.TotalSeconds < 8)
            {
                for (int i = 0; i < 200; i++)
                {
                    await udp.SendAsync(payload, payload.Length, remote);
                    total += payload.Length;
                }
                await Task.Delay(5);
            }
            return total;
        }));

        ev.Set("performancePhases", results);
        ev.Set("performanceSummary", new
        {
            processorCount = Environment.ProcessorCount,
            etwSessionEnabled = withEtw,
            note = "CpuPercentOfMachine is process CPU time divided by wall time divided by logical processor count. " +
                   "With etwSessionEnabled=false the same traffic shapes run WITHOUT any ETW session, which attributes CPU to the generator.",
        });

        source?.Stop();
        source?.Dispose();
        return 0;
    }

    private static async Task<PhaseResult> Measure(
        Evidence ev,
        EtwNetworkTrafficEventSource? source,
        NetworkTrafficFilter? filter,
        string phase,
        int expectedSeconds,
        Func<Task<long>> workload)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocBefore = GC.GetTotalAllocatedBytes(true);
        long heapBefore = GC.GetTotalMemory(true);
        long eventsBefore = source?.EventsProcessed ?? 0;
        long lostBefore = source?.EventsLost ?? 0;
        long zeroBefore = source?.ZeroCopyDecoded ?? 0;
        long fbDecodedBefore = source?.CopyFallbackDecoded ?? 0;
        long fbFailedBefore = source?.CopyFallbackFailures ?? 0;
        long cpuBefore = Process.GetCurrentProcess().TotalProcessorTime.Ticks;
        long swStart = Stopwatch.GetTimestamp();

        long bytes = await workload();
        long trafficEnd = Stopwatch.GetTimestamp();

        // Let the ETW consumer drain so the event count of the phase is complete.
        if (source != null)
        {
            await ProbeSupport.DrainAsync(source);
        }

        long swEnd = Stopwatch.GetTimestamp();
        long cpuAfter = Process.GetCurrentProcess().TotalProcessorTime.Ticks;
        long allocAfter = GC.GetTotalAllocatedBytes(true);
        long heapAfter = GC.GetTotalMemory(false);
        long eventsAfter = source?.EventsProcessed ?? 0;
        long lostAfter = source?.EventsLost ?? 0;
        long zeroAfter = source?.ZeroCopyDecoded ?? 0;
        long fbDecodedAfter = source?.CopyFallbackDecoded ?? 0;
        long fbFailedAfter = source?.CopyFallbackFailures ?? 0;

        double seconds = (swEnd - swStart) / (double)Stopwatch.Frequency;
        double trafficSeconds = (trafficEnd - swStart) / (double)Stopwatch.Frequency;
        long cpuMs = (cpuAfter - cpuBefore) * 1000 / Stopwatch.Frequency;
        long events = eventsAfter - eventsBefore;
        long alloc = allocAfter - allocBefore;
        double cpuPercentOfMachine = seconds > 0 ? (cpuMs / 1000.0) / seconds / Environment.ProcessorCount * 100.0 : 0;

        var result = new PhaseResult(
            phase, Math.Round(seconds, 3), Math.Round(trafficSeconds, 3), cpuMs, Math.Round(cpuPercentOfMachine, 4),
            alloc, events == 0 ? 0 : alloc / events, heapAfter,
            Process.GetCurrentProcess().WorkingSet64, events, lostAfter - lostBefore, 0, bytes,
            zeroAfter - zeroBefore, fbDecodedAfter - fbDecodedBefore, fbFailedAfter - fbFailedBefore);

        ev.Log("phase " + phase + ": " + result.Seconds + "s cpu=" + result.CpuMs + "ms (" +
               result.CpuPercentOfMachine + "% of machine) events=" + result.EventsProcessed +
               " alloc=" + result.AllocatedBytes + "B lost=" + result.EventsLost +
               " zeroCopy=" + result.ZeroCopyDecoded +
               " copyFallbackDecoded=" + result.CopyFallbackDecoded +
               " copyFallbackFailures=" + result.CopyFallbackFailures);
        return result;
    }
}
