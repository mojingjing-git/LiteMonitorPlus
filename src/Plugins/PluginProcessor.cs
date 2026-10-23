using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件数据处理类
    /// 负责 JSON 解析、变量提取和数据转换
    ///
    /// <para><b>【LM-009】声明式插件仍然具备网络访问能力</b></para>
    /// <para>
    /// 本项目的插件是<b>声明式</b>的：一份 JSON 描述"步骤 + 目标 + 输入 + 提取规则"，
    /// 这里<b>没有</b> eval / 脚本引擎 / 动态编译（本文件也不引入）。
    /// <b>但声明式不等于没有权限</b>：步骤里的 URL 会被真的发起 HTTP(S) 请求
    /// （发送由 <see cref="PluginExecutor"/> 负责），插件作者可以用它读取任何可达的目标
    /// —— 也就是说，<b>安装一个插件 = 授予它发起网络请求的能力</b>。
    /// </para>
    ///
    /// <para><b>可信插件来源（用户从何处获得插件）</b></para>
    /// <list type="number">
    /// <item>内置插件：随程序发布的 <c>resources/plugins/*.json</c>，由本仓库维护。</item>
    /// <item>插件市场：索引地址写死在 <c>PluginMarket.DefaultIndexUrl</c>
    /// （社区索引仓库 <c>awesome-litemonitor-plugins</c>），下载前缀白名单锁定在同一仓库的
    /// <c>plugins/</c> 目录，安装时按原始字节校验 SHA-256。</item>
    /// <item>用户手动放置：用户可自行把 <c>plugin.json</c> 放进 <c>resources/plugins/</c>
    /// 目录 —— 这条路径<b>没有</b>签名校验，风险由用户自担。</item>
    /// </list>
    ///
    /// <para><b>本地网络访问策略（插件可访问哪些目标、如何限制）</b></para>
    /// <list type="bullet">
    /// <item>响应体 ≤ <see cref="PluginSandboxLimits.MaxResponseBytes"/> 字节（2 MiB）：超出即中止读取并让该步骤失败；</item>
    /// <item>单次正则匹配 ≤ <see cref="PluginSandboxLimits.MaxRegexMatchTimeoutMs"/> 毫秒（500 ms）：超时即明确失败；</item>
    /// <item>单个插件实例的步骤数 ≤ <see cref="PluginSandboxLimits.MaxStepsPerExecution"/>（16）；</item>
    /// <item>单个步骤展开出的目标数 ≤ <see cref="PluginSandboxLimits.MaxTargetsPerStep"/>（64）；</item>
    /// <item>单次执行总时长 ≤ <see cref="PluginSandboxLimits.MaxExecutionDurationMs"/> 毫秒（30 s，含所有步骤与所有网络等待）；</item>
    /// <item>同时执行的插件步骤/请求数 ≤ <see cref="PluginSandboxLimits.MaxConcurrentRequests"/>（8，进程级）。</item>
    /// </list>
    /// <para>
    /// 触达任何上限都表现为<b>可判定的失败</b>：抛出带原因码的
    /// <see cref="PluginSandboxException"/>（原因码见 <see cref="PluginSandboxReasons"/>），
    /// <b>不会</b>静默截断后当作成功。
    /// </para>
    ///
    /// <para><b>凭据去向（API Key / Token / Cookie 只发往用户知晓并允许的目标）</b></para>
    /// <para>
    /// 带凭据的请求（Authorization / Cookie / API Key 等）只允许发往插件<b>显式声明</b>的主机
    /// —— 声明集合由 <see cref="GetDeclaredHosts"/> 从步骤 URL 里的<b>字面量主机</b>收集；
    /// 目标主机不在集合内时 <see cref="PluginSandbox.EnsureCredentialTargetAllowed"/>
    /// 直接拒绝，<b>凭据不会被发出去</b>。不带凭据的普通请求不受此限制。
    /// </para>
    ///
    /// <para><b>其他约束</b></para>
    /// <list type="bullet">
    /// <item>本文件<b>不</b>把口令 / 令牌 / Cookie 写进任何日志（<c>Debug.WriteLine</c>）。</item>
    /// <item>本文件<b>不</b>执行任何外部进程或命令：主程序以管理员权限运行，
    /// 用户输入与远端响应内容都不得进入命令行 —— 这里根本不存在这样的入口。</item>
    /// </list>
    ///
    /// <para><b>覆盖边界（诚实说明）</b></para>
    /// <para>
    /// 本文件提供的是沙箱<b>原语</b>；执行路径要真正受这些上限保护，调用方必须显式使用
    /// <see cref="PluginSandboxScope"/> / <see cref="PluginSandbox"/>。
    /// <see cref="PluginExecutor"/> 目前只带传输层上限（8 MiB 正文 / 10 s 客户端超时），
    /// <b>尚未</b>接入本沙箱 —— 接入属于 <c>PluginExecutor.cs</c> 的改动范围，不在本文件内。
    /// </para>
    /// </summary>
    public static class PluginProcessor
    {
        // [Optimization] Cached Compiled Regex for Template Resolution
        // [LM-009] 模板解析同样跑在插件内容上，因此也带匹配时限（超时 → 明确失败，而不是静默变空串）。
        private static readonly Regex _templateRegex = new Regex(@"\{\{(.+?)\}\}", RegexOptions.Compiled, PluginSandboxLimits.RegexMatchTimeout);

        /// <summary>
        /// [LM-009] 受时限约束的正则缓存（key = pattern）。上限固定，避免插件用无限多 pattern 撑爆缓存。
        /// </summary>
        private const int MaxGuardedRegexCacheEntries = 64;

        private static readonly ConcurrentDictionary<string, Regex> _guardedRegexCache =
            new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);

        /// <summary>
        /// 从 JSON 对象中提取值
        /// 支持路径语法: "data.current.temp" 或 "list[0].id"
        /// </summary>
        public static string ExtractJsonValue(JsonElement root, string path)
        {
            try
            {
                var current = root;
                // [Optimization] Avoid frequent Split if path is simple
                if (!path.Contains('.') && !path.Contains('['))
                {
                    if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(path, out var prop))
                        return JsonElementToString(prop);
                    return PluginConstants.STATUS_UNKNOWN;
                }

                var parts = path.Split('.');

                foreach (var part in parts)
                {
                    if (part.Contains("[") && part.EndsWith("]"))
                    {
                        // Handle Array Index: list[0]
                        int idxStart = part.IndexOf('[');
                        string name = part.Substring(0, idxStart);

                        // Parse index (fast span-like logic not available in .NET Framework / Standard 2.0 easily, keep substring)
                        if (!int.TryParse(part.Substring(idxStart + 1, part.Length - idxStart - 2), out int index)) return PluginConstants.STATUS_UNKNOWN;

                        if (!string.IsNullOrEmpty(name))
                        {
                            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return PluginConstants.STATUS_UNKNOWN;
                        }

                        if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength()) return PluginConstants.STATUS_UNKNOWN;
                        current = current[index];
                    }
                    else
                    {
                        // Handle Object Property
                        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return PluginConstants.STATUS_UNKNOWN;
                    }
                }

                return JsonElementToString(current);
            }
            catch
            {
                return PluginConstants.STATUS_UNKNOWN;
            }
        }

        private static string JsonElementToString(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? "",
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => element.GetRawText() // Object or Array as string
            };
        }

        /// <summary>
        /// 应用数据转换规则 (Transforms)
        /// </summary>
        public static void ApplyTransforms(List<PluginTransform> transforms, Dictionary<string, string> context)
        {
            ApplyTransforms(transforms, context, CancellationToken.None);
        }

        /// <summary>
        /// 应用数据转换规则 (Transforms)
        /// <para>[LM-009] 正则变换必须带匹配时限；超时会让本步骤明确失败（而不是静默变空串）。</para>
        /// </summary>
        public static void ApplyTransforms(List<PluginTransform> transforms, Dictionary<string, string> context, CancellationToken token)
        {
            if (transforms == null) return;

            foreach (var t in transforms)
            {
                token.ThrowIfCancellationRequested();

                string src = !string.IsNullOrEmpty(t.SourceVar) ? t.SourceVar : t.TargetVar;

                if (!context.ContainsKey(src)) continue;

                string val = context[src];

                switch (t.Function)
                {
                    case "regex_replace":
                        val = ApplyRegexReplace(val, t, context);
                        break;
                    case "regex_match":
                        val = ApplyRegexMatch(val, t);
                        break;
                    case "map":
                        val = ApplyMap(val, t);
                        break;
                    case "resolve_template":
                        val = ResolveTemplate(val, context);
                        break;
                    case "threshold_switch":
                        val = ApplyThresholdSwitch(val, t);
                        break;
                }

                context[t.TargetVar] = val;
            }
        }

        private static string ApplyRegexReplace(string val, PluginTransform t, Dictionary<string, string> context)
        {
            string replacement = t.To ?? "";
            if (replacement.Contains("{{"))
            {
                replacement = ResolveTemplate(replacement, context);
            }

            // [LM-009] 收窄吞异常：
            //   · 只有明确的正则超时（RegexMatchTimeoutException）会被转成"超时失败"；
            //   · 其余异常（非法 pattern 等 ArgumentException 子类）**向上传播**，
            //     绝不再被 `catch { return val; }` 吞掉后静默当作成功。
            return RegexReplaceGuarded(val, t.Pattern, replacement);
        }

        private static string ApplyRegexMatch(string val, PluginTransform t)
        {
            int groupIndex = 1;
            if (int.TryParse(t.To, out int idx)) groupIndex = idx;

            // [LM-009] 与 regex_replace 同样的口径：只有超时被转成失败，其余异常向上传播。
            return RegexMatchGuarded(val, t.Pattern, groupIndex);
        }

        /// <summary>
        /// [LM-009] 带<b>匹配时限</b>的正则替换。
        /// <para>超时（<see cref="RegexMatchTimeoutException"/>）→ 抛
        /// <see cref="PluginSandboxException"/>（原因码 <see cref="PluginSandboxReasons.RegexTimeout"/>）；
        /// 其它异常（如非法 pattern 抛出的 <see cref="ArgumentException"/>）**向上传播**，不被吞掉。</para>
        /// </summary>
        public static string RegexReplaceGuarded(string input, string pattern, string replacement)
        {
            var rx = GetGuardedRegex(pattern);
            try
            {
                return rx.Replace(input ?? "", replacement ?? "");
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.RegexTimeout,
                    $"正则匹配超时（上限 {PluginSandboxLimits.MaxRegexMatchTimeoutMs} ms）：{pattern}", ex);
            }
        }

        /// <summary>
        /// [LM-009] 带<b>匹配时限</b>的正则匹配（返回指定分组；无匹配 / 分组不存在时返回空串）。
        /// </summary>
        public static string RegexMatchGuarded(string input, string pattern, int groupIndex)
        {
            var rx = GetGuardedRegex(pattern);
            Match match;
            try
            {
                match = rx.Match(input ?? "");
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.RegexTimeout,
                    $"正则匹配超时（上限 {PluginSandboxLimits.MaxRegexMatchTimeoutMs} ms）：{pattern}", ex);
            }

            if (!match.Success) return "";
            if (groupIndex < 0 || groupIndex >= match.Groups.Count) return "";
            return match.Groups[groupIndex].Value;
        }

        /// <summary>
        /// [LM-009] 取（并缓存）带时限的正则实例。
        /// <para>非法 pattern 会在构造时抛 <see cref="RegexParseException"/>
        ///（<see cref="ArgumentException"/> 子类）—— 该异常**故意不捕获**，让它向上传播。</para>
        /// </summary>
        private static Regex GetGuardedRegex(string pattern)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (_guardedRegexCache.TryGetValue(pattern, out var cached)) return cached;

            var rx = new Regex(pattern, RegexOptions.None, PluginSandboxLimits.RegexMatchTimeout);
            if (_guardedRegexCache.Count < MaxGuardedRegexCacheEntries) _guardedRegexCache.TryAdd(pattern, rx);
            return rx;
        }

        private static string ApplyMap(string val, PluginTransform t)
        {
            if (t.Map != null && t.Map.ContainsKey(val))
            {
                return t.Map[val];
            }
            return val;
        }

        private static string ApplyThresholdSwitch(string val, PluginTransform t)
        {
            if (double.TryParse(val, out double numVal))
            {
                if (t.ValueMap != null && t.ValueMap.Count > 0)
                {
                    var sorted = new List<(double Th, string Val)>();
                    foreach (var kv in t.ValueMap)
                    {
                        if (double.TryParse(kv.Key, out double k)) sorted.Add((k, kv.Value));
                    }
                    sorted.Sort((a, b) => a.Th.CompareTo(b.Th));

                    string result = sorted[0].Val;

                    for (int i = 0; i < sorted.Count; i++)
                    {
                        if (numVal >= sorted[i].Th) result = sorted[i].Val;
                        else break;
                    }
                    return result;
                }
                return "0";
            }
            return "0";
        }

        /// <summary>
        /// 处理字符串模版替换
        /// </summary>
        public static string ResolveTemplate(string template, Dictionary<string, string> context)
        {
            if (string.IsNullOrEmpty(template)) return "";
            if (!template.Contains("{{")) return template;

            return _templateRegex.Replace(template, m =>
            {
                string content = m.Groups[1].Value.Trim();

                // Handle Ternary Syntax: "cond ? trueVal : falseVal"
                int qIdx = content.IndexOf('?');
                int cIdx = content.LastIndexOf(':');
                if (qIdx > 0 && cIdx > qIdx && (qIdx + 1 >= content.Length || content[qIdx + 1] != '?'))
                {
                    string condKey = content.Substring(0, qIdx).Trim();
                    string truePart = content.Substring(qIdx + 1, cIdx - (qIdx + 1)).Trim();
                    string falsePart = content.Substring(cIdx + 1).Trim();

                    bool isTrue = context.TryGetValue(condKey, out string val) && !string.IsNullOrEmpty(val);
                    string target = isTrue ? truePart : falsePart;

                    // Remove quotes if present
                    if (target.Length >= 2 && ((target.StartsWith("\"") && target.EndsWith("\"")) || (target.StartsWith("'") && target.EndsWith("'"))))
                    {
                        return target.Substring(1, target.Length - 2);
                    }

                    // Treat as variable if not quoted
                    return context.TryGetValue(target, out string tVal) ? tVal : "";
                }

                // Handle Fallback Syntax: "var ?? fallback"
                if (content.Contains("??"))
                {
                    var parts = content.Split(new[] { "??" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        string key = part.Trim();

                        // Check for string literal
                        if (key.Length >= 2 && ((key.StartsWith("\"") && key.EndsWith("\"")) || (key.StartsWith("'") && key.EndsWith("'"))))
                        {
                            return key.Substring(1, key.Length - 2);
                        }

                        if (context.TryGetValue(key, out string val) && !string.IsNullOrEmpty(val))
                        {
                            return val;
                        }
                    }
                    return ""; // All fallbacks failed
                }

                // Standard Lookup
                if (context.TryGetValue(content, out string value))
                {
                    return value;
                }

                return "";
            });
        }

        /// <summary>
        /// [LM-009] 收集插件<b>显式声明</b>的目标主机集合。
        /// <para>只认步骤 URL / 根 URL 里的<b>字面量主机</b>；主机部分来自模板（如
        /// <c>{{target_url}}</c>）时<b>不</b>构成声明 —— 那种目标由用户在界面上填写，
        /// 插件并未声明它，带凭据的请求不得发往该主机。</para>
        /// </summary>
        public static HashSet<string> GetDeclaredHosts(PluginTemplate tmpl)
        {
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tmpl == null) return hosts;

            AddDeclaredHost(hosts, tmpl.Execution?.Url);

            var steps = tmpl.Execution?.Steps;
            if (steps != null)
            {
                foreach (var step in steps)
                {
                    AddDeclaredHost(hosts, step?.Url);
                }
            }

            return hosts;
        }

        /// <summary>
        /// [LM-080] authority 的**终止符**：路径分隔符、查询串起始、片段起始。
        /// <para>主机名**不可能**包含 <c>?</c> 或 <c>#</c> ⇒ 它们出现即表示 authority 已经结束。</para>
        /// </summary>
        private static readonly char[] AuthorityTerminators = { '/', '?', '#' };

        private static void AddDeclaredHost(HashSet<string> hosts, string urlTemplate)
        {
            if (string.IsNullOrWhiteSpace(urlTemplate)) return;

            string trimmed = urlTemplate.Trim();
            int schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return;

            string rest = trimmed.Substring(schemeEnd + 3);

            // [LM-080] authority 在 '/'、'?'、'#' 三者中**最先出现**的位置结束。
            // 修复前只按 '/' 截断 ⇒ 无路径 URL（"https://host?x=1" / "https://host#frag"）的
            // **查询串 / 片段被当成了主机名的一部分** ⇒ 声明集合里出现一枚垃圾串
            // （该主机的带凭据请求被一律拒绝；危害方向偏严 / fail-closed，不会误放行）。
            int authorityEnd = rest.Length;
            foreach (char terminator in AuthorityTerminators)
            {
                int idx = rest.IndexOf(terminator);
                if (idx >= 0 && idx < authorityEnd) authorityEnd = idx;
            }
            string authority = rest.Substring(0, authorityEnd);

            // 主机来自模板 → 不是插件的显式声明
            if (authority.Contains("{{")) return;

            int at = authority.LastIndexOf('@');
            if (at >= 0) authority = authority.Substring(at + 1);

            int colon = authority.IndexOf(':');
            if (colon >= 0) authority = authority.Substring(0, colon);

            authority = authority.Trim();
            if (authority.Length > 0) hosts.Add(authority);
        }
    }

    /// <summary>
    /// [LM-009] 插件沙箱上限常量。
    /// <para>这些数值是<b>契约</b>：不得改成 0，不得改成"无限制"，也不得留空。
    /// 回归测试 <c>PluginSandboxLimitsTests</c> 会逐条断言具体数值。</para>
    /// </summary>
    public static class PluginSandboxLimits
    {
        /// <summary>响应体字节上限：2 MiB = 2097152 字节。超出即中止读取并让该步骤失败。</summary>
        public const int MaxResponseBytes = 2 * 1024 * 1024;

        /// <summary>单次正则匹配时限：500 毫秒。超时即该步骤失败（明确的"超时失败"）。</summary>
        public const int MaxRegexMatchTimeoutMs = 500;

        /// <summary>单个插件实例一次执行的步骤数上限：16。</summary>
        public const int MaxStepsPerExecution = 16;

        /// <summary>单个步骤展开出的目标数上限：64。</summary>
        public const int MaxTargetsPerStep = 64;

        /// <summary>单次执行总时长上限：30000 毫秒（30 秒，含所有步骤与所有网络等待）。</summary>
        public const int MaxExecutionDurationMs = 30_000;

        /// <summary>同时执行的插件步骤/请求数上限：8（进程级）。</summary>
        public const int MaxConcurrentRequests = 8;

        /// <summary>并发槽位的排队等待上限：1000 毫秒；等不到即明确失败（不无限排队）。</summary>
        public const int ConcurrencyAcquireTimeoutMs = 1_000;

        /// <summary>正则匹配时限（<see cref="MaxRegexMatchTimeoutMs"/> 的 TimeSpan 形式）。</summary>
        public static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(MaxRegexMatchTimeoutMs);
    }

    /// <summary>
    /// [LM-009] 沙箱失败原因码。触达任何上限都必须落到这些码之一（可观测、可断言）。
    /// </summary>
    public static class PluginSandboxReasons
    {
        /// <summary>响应体超过 <see cref="PluginSandboxLimits.MaxResponseBytes"/> 字节。</summary>
        public const string ResponseTooLarge = "LM009_RESPONSE_TOO_LARGE";

        /// <summary>正则匹配超过 <see cref="PluginSandboxLimits.MaxRegexMatchTimeoutMs"/> 毫秒。</summary>
        public const string RegexTimeout = "LM009_REGEX_TIMEOUT";

        /// <summary>步骤数超过 <see cref="PluginSandboxLimits.MaxStepsPerExecution"/>。</summary>
        public const string StepLimit = "LM009_STEP_LIMIT";

        /// <summary>单步骤展开的目标数超过 <see cref="PluginSandboxLimits.MaxTargetsPerStep"/>。</summary>
        public const string TargetLimit = "LM009_TARGET_LIMIT";

        /// <summary>单次执行总时长超过 <see cref="PluginSandboxLimits.MaxExecutionDurationMs"/> 毫秒。</summary>
        public const string DeadlineExceeded = "LM009_DEADLINE_EXCEEDED";

        /// <summary>并发插件请求数超过 <see cref="PluginSandboxLimits.MaxConcurrentRequests"/>。</summary>
        public const string ConcurrencyLimit = "LM009_CONCURRENCY_LIMIT";

        /// <summary>带凭据的请求目标主机不在插件显式声明的允许目标集合内（凭据未发送）。</summary>
        public const string CredentialTargetNotAllowed = "LM009_CREDENTIAL_TARGET_NOT_ALLOWED";

        /// <summary>
        /// [LM-045] 重定向目标主机不在允许集合内（该跳请求**未发出**，响应数据也未进入流水线）。
        /// <para>用于**不带凭据**的重定向：允许集合 = 声明主机集合 ∪ {原始直连目标主机}。</para>
        /// </summary>
        public const string RedirectTargetNotAllowed = "LM009_REDIRECT_TARGET_NOT_ALLOWED";

        /// <summary>
        /// [LM-045] 重定向跳数超过上限（上限 5 跳，与 <c>PluginMarket.MaxDownloadRedirects</c> 对齐）。
        /// </summary>
        public const string RedirectLimit = "LM009_REDIRECT_LIMIT";
    }

    /// <summary>
    /// [LM-009] 沙箱失败异常：带机器可读的原因码（<see cref="Reason"/>）。
    /// </summary>
    public class PluginSandboxException : Exception
    {
        /// <summary>原因码，取值见 <see cref="PluginSandboxReasons"/>。</summary>
        public string Reason { get; }

        /// <summary>构造沙箱失败异常。</summary>
        public PluginSandboxException(string reason, string message) : base(message)
        {
            Reason = reason;
        }

        /// <summary>构造沙箱失败异常（带内部异常）。</summary>
        public PluginSandboxException(string reason, string message, Exception inner) : base(message, inner)
        {
            Reason = reason;
        }
    }

    /// <summary>
    /// [LM-009] 单次插件执行的沙箱作用域。
    /// <para>把【步骤数 / 目标展开数 / 单次执行总时长 / 并发】四个上限收在同一个对象里；
    /// 触达任何上限都抛 <see cref="PluginSandboxException"/>（明确原因码），
    /// <b>不会</b>静默截断后当作成功。</para>
    ///
    /// <para><b>计数口径（S3.4 裁决）</b> —— 三种口径互不重叠，各有且只有一个判定点：</para>
    /// <list type="bullet">
    /// <item><b>步骤数 —— 按目标计</b>：每个 target 拥有<b>独立的</b>步骤预算
    /// （≤ <see cref="PluginSandboxLimits.MaxStepsPerExecution"/>）。
    /// 该上限针对的是"单条链无限长"（声明式插件用超长步骤链耗尽资源），
    /// 跨 target <b>不</b>累加：目标数来自<b>用户配置</b>（<c>PluginInstanceConfig.Targets</c>），
    /// 插件无法自行增加目标数；且跨目标的总量放大已被"目标数 ≤ 64 × 总时长 ≤ 30 s × 并发 ≤ 8"
    /// 三道<b>独立</b>闸门覆盖 ⇒ 改成按目标计<b>不引入新的攻击面</b>。
    /// 判定点：<see cref="EnsureStepAllowed"/>（在<b>目标级作用域</b>上调用）。</item>
    /// <item><b>目标展开数 —— 按实例计</b>：一次执行内所有 target 的目标数<b>累计</b>
    /// （≤ <see cref="PluginSandboxLimits.MaxTargetsPerStep"/>）。
    /// 判定点：<see cref="EnsureTargetsAllowed"/>（在<b>实例级作用域</b>上调用）——
    /// 判定依据是<b>累计值</b>（<b>不是</b>本次传入的展开数）：跨 target 累计超限同样失败。</item>
    /// <item><b>单次执行总时长 —— 按实例计</b>：一次执行<b>共用一个截止时刻</b>
    /// （≤ <see cref="PluginSandboxLimits.MaxExecutionDurationMs"/>），跨 target 累计。
    /// 判定点：<see cref="EnsureNotExpired"/>（实例级与目标级作用域共用同一截止时刻）。</item>
    /// <item><b>并发 —— 按进程计</b>：进程级信号量（见 <see cref="PluginSandbox"/>）。</item>
    /// </list>
    ///
    /// <para><b>用法</b>：实例执行时用<b>实例级作用域</b>（<c>new PluginSandboxScope(token)</c>），
    /// 每个 target 再用 <see cref="BeginTarget"/> 开一个<b>目标级作用域</b>：
    /// 目标级作用域与实例级<b>共享</b>截止时刻 / 取消令牌 / 目标展开数累计，
    /// 只有<b>步骤计数</b>是独立的。没有目标上下文的单作用域调用（直接使用原语），
    /// 步骤数在<b>该作用域内累计</b>。</para>
    /// </summary>
    public sealed class PluginSandboxScope : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly bool _ownsCts;
        private readonly CancellationToken _outer;
        private readonly DateTime _deadlineUtc;

        /// <summary>实例级作用域（非 null ⇒ 本作用域是目标级作用域，owner 为实例级作用域）。</summary>
        private readonly PluginSandboxScope _owner;

        private int _stepCount;
        private int _targetCount;
        private int _disposed;

        /// <summary>
        /// 生产入口（实例级作用域）默认使用的<b>单次执行总时长</b>。
        /// <para>取值来自 <see cref="PluginSandboxLimits.MaxExecutionDurationMs"/>（契约值，不得改动）；
        /// 生产入口 <c>PluginExecutor.ExecuteInstanceAsync</c> 经 <c>PluginExecutor.CreateScope</c>
        /// 用<b>默认构造</b>取到它 ⇒ 生产默认时长只有这一个出处，
        /// 不会出现"入口硬编码 5 分钟"这类漂移。</para>
        /// </summary>
        public static TimeSpan DefaultExecutionDuration =>
            TimeSpan.FromMilliseconds(PluginSandboxLimits.MaxExecutionDurationMs);



        /// <summary>
        /// 用默认上限创建作用域（<see cref="PluginSandboxLimits"/> 里的全部数值）。
        /// <para>总时长取 <see cref="DefaultExecutionDuration"/>（= 生产入口使用的契约值）。</para>
        /// </summary>
        public PluginSandboxScope(CancellationToken cancellationToken = default)
            : this(cancellationToken,
                   DefaultExecutionDuration,
                   PluginSandboxLimits.MaxStepsPerExecution,
                   PluginSandboxLimits.MaxTargetsPerStep)
        {
        }

        /// <summary>
        /// 用指定上限创建作用域（供测试与定制场景使用；上限必须 &gt; 0，不允许"无限制"）。
        /// </summary>
        public PluginSandboxScope(CancellationToken cancellationToken, TimeSpan maxDuration, int maxSteps, int maxTargets)
        {
            if (maxDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxDuration));
            if (maxSteps <= 0) throw new ArgumentOutOfRangeException(nameof(maxSteps));
            if (maxTargets <= 0) throw new ArgumentOutOfRangeException(nameof(maxTargets));

            _outer = cancellationToken;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ownsCts = true;
            _deadlineUtc = DateTime.UtcNow + maxDuration;
            _cts.CancelAfter(maxDuration);

            MaxDuration = maxDuration;
            MaxSteps = maxSteps;
            MaxTargets = maxTargets;
        }

        /// <summary>
        /// 目标级作用域构造：与实例级作用域<b>共享</b>截止时刻 / 取消令牌 / 目标展开数累计，
        /// 只有<b>步骤计数</b>是独立的（步骤数按目标计）。
        /// </summary>
        private PluginSandboxScope(PluginSandboxScope owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));

            _owner = owner;
            _outer = owner._outer;
            _cts = owner._cts;            // 共享：同一把取消令牌 ⇒ 总时长仍是"按实例计"
            _ownsCts = false;              // 目标级作用域不拥有 CTS，释放时不连带释放实例级的
            _deadlineUtc = owner._deadlineUtc;

            MaxDuration = owner.MaxDuration;
            MaxSteps = owner.MaxSteps;
            MaxTargets = owner.MaxTargets;
        }

        /// <summary>
        /// [LM-009] 为一个 target 开启<b>独立的步骤计数窗口</b>（步骤数按目标计）。
        /// <para>返回的目标级作用域与本作用域共享<b>总时长截止时刻</b>、<b>取消令牌</b>、
        /// <b>目标展开数累计</b>（都按实例计），只有<b>步骤计数</b>是独立的
        /// ⇒ 每个 target 至多 <see cref="PluginSandboxLimits.MaxStepsPerExecution"/> 步，
        /// 跨 target <b>不</b>累加。</para>
        /// </summary>
        public PluginSandboxScope BeginTarget()
        {
            return new PluginSandboxScope(this);
        }

        /// <summary>本次执行允许的总时长。</summary>
        public TimeSpan MaxDuration { get; }

        /// <summary>本次执行允许的步骤数上限（<b>每个目标</b>各自的上限）。</summary>
        public int MaxSteps { get; }

        /// <summary>单个步骤允许展开的目标数上限（<b>按实例累计</b>）。</summary>
        public int MaxTargets { get; }

        /// <summary>
        /// 已执行的步骤数。
        /// <para>实例级作用域 = 本作用域内累计（无目标上下文的单作用域用法）；
        /// 目标级作用域 = <b>该目标</b>内的累计（步骤数按目标计）。</para>
        /// </summary>
        public int StepCount => Volatile.Read(ref _stepCount);

        /// <summary>已展开的目标数（<b>按实例累计</b>；目标级作用域读到的是实例级累计值）。</summary>
        public int TargetCount => _owner != null ? _owner.TargetCount : Volatile.Read(ref _targetCount);

        /// <summary>实例级作用域（非 null ⇒ 本作用域是目标级作用域）。</summary>
        public PluginSandboxScope Owner => _owner;

        /// <summary>作用域令牌：与外部取消联动，并在总时长到期时自动取消（网络等待因此可被中止）。</summary>
        public CancellationToken Token => _cts.Token;

        /// <summary>总时长到期时刻（UTC）。</summary>
        public DateTime DeadlineUtc => _deadlineUtc;

        /// <summary>剩余时长（可能为负）。</summary>
        public TimeSpan Remaining => _deadlineUtc - DateTime.UtcNow;

        /// <summary>总时长是否已到期。</summary>
        public bool IsExpired => DateTime.UtcNow >= _deadlineUtc;

        /// <summary>
        /// 步骤数上限检查：每步开始前调用一次；超过上限抛
        /// <see cref="PluginSandboxException"/>（原因码 <see cref="PluginSandboxReasons.StepLimit"/>）。
        /// <para><b>按目标计</b>（S3.4）：在<b>目标级作用域</b>（<see cref="BeginTarget"/>）上调用时，
        /// 计数只覆盖<b>该目标</b>的步骤链（跨 target <b>不</b>累加）；
        /// 在实例级作用域上直接调用时，计数覆盖<b>该作用域内</b>的步骤。</para>
        /// </summary>
        public void EnsureStepAllowed()
        {
            int next = Interlocked.Increment(ref _stepCount);
            if (next > MaxSteps)
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.StepLimit,
                    $"插件步骤数超过上限（{MaxSteps}）");
            }
        }

        /// <summary>
        /// 目标展开数上限检查：<paramref name="targetCount"/> 为本步骤展开出的目标数；
        /// 超过上限抛 <see cref="PluginSandboxException"/>（原因码 <see cref="PluginSandboxReasons.TargetLimit"/>）。
        /// <para><b>按实例计</b>：目标级作用域上的调用会累加到<b>实例级作用域</b>上，
        /// 目标展开数在一次执行内跨 target 累计。
        /// <para><b>[LM-040] 判定依据是「累计值」</b>：本方法把传入的展开数累加到实例级计数器上，
        /// 再拿<b>累加后的总数</b>与上限比较（<b>不是</b>拿本次传入的展开数比较）——
        /// 否则"每次都传一个 ≤ 上限的数字"就能把累计值撑爆，"跨 target 累计 ≤ 64"只是文档说法、并非强制约束。</para>
        /// </summary>
        public void EnsureTargetsAllowed(int targetCount)
        {
            if (targetCount < 0) throw new ArgumentOutOfRangeException(nameof(targetCount));

            // 目标展开数按实例计：目标级作用域的计数累加到实例级作用域上。
            if (_owner != null)
            {
                _owner.EnsureTargetsAllowed(targetCount);
                return;
            }

            // Interlocked.Add 的返回值就是「累加后的总数」⇒ 直接拿它判定，避免"读—比较"之间的竞态。
            int total = Interlocked.Add(ref _targetCount, targetCount);

            if (total > MaxTargets)
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.TargetLimit,
                    $"单次执行展开的目标数超过上限（{MaxTargets}，累计 {total}）");
            }
        }

        /// <summary>
        /// 总时长上限检查：已到期则抛 <see cref="PluginSandboxException"/>
        /// （原因码 <see cref="PluginSandboxReasons.DeadlineExceeded"/>）。
        /// </summary>
        public void EnsureNotExpired()
        {
            if (DateTime.UtcNow >= _deadlineUtc)
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.DeadlineExceeded,
                    $"单次执行总时长超过上限（{(int)MaxDuration.TotalMilliseconds} ms）");
            }
        }

        /// <summary>
        /// 在作用域内执行一段操作：外部主动取消保持 <see cref="OperationCanceledException"/> 原样上抛；
        /// 因总时长到期而被取消时转成
        /// <see cref="PluginSandboxException"/>（原因码 <see cref="PluginSandboxReasons.DeadlineExceeded"/>）。
        /// </summary>
        public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            try
            {
                return await operation(Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_outer.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.DeadlineExceeded,
                    $"单次执行总时长超过上限（{(int)MaxDuration.TotalMilliseconds} ms）");
            }
        }

        /// <summary>
        /// 沙箱化的<b>单步执行</b>：步骤数上限 → 总时长上限 → 并发槽位 →
        /// 受字节上限约束且<b>可取消</b>的响应体读取。
        /// <para>任一上限触达 ⇒ 抛 <see cref="PluginSandboxException"/>（明确原因码）。</para>
        /// </summary>
        /// <param name="openResponseBody">打开响应体的回调（收到可取消的令牌）。</param>
        /// <param name="maxResponseBytes">响应体字节上限；&lt;= 0 时回落到 <see cref="PluginSandboxLimits.MaxResponseBytes"/>。</param>
        public async Task<string> RunStepAsync(Func<CancellationToken, Task<Stream>> openResponseBody, int maxResponseBytes = 0)
        {
            if (openResponseBody == null) throw new ArgumentNullException(nameof(openResponseBody));

            EnsureStepAllowed();
            EnsureNotExpired();

            try
            {
                using var slot = await PluginSandbox.AcquireRequestSlotAsync(Token).ConfigureAwait(false);
                using var body = await openResponseBody(Token).ConfigureAwait(false);
                byte[] bytes = await PluginSandbox.ReadResponseBodyAsync(body, maxResponseBytes, Token).ConfigureAwait(false);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) when (_outer.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.DeadlineExceeded,
                    $"单次执行总时长超过上限（{(int)MaxDuration.TotalMilliseconds} ms）");
            }
        }

        /// <summary>释放作用域令牌。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // 目标级作用域与实例级作用域**共享** CTS ⇒ 只有拥有者才能释放它，
            // 否则一个 target 结束就会把整次执行的取消令牌一并释放掉。
            if (!_ownsCts) return;

            try { _cts.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// [LM-009] 插件沙箱的静态原语：并发闸门、受上限约束的响应体读取、凭据目标校验。
    /// </summary>
    public static class PluginSandbox
    {
        /// <summary>
        /// 进程级并发闸门：同时执行的插件步骤/请求数不超过
        /// <see cref="PluginSandboxLimits.MaxConcurrentRequests"/>。
        /// </summary>
        private static readonly SemaphoreSlim GlobalRequestGate =
            new SemaphoreSlim(PluginSandboxLimits.MaxConcurrentRequests, PluginSandboxLimits.MaxConcurrentRequests);

        /// <summary>并发上限（= <see cref="PluginSandboxLimits.MaxConcurrentRequests"/>）。</summary>
        public static int MaxConcurrentRequests => PluginSandboxLimits.MaxConcurrentRequests;

        /// <summary>当前空闲槽位数（观测用）。</summary>
        public static int AvailableRequestSlots => GlobalRequestGate.CurrentCount;

        /// <summary>
        /// 占用一个全局并发槽位。
        /// <para>槽位用尽时最多等待 <paramref name="acquireTimeoutMs"/> 毫秒；
        /// 仍拿不到 ⇒ 抛 <see cref="PluginSandboxException"/>
        /// （原因码 <see cref="PluginSandboxReasons.ConcurrencyLimit"/>），<b>不会</b>无限排队。</para>
        /// </summary>
        public static async Task<IDisposable> AcquireRequestSlotAsync(CancellationToken token, int acquireTimeoutMs = PluginSandboxLimits.ConcurrencyAcquireTimeoutMs)
        {
            int timeout = acquireTimeoutMs > 0 ? acquireTimeoutMs : PluginSandboxLimits.ConcurrencyAcquireTimeoutMs;

            bool acquired;
            try
            {
                acquired = await GlobalRequestGate.WaitAsync(timeout, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            if (!acquired)
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.ConcurrencyLimit,
                    $"并发插件请求数超过上限（{MaxConcurrentRequests}），等待 {timeout} ms 后仍无空闲槽位");
            }

            return new RequestSlot();
        }

        /// <summary>
        /// 在并发上限内执行一个请求（占槽 → 执行 → 释放槽位）。
        /// </summary>
        public static async Task<T> RunBoundedRequestAsync<T>(Func<Task<T>> operation, CancellationToken token)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            using var slot = await AcquireRequestSlotAsync(token).ConfigureAwait(false);
            return await operation().ConfigureAwait(false);
        }

        private sealed class RequestSlot : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) GlobalRequestGate.Release();
            }
        }

        /// <summary>
        /// [LM-009] 带<b>字节上限</b>且<b>可取消</b>的响应体读取。
        /// <para>超出上限 ⇒ 立即中止读取并抛 <see cref="PluginSandboxException"/>
        /// （原因码 <see cref="PluginSandboxReasons.ResponseTooLarge"/>），
        /// <b>不会</b>静默截断成"成功"。</para>
        /// </summary>
        public static async Task<byte[]> ReadResponseBodyAsync(Stream body, int maxBytes, CancellationToken token)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            // [LM-047] 契约天花板是**硬顶**：即便调用方传入更大的上限，也不得超过
            // PluginSandboxLimits.MaxResponseBytes（2 MiB）——否则"2 MiB"只是调用方约定、
            // 不是原语层保证。生产调用方当前传的都是 EffectiveMaxResponseBytes
            // （= min(实例级 8 MiB, 沙箱 2 MiB)），故本夹取对生产行为**无变化**；
            // 它只是把契约值从"约定"变成"强制"。
            int limit = maxBytes > 0
                ? Math.Min(maxBytes, PluginSandboxLimits.MaxResponseBytes)
                : PluginSandboxLimits.MaxResponseBytes;

            byte[] buffer = new byte[8192];
            using var ms = new MemoryStream();
            long total = 0;

            while (true)
            {
                int read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read <= 0) break;

                total += read;
                if (total > limit)
                {
                    throw new PluginSandboxException(
                        PluginSandboxReasons.ResponseTooLarge,
                        $"响应体超过上限（{limit} 字节，已读 {total} 字节）");
                }

                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }

        /// <summary>
        /// [LM-009] 带字节上限的响应体读取（按指定编码解码）。
        /// </summary>
        public static async Task<string> ReadResponseTextAsync(Stream body, int maxBytes, string encoding, CancellationToken token)
        {
            byte[] bytes = await ReadResponseBodyAsync(body, maxBytes, token).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(encoding) && encoding.Equals("gbk", StringComparison.OrdinalIgnoreCase))
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding("GBK").GetString(bytes);
            }

            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// 会携带凭据的请求头名称（大小写不敏感）。
        /// </summary>
        private static readonly HashSet<string> CredentialHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "authorization",
            "proxy-authorization",
            "cookie",
            "set-cookie",
            "x-api-key",
            "api-key",
            "apikey",
            "x-auth-token",
            "x-access-token",
            "x-token",
            "x-secret",
            "x-client-secret",
            "x-hub-signature"
        };

        /// <summary>判断请求头是否是凭据类（会带上秘密的）请求头。</summary>
        public static bool IsCredentialHeaderName(string headerName)
        {
            if (string.IsNullOrWhiteSpace(headerName)) return false;
            return CredentialHeaderNames.Contains(headerName.Trim());
        }

        /// <summary>判断一组请求头里是否含凭据。</summary>
        public static bool HasCredentialHeader(IEnumerable<KeyValuePair<string, string>> headers)
        {
            if (headers == null) return false;
            foreach (var kv in headers)
            {
                if (IsCredentialHeaderName(kv.Key)) return true;
            }
            return false;
        }

        /// <summary>
        /// [LM-009] 校验带凭据请求的目标主机。
        /// <para>凭据只允许发往<b>插件显式声明</b>的主机；不在声明集合内 ⇒ 抛
        /// <see cref="PluginSandboxException"/>（原因码
        /// <see cref="PluginSandboxReasons.CredentialTargetNotAllowed"/>），请求必须被放弃，
        /// 凭据不得发送。不带凭据的请求不受此限制。</para>
        /// </summary>
        public static void EnsureCredentialTargetAllowed(string url, IEnumerable<KeyValuePair<string, string>> headers, IReadOnlyCollection<string> allowedHosts)
        {
            if (!HasCredentialHeader(headers)) return;

            if (string.IsNullOrWhiteSpace(url))
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.CredentialTargetNotAllowed,
                    "带凭据的请求缺少目标地址，凭据未发送");
            }

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.CredentialTargetNotAllowed,
                    "带凭据的请求目标地址非法，凭据未发送");
            }

            // native:// 是本地处理器，不产生网络请求，也就没有凭据外发问题。
            if (string.Equals(uri.Scheme, "native", StringComparison.OrdinalIgnoreCase)) return;

            string host = uri.Host;
            if (string.IsNullOrEmpty(host))
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.CredentialTargetNotAllowed,
                    "带凭据的请求无法确定目标主机，凭据未发送");
            }

            if (allowedHosts != null)
            {
                foreach (var allowed in allowedHosts)
                {
                    if (string.IsNullOrWhiteSpace(allowed)) continue;
                    if (HostMatches(allowed, host)) return;
                }
            }

            // 只记录主机名与原因码，绝不记录凭据值本身。
            System.Diagnostics.Debug.WriteLine($"[PluginSandbox] 拒绝向未声明主机发送凭据：{host}");

            throw new PluginSandboxException(
                PluginSandboxReasons.CredentialTargetNotAllowed,
                $"目标主机 '{host}' 不在插件显式声明的允许目标集合内，凭据未发送");
        }

        /// <summary>
        /// [LM-009] 便捷重载：允许目标集合取自插件声明（<see cref="PluginProcessor.GetDeclaredHosts"/>）。
        /// </summary>
        public static void EnsureCredentialTargetAllowed(string url, IEnumerable<KeyValuePair<string, string>> headers, PluginTemplate tmpl)
        {
            EnsureCredentialTargetAllowed(url, headers, PluginProcessor.GetDeclaredHosts(tmpl));
        }

        private static bool HostMatches(string allowed, string host)
        {
            string a = StripPort(allowed.Trim());
            string h = StripPort(host.Trim());
            return a.Length > 0 && string.Equals(a, h, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 去掉主机里的端口。
        /// <para>[LM-079] **IPv6 字面量（带方括号）整体就是主机名**："[fd00::1]" / "[fd00::2]"
        /// 只能**整体**带端口（"[fd00::1]:8080"），**不得**按最后一个冒号截断 ——
        /// 否则两个不同的 IPv6 地址会被截成同一个 "[fd00:" ⇒ **地址碰撞**
        /// （fail-open：声明了 [fd00::1] 却能把请求/凭据发往 [fd00::2]）。</para>
        /// <para>非方括号形式：只有**单个**冒号才是 "host:port"；多个冒号是**裸 IPv6 字面量**，
        /// 整体就是主机名（不得截出垃圾串）。</para>
        /// </summary>
        private static string StripPort(string value)
        {
            // 与原实现一致：null 会抛 NullReferenceException（调用方均已先行判空）。
            if (value.Length == 0) return value;

            // IPv6 字面量（带方括号）：方括号内整体是主机名，只可能整体带端口。
            if (value[0] == '[')
            {
                int close = value.IndexOf(']');
                if (close > 0) return value.Substring(0, close + 1);
                return value;
            }

            // 非方括号形式：单个冒号才是 "host:port"；多个冒号 ⇒ 裸 IPv6 字面量，整体是主机名。
            int colon = value.IndexOf(':');
            if (colon > 0 && colon == value.LastIndexOf(':') && colon < value.Length - 1)
            {
                return value.Substring(0, colon);
            }
            return value;
        }
    }
}
