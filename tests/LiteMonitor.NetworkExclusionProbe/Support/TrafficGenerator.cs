using System.Net;
using System.Net.Sockets;

namespace LiteMonitor.NetworkExclusionProbe.Support;

public sealed record TrafficRecord(
    string Label,
    string Protocol,
    bool IsMulticast,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    long BytesSent,
    int PacketsSent,
    long BytesReceived,
    int PacketsReceived,
    DateTime StartUtc,
    DateTime EndUtc)
{
    public double DurationSeconds => Math.Max(0.000001, (EndUtc - StartUtc).TotalSeconds);
}

/// <summary>
/// Controlled traffic sources. Every send records its exact byte/packet count and
/// its 4-tuple so the ETW side can be verified against an independent source.
/// Only explicitly controlled endpoints (loopback, the host's own LAN address and
/// one administratively scoped multicast group) are used.
/// </summary>
public static class TrafficGenerator
{
    public static async Task<TrafficRecord> SendUdpAsync(
        string label,
        IPAddress localAddress,
        IPAddress remoteAddress,
        int remotePort,
        int payloadSize,
        int packetCount,
        int intervalMs = 0)
    {
        DateTime start = DateTime.UtcNow;
        long bytes = 0;
        int packets = 0;
        int localPort = 0;

        using var udp = new UdpClient(new IPEndPoint(localAddress, 0));
        localPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        byte[] payload = new byte[payloadSize];
        for (int i = 0; i < payloadSize; i++) { payload[i] = (byte)(i & 0xFF); }

        var remote = new IPEndPoint(remoteAddress, remotePort);
        for (int i = 0; i < packetCount; i++)
        {
            await udp.SendAsync(payload, payload.Length, remote);
            bytes += payload.Length;
            packets++;
            if (intervalMs > 0) { await Task.Delay(intervalMs); }
        }

        DateTime end = DateTime.UtcNow;
        return new TrafficRecord(label, "UDP", false, localAddress.ToString(), localPort,
            remoteAddress.ToString(), remotePort, bytes, packets, 0, 0, start, end);
    }

    /// <summary>
    /// Sends to an administratively scoped multicast group (239.255.x.x) with TTL 1,
    /// i.e. it never leaves the local link. Multicast loopback is enabled so the
    /// same host can act as the receiver and provide an independent byte count.
    /// </summary>
    public static async Task<TrafficRecord> SendMulticastAsync(
        string label,
        IPAddress group,
        IPAddress interfaceAddress,
        int groupPort,
        int payloadSize,
        int packetCount,
        int intervalMs = 0)
    {
        DateTime start = DateTime.UtcNow;
        long bytes = 0;
        int packets = 0;
        int localPort = 0;

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(interfaceAddress, 0));
        localPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        byte[] payload = new byte[payloadSize];
        var remote = new IPEndPoint(group, groupPort);
        for (int i = 0; i < packetCount; i++)
        {
            await udp.SendAsync(payload, payload.Length, remote);
            bytes += payload.Length;
            packets++;
            if (intervalMs > 0) { await Task.Delay(intervalMs); }
        }

        DateTime end = DateTime.UtcNow;
        return new TrafficRecord(label, "UDP", true, interfaceAddress.ToString(), localPort,
            group.ToString(), groupPort, bytes, packets, 0, 0, start, end);
    }

    /// <summary>Joins a multicast group and counts the bytes that arrive.</summary>
    public static async Task<(long Bytes, int Packets, int LocalPort)> ReceiveMulticastAsync(
        IPAddress group,
        IPAddress interfaceAddress,
        int groupPort,
        CancellationToken token)
    {
        long bytes = 0;
        int packets = 0;
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(interfaceAddress, groupPort));
        int localPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        udp.JoinMulticastGroup(group, interfaceAddress);

        var buffer = new byte[65535];
        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult r = await udp.ReceiveAsync(token);
                bytes += r.Buffer.Length;
                packets++;
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (SocketException)
        {
            // expected on cancellation
        }
        return (bytes, packets, localPort);
    }

    /// <summary>
    /// Rate paced UDP sender. Paces with a Stopwatch instead of a fixed per packet
    /// delay, because Task.Delay(1) on Windows resolves to ~2 ms and would halve the
    /// requested rate.
    /// </summary>
    public static async Task<TrafficRecord> SendUdpRateAsync(
        string label,
        IPAddress localAddress,
        IPAddress remoteAddress,
        int remotePort,
        int payloadSize,
        int targetBytesPerSecond,
        int durationSeconds)
    {
        DateTime start = DateTime.UtcNow;
        long bytes = 0;
        int packets = 0;
        int localPort = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var udp = new UdpClient(new IPEndPoint(localAddress, 0));
        localPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        byte[] payload = new byte[payloadSize];
        var remote = new IPEndPoint(remoteAddress, remotePort);

        long sentBytes = 0;
        while (sw.Elapsed.TotalSeconds < durationSeconds)
        {
            long shouldHave = (long)(targetBytesPerSecond * sw.Elapsed.TotalSeconds);
            int toSend = (int)((shouldHave - sentBytes) / payloadSize);
            if (toSend > 0)
            {
                int batch = Math.Min(toSend, 200);
                for (int i = 0; i < batch; i++)
                {
                    await udp.SendAsync(payload, payload.Length, remote);
                    sentBytes += payload.Length;
                    bytes += payload.Length;
                    packets++;
                }
            }
            await Task.Delay(5);
        }

        sw.Stop();
        DateTime end = DateTime.UtcNow;
        return new TrafficRecord(label, "UDP", false, localAddress.ToString(), localPort,
            remoteAddress.ToString(), remotePort, bytes, packets, 0, 0, start, end);
    }

    public static async Task<TrafficRecord> SendTcpAsync(
        string label,
        IPAddress localAddress,
        IPAddress remoteAddress,
        int remotePort,
        int payloadSize,
        int packetCount,
        int intervalMs = 0)
    {
        DateTime start = DateTime.UtcNow;
        long bytes = 0;
        int packets = 0;
        int localPort = 0;

        using var client = new TcpClient(localAddress.AddressFamily);
        await client.ConnectAsync(remoteAddress, remotePort);
        localPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        NetworkStream stream = client.GetStream();
        byte[] payload = new byte[payloadSize];
        for (int i = 0; i < payloadSize; i++) { payload[i] = (byte)(i & 0xFF); }

        for (int i = 0; i < packetCount; i++)
        {
            await stream.WriteAsync(payload);
            bytes += payload.Length;
            packets++;
            if (intervalMs > 0) { await Task.Delay(intervalMs); }
        }
        await stream.FlushAsync();

        DateTime end = DateTime.UtcNow;
        return new TrafficRecord(label, "TCP", false, localAddress.ToString(), localPort,
            remoteAddress.ToString(), remotePort, bytes, packets, 0, 0, start, end);
    }

    /// <summary>Starts a loopback TCP sink that just drains bytes.</summary>
    public static (TcpListener Listener, Task<int> Task) StartTcpSink(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        listener.Start();
        int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<int> task = Task.Run(async () =>
        {
            long total = 0;
            var buf = new byte[65536];
            try
            {
                while (true)
                {
                    TcpClient c = await listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            NetworkStream s = c.GetStream();
                            while (true)
                            {
                                int n = await s.ReadAsync(buf);
                                if (n <= 0) { break; }
                                Interlocked.Add(ref total, n);
                            }
                        }
                        catch { /* ignore */ }
                        finally { c.Dispose(); }
                    });
                }
            }
            catch { /* ignore */ }
            return (int)total;
        });
        return (listener, task);
    }
}
