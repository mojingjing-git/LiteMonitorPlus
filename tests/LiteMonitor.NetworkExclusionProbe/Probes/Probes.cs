using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using LiteMonitor.src.SystemServices.NetworkTraffic;
using LiteMonitor.NetworkExclusionProbe.Support;

namespace LiteMonitor.NetworkExclusionProbe.Probes;

/// <summary>Shared helpers for every N0 probe scenario.</summary>
internal static class ProbeSupport
{
    /// <summary>本地地址 → 网卡的解析结果（视图）。</summary>
    internal sealed record AdapterResolutionView(int? IfIndex, bool Ambiguous, string Reason, IReadOnlyList<int> Candidates);

    /// <summary>用产品模块的归属映射解析本地地址。</summary>
    public static AdapterResolutionView Resolve(NetworkAdapterResolver resolver, IpKey local)
    {
        bool ok = resolver.TryResolveIfIndex(local, out int ifIndex);
        bool ambiguous = resolver.IsAmbiguous(local);
        IReadOnlyList<int> candidates = resolver.Candidates(local);
        string reason = ok ? "exact"
            : ambiguous ? "same address present on " + candidates.Count + " interfaces"
            : "local address not owned by any known interface";
        return new AdapterResolutionView(ok ? ifIndex : null, ambiguous, reason, candidates);
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static string DescribeException(Exception ex)
    {
        int win32 = ex.HResult & 0xFFFF;
        string socket = ex is System.Net.Sockets.SocketException se
            ? " | SocketErrorCode=" + se.SocketErrorCode + " (" + (int)se.SocketErrorCode + ")"
            : string.Empty;
        return ex.GetType().FullName + " | HResult=0x" + ex.HResult.ToString("X8") +
               " | Win32=" + win32 + " (" + win32 + ")" + socket + " | Message=" + ex.Message +
               " | Stack=" + (ex.StackTrace ?? string.Empty).Replace("\r", " ").Replace("\n", " <- ");
    }

    public static List<string> ActiveSessionNames()
    {
        try
        {
            return Microsoft.Diagnostics.Tracing.Session.TraceEventSession.GetActiveSessionNames()
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex)
        {
            return new List<string> { "<enumerate failed: " + ex.Message + ">" };
        }
    }

    public static NetworkInterface? FindNic(int ifIndex)
    {
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.GetIPProperties().GetIPv4Properties().Index == ifIndex) { return ni; }
            }
            catch { /* ignore */ }
        }
        return null;
    }

    /// <summary>
    /// Waits until the ETW consumer has caught up with the generated traffic.
    /// ETW delivers asynchronously, so measuring immediately after the send would
    /// under-count badly.
    /// </summary>
    public static async Task<long> DrainAsync(EtwNetworkTrafficEventSource source, int quietMs = 400, int timeoutMs = 8000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        long last = source.EventsProcessed;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(100);
            long now = source.EventsProcessed;
            if (now == last)
            {
                await Task.Delay(quietMs);
                if (source.EventsProcessed == now) { return now; }
                last = source.EventsProcessed;
            }
            else
            {
                last = now;
            }
        }
        return source.EventsProcessed;
    }

    public static (long Sent, long Received) NicByteCounters(int ifIndex)
    {
        NetworkInterface? nic = FindNic(ifIndex);
        if (nic == null) { return (0, 0); }
        try
        {
            IPInterfaceStatistics s = nic.GetIPStatistics();
            return (s.BytesSent, s.BytesReceived);
        }
        catch
        {
            return (0, 0);
        }
    }

    public static string[] DescribeAdapters(NetworkAdapterResolver map)
    {
        var list = new List<string>();
        foreach (NetworkAdapterInfo a in map.Adapters)
        {
            list.Add("ifIndex=" + a.IfIndex + " name='" + a.FriendlyName + "' type=" + a.Type +
                     " up=" + a.IsUp + " addrs=[" + string.Join(", ", a.Addresses.Select(x => x.ToString())) + "]");
        }
        return list.ToArray();
    }
}

/// <summary>Item 1 + item 6: environment facts, provider enable, failure codes, session lifecycle.</summary>
internal static class CapabilityProbe
{
    public static async Task<int> Run(Evidence ev, string outDir)
    {
        ev.Set("windows", new
        {
            osVersion = Environment.OSVersion.VersionString,
            osDescription = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            osDescriptionFull = Environment.OSVersion.Version.ToString(),
            processorCount = Environment.ProcessorCount,
            dotnet = RuntimeInformation.FrameworkDescription,
            machineName = Environment.MachineName,
        });

        ev.Set("privileges", new
        {
            isAdministrator = ProbeSupport.IsAdministrator(),
            user = Environment.UserName,
            userDomain = Environment.UserDomainName,
            integrityLevel = ProbeSupport.IsAdministrator() ? "High" : "Medium (filtered token)",
            note = "The product's own app.manifest declares requireAdministrator, so the shipping app already runs elevated.",
        });

        // TraceEvent package facts (license + redistributability).
        var asm = typeof(Microsoft.Diagnostics.Tracing.TraceEvent).Assembly;
        ev.Set("decoderLibrary", new
        {
            name = "Microsoft.Diagnostics.Tracing.TraceEvent",
            assemblyVersion = asm.GetName().Version?.ToString(),
            fileVersion = asm.GetCustomAttribute<System.Reflection.AssemblyFileVersionAttribute>()?.Version,
            nugetVersion = "4.0.0",
            license = "MIT (license expression in the .nuspec: <license type=\"expression\">MIT</license>)",
            targetFramework = "netstandard2.0 (runs on .NET 8)",
            projectUrl = "https://github.com/Microsoft/perfview",
            redistributableWithProduct = true,
        });

        ev.Set("provider", new
        {
            name = EtwNetworkTrafficEventSource.ProviderName,
            guid = EtwNetworkTrafficEventSource.ProviderGuid.ToString(),
            keywords = "0x10 IPV4 | 0x20 IPV6 (matchAny = 0x30)",
            level = "Verbose",
            channel = "Microsoft-Windows-Kernel-Network/Analytic (id 16)",
            tasks = "10 = TCPIP, 11 = UDPIP",
            opcodes = "10 SEND, 11 RECV, 12 CONNECT, 13 DISCONNECT, 14 RETRANSMIT, 15 ACCEPT, 16 RECONNECT, 17 FAIL, 18 TCPCOPY, 42 SENDUDP, 43 RECVUDP, 49 FAILUDP",
            referenceDocs = new[]
            {
                "https://learn.microsoft.com/en-us/windows/win32/etw/tcpip-typegroup1",
                "https://learn.microsoft.com/en-us/windows/win32/etw/udpip-typegroup1",
                "https://learn.microsoft.com/en-us/windows/win32/etw/udpip-typegroup2",
                "https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracea",
            },
        });

        var before = ProbeSupport.ActiveSessionNames();
        ev.Set("etwSessionsBefore", before);
        ev.Log("ETW sessions before probe: " + string.Join(", ", before));

        // --- session lifecycle ---
        var lifecycle = new List<object>();
        var source = new EtwNetworkTrafficEventSource(ev.Log);
        lifecycle.Add(new { step = "sessionName", value = source.SessionName, reservedNameReuse = false });

        string? enableError = null;
        string? enableErrorType = null;
        int enableHResult = 0;
        int enableWin32 = 0;
        try
        {
            source.Start();
            ev.Log("EnableProvider succeeded. generation=" + source.Generation + " status=" + source.Status);
        }
        catch (Exception ex)
        {
            enableError = ex.Message;
            enableErrorType = ex.GetType().FullName;
            enableHResult = ex.HResult;
            enableWin32 = ex.HResult & 0xFFFF;
            ev.Log("EnableProvider FAILED: " + ProbeSupport.DescribeException(ex));
        }

        long processed = 0;
        long lost = 0;
        if (source.Status == TrafficSourceStatus.Running)
        {
            await Task.Delay(1500);
            processed = source.EventsProcessed;
            lost = source.EventsLost;
        }

        source.Stop();
        await Task.Delay(400);
        var after = ProbeSupport.ActiveSessionNames();
        ev.Set("sessionLifecycle", new
        {
            sessionName = source.SessionName,
            status = source.Status.ToString(),
            statusDetail = source.StatusDetail,
            enableErrorType,
            enableError,
            enableHResult = "0x" + enableHResult.ToString("X8"),
            enableWin32Code = enableWin32,
            eventsWhileRunning = processed,
            eventsLost = lost,
            sessionsAfterStop = after,
            residueAfterStop = after.Except(before).ToList(),
            presentMonSessionStillPresent = after.Contains("LiteMonitor++_Golden_Session"),
        });
        ev.Set("etwSessionsAfter", after);
        ev.Log("ETW sessions after probe: " + string.Join(", ", after));

        // --- stop / restart recoverability ---
        var restart = new List<object>();
        for (int i = 0; i < 2; i++)
        {
            try
            {
                source.Start();
                await Task.Delay(600);
                restart.Add(new { attempt = i + 1, ok = true, generation = source.Generation, events = source.EventsProcessed });
                source.Stop();
            }
            catch (Exception ex)
            {
                restart.Add(new { attempt = i + 1, ok = false, error = ProbeSupport.DescribeException(ex) });
            }
        }
        var afterRestart = ProbeSupport.ActiveSessionNames();
        ev.Set("restartBehaviour", new
        {
            attempts = restart,
            sessionsAfter = afterRestart,
            residue = afterRestart.Except(before).ToList(),
        });

        // --- can the classic TCPIP / UDPIP providers (the ones behind the MSDN
        //     TcpIp_TypeGroup1 / UdpIp_TypeGroup1 pages) be enabled on a PRIVATE session?
        //     Enabling them on the NT Kernel Logger is excluded by the implementation plan. ---
        var classicAttempts = new List<object>();
        var classicCandidates = new (string Name, Guid Guid)[]
        {
            ("TCPIP (TcpIp_TypeGroup1)", new Guid("3D6FA8D4-FE05-4A9D-8C91-7C7B5FA7E4D1")),
            ("UDPIP (UdpIp_TypeGroup1)", new Guid("3D6FA8D1-FE05-4A9D-8C91-7C7B5FA7E4D1")),
        };
        foreach (var (name, guid) in classicCandidates)
        {
            try
            {
                using var classicSession = new Microsoft.Diagnostics.Tracing.Session.TraceEventSession(
                    "LiteMonitor.NetExclusion.Classic." + Environment.ProcessId + "." + name.GetHashCode());
                bool ok = classicSession.EnableProvider(guid, Microsoft.Diagnostics.Tracing.TraceEventLevel.Verbose, 0UL);
                classicAttempts.Add(new { provider = name, guid = guid.ToString(), enabled = ok, error = (string?)null });
            }
            catch (Exception ex)
            {
                classicAttempts.Add(new
                {
                    provider = name,
                    guid = guid.ToString(),
                    enabled = false,
                    error = ProbeSupport.DescribeException(ex),
                });
            }
        }
        ev.Set("classicProviderAttempts", classicAttempts);

        // --- decoding contract (pure function, no traffic needed) ---
        var decodeSamples = new List<object>();
        decodeSamples.Add(DescribeDecode(new byte[] { 0xA0, 0x58, 0x00, 0x00, 0xE8, 0x03, 0x00, 0x00, 0x7F, 0x00, 0x00, 0x01, 0x7F, 0x00, 0x00, 0x01, 0xAF, 0xC9, 0xF9, 0x4C, 0, 0, 0, 0, 0, 0, 0, 0 }, 11, 42));
        decodeSamples.Add(DescribeDecode(new byte[] { 0xA0, 0x58, 0x00, 0x00, 0xB8, 0x0B, 0x00, 0x00, 0x7F, 0x00, 0x00, 0x01, 0x7F, 0x00, 0x00, 0x01, 0x04, 0x7E, 0x04, 0x7F, 0x39, 0x45, 0x44, 0x00, 0x39, 0x45, 0x44, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 }, 10, 10));
        ev.Set("decodeContract", decodeSamples);

        return 0;
    }

    private static object DescribeDecode(byte[] data, int task, int opcode)
    {
        bool ok = EtwNetworkTrafficEventSource.TryDecodePayload(data, task, opcode, DateTime.UnixEpoch, out NetworkTrafficEvent e);
        return new
        {
            task,
            opcode,
            payloadLength = data.Length,
            decoded = ok,
            protocol = ok ? e.Protocol.ToString() : null,
            direction = ok ? e.Direction.ToString() : null,
            kind = ok ? e.Kind.ToString() : null,
            remote = ok ? e.RemoteAddress.ToString() : null,
            remotePort = ok ? e.RemotePort : 0,
            local = ok ? e.LocalAddress.ToString() : null,
            localPort = ok ? e.LocalPort : 0,
            sizeBytes = ok ? e.SizeBytes : 0,
            processId = ok ? e.ProcessId : 0,
        };
    }
}

/// <summary>Items 2, 4, 5: which events count, direction correctness, adapter attribution, RTP/multicast.</summary>
internal static class TrafficProbe
{
    public static async Task<int> Run(Evidence ev, string outDir)
    {
        var adapters = NetworkAdapterResolver.Build();
        ev.Set("adapters", ProbeSupport.DescribeAdapters(adapters));

        var captured = new List<NetworkTrafficEvent>();
        var source = new EtwNetworkTrafficEventSource(ev.Log);
        var filter = new NetworkTrafficFilter(adapters);
        var rules = IpExclusionRules.Compile(new[] { "127.0.0.1", "::1", "239.255.42.99" }, out _);
        filter.SetRules(rules, -1, RetransmitPolicy.ExcludeRetransmitFromTotal);

        source.EventReceived += e =>
        {
            lock (captured) { captured.Add(e); }
            filter.Apply(e, source.Generation);
        };

        source.Start();
        await Task.Delay(700);
        long swStart = Stopwatch.GetTimestamp();
        filter.BeginWindow(swStart, swStart, 1.0, source.Generation);

        // ---- controlled traffic ----
        var records = new List<TrafficRecord>();
        var cts = new CancellationTokenSource();

        // Multicast receiver (same host, loopback of the group) for an independent byte count.
        Task<(long Bytes, int Packets, int LocalPort)>? mcReceive = null;
        IPAddress group = IPAddress.Parse("239.255.42.99");
        const int GroupPort = 5004;
        IPAddress lanAddress = FindLanAddress(adapters);
        try
        {
            mcReceive = TrafficGenerator.ReceiveMulticastAsync(group, lanAddress, GroupPort, cts.Token);
            await Task.Delay(200);
        }
        catch (Exception ex)
        {
            ev.Log("multicast receiver failed: " + ProbeSupport.DescribeException(ex));
            ev.Add("multicastReceiverFailures", ProbeSupport.DescribeException(ex));
        }

        records.Add(await RunScenario(ev, "udp4-loopback", () =>
            TrafficGenerator.SendUdpAsync("udp4-loopback", IPAddress.Loopback, IPAddress.Loopback, 45001, 1000, 50)));
        records.Add(await RunScenario(ev, "udp6-loopback", () =>
            TrafficGenerator.SendUdpAsync("udp6-loopback", IPAddress.IPv6Loopback, IPAddress.IPv6Loopback, 45002, 1200, 50)));
        records.Add(await RunScenario(ev, "udp4-multicast", () =>
            TrafficGenerator.SendMulticastAsync("udp4-multicast", group, lanAddress, GroupPort, 800, 50)));
        records.Add(await RunScenario(ev, "udp4-lan-self", () =>
            TrafficGenerator.SendUdpAsync("udp4-lan-self", lanAddress, lanAddress, 45003, 1000, 50)));

        // TCP loopback v4 + v6
        var (sink4, sinkTask4) = TrafficGenerator.StartTcpSink(IPAddress.Loopback, 0);
        int sinkPort4 = ((IPEndPoint)sink4.LocalEndpoint).Port;
        records.Add(await RunScenario(ev, "tcp4-loopback", () =>
            TrafficGenerator.SendTcpAsync("tcp4-loopback", IPAddress.Loopback, IPAddress.Loopback, sinkPort4, 3000, 10)));

        var (sink6, sinkTask6) = TrafficGenerator.StartTcpSink(IPAddress.IPv6Loopback, 0);
        int sinkPort6 = ((IPEndPoint)sink6.LocalEndpoint).Port;
        records.Add(await RunScenario(ev, "tcp6-loopback", () =>
            TrafficGenerator.SendTcpAsync("tcp6-loopback", IPAddress.IPv6Loopback, IPAddress.IPv6Loopback, sinkPort6, 3000, 10)));

        await Task.Delay(700);
        cts.Cancel();
        long mcBytes = 0;
        int mcPackets = 0;
        if (mcReceive != null)
        {
            try { (mcBytes, mcPackets, _) = await mcReceive; } catch { /* ignore */ }
        }
        sink4.Stop();
        sink6.Stop();

        // Let the ETW consumer catch up before measuring anything.
        long drained = await ProbeSupport.DrainAsync(source);
        ev.Log("ETW consumer drained, events processed = " + drained);

        // ---- match ETW events against the generator records ----
        List<NetworkTrafficEvent> snapshot;
        lock (captured) { snapshot = new List<NetworkTrafficEvent>(captured); }

        var perScenario = new List<object>();
        foreach (TrafficRecord r in records)
        {
            if (r.Protocol == "n/a") { continue; }
            IpKey remote = IpKey.FromIPAddress(IPAddress.Parse(r.RemoteAddress));
            int remotePort = r.RemotePort;
            var matched = snapshot.Where(e =>
                    e.RemoteAddress.Equals(remote) &&
                    (remotePort == 0 || e.RemotePort == remotePort) &&
                    e.Direction == NetworkTrafficDirection.Send)
                .ToList();

            long etwBytes = matched.Sum(e => e.SizeBytes);
            int etwPackets = matched.Count;

            var attribution = new List<object>();
            foreach (var grp in matched.GroupBy(e => e.LocalAddress.ToString()))
            {
                var res = ProbeSupport.Resolve(adapters, IpKey.Parse(grp.Key));
                attribution.Add(new
                {
                    localAddress = grp.Key,
                    packets = grp.Count(),
                    bytes = grp.Sum(x => x.SizeBytes),
                    resolvedIfIndex = res.IfIndex,
                    ambiguous = res.Ambiguous,
                    reason = res.Reason,
                    candidates = res.Candidates,
                });
            }

            perScenario.Add(new
            {
                scenario = r.Label,
                protocol = r.Protocol,
                multicast = r.IsMulticast,
                local = r.LocalAddress + ":" + r.LocalPort,
                remote = r.RemoteAddress + ":" + r.RemotePort,
                generatorBytes = r.BytesSent,
                generatorPackets = r.PacketsSent,
                etwBytes,
                etwPackets,
                byteErrorPercent = r.BytesSent == 0 ? 0 : Math.Round(100.0 * (etwBytes - r.BytesSent) / r.BytesSent, 3),
                packetErrorPercent = r.PacketsSent == 0 ? 0 : Math.Round(100.0 * (etwPackets - r.PacketsSent) / r.PacketsSent, 3),
                attribution,
            });
        }

        ev.Set("scenarios", perScenario);
        ev.Set("multicastIndependentCount", new
        {
            group = group.ToString(),
            interfaceAddress = lanAddress.ToString(),
            receiverBytes = mcBytes,
            receiverPackets = mcPackets,
            note = "Receiver count is independent of ETW.",
        });

        // ---- event set census (which events exist and which are counted) ----
        var census = snapshot
            .GroupBy(e => e.Protocol + "/" + e.Direction + "/" + e.Kind)
            .Select(g => new { kind = g.Key, count = g.Count(), bytes = g.Sum(x => x.SizeBytes) })
            .OrderBy(x => x.kind, StringComparer.Ordinal)
            .ToList();
        ev.Set("eventCensus", census);
        ev.Set("eventCounts", new
        {
            totalCaptured = snapshot.Count,
            eventsSeenBySource = source.EventsSeen,
            copyUpEvents = source.CopyEventsObserved,
            retransmitEvents = source.RetransmitEventsObserved,
            controlEvents = source.ControlEventsObserved,
            undecodable = source.EventsUndecodable,
            processed = source.EventsProcessed,
            eventsLost = source.EventsLost,
            note = "copyUp = TCP copy path (never new data); controlEvents = connect/disconnect/accept/fail; retransmit = reported separately.",
        });

        // ---- filter / exclusion behaviour ----
        var snap = filter.Snapshot();
        ev.Set("filterSnapshot", snap);

        // duplicate rule + same packet matched by several rules
        var dupRules = IpExclusionRules.Compile(new[] { "127.0.0.1", "127.0.0.1", "::1", "239.255.42.99" }, out var dupErrors);
        ev.Set("duplicateRuleHandling", new
        {
            input = new[] { "127.0.0.1", "127.0.0.1", "::1", "239.255.42.99" },
            unicastRuleCount = dupRules.UnicastRuleCount,
            multicastRuleCount = dupRules.MulticastRuleCount,
            ruleCount = dupRules.RuleCount,
            errors = dupErrors.Select(x => new { x.LineNumber, x.Text, x.Reason }),
            note = "Duplicates collapse in the compiled set, so a packet is excluded at most once.",
        });

        // invalid rule rejection
        var badRules = IpExclusionRules.Compile(
            new[] { "192.168.0.0/24", "example.com", "10.0.0.1:8080", "*.*.*.*", "not-an-ip", "  127.0.0.1  " },
            out var badErrors);
        ev.Set("invalidRuleRejection", new
        {
            errors = badErrors.Select(x => new { x.LineNumber, x.Text, x.Reason }),
            acceptedRuleCount = badRules.RuleCount,
        });

        // ---- adapter attribution coverage across every observed local address ----
        var addressCoverage = snapshot
            .GroupBy(e => e.LocalAddress.ToString())
            .Select(g =>
            {
                var res = ProbeSupport.Resolve(adapters, IpKey.Parse(g.Key));
                return new
                {
                    localAddress = g.Key,
                    events = g.Count(),
                    bytes = g.Sum(x => x.SizeBytes),
                    resolvedIfIndex = res.IfIndex,
                    ambiguous = res.Ambiguous,
                    reason = res.Reason,
                    candidates = res.Candidates,
                };
            })
            .OrderByDescending(x => x.events)
            .ToList();
        ev.Set("localAddressAttribution", addressCoverage);

        long swEnd = Stopwatch.GetTimestamp();
        filter.UpdateWindowEnd(swEnd, (swEnd - swStart) / (double)Stopwatch.Frequency);

        source.Stop();
        return 0;
    }

    /// <summary>Runs one traffic scenario; a failing scenario is recorded, never fatal.</summary>
    private static async Task<TrafficRecord> RunScenario(Evidence ev, string label, Func<Task<TrafficRecord>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            ev.Log("scenario " + label + " FAILED: " + ProbeSupport.DescribeException(ex));
            ev.Add("scenarioFailures", new { label, error = ProbeSupport.DescribeException(ex) });
            return new TrafficRecord(label, "n/a", false, "", 0, "", 0, 0, 0, 0, 0, DateTime.UtcNow, DateTime.UtcNow);
        }
    }

    internal static IPAddress FindLanAddress(NetworkAdapterResolver map)
    {
        // Prefer a real, up, non-tunnel Ethernet adapter with a private IPv4 address.
        // VPN / tunnel / virtual adapters are excluded on purpose: multicast on them
        // is not supported and would not represent the LAN scenario of the issue.
        foreach (NetworkAdapterInfo a in map.Adapters)
        {
            if (!a.IsUp || a.Type == NetworkInterfaceType.Loopback || a.Type == NetworkInterfaceType.Tunnel) { continue; }
            string n = a.FriendlyName ?? string.Empty;
            if (n.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Tunnel", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || n.Contains("WSL", StringComparison.OrdinalIgnoreCase)) { continue; }
            foreach (IpKey k in a.Addresses)
            {
                if (!k.IsV6 && !k.IsLoopback && !k.IsMulticast) { return k.ToIPAddress(); }
            }
        }
        foreach (NetworkAdapterInfo a in map.Adapters)
        {
            if (!a.IsUp || a.Type == NetworkInterfaceType.Loopback) { continue; }
            foreach (IpKey k in a.Addresses)
            {
                if (!k.IsV6 && !k.IsLoopback && !k.IsMulticast) { return k.ToIPAddress(); }
            }
        }
        return IPAddress.Loopback;
    }
}
