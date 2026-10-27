using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// [S02a] 插件显式代理确认授权器。
    /// <para>插件模板步骤指定的代理端点在首次使用前必须由用户确认（实例 + 端点）；
    /// 端点字符串变化即视为新端点，需要重新确认。系统代理不经过本授权器（默认沿用，
    /// 不新增确认）。授权变更同时通知 <see cref="PluginNetworkPolicy"/> 使连接池失效。</para>
    /// <para>存储：%LocalAppData%/LiteMonitor/plugin-proxy-authorizations.json</para>
    /// </summary>
    public sealed class PluginProxyAuthorizer
    {
        public sealed record ProxyAuthorizationRecord(
            string PluginInstanceId,
            string ProxyEndpoint,
            DateTimeOffset AuthorizedAtUtc);

        private static readonly Lazy<PluginProxyAuthorizer> _default =
            new(() => new PluginProxyAuthorizer(null));

        public static PluginProxyAuthorizer Default => _default.Value;

        /// <summary>
        /// [S02a 测试接缝] 非 null 时 <see cref="IsAuthorized"/> 直接返回其结果
        /// （参数 instanceId+proxy），替代存储查询；规范化语义不受影响。生产路径不受影响。
        /// </summary>
        internal static Func<string, string, bool>? AuthorizationOverrideForTests;

        private readonly object _gate = new();
        private readonly string _storePath;
        private Dictionary<string, List<ProxyAuthorizationRecord>>? _records;

        public PluginProxyAuthorizer(string? storePath)
        {
            _storePath = string.IsNullOrWhiteSpace(storePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LiteMonitor",
                    "plugin-proxy-authorizations.json")
                : Path.GetFullPath(storePath);
        }

        /// <summary>仅用户操作可写入授权。</summary>
        public bool AuthorizeForUser(string instanceId, string proxy)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return false;
            if (!TryNormalizeProxyEndpoint(proxy, out string endpoint)) return false;

            lock (_gate)
            {
                Dictionary<string, List<ProxyAuthorizationRecord>> records = Load();
                if (!records.TryGetValue(instanceId, out List<ProxyAuthorizationRecord>? list))
                {
                    list = new List<ProxyAuthorizationRecord>();
                    records[instanceId] = list;
                }

                if (list.Any(r => string.Equals(r.ProxyEndpoint, endpoint, StringComparison.Ordinal)))
                    return true;

                list.Add(new ProxyAuthorizationRecord(instanceId, endpoint, DateTimeOffset.UtcNow));
                if (!TrySave(records))
                {
                    list.RemoveAt(list.Count - 1);
                    return false;
                }

                PluginNetworkPolicy.OnAuthorizationChanged();
                return true;
            }
        }

        /// <summary>该实例是否已确认可使用该代理端点。</summary>
        public bool IsAuthorized(string instanceId, string proxy)
        {
            if (string.IsNullOrWhiteSpace(instanceId) ||
                !TryNormalizeProxyEndpoint(proxy, out string endpoint))
                return false;

            Func<string, string, bool>? @override = AuthorizationOverrideForTests;
            if (@override != null) return @override(instanceId, proxy);

            lock (_gate)
            {
                Dictionary<string, List<ProxyAuthorizationRecord>> records = Load();
                return records.TryGetValue(instanceId, out List<ProxyAuthorizationRecord>? list) &&
                       list.Any(r => string.Equals(r.ProxyEndpoint, endpoint, StringComparison.Ordinal));
            }
        }

        /// <summary>删除实例时清理其全部代理确认。</summary>
        public bool RevokeAll(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return false;
            lock (_gate)
            {
                Dictionary<string, List<ProxyAuthorizationRecord>> records = Load();
                if (!records.TryGetValue(instanceId, out List<ProxyAuthorizationRecord>? removed))
                    return false;
                records.Remove(instanceId);
                if (TrySave(records))
                {
                    PluginNetworkPolicy.OnAuthorizationChanged();
                    return true;
                }
                records[instanceId] = removed;
                return false;
            }
        }

        /// <summary>
        /// 代理端点规范化。接受 "host:port"、"http(s)://host[:port]"；
        /// 归一为 scheme://authority[:port]（默认端口省略、IDN 小写、IPv6 加方括号）。
        /// </summary>
        public static bool TryNormalizeProxyEndpoint(string? proxy, out string endpoint)
        {
            endpoint = "";
            if (string.IsNullOrWhiteSpace(proxy)) return false;

            string value = proxy.Trim();
            if (!value.Contains(Uri.SchemeDelimiter, StringComparison.Ordinal))
                value = Uri.UriSchemeHttp + Uri.SchemeDelimiter + value;

            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                return false;

            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                return false;

            string host = uri.IdnHost;
            if (string.IsNullOrWhiteSpace(host) || host.Contains('*')) return false;
            if (uri.Port is < 1 or > 65535) return false;

            string authority = host.Contains(':') ? $"[{host}]" : host;
            bool defaultPort = (uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443) ||
                               (uri.Scheme == Uri.UriSchemeHttp && uri.Port == 80);
            endpoint = defaultPort
                ? $"{uri.Scheme}://{authority.ToLowerInvariant()}"
                : $"{uri.Scheme}://{authority.ToLowerInvariant()}:{uri.Port}";
            return true;
        }

        private Dictionary<string, List<ProxyAuthorizationRecord>> Load()
        {
            if (_records != null) return _records;
            if (!File.Exists(_storePath)) return _records = new(StringComparer.Ordinal);

            try
            {
                string json = File.ReadAllText(_storePath);
                Dictionary<string, List<ProxyAuthorizationRecord>>? loaded =
                    JsonSerializer.Deserialize<Dictionary<string, List<ProxyAuthorizationRecord>>>(json);
                _records = loaded != null
                    ? new Dictionary<string, List<ProxyAuthorizationRecord>>(loaded, StringComparer.Ordinal)
                    : new(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _records = new(StringComparer.Ordinal);
            }

            return _records;
        }

        private bool TrySave(Dictionary<string, List<ProxyAuthorizationRecord>> records)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string tmp = _storePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(records, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
                File.Move(tmp, _storePath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
