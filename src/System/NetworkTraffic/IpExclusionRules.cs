namespace LiteMonitor.src.SystemServices.NetworkTraffic;

/// <summary>一条被拒绝的规则（行号 + 原文 + 原因）。</summary>
public sealed record IpRuleError(int LineNumber, string Text, string Reason);

/// <summary>
/// 精确 IP 排除规则的**编译后集合**。
/// <para>首版只支持精确 IPv4/IPv6 —— 不支持 CIDR、域名、通配符、端口、整段内网、进程黑名单。</para>
/// <para>匹配是 <see cref="IpKey"/> 上的哈希查找：热路径**不做**字符串格式化，**不做** DNS 查询。</para>
/// </summary>
public sealed class IpExclusionRules
{
    public const int MaxRuleCount = 256;

    private static int _versionCounter;

    private readonly HashSet<IpKey> _unicastRemotes;
    private readonly HashSet<IpKey> _multicastGroups;

    private IpExclusionRules(HashSet<IpKey> unicast, HashSet<IpKey> multicast, int version, int ruleCount, bool limitExceeded)
    {
        _unicastRemotes = unicast;
        _multicastGroups = multicast;
        Version = version;
        RuleCount = ruleCount;
        LimitExceeded = limitExceeded;
    }

    /// <summary>规则集每次重新编译都会变化（单调递增）。</summary>
    public int Version { get; }

    /// <summary>生效的规则条数（去重后）。</summary>
    public int RuleCount { get; }

    public int UnicastRuleCount => _unicastRemotes.Count;

    public int MulticastRuleCount => _multicastGroups.Count;

    public bool IsEmpty => _unicastRemotes.Count == 0 && _multicastGroups.Count == 0;

    /// <summary>True 表示超出了 <see cref="MaxRuleCount"/> —— 整个集合必须被拒绝，不能半生效。</summary>
    public bool LimitExceeded { get; }

    public static IpExclusionRules Empty { get; } =
        new(new HashSet<IpKey>(), new HashSet<IpKey>(), 0, 0, false);

    /// <summary>
    /// 编译用户输入的原始行。被拒绝的行**逐条**报告，绝不静默忽略。
    /// </summary>
    public static IpExclusionRules Compile(IReadOnlyList<string> lines, out IReadOnlyList<IpRuleError> errors)
    {
        var result = CompileWithResult(lines);
        errors = result.Errors;
        return result.Rules;
    }

    /// <summary>编译并返回带错误列表的结果对象。</summary>
    public static IpRuleCompilationResult CompileWithResult(IReadOnlyList<string> lines)
    {
        var errs = new List<IpRuleError>();
        var unicast = new HashSet<IpKey>();
        var multicast = new HashSet<IpKey>();
        int accepted = 0;
        bool limitExceeded = false;

        for (int i = 0; i < lines.Count; i++)
        {
            string raw = lines[i] ?? string.Empty;
            string text = raw.Trim();
            if (text.Length == 0) { continue; }

            // ---- 逐条拒绝：首版明确不支持的写法 ----
            if (text.Contains('/'))
            {
                errs.Add(new IpRuleError(i, text, "CIDR / prefix notation is not supported in the first version."));
                continue;
            }
            if (text.Contains('*') || text.Contains('?'))
            {
                errs.Add(new IpRuleError(i, text, "Wildcards are not supported."));
                continue;
            }
            if (text.Contains('%'))
            {
                errs.Add(new IpRuleError(i, text, "IPv6 scope id (fe80::1%12) is not supported: the interface cannot be identified reliably."));
                continue;
            }
            if (text.Contains(' ') || text.Contains('\t'))
            {
                errs.Add(new IpRuleError(i, text, "Addresses must not contain a port or extra text."));
                continue;
            }
            // 单个冒号 => host:port 形式（纯 IPv6 至少有两个冒号）
            if (text.Count(c => c == ':') == 1)
            {
                errs.Add(new IpRuleError(i, text, "A port or host:port form is not supported; enter a bare IP address."));
                continue;
            }

            if (!System.Net.IPAddress.TryParse(text, out System.Net.IPAddress? parsed))
            {
                errs.Add(new IpRuleError(i, text, "Not a valid IP address (host names are not resolved)."));
                continue;
            }
            if (parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                && parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                errs.Add(new IpRuleError(i, text, "Only IPv4 and IPv6 addresses are supported."));
                continue;
            }

            IpKey key = IpKey.FromIPAddress(parsed);

            // 重复规则只算一条（同一事件也只会被排除一次）。
            bool added = key.IsMulticast ? multicast.Add(key) : unicast.Add(key);
            if (!added) { continue; }

            if (accepted >= MaxRuleCount)
            {
                limitExceeded = true;
                errs.Add(new IpRuleError(i, text, "Rule limit of " + MaxRuleCount + " exceeded."));
                // 回滚这一条：超限的规则不进入集合。
                if (key.IsMulticast) { multicast.Remove(key); } else { unicast.Remove(key); }
                continue;
            }
            accepted++;
        }

        int version = Interlocked.Increment(ref _versionCounter);
        var rules = new IpExclusionRules(unicast, multicast, version, accepted, limitExceeded);
        return new IpRuleCompilationResult(rules, errs);
    }

    /// <summary>
    /// 判断一个事件的**远端**是否被排除。
    /// 发送按远端目的地址匹配，接收按远端源地址匹配（载荷里 #1 恒为远端）。
    /// 多播组地址在**单独**的集合里，不与单播远端规则混淆。
    /// </summary>
    public bool IsExcludedRemote(in IpKey remote, out bool matchedMulticastGroup)
    {
        if (remote.IsMulticast)
        {
            matchedMulticastGroup = _multicastGroups.Contains(remote);
            return matchedMulticastGroup;
        }
        matchedMulticastGroup = false;
        return _unicastRemotes.Contains(remote);
    }

    public IReadOnlyCollection<IpKey> UnicastRemotes => _unicastRemotes;

    public IReadOnlyCollection<IpKey> MulticastGroups => _multicastGroups;
}

/// <summary>规则编译结果：规则集 + 逐条错误。</summary>
public sealed record IpRuleCompilationResult(IpExclusionRules Rules, IReadOnlyList<IpRuleError> Errors)
{
    /// <summary>没有任何错误行、且至少有一条有效规则时才算可用。</summary>
    public bool IsUsable => Errors.Count == 0 && !Rules.IsEmpty && !Rules.LimitExceeded;
}
