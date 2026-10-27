using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 动态目标授权记录。插件模板只能声明字面量主机；用户在界面上填写的目标
    /// （如 <c>https://{{host}}/api</c>）必须由用户显式授权到具体 origin，
    /// 插件 JSON 不能自授，未授权目标不会收到任何凭据。
    /// </summary>
    public sealed class DynamicHostAuthorizer
    {
        public sealed record AuthorizationRecord(
            string PluginInstanceId,
            string Origin,
            DateTimeOffset AuthorizedAtUtc);

        public sealed record AuthorizationDecision(
            bool Allow,
            string ReasonCode,
            string Reason,
            bool Persisted);

        private static readonly Lazy<DynamicHostAuthorizer> _default =
            new(() => new DynamicHostAuthorizer(null));

        public static DynamicHostAuthorizer Default => _default.Value;

        private readonly object _gate = new();
        private readonly string _storePath;
        private Dictionary<string, List<AuthorizationRecord>>? _records;

        public DynamicHostAuthorizer(string? storePath)
        {
            _storePath = string.IsNullOrWhiteSpace(storePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LiteMonitor",
                    "plugin-host-authorizations.json")
                : Path.GetFullPath(storePath);
        }

        /// <summary>仅用户操作可写入授权；<paramref name="userInitiated"/> 必须为 true。</summary>
        public AuthorizationDecision AuthorizeForUser(string instanceId, Uri endpoint)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                return Deny("PluginInstanceMissing", "缺少插件实例，无法授权目标。");

            if (!TryNormalizeOrigin(endpoint, out string origin, out string error))
                return Deny("OriginInvalid", error);

            lock (_gate)
            {
                Dictionary<string, List<AuthorizationRecord>> records = Load();
                if (!records.TryGetValue(instanceId, out List<AuthorizationRecord>? list))
                {
                    list = new List<AuthorizationRecord>();
                    records[instanceId] = list;
                }

                if (list.Any(r => string.Equals(r.Origin, origin, StringComparison.Ordinal)))
                    return new AuthorizationDecision(true, "AlreadyAuthorized", "该目标已授权。", true);

                list.Add(new AuthorizationRecord(instanceId, origin, DateTimeOffset.UtcNow));
                if (!TrySave(records))
                {
                    list.RemoveAt(list.Count - 1);
                    return Deny("PersistenceFailed", "授权无法写入磁盘，凭据不会发送。");
                }

                return new AuthorizationDecision(true, "Authorized", "目标已授权，后续凭据只发往该 origin。", true);
            }
        }

        public bool IsAuthorized(string instanceId, Uri endpoint)
        {
            if (string.IsNullOrWhiteSpace(instanceId) ||
                !TryNormalizeOrigin(endpoint, out string origin, out _))
                return false;

            lock (_gate)
            {
                Dictionary<string, List<AuthorizationRecord>> records = Load();
                return records.TryGetValue(instanceId, out List<AuthorizationRecord>? list) &&
                       list.Any(r => string.Equals(r.Origin, origin, StringComparison.Ordinal));
            }
        }

        // [S02a 测试接缝] 非 null 时替代持久化授权查询（返回该 instanceId 的已授权 origin
        // 集合）；沙箱回归用回环服务器 + 模板变量目标，逐用例写真实授权存储不可行。
        internal static Func<string, IReadOnlyList<string>>? AuthorizedOriginsOverrideForTests;

        public IReadOnlyList<string> GetAuthorizedOrigins(string instanceId)
        {
            Func<string, IReadOnlyList<string>>? @override = AuthorizedOriginsOverrideForTests;
            if (@override != null) return @override(instanceId);
            if (string.IsNullOrWhiteSpace(instanceId)) return Array.Empty<string>();

            lock (_gate)
            {
                Dictionary<string, List<AuthorizationRecord>> records = Load();
                return records.TryGetValue(instanceId, out List<AuthorizationRecord>? list)
                    ? list.Select(r => r.Origin).ToArray()
                    : Array.Empty<string>();
            }
        }

        public bool Revoke(string instanceId, Uri endpoint)
        {
            if (string.IsNullOrWhiteSpace(instanceId) ||
                !TryNormalizeOrigin(endpoint, out string origin, out _))
                return false;

            lock (_gate)
            {
                Dictionary<string, List<AuthorizationRecord>> records = Load();
                if (!records.TryGetValue(instanceId, out List<AuthorizationRecord>? list))
                    return false;

                int removed = list.RemoveAll(r => string.Equals(r.Origin, origin, StringComparison.Ordinal));
                if (removed == 0) return false;
                if (list.Count == 0) records.Remove(instanceId);
                return TrySave(records);
            }
        }

        /// <summary>删除实例时清理其全部授权；返回是否删除了至少一条记录。</summary>
        public bool RevokeAll(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return false;
            lock (_gate)
            {
                Dictionary<string, List<AuthorizationRecord>> records = Load();
                if (!records.TryGetValue(instanceId, out List<AuthorizationRecord>? removed))
                    return false;
                records.Remove(instanceId);
                if (TrySave(records)) return true;
                // 磁盘没删掉就不能假装已清理：恢复内存记录，防止下次启动重新继承。
                records[instanceId] = removed;
                return false;
            }
        }

        /// <summary>授权条目以完整 origin 保存；与请求 URL 做精确比较。</summary>
        public static bool MatchesAuthorizedOrigin(string allowedEntry, Uri request)
        {
            if (!TryNormalizeOrigin(request, out string requestOrigin, out _)) return false;
            return TryNormalizeOriginString(allowedEntry, out string allowedOrigin) &&
                   string.Equals(requestOrigin, allowedOrigin, StringComparison.Ordinal);
        }

        public static bool TryNormalizeOrigin(Uri endpoint, out string origin, out string error)
        {
            origin = "";
            error = "";
            if (endpoint is null || !endpoint.IsAbsoluteUri)
            {
                error = "目标不是绝对 URL，凭据不会发送。";
                return false;
            }

            if (endpoint.Scheme != Uri.UriSchemeHttps &&
                !(endpoint.Scheme == Uri.UriSchemeHttp && IsLoopback(endpoint)))
            {
                error = "只允许 HTTPS；本机回环地址可用于本地测试。";
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

        private static bool TryNormalizeOriginString(string value, out string origin)
        {
            origin = "";
            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
                   TryNormalizeOrigin(uri, out origin, out _);
        }

        private static bool IsLoopback(Uri uri)
            => uri.IsLoopback || string.Equals(uri.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase);

        private static AuthorizationDecision Deny(string code, string reason)
            => new(false, code, reason, false);

        private Dictionary<string, List<AuthorizationRecord>> Load()
        {
            if (_records != null) return _records;
            if (!File.Exists(_storePath)) return _records = new(StringComparer.Ordinal);

            try
            {
                string json = File.ReadAllText(_storePath);
                Dictionary<string, List<AuthorizationRecord>>? loaded =
                    JsonSerializer.Deserialize<Dictionary<string, List<AuthorizationRecord>>>(json);
                _records = loaded != null
                    ? new Dictionary<string, List<AuthorizationRecord>>(loaded, StringComparer.Ordinal)
                    : new(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 授权文件损坏时按“无授权”处理，绝不因读取失败放行。
                _records = new(StringComparer.Ordinal);
            }

            return _records;
        }

        private bool TrySave(Dictionary<string, List<AuthorizationRecord>> records)
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
