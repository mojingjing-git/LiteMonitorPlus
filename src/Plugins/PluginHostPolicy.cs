using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace LiteMonitor.src.Plugins
{
    // Keep raw authority validation ahead of Uri.Host: Uri.Host drops IPv6 zone ids.
    internal static class PluginHostPolicy
    {
        internal static bool TryGetDeclaredHost(string template, out string host)
            => TryUrlHost(template, out host);

        /// <param name="value">★ 允许为 null —— 函数首行的 string.IsNullOrWhiteSpace
        /// 已把 null 判为非法地址（返回 false），与原先的运行期行为完全一致。
        /// 标注为可空可让调用方不必再为"地址可能缺失"单独兜底。</param>
        private static bool TryUrlHost(string? value, out string host)
        {
            host = "";
            if (string.IsNullOrWhiteSpace(value)) return false;
            string url = value.Trim();
            int separator = url.IndexOf("://", StringComparison.Ordinal);
            if (separator <= 0 || !Uri.CheckSchemeName(url.Substring(0, separator))) return false;
            string rest = url.Substring(separator + 3);
            int end = rest.IndexOfAny(new[] { '/', '?', '#' });
            string authority = end < 0 ? rest : rest.Substring(0, end);
            return TryAuthority(authority, out host);
        }

        private static bool TryAuthority(string authority, out string host)
        {
            host = "";
            if (string.IsNullOrEmpty(authority) || authority.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
                authority.IndexOfAny(new[] { '/', '?', '#', '\\', '{', '}' }) >= 0) return false;

            int at = authority.IndexOf('@');
            if (at != authority.LastIndexOf('@')) return false;
            string hostPort = at < 0 ? authority : authority.Substring(at + 1);
            if (at >= 0)
            {
                string user = authority.Substring(0, at);
                for (int i = 0; i < user.Length; i++)
                {
                    char c = user[i];
                    if (c == '%')
                    {
                        if (i + 2 >= user.Length || !Uri.IsHexDigit(user[i + 1]) || !Uri.IsHexDigit(user[i + 2])) return false;
                        i += 2;
                    }
                    else if (!(char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*+,;=:".Contains(c))) return false;
                }
            }

            string name;
            string? port = null;
            if (hostPort.StartsWith("[", StringComparison.Ordinal))
            {
                int close = hostPort.IndexOf(']');
                if (close <= 1) return false;
                name = hostPort.Substring(1, close - 1);
                // Reject both raw and percent-encoded scopes before any URI normalization.
                if (name.Contains('%') || !IPAddress.TryParse(name, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
                string tail = hostPort.Substring(close + 1);
                if (tail.Length > 0)
                {
                    if (!tail.StartsWith(":", StringComparison.Ordinal)) return false;
                    port = tail.Substring(1);
                }
                host = "[" + ip.ToString().ToLowerInvariant() + "]";
            }
            else
            {
                if (hostPort.IndexOfAny(new[] { '[', ']', '%' }) >= 0) return false;
                int colon = hostPort.IndexOf(':');
                if (colon != hostPort.LastIndexOf(':')) return false;
                name = colon < 0 ? hostPort : hostPort.Substring(0, colon);
                if (colon >= 0) port = hostPort.Substring(colon + 1);
                if (name.Length == 0 || name.Contains('*') || Uri.CheckHostName(name) == UriHostNameType.Unknown) return false;
            }

            if (port != null && (port.Length == 0 || port.Any(c => c < '0' || c > '9') ||
                !int.TryParse(port, out int number) || number > 65535)) return false;
            if (!Uri.TryCreate("http://" + authority + "/", UriKind.Absolute, out var parsed)) return false;
            if (host.Length == 0) host = parsed.IdnHost.ToLowerInvariant();
            return host.Length > 0;
        }

        internal static bool TryNormalizeHost(string value, out string host)
        {
            host = "";
            if (string.IsNullOrWhiteSpace(value)) return false;
            string raw = value.Trim();
            // This entry takes a host (optionally a legal port), never userinfo or a URL.
            if (raw.IndexOfAny(new[] { '@', '/', '?', '#', '\\', '{', '}', '%' }) >= 0) return false;
            if (IPAddress.TryParse(raw, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                host = "[" + ip.ToString().ToLowerInvariant() + "]";
                return true;
            }
            return TryAuthority(raw, out host);
        }

        internal static bool HostInSet(string host, IEnumerable<string> allowed)
        {
            if (allowed == null || !TryNormalizeHost(host, out var normalized)) return false;
            return allowed.Any(a => TryNormalizeHost(a, out var canonical) && canonical == normalized);
        }

        /// <param name="url">★ 允许为 null —— 传 null 等价于"地址非法"，
        /// 与原先 <c>url?.Trim()</c> 的运行期行为完全一致（TryUrlHost 对 null 返回 false ⇒ 抛策略异常）。
        /// 标注为可空可让调用方不必再为"地址可能缺失"单独兜底。</param>
        internal static string RequireHttpHost(string? url, string reason = PluginSandboxReasons.CredentialTargetNotAllowed)
        {
            if (!TryUrlHost(url, out var host) || !Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new PluginSandboxException(reason, "Invalid HTTP authority or unsupported scoped IPv6; request not sent");
            return host;
        }

        internal static IEnumerable<string> EmbeddedHttpUrls(string nativeUrl)
        {
            if (string.IsNullOrWhiteSpace(nativeUrl)) yield break;
            int query = nativeUrl.IndexOf('?');
            if (query < 0) yield break;
            string arguments = nativeUrl.Substring(query + 1).Split('#')[0];
            foreach (string part in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = part.IndexOf('=');
                if (equals < 0) continue;
                string value = WebUtility.UrlDecode(part.Substring(equals + 1));
                if (IsHttp(value)) yield return value;
            }
        }

        private static bool IsHttp(string value)
            => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        internal static void ValidateRequestUrl(string url)
        {
            // ★ 不用 url?.：条件访问会把 url 的 null-state 置为「可能为 null」，
            //   之后 RequireHttpHost(url) 就会报 CS8604（见 §4.3 铁律七）。
            //   改用显式判空，判定结果与原 `url?.StartsWith(...) == true` 逐位等价。
            if (url != null && url.StartsWith("native://", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string embedded in EmbeddedHttpUrls(url)) RequireHttpHost(embedded);
            }
            else RequireHttpHost(url);
        }

        /// <param name="current">★ 当前地址；可为空（此时回落到 https，与原 `current?.Scheme ?? "https"` 一致）。</param>
        internal static void ValidateRedirectLocation(Uri? location, Uri? current)
        {
            string raw = location?.OriginalString ?? "";
            if (location?.IsAbsoluteUri == true)
                RequireHttpHost(raw, PluginSandboxReasons.RedirectTargetNotAllowed);
            else if (raw.StartsWith("//", StringComparison.Ordinal))
                RequireHttpHost((current?.Scheme ?? "https") + ":" + raw, PluginSandboxReasons.RedirectTargetNotAllowed);
        }
    }
}
