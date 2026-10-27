using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteMonitor.Diagnostics;

/// <summary>
/// C2 脱敏允许列表：诊断上下文只允许固定键；值看起来像路径、URL、令牌或凭据时整项丢弃。
/// <para>宁可少一条上下文，也不把原始路径/IP/授权头带进诊断包。</para>
/// </summary>
public static class DiagnosticRedaction
{
    private static readonly Regex WindowsAbsolutePath =
        new(@"(?i)(?:^|[^a-z0-9])(?:[a-z]:[\\/]|\\\\[^\\\s]+\\[^\\\s]+)", RegexOptions.Compiled);

    private static readonly Regex UnixAbsolutePath =
        new(@"(?<![A-Za-z0-9])/(?:[^\s/]+/)+", RegexOptions.Compiled);

    private static readonly Regex IpAddress =
        new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);

    // 覆盖完整/压缩 IPv6（2001:db8::1234、::1、fe80::1%eth0 等）。
    private static readonly Regex Ipv6Address =
        new(@"(?i)(?<![\w:])(?:[0-9a-f]{1,4}:){2,7}[0-9a-f]{0,4}(?![\w:])|(?<![\w:])::[0-9a-f]{1,4}(?![\w:])", RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "stage",
        "status",
        "reason",
        "route",
        "device_kind",
        "revision",
        "count",
        "duration_ms",
        "source",
        "error_code",
        "window_state",
        "gpu_state",
        "web_state",
        "profile_id",
        "operation_id",
        "ruleId", "rule_id",
        "deviceId", "device_id",
        "incidentId", "incident_id",
        "eventKind", "event_kind",
        "batchId", "batch_id",
        "state",
        "notification",
    };

    public static IReadOnlyDictionary<string, string> Filter(IReadOnlyDictionary<string, string>? context)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (context is null) return result;

        foreach (var pair in context)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            if (!AllowedKeys.Contains(pair.Key)) continue;
            if (LooksSensitive(pair.Value)) continue;
            result[pair.Key] = pair.Value ?? string.Empty;
        }

        return result;
    }

    public static bool IsSafeValue(string? value) => !LooksSensitive(value);

    public static int EstimateBytes(IReadOnlyDictionary<string, string>? context)
    {
        if (context is null) return 0;
        int total = 0;
        foreach (var pair in context)
        {
            total += Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value) + 16;
        }
        return total;
    }

    private static bool LooksSensitive(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string v = value.ToLowerInvariant();
        return v.Contains("bearer ", StringComparison.Ordinal)
            || v.Contains("authorization", StringComparison.Ordinal)
            || v.Contains("cookie", StringComparison.Ordinal)
            || v.Contains("password", StringComparison.Ordinal)
            || v.Contains("secret", StringComparison.Ordinal)
            || v.Contains("token", StringComparison.Ordinal)
            || v.Contains("api_key", StringComparison.Ordinal)
            || v.Contains("api-key", StringComparison.Ordinal)
            || v.Contains("http://", StringComparison.Ordinal)
            || v.Contains("https://", StringComparison.Ordinal)
            || v.Contains(@"c:\", StringComparison.Ordinal)
            || v.Contains(@"\\", StringComparison.Ordinal)
            || WindowsAbsolutePath.IsMatch(value)
            || UnixAbsolutePath.IsMatch(value)
            || IpAddress.IsMatch(value)
            || Ipv6Address.IsMatch(value)
            || ContainsIpLiteral(value);
    }

    private static bool ContainsIpLiteral(string value)
    {
        foreach (string token in value.Split(
            new[] { ' ', '\t', '\r', '\n', ';', ',', '"', '\'', '(', ')', '[', ']', '{', '}' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = token.Trim('.', '!', '?', ':');
            if (candidate.Length == 0) continue;
            // IPAddress.TryParse 兼容 "0"、"512"、"42.0" 这类旧式 IPv4 缩写；
            // 诊断事实里的 revision/count/duration 不能被误判成地址。
            bool hasColon = candidate.Contains(':');
            bool hasDot = candidate.Contains('.');
            if (!hasColon && !hasDot) continue;
            if (hasDot && !hasColon && candidate.Split('.').Length != 4) continue;
            if (IPAddress.TryParse(candidate, out IPAddress? address)
                && (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    || address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))
            {
                return true;
            }
        }
        return false;
    }
}
