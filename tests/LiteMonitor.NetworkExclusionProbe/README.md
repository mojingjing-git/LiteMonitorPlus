# LiteMonitor.NetworkExclusionProbe

Issue #610 N0 step — **independent technical feasibility probe** for "exclude real-time
network speed by IP address".

This project is **not** product code. It is a throw-away measurement harness that lives
completely outside `src/**` and shares no assembly with the shipping application.
It exists to answer one question with numbers: *can Windows ETW give us per-remote-IP
byte counts good enough to subtract from the real-time network speed?*

## What it measures

| Probe | N0 item | What it produces |
|---|---|---|
| `env` / `capability` | 1, 6 | OS build, privilege level, provider + keyword facts, decoder library version/licence, exact non-admin failure code, session lifecycle and residue check |
| `traffic` | 2, 4, 5 | Controlled TCP/UDP over IPv4/IPv6 plus multicast; per-scenario ETW bytes vs. an independent generator count; direction correctness; adapter attribution; rule/exclusion behaviour |
| `sizelayer` | 3 | Which layer the ETW `size` field lives on: TCP segmentation, UDP offload, and a same-window ETW-vs-native-NIC byte comparison |
| `perf` | 7 | CPU, allocations, managed heap, working set and event loss for idle / 200 KB/s RTP / high-rate TCP / high-packet-rate |

Every probe writes `<command>.json` (structured evidence) and `<command>.log` (human
readable) into the `--out` directory.

## Build

The probe uses its own artifacts path so it never fights with other work in the repo:

```powershell
cd G:\TOOL\LiteMonitor-fork
dotnet build tests\LiteMonitor.NetworkExclusionProbe\LiteMonitor.NetworkExclusionProbe.csproj -c Release --artifacts-path G:\TOOL\LiteMonitor-fork\.artifacts\w610
```

## Run

Enabling `Microsoft-Windows-Kernel-Network` requires an elevated process. The shipping
application already declares `requireAdministrator` in `app.manifest`, so this is not a
new constraint for the product — but the probe itself has to be started elevated:

```powershell
$exe = 'G:\TOOL\LiteMonitor-fork\.artifacts\w610\bin\LiteMonitor.NetworkExclusionProbe\release_win-x64\LiteMonitor.NetworkExclusionProbe.exe'
Start-Process -Verb RunAs -FilePath $exe -ArgumentList 'traffic --out G:\TOOL\LiteMonitor-fork\.artifacts\w610\evidence'
```

Run **without** elevation to capture the real non-admin failure code:

```powershell
& $exe capability --out G:\TOOL\LiteMonitor-fork\.artifacts\w610\evidence-nonadmin
```

## Safety rules the probe obeys

* It only ever touches ETW sessions it created itself (unique name per process/start).
  It never starts, stops or reuses `NT Kernel Logger`, `LiteMonitor++_Golden_Session`
  (PresentMon / FPS) or any other session. `EtwNetworkTrafficEventSource` refuses to
  start if its own name collides with a reserved name.
* It does not capture packet content, does not scan the LAN and does not change
  firewall rules. Traffic only goes to controlled endpoints: loopback, the host's own
  LAN address, and one administratively scoped multicast group (239.255.42.99) with the
  default multicast TTL of 1 (link-local only).
* No capture driver (Npcap / WinDivert) is installed or required.
* It never writes outside its `--out` directory.

## Layout

```
Product/     production-shaped modules (the shape N1/N2/N3 are expected to take)
  NetworkTrafficModel.cs        IpKey (allocation free), NetworkTrafficEvent
  INetworkTrafficEventSource.cs injectable event source contract + status
  EtwNetworkTrafficEventSource.cs  ETW session lifecycle + payload decoding
  IpExclusionRules.cs           exact-IP rule parsing / normalisation / compilation
  NetworkTrafficFilter.cs       same-source windowed totals + excluded bytes
Support/     adapter map, controlled traffic generator, evidence writer
Probes/      the four measurement scenarios
```

`Product/` intentionally mirrors the module split proposed in the implementation plan
(`INetworkTrafficEventSource`, `NetworkTrafficFilter`, `FilteredNetworkSnapshot`,
`IpExclusionRules`) so that the probe doubles as a design proof for N1/N2.

## ETW payload layout (measured, not guessed)

`Microsoft-Windows-Kernel-Network` events carry a **binary** payload — the manifest
declares no named fields, so the layout was reverse engineered and then verified with
fully controlled traffic (unique destination ports and payload sizes):

```
IPv4 : PID(4) size(4) remoteAddr(4) localAddr(4) remotePort(2) localPort(2) [seq(4) conn(4)] pad(8)
IPv6 : PID(4) size(4) remoteAddr(16) localAddr(16) remotePort(2) localPort(2) [seq(4) conn(4)] pad(8)
```

* `PID` and `size` are little endian; addresses are network order bytes; ports are
  uint16 network order.
* Address/port **#1 is always the remote endpoint**, address/port **#2 is always the
  local endpoint**, for both SEND and RECV events.
* Payload length identifies the family: 28 = IPv4 base, 36 = IPv4 + seq/conn,
  52 = IPv6 base, 60 = IPv6 + seq/conn.
* There is **no adapter / interface id** in the payload — attribution has to be done by
  mapping the local address to an interface.

## Decision

See `docs/reviews/evidence/network-exclusion/N0/network-exclusion-backend-decision.md`.
