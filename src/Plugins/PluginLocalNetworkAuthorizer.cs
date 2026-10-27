using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// [S02a] 本地网络权限授权器。
    /// <para>插件请求解析到本机回环或私网地址时，必须存在"实例 + 具体 origin"
    /// 的本地网络授权；插件模板声明不能代替该确认。与凭据授权
    /// （<see cref="DynamicHostAuthorizer"/>，仅 HTTPS）刻意分离：本地服务常见
    /// <c>http://192.168.1.1:8080</c> 这类明文私网地址，若混入凭据授权集合会把
    /// 凭据发往 HTTP——本授权器只放行**不带凭据**的本地访问，凭据规则不放宽。</para>
    /// <para>存储：%LocalAppData%/LiteMonitor/plugin-local-network-authorizations.json</para>
    /// </summary>
    public sealed class PluginLocalNetworkAuthorizer
    {
        public sealed record LocalNetworkRecord(
            string PluginInstanceId,
            string Origin,
            DateTimeOffset AuthorizedAtUtc);

        private static readonly Lazy<PluginLocalNetworkAuthorizer> _default =
            new(() => new PluginLocalNetworkAuthorizer(null));

        public static PluginLocalNetworkAuthorizer Default => _default.Value;

        private readonly object _gate = new();
        private readonly string _storePath;
        private Dictionary<string, List<LocalNetworkRecord>>? _records;

        public PluginLocalNetworkAuthorizer(string? storePath)
        {
            _storePath = string.IsNullOrWhiteSpace(storePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LiteMonitor",
                    "plugin-local-network-authorizations.json")
                : Path.GetFullPath(storePath);
        }

        /// <summary>仅用户操作可写入授权。</summary>
        public bool AuthorizeForUser(string instanceId, Uri endpoint)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return false;
            if (!TryNormalizeNetworkOrigin(endpoint, out string origin, out _)) return false;

            lock (_gate)
            {
                Dictionary<string, List<LocalNetworkRecord>> records = Load();
                if (!records.TryGetValue(instanceId, out List<LocalNetworkRecord>? list))
                {
                    list = new List<LocalNetworkRecord>();
                    records[instanceId] = list;
                }

                if (list.Any(r => string.Equals(r.Origin, origin, StringComparison.Ordinal)))
                    return true;

                list.Add(new LocalNetworkRecord(instanceId, origin, DateTimeOffset.UtcNow));
                if (!TrySave(records))
                {
                    list.RemoveAt(list.Count - 1);
                    return false;
                }

                PluginNetworkPolicy.OnAuthorizationChanged();
                return true;
            }
        }

        /// <summary>请求目标是否已被用户确认可访问本地网络（实例 + origin 精确匹配）。</summary>
        public bool IsLocalAuthorized(string instanceId, Uri endpoint)
        {
            if (string.IsNullOrWhiteSpace(instanceId) ||
                !TryNormalizeNetworkOrigin(endpoint, out string origin, out _))
                return false;

            lock (_gate)
            {
                Dictionary<string, List<LocalNetworkRecord>> records = Load();
                return records.TryGetValue(instanceId, out List<LocalNetworkRecord>? list) &&
                       list.Any(r => string.Equals(r.Origin, origin, StringComparison.Ordinal));
            }
        }

        /// <summary>删除实例时清理其全部本地网络授权。</summary>
        public bool RevokeAll(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return false;
            lock (_gate)
            {
                Dictionary<string, List<LocalNetworkRecord>> records = Load();
                if (!records.TryGetValue(instanceId, out List<LocalNetworkRecord>? removed))
                    return false;
                records.Remove(instanceId);
                if (TrySave(records))
                {
                    PluginNetworkPolicy.OnAuthorizationChanged();
                    return true;
                }
                // 磁盘没删掉就不能假装已清理：恢复内存记录，防止下次启动重新继承。
                records[instanceId] = removed;
                return false;
            }
        }

        /// <summary>
        /// 本地网络授权的 origin 规范化：允许 http 与 https（与凭据授权的 HTTPS-only 不同）。
        /// 输出形态与 <see cref="DynamicHostAuthorizer.TryNormalizeOrigin"/> 一致：
        /// IDN 小写、IPv6 加方括号、默认端口省略。
        /// </summary>
        public static bool TryNormalizeNetworkOrigin(Uri endpoint, out string origin, out string error)
        {
            origin = "";
            error = "";
            if (endpoint is null || !endpoint.IsAbsoluteUri)
            {
                error = "目标不是绝对 URL。";
                return false;
            }

            if (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)
            {
                error = "本地网络授权只支持 http/https 目标。";
                return false;
            }

            string host = endpoint.IdnHost;
            if (string.IsNullOrWhiteSpace(host) || host.Contains('*'))
            {
                error = "目标主机无效，不能使用通配符。";
                return false;
            }

            if (endpoint.Port is < 1 or > 65535)
            {
                error = "目标端口无效。";
                return false;
            }

            string authority = host.Contains(':') ? $"[{host}]" : host;
            bool defaultPort = (endpoint.Scheme == Uri.UriSchemeHttps && endpoint.Port == 443) ||
                               (endpoint.Scheme == Uri.UriSchemeHttp && endpoint.Port == 80);
            origin = defaultPort
                ? $"{endpoint.Scheme}://{authority.ToLowerInvariant()}"
                : $"{endpoint.Scheme}://{authority.ToLowerInvariant()}:{endpoint.Port}";
            return true;
        }

        private Dictionary<string, List<LocalNetworkRecord>> Load()
        {
            if (_records != null) return _records;
            if (!File.Exists(_storePath)) return _records = new(StringComparer.Ordinal);

            try
            {
                string json = File.ReadAllText(_storePath);
                Dictionary<string, List<LocalNetworkRecord>>? loaded =
                    JsonSerializer.Deserialize<Dictionary<string, List<LocalNetworkRecord>>>(json);
                _records = loaded != null
                    ? new Dictionary<string, List<LocalNetworkRecord>>(loaded, StringComparer.Ordinal)
                    : new(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 授权文件损坏时按"无授权"处理，绝不因读取失败放行。
                _records = new(StringComparer.Ordinal);
            }

            return _records;
        }

        private bool TrySave(Dictionary<string, List<LocalNetworkRecord>> records)
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
