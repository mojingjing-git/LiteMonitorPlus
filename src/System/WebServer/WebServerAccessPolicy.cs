using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LiteMonitor.src.WebServer
{
    /// <summary>
    /// Web 监听的访问策略判定（纯函数 · 无副作用 · 可单测）。
    /// </summary>
    /// <remarks>
    /// ★ 「局域网暴露必须配口令」这条规则有**两个**执行点，必须共用同一个判定：
    ///   <list type="number">
    ///   <item><b>冷启动</b>：<see cref="LiteWebServer.Start"/> —— 局域网模式 + 无有效口令 ⇒ 拒绝启动；</item>
    ///   <item><b>运行中变更</b>：<c>AppActions.ApplyWebServer</c> —— 运行中把口令清掉 ⇒ 必须停止监听。</item>
    ///   </list>
    ///   两处共用 ⇒ 运行中的变更**绕不过**冷启动那条约束（R03）。
    ///
    /// ★ [F02] 「**外部**（反向代理）暴露必须配口令」是**同一条规则的第三个执行点**：
    ///   反代即使从环回连进来也算对外暴露 ⇒ 同样必须有口令。
    ///   判定在 <see cref="IsExternalAccessForbidden"/>，与局域网那条**共用同一套凭据口径**
    ///   （<see cref="Settings.HasWebPasswordConfigured"/>）。
    /// </remarks>
    public static class WebServerAccessPolicy
    {
        /// <summary>「对局域网暴露，但当前没有有效凭据」⇒ 必须拒绝监听。</summary>
        /// <param name="cfg">当前设置；<c>null</c> 视为无配置 ⇒ 不拒绝。</param>
        /// <param name="runtimeLanExposed">
        /// 运行时事实：正在监听**非回环**地址。
        /// ★ 之所以不只看配置：配置写着「仅本机」但服务其实还绑在 0.0.0.0 上时（监听范围尚未重绑），
        ///   凭据一旦被清掉也必须停 —— 否则旧的对外监听会一直敞着。
        /// </param>
        public static bool IsLanAccessForbidden(Settings? cfg, bool runtimeLanExposed)
        {
            if (cfg == null) return false;

            bool lanRequired = cfg.WebServerBindMode == WebServerBindMode.Lan;
            if (!lanRequired && !runtimeLanExposed) return false;

            // ★ 没有有效凭据（既没有明文，也没有落盘的哈希）⇒ 对局域网暴露就是无凭据暴露
            return !cfg.HasWebPasswordConfigured();
        }

        // ==================================================================
        //  [F02] 外部 authority / 可信代理
        // ==================================================================

        /// <summary>HTTP 的默认端口。</summary>
        public const int DefaultHttpPort = 80;

        /// <summary>HTTPS 的默认端口。</summary>
        public const int DefaultHttpsPort = 443;

        /// <summary>
        /// 「配置了外部 authority（反代白名单），但当前没有有效凭据」⇒ 必须拒绝监听。
        /// </summary>
        /// <remarks>
        /// ★ 与 <see cref="IsLanAccessForbidden"/> 同口径：反代从**环回**连进来也算对外暴露，
        ///   不能因为"对端是 127.0.0.1"就继承"环回无凭据"的信任。
        /// ★ 只有"白名单里确实有可用的外部 authority"才算对外暴露 ——
        ///   白名单写满了非法条目（解析后为空）时按"未配置"处理，不平白把服务锁死。
        /// </remarks>
        public static bool IsExternalAccessForbidden(Settings? cfg, bool runtimeExternalExposed)
        {
            if (cfg == null) return false;

            bool externalRequired = ParseExternalAuthorities(cfg.WebServerAllowedHosts).Count > 0;
            if (!externalRequired && !runtimeExternalExposed) return false;

            return !cfg.HasWebPasswordConfigured();
        }

        /// <summary>
        /// 主机名归一化（小写、去 IPv6 方括号、去首尾空白）。
        /// </summary>
        /// <remarks>
        /// ★ 白名单条目与请求里的主机名**必须走同一个归一化**，
        ///   否则 "Tunnel.Example.com" 与 "tunnel.example.com" 会算出两个不同的白名单。
        /// </remarks>
        public static string NormalizeHost(string? host)
        {
            if (string.IsNullOrEmpty(host)) return "";
            string h = host.Trim().ToLowerInvariant();
            if (h.StartsWith("[", StringComparison.Ordinal) && h.EndsWith("]", StringComparison.Ordinal) && h.Length >= 2)
            {
                h = h.Substring(1, h.Length - 2);
            }
            return h.Trim();
        }

        /// <summary>
        /// 严格解析 authority：<c>host</c> / <c>host:port</c> / <c>[v6]:port</c>。
        /// </summary>
        /// <remarks>
        /// ★ 严格 = 非法端口（空 / 非数字 / 越界）**一律失败**，不"猜"一个端口出来。
        ///   旧实现遇到 <c>127.0.0.1:abc</c> 会把端口丢掉、只按主机名放行 ⇒ 那是个绕过口。
        /// ★ 多个冒号（不带方括号的 IPv6）⇒ 失败：必须写成 <c>[::1]</c>。
        /// </remarks>
        public static bool TryParseAuthority(string? value, out string host, out int? port)
        {
            host = "";
            port = null;
            if (value == null) return false;

            string v = value.Trim();
            if (v.Length == 0) return false;

            if (v[0] == '[')
            {
                int close = v.IndexOf(']');
                if (close <= 1) return false;
                string inner = v.Substring(1, close - 1);
                if (inner.Length == 0 || inner.IndexOf('[') >= 0 || inner.IndexOf(']') >= 0) return false;

                string rest = v.Substring(close + 1);
                if (rest.Length == 0)
                {
                    host = inner;
                    return true;
                }
                if (rest[0] != ':') return false;
                if (!TryParsePort(rest.Substring(1), out int p)) return false;

                host = inner;
                port = p;
                return true;
            }

            if (v.IndexOf(']') >= 0) return false;

            int colon = v.IndexOf(':');
            if (colon < 0)
            {
                host = v;
                return true;
            }
            if (v.IndexOf(':', colon + 1) >= 0) return false;   // 多个冒号 ⇒ 非法（IPv6 必须带方括号）

            string h = v.Substring(0, colon);
            if (h.Length == 0) return false;
            if (!TryParsePort(v.Substring(colon + 1), out int portValue)) return false;

            host = h;
            port = portValue;
            return true;
        }

        /// <summary>
        /// 严格解析端口（1..65535）；空 / 非数字 / 越界一律失败。
        /// </summary>
        private static bool TryParsePort(string s, out int port)
        {
            port = 0;
            if (string.IsNullOrEmpty(s) || s.Length > 5) return false;
            foreach (char c in s)
            {
                if (c < '0' || c > '9') return false;
            }
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
            return port > 0 && port <= 65535;
        }

        /// <summary>
        /// 解析一条**白名单条目**：<c>[scheme://]host[:port]</c>。
        /// </summary>
        /// <remarks>
        /// ★ 语法（精确匹配，**无通配符**）：
        ///   <list type="bullet">
        ///   <item><c>tunnel.example.com</c> —— 主机名；端口按协议取默认值（https⇒443 / http⇒80）。</item>
        ///   <item><c>tunnel.example.com:8443</c> —— 主机名 + 精确端口。</item>
        ///   <item><c>https://tunnel.example.com</c> —— 固定协议（有效协议 = https，端口默认 443）。</item>
        ///   <item><c>https://tunnel.example.com:8443</c> —— 固定协议 + 精确端口。</item>
        ///   <item><c>[2001:db8::1]:443</c> —— IPv6 必须带方括号。</item>
        ///   </list>
        /// ★ 固定协议的写法解决"外部是 HTTPS、但我们只看到明文 HTTP"的问题：
        ///   白名单是用户对"我的对外地址"的**权威声明**，因此由它决定有效协议。
        /// </remarks>
        public static bool TryParseAuthorityEntry(string? entry, out string? scheme, out string host, out int? port)
        {
            scheme = null;
            host = "";
            port = null;
            if (entry == null) return false;

            string v = entry.Trim();
            if (v.Length == 0) return false;

            int sep = v.IndexOf("://", StringComparison.Ordinal);
            if (sep >= 0)
            {
                string s = v.Substring(0, sep).Trim().ToLowerInvariant();
                if (s != "http" && s != "https") return false;
                scheme = s;
                v = v.Substring(sep + 3).Trim();
                if (v.Length == 0) return false;
            }

            if (!TryParseAuthority(v, out string rawHost, out port)) return false;

            string normalized = NormalizeHost(rawHost);
            if (normalized.Length == 0) return false;

            host = normalized;
            return true;
        }

        /// <summary>
        /// 解析外部 authority 白名单（配置文本 ⇒ 条目列表）。
        /// </summary>
        /// <remarks>
        /// ★ 非法条目**静默丢弃**（不整体拒绝）：一条写错的地址不该让整份配置失效，
        ///   但它也**绝不会**被当成"允许"—— 丢弃即等于不在白名单里。
        /// </remarks>
        public static List<WebAuthorityEntry> ParseExternalAuthorities(string? configured)
        {
            var result = new List<WebAuthorityEntry>();
            foreach (string raw in SplitConfigList(configured))
            {
                if (TryParseAuthorityEntry(raw, out string? scheme, out string host, out int? port))
                {
                    result.Add(new WebAuthorityEntry(host, port, scheme));
                }
            }
            return result;
        }

        /// <summary>
        /// 判定"外部 authority"是否被白名单放行，并给出**有效 authority**。
        /// </summary>
        /// <param name="entries">外部 authority 白名单。</param>
        /// <param name="requestScheme">请求侧已验证的协议（"http" / "https"）。</param>
        /// <param name="host">已归一化的主机名。</param>
        /// <param name="explicitPort">请求里**显式**带的端口；<c>null</c> = 没带。</param>
        /// <param name="authority">命中时的有效 authority（未命中时为 default）。</param>
        /// <remarks>
        /// ★ 端口口径（**不得**拿外部 443 与内部 5000 直接比较）：
        ///   <list type="bullet">
        ///   <item>请求带了显式端口 ⇒ 必须与条目的端口**精确一致**；条目没写端口时只接受该协议的默认端口。</item>
        ///   <item>请求没带端口 ⇒ 条目写了端口就用它（它就是这条外部 authority 的对外端口），没写则取协议默认端口。</item>
        ///   </list>
        /// ★ 条目固定了协议 ⇒ 有效协议由**条目**决定（"我的对外地址是 https"是用户的声明），
        ///   请求侧协议不同也不影响匹配；没固定协议时有效协议 = 请求侧协议。
        /// </remarks>
        public static bool MatchExternalAuthority(
            IReadOnlyList<WebAuthorityEntry> entries,
            string requestScheme,
            string host,
            int? explicitPort,
            out WebEffectiveAuthority authority)
        {
            authority = default;
            if (entries == null || host.Length == 0) return false;

            foreach (WebAuthorityEntry entry in entries)
            {
                if (!string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase)) continue;

                string scheme = entry.Scheme ?? requestScheme;
                int defaultPort = DefaultPortOf(scheme);

                if (explicitPort.HasValue)
                {
                    int expected = entry.Port ?? defaultPort;
                    if (explicitPort.Value != expected) continue;
                    authority = new WebEffectiveAuthority(scheme, host, explicitPort.Value, true);
                    return true;
                }

                authority = new WebEffectiveAuthority(scheme, host, entry.Port ?? defaultPort, true);
                return true;
            }

            authority = default;
            return false;
        }

        /// <summary>协议对应的默认端口（未知协议按 http 处理）。</summary>
        public static int DefaultPortOf(string? scheme)
            => string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? DefaultHttpsPort : DefaultHttpPort;

        /// <summary>
        /// 对端地址是否命中可信代理列表（**精确 IP**，不接受 CIDR / 代理链）。
        /// </summary>
        /// <remarks>
        /// ★ 精确匹配是刻意的：可信代理列表是"哪些对端可以代表我说话"的授权名单，
        ///   写成网段就等于把整个网段都授权了。
        /// ★ IPv4 / IPv6 分开比较（不做 IPv4-mapped 归一）—— 归一化会把
        ///   <c>::ffff:127.0.0.1</c> 变成 <c>127.0.0.1</c>，那等于凭空多授了信任。
        /// </remarks>
        public static bool IsTrustedProxyAddress(IReadOnlyCollection<string>? trustedProxies, IPAddress? peerAddress)
        {
            if (trustedProxies == null || trustedProxies.Count == 0) return false;
            if (peerAddress == null) return false;

            string peer = peerAddress.ToString();
            foreach (string raw in trustedProxies)
            {
                string candidate = (raw ?? "").Trim();
                if (candidate.Length == 0) continue;
                if (string.Equals(candidate, peer, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>按逗号 / 分号 / 换行拆分配置列表。</summary>
        public static IEnumerable<string> SplitConfigList(string? value)
        {
            if (string.IsNullOrEmpty(value)) yield break;
            foreach (string part in value.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (p.Length > 0) yield return p;
            }
        }

        /// <summary>把可信代理配置文本解析成**归一化后的 IP 字符串集合**。</summary>
        public static HashSet<string> ParseTrustedProxies(string? configured)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in SplitConfigList(configured))
            {
                string candidate = raw.Trim();
                if (candidate.Length == 0) continue;
                if (IPAddress.TryParse(candidate, out IPAddress? parsed))
                {
                    result.Add(parsed.ToString());
                    continue;
                }
                // ★ 不是合法 IP ⇒ 丢弃（绝不"当成通配"）。
                result.Add(candidate);
            }
            return result;
        }

        // ==================================================================
        //  访问边界快照（可比较 ⇒ 可当"策略代次"的指纹来源）
        // ==================================================================

        /// <summary>
        /// 构造当前的访问边界快照（Host / Origin / 外部 authority / 可信代理）。
        /// </summary>
        /// <remarks>
        /// ★ 指纹（Fingerprint）覆盖**所有会改变访问边界**的配置项：
        ///   白名单收紧 / 撤销代理信任 ⇒ 指纹变 ⇒ 策略代次变 ⇒ 旧会话与旧 WS 失效。
        /// ★ <b>刻意不含</b>访问口令：口令变更走"口令指纹"那条既有通道（会话表已按它撤销）。
        /// ★ 也<b>刻意不含</b>跨源白名单：改跨源例外不影响"谁能连上来"。
        /// </remarks>
        public static WebAccessBoundary BuildBoundary(Settings? cfg, int? listenPort)
        {
            var boundary = new WebAccessBoundary
            {
                BindMode = cfg?.WebServerBindMode ?? WebServerBindMode.Unset,
                TrustProxyHeaders = cfg?.WebServerTrustProxyHeaders ?? false,
                ListenPort = listenPort
            };

            // 本机地址始终允许（契约 W5）
            boundary.AllowedHosts.Add("127.0.0.1");
            boundary.AllowedHosts.Add("localhost");
            boundary.AllowedHosts.Add("::1");

            if (cfg != null)
            {
                // 局域网模式：额外允许用户配置的地址
                if (cfg.WebServerBindMode == WebServerBindMode.Lan)
                {
                    foreach (string addr in SplitConfigList(cfg.WebServerLanAddresses))
                    {
                        string host = NormalizeHost(addr);
                        if (host.Length > 0) boundary.AllowedHosts.Add(host);
                    }
                }

                // 显式配置的跨源（精确匹配）
                foreach (string origin in SplitConfigList(cfg.WebServerAllowedOrigins))
                {
                    string o = origin.Trim().TrimEnd('/');
                    if (o.Length > 0) boundary.AllowedOrigins.Add(o);
                }

                boundary.ExternalAuthorities.AddRange(ParseExternalAuthorities(cfg.WebServerAllowedHosts));
                foreach (string proxy in ParseTrustedProxies(cfg.WebServerTrustedProxies))
                {
                    boundary.TrustedProxies.Add(proxy);
                }
            }

            boundary.Fingerprint = ComputeFingerprint(boundary);
            return boundary;
        }

        /// <summary>计算访问边界的指纹（稳定、可比较）。</summary>
        private static string ComputeFingerprint(WebAccessBoundary boundary)
        {
            var sb = new StringBuilder();
            sb.Append("bind=").Append((int)boundary.BindMode).Append(';');
            sb.Append("trust=").Append(boundary.TrustProxyHeaders ? '1' : '0').Append(';');
            sb.Append("port=").Append(boundary.ListenPort?.ToString(CultureInfo.InvariantCulture) ?? "-").Append(';');

            var hosts = new List<string>(boundary.AllowedHosts);
            hosts.Sort(StringComparer.Ordinal);
            sb.Append("hosts=").Append(string.Join('|', hosts)).Append(';');

            var externals = new List<string>();
            foreach (WebAuthorityEntry e in boundary.ExternalAuthorities) externals.Add(e.ToString());
            externals.Sort(StringComparer.Ordinal);
            sb.Append("ext=").Append(string.Join('|', externals)).Append(';');

            var proxies = new List<string>(boundary.TrustedProxies);
            proxies.Sort(StringComparer.Ordinal);
            sb.Append("proxies=").Append(string.Join('|', proxies));

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToBase64String(hash);
        }
    }

    /// <summary>
    /// 一条外部 authority 白名单条目（精确匹配，无通配符）。
    /// </summary>
    public readonly struct WebAuthorityEntry
    {
        /// <summary>归一化后的主机名（小写、无 IPv6 方括号）。</summary>
        public string Host { get; }

        /// <summary>端口；<c>null</c> = 未指定（按协议取默认端口）。</summary>
        public int? Port { get; }

        /// <summary>固定的协议（"http" / "https"）；<c>null</c> = 不固定。</summary>
        public string? Scheme { get; }

        public WebAuthorityEntry(string host, int? port, string? scheme)
        {
            Host = host ?? "";
            Port = port;
            Scheme = string.IsNullOrEmpty(scheme) ? null : scheme;
        }

        public override string ToString()
        {
            string s = Scheme != null ? Scheme + "://" : "";
            s += Host;
            if (Port.HasValue) s += ":" + Port.Value.ToString(CultureInfo.InvariantCulture);
            return s;
        }
    }

    /// <summary>
    /// 请求的**有效 authority**（这次请求到底是对哪个"对外可见的地址"发起的）。
    /// </summary>
    public readonly struct WebEffectiveAuthority
    {
        /// <summary>有效协议（"http" / "https"）。</summary>
        public string Scheme { get; }

        /// <summary>有效主机名（已归一化）。</summary>
        public string Host { get; }

        /// <summary>有效端口。</summary>
        public int Port { get; }

        /// <summary>是否为**外部** authority（不在本机 / 局域网白名单里）。</summary>
        public bool IsExternal { get; }

        public WebEffectiveAuthority(string scheme, string host, int port, bool isExternal)
        {
            Scheme = scheme ?? "http";
            Host = host ?? "";
            Port = port;
            IsExternal = isExternal;
        }
    }

    /// <summary>
    /// 访问边界的配置快照（一次读取 ⇒ 判定口径不会撕裂）。
    /// </summary>
    public sealed class WebAccessBoundary
    {
        /// <summary>允许的主机名集合（本机 + 局域网）。</summary>
        public readonly HashSet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>显式允许的跨源（精确匹配）。</summary>
        public readonly HashSet<string> AllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>外部 authority 白名单（反代场景）。</summary>
        public readonly List<WebAuthorityEntry> ExternalAuthorities = new List<WebAuthorityEntry>();

        /// <summary>可信代理的 TCP 对端 IP（精确匹配）。</summary>
        public readonly HashSet<string> TrustedProxies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>是否信任转发头（降级开关，默认 false）。</summary>
        public bool TrustProxyHeaders;

        /// <summary>监听范围。</summary>
        public WebServerBindMode BindMode = WebServerBindMode.Unset;

        /// <summary>真实监听端口（Host 端口校验用）。</summary>
        public int? ListenPort;

        /// <summary>边界指纹（变化 ⇒ 策略代次变化 ⇒ 旧会话 / 旧 WS 失效）。</summary>
        public string Fingerprint = "";
    }
}
