using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.WebServer
{
    /// <summary>
    /// Web 有效数据需求的一次性快照（同一把锁内读取，保证连接数与租期状态不撕裂）。
    /// </summary>
    public readonly struct WebDemandSnapshot
    {
        /// <summary>已鉴权且尚未关闭的 WebSocket 连接数。</summary>
        public int AuthenticatedConnections { get; }

        /// <summary>已鉴权的 /api/snapshot 请求持有的短需求租期是否仍然有效。</summary>
        public bool HasHttpLease { get; }

        public WebDemandSnapshot(int authenticatedConnections, bool hasHttpLease)
        {
            AuthenticatedConnections = authenticatedConnections;
            HasHttpLease = hasHttpLease;
        }

        /// <summary>是否存在合法的 Web 数据需求（连接或 HTTP 租期任一成立）。</summary>
        public bool HasAnyDemand => AuthenticatedConnections > 0 || HasHttpLease;
    }

    /// <summary>
    /// Web 有效数据需求状态（线程安全）。
    ///
    /// 语义（计划 §4.4）：
    ///  - **已鉴权且尚未关闭的 WebSocket 连接**计入需求；连接数变化不按浏览器发送频率增加采样频率；
    ///  - **已鉴权的 /api/snapshot 请求**获得一个短需求租期（默认 2 秒），连续轮询会不断延长租期；
    ///  - 未鉴权请求、仅取 HTML / favicon 的请求**不**产生任何硬件需求；
    ///  - 断开、写失败、异常、Stop / 重启都必须正确回收这些状态。
    ///
    /// 租期用**单调时钟**（TimeProvider）判断，墙上时间回拨不会让租期提前失效或一直有效。
    /// 状态全部由实例持有（不做全局静态时间戳），便于用假时钟做单元测试。
    /// </summary>
    public sealed class WebDemandState
    {
        /// <summary>HTTP 快照需求的默认租期（2 秒）。</summary>
        public static readonly TimeSpan DefaultHttpLease = TimeSpan.FromSeconds(2);

        private readonly object _gate = new object();
        private readonly TimeProvider _time;
        private readonly long _httpLeaseTicks;   // 租期长度（单调时钟刻度）

        private int _authenticatedConnections;
        private long _httpLeaseExpiry;          // 租期到期时刻（单调时钟刻度），0 = 没有租期

        /// <param name="timeProvider">可注入的单调时钟，默认 <see cref="TimeProvider.System"/>。</param>
        /// <param name="httpLease">HTTP 快照租期长度，默认 <see cref="DefaultHttpLease"/>。</param>
        public WebDemandState(TimeProvider? timeProvider = null, TimeSpan? httpLease = null)
        {
            _time = timeProvider ?? TimeProvider.System;

            TimeSpan lease = httpLease ?? DefaultHttpLease;
            if (lease < TimeSpan.Zero) lease = TimeSpan.Zero;

            long ticks = (long)Math.Round(_time.TimestampFrequency * lease.TotalSeconds);
            _httpLeaseTicks = ticks > 0 ? ticks : 0;
        }

        /// <summary>已鉴权且尚未关闭的 WebSocket 连接数。</summary>
        public int AuthenticatedConnectionCount
        {
            get { lock (_gate) return _authenticatedConnections; }
        }

        /// <summary>HTTP 快照租期是否仍然有效（已过期的租期会被顺手清掉）。</summary>
        public bool HasHttpLease
        {
            get { lock (_gate) return LeaseValidLocked(); }
        }

        /// <summary>当前是否存在合法的 Web 数据需求。</summary>
        public bool HasAnyDemand
        {
            get { lock (_gate) return _authenticatedConnections > 0 || LeaseValidLocked(); }
        }

        /// <summary>一次性读取全部需求状态（同一把锁内读取，连接数与租期状态不会撕裂）。</summary>
        public WebDemandSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                bool lease = LeaseValidLocked();
                return new WebDemandSnapshot(_authenticatedConnections, lease);
            }
        }

        /// <summary>登记一个已鉴权的 WebSocket 连接。</summary>
        public void AddAuthenticatedConnection()
        {
            lock (_gate) _authenticatedConnections++;
        }

        /// <summary>回收一个 WebSocket 连接（计数不会变成负数）。</summary>
        public void RemoveAuthenticatedConnection()
        {
            lock (_gate)
            {
                if (_authenticatedConnections > 0) _authenticatedConnections--;
            }
        }

        /// <summary>授予一次 HTTP 快照需求租期（从此刻起算，连续轮询会不断延长）。</summary>
        public void GrantHttpLease()
        {
            if (_httpLeaseTicks <= 0) return;
            lock (_gate)
            {
                _httpLeaseExpiry = _time.GetTimestamp() + _httpLeaseTicks;
            }
        }

        /// <summary>清空全部需求（服务 Stop / 重启时调用）。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _authenticatedConnections = 0;
                _httpLeaseExpiry = 0;
            }
        }

        /// <summary>租期是否有效（调用方必须已持有 _gate）。</summary>
        private bool LeaseValidLocked()
        {
            long expiry = _httpLeaseExpiry;
            if (expiry == 0) return false;
            if (_time.GetTimestamp() < expiry) return true;

            // 已过期：顺手清零，避免过期状态一直挂着
            _httpLeaseExpiry = 0;
            return false;
        }
    }

    /// <summary>
    /// 共享采样请求入口（计划 §4.4 / §6.1）。
    ///
    /// 语义：请求一次**限频**的真实硬件采集，返回 true 表示本次请求触发（或共享到）了一次真实采集。
    /// 生产实现 = <c>HardwareMonitor.Instance?.UpdateIfDueAsync(ct)</c>：
    /// 协调器保证"1 秒 1 次 + 并发合并"，因此多个网页 + HTTP 轮询**不会**叠加真实采集。
    /// </summary>
    public delegate Task<bool> WebSamplingRequester(CancellationToken cancellationToken);

    /// <summary>
    /// Web 需求 → 硬件采集需求 的同步器（计划 §4.4）。
    ///
    /// ★ 为什么需要它：Web 需求消失（最后一个连接关闭 / HTTP 租期过期）之后，
    ///   必须把 <c>SetWebDataDemand(false)</c> **及时**送达采集层，
    ///   否则硬件会继续按"全量普通指标"采集 —— 那正是本轮要省掉的 CPU。
    ///
    /// ★ 两条同步路径（缺一不可）：
    ///   1. **事件驱动**：连接增删 / HTTP 租期授予 / 停止清空 —— 立即同步（毫秒级）；
    ///   2. **周期监视**：HTTP 租期**到期**没有任何事件可挂靠，只能靠周期监视发现
    ///      （默认 200 毫秒，远小于 1 个采集周期，满足"≤1 采集周期 + 合理宽限"）。
    ///      ★ 不能只依赖广播周期同步：广播间隔可能 ≥ 2 秒（大于 HTTP 租期 2 秒），
    ///        那会让硬件在"已经没有需求"时继续全量采集。
    ///
    /// ★ 规则单一来源：需求值一律实时读取（<see cref="WebSessionManager.HasWebDataDemand"/> 或其等价来源），
    ///   绝不自己缓存另一份；写入目标由调用方提供。
    /// </summary>
    public sealed class WebDemandSynchronizer
    {
        /// <summary>需求监视的默认间隔（200 毫秒）。</summary>
        public static readonly TimeSpan DefaultMonitorInterval = TimeSpan.FromMilliseconds(200);

        private readonly Func<bool> _demandProvider;
        private readonly Func<bool, bool> _applyDemand;
        private readonly Func<object?>? _targetProvider;
        private readonly object _gate = new object();

        private bool _hasApplied;
        private bool _appliedValue;
        private object? _appliedTarget;
        private bool _running;
        private CancellationTokenSource? _monitorCts;

        /// <param name="demandProvider">需求真值来源（实时读取，线程安全由调用方保证）。</param>
        /// <param name="applyDemand">
        /// 把需求值写入采集层；返回 true 表示写入成功。
        /// 返回 false（例如采集层实例还不存在）时**不会**记账，下一轮监视会重试。
        /// </param>
        /// <param name="targetProvider">
        /// 写入目标的身份标识（通常是采集层实例）。目标变了（换了实例）时会重新写入一次，
        /// 避免新实例漏掉需求。为 null 表示不跟踪目标。
        /// </param>
        public WebDemandSynchronizer(
            Func<bool> demandProvider,
            Func<bool, bool> applyDemand,
            Func<object?>? targetProvider = null)
        {
            _demandProvider = demandProvider ?? throw new ArgumentNullException(nameof(demandProvider));
            _applyDemand = applyDemand ?? throw new ArgumentNullException(nameof(applyDemand));
            _targetProvider = targetProvider;
        }

        /// <summary>当前需求值（唯一真值来源，实时读取）。</summary>
        public bool CurrentDemand => _demandProvider();

        /// <summary>上次成功写入采集层的需求值（从未写入过时为 false）。</summary>
        public bool AppliedValue { get { lock (_gate) return _appliedValue; } }

        /// <summary>是否已经成功写入过采集层。</summary>
        public bool HasApplied { get { lock (_gate) return _hasApplied; } }

        /// <summary>
        /// 幂等同步一次：读取当前需求 → 需要时写入采集层。
        /// </summary>
        /// <returns>当前需求值（是否写入成功请看 <see cref="HasApplied"/>）。</returns>
        public bool SyncNow()
        {
            // ★ 读 + 比较 + 写必须在**同一把锁**里（次要#4）。
            //   原来"读在锁外、写在锁内"：事件线程（需求刚消失）与 200ms 监视线程各读各的，
            //   后读到 false 的线程可能**先**写 false，先读到 true 的线程**后**写 true
            //   → 陈旧的 true 落在 false 之后 → 采集层**瞬时过度采集**（≤200ms 内自愈，影响有界）。
            //   放进同一把锁后，"读到需求"与"写入需求"变成原子的一对，
            //   后写的一定是后读的 → 不可能再写出陈旧值。
            // ★ 锁内调用 _demandProvider()：它读的是 WebDemandState 的**另一把**锁，
            //   两把锁之间没有反向持有顺序（需求状态从不回调同步器）→ 不会死锁。
            lock (_gate)
            {
                bool demand = _demandProvider();
                object? target = _targetProvider?.Invoke();

                // 值没变、目标也没变 → 不重复写（写入是幂等的，但少写一次是一次）
                if (_hasApplied && _appliedValue == demand && ReferenceEquals(_appliedTarget, target))
                {
                    return demand;
                }

                bool applied = false;
                try
                {
                    applied = _applyDemand(demand);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Web 需求同步失败: " + ex.Message);
                }

                if (applied)
                {
                    _appliedValue = demand;
                    _appliedTarget = target;
                    _hasApplied = true;
                }
                return demand;
            }
        }

        /// <summary>
        /// 启动需求监视（周期检查）。用于发现"没有事件可挂靠"的需求消失 —— 例如 HTTP 租期到期。
        /// </summary>
        /// <param name="interval">监视间隔，默认 <see cref="DefaultMonitorInterval"/>。</param>
        public void Start(TimeSpan? interval = null)
        {
            TimeSpan period = interval ?? DefaultMonitorInterval;
            if (period <= TimeSpan.Zero) period = DefaultMonitorInterval;

            lock (_gate)
            {
                if (_running) return;
                _running = true;
            }

            var cts = new CancellationTokenSource();
            var old = Interlocked.Exchange(ref _monitorCts, cts);
            try { old?.Cancel(); } catch { }
            try { old?.Dispose(); } catch { }

            // ★ 先取出 token 再进任务体：cts.Token 在 lambda 执行时才求值，
            //   若任务体在 Start() 释放旧 CTS 之后才启动，会读到已释放的 CTS。
            var token = cts.Token;
            _ = Task.Run(() => MonitorLoopAsync(period, token));
        }

        /// <summary>停止需求监视（幂等）。</summary>
        public void Stop()
        {
            lock (_gate) { _running = false; }
            var cts = Interlocked.Exchange(ref _monitorCts, null);
            try { cts?.Cancel(); } catch { }
        }

        private async Task MonitorLoopAsync(TimeSpan interval, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    SyncNow();
                    await Task.Delay(interval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Stop 时的正常退出
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Web 需求监视循环异常: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 主会话记录（契约 W2）。
    /// </summary>
    /// <remarks>
    /// ★ 会话 ID 是 32 字节 CSPRNG 的 base64url —— **不可预测**、**不由口令派生**。
    /// ★ 口令指纹用于"改密 ⇒ 全部会话与票据立即撤销"：指纹一变，存量会话全部作废。
    /// </remarks>
    internal sealed class WebSessionRecord
    {
        /// <summary>会话 ID（base64url，无填充）。</summary>
        public string Id = "";

        /// <summary>创建时刻（单调时钟刻度）。</summary>
        public long CreatedTicks;

        /// <summary>最后使用时刻（单调时钟刻度）—— 空闲 TTL 依据它判定。</summary>
        public long LastUsedTicks;

        /// <summary>签发时的口令指纹。</summary>
        public string PasswordFingerprint = "";

        /// <summary>撤销标记。</summary>
        public bool Revoked;
    }

    /// <summary>
    /// 一次性票据记录（契约 W3）。
    /// </summary>
    internal sealed class WebTicketRecord
    {
        /// <summary>票据 ID（base64url，无填充）。</summary>
        public string Id = "";

        /// <summary>到期时刻（单调时钟刻度）。</summary>
        public long ExpiryTicks;

        /// <summary>是否已使用（一次性：用过即废）。</summary>
        public bool Used;

        /// <summary>申请该票据的主会话 ID（会话被撤销 ⇒ 票据一并作废）。</summary>
        public string SessionId = "";

        /// <summary>申请时的口令指纹（改密 ⇒ 票据立即作废）。</summary>
        public string PasswordFingerprint = "";
    }

    /// <summary>
    /// 解析后的 HTTP 请求（请求行 + 头）。
    /// </summary>
    /// <remarks>
    /// ★ 头名统一小写；同名头**保留全部取值**（重复头要能被识别出来并拒绝，
    ///   而不是"取第一个"—— 那正是请求走私 / 参数污染的温床）。
    /// </remarks>
    internal sealed class HttpRequestInfo
    {
        /// <summary>请求方法（GET / POST / OPTIONS …）。</summary>
        public string Method = "";

        /// <summary>请求目标（原始，含查询串）。</summary>
        public string Target = "";

        /// <summary>路径部分（不含查询串）。</summary>
        public string Path = "";

        /// <summary>查询串（不含 '?'）。</summary>
        public string Query = "";

        /// <summary>头集合：头名（小写）→ 全部取值。</summary>
        public readonly Dictionary<string, List<string>> Headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>取某个头的**唯一**取值；缺失或重复都返回 null（重复即视为非法）。</summary>
        public string GetSingleHeader(string name)
        {
            if (!Headers.TryGetValue(name, out var list)) return null;
            if (list.Count != 1) return null;      // 重复头 ⇒ 不猜
            return list[0];
        }

        /// <summary>取某个头的第一个取值（仅用于"存在性"判断，不用于鉴权取值）。</summary>
        public bool HasHeader(string name) => Headers.ContainsKey(name);
    }

    public class WebSessionManager
    {
        // ★ 值 = 该连接登记时的**运行代次**（不是无意义的 bool）：
        //   广播收件人必须与广播循环所属的服务代次绑定 ——
        //   旧代次的广播绝不能发给新代次（Stop → Start 之后才连上）的连接。
        private readonly ConcurrentDictionary<TcpClient, int> _wsClients = new();
        private readonly Action<Utf8JsonWriter> _dataProvider;
        private readonly Func<Utf8JsonWriter, CancellationToken, Task>? _asyncDataProvider;
        private readonly WebSamplingRequester? _samplingRequester;
        private readonly WebDemandState _demand;
        private readonly TimeProvider _time;

        // 连接集合与需求计数的联动锁：保证"加入连接"与"清空全部需求"不会交错出脏状态
        private readonly object _clientsGate = new object();

        // ===== 主会话 / 一次性票据（契约 W2 / W3）=====
        // ★ 全部状态在**进程内存**里：服务重启 ⇒ 全部会话与票据立即失效（天然如此，无需额外清理）。
        private readonly object _authGate = new object();
        private readonly Dictionary<string, WebSessionRecord> _sessions = new Dictionary<string, WebSessionRecord>();
        private readonly Dictionary<string, WebTicketRecord> _tickets = new Dictionary<string, WebTicketRecord>();

        // TTL 以单调时钟刻度表示（TimeProvider 可注入 ⇒ TTL 用例完全确定性）
        private readonly long _sessionAbsoluteTtlTicks;
        private readonly long _sessionIdleTtlTicks;
        private readonly long _ticketTtlTicks;

        /// <summary>
        /// 实际监听端口（由 <see cref="LiteWebServer"/> 在启动时写入）。
        /// </summary>
        /// <remarks>
        /// ★ Host / Origin 校验需要它：<c>Host: 127.0.0.1:5000</c> 里的端口必须与真实监听端口一致。
        ///   直接构造本类做测试时它是 null ⇒ 端口部分不做校验（主机名仍必须匹配）。
        /// </remarks>
        internal int? ListenPort { get; set; }

        private volatile bool _isRunning = false;
        private int _generation = 0;                       // Start/Stop 代次：用来区分 Stop 前后的广播任务
        private CancellationTokenSource? _broadcastCts;
        private CancellationTokenSource? _lifetimeCts;     // 服务级取消令牌：Stop 时让在途采样等待立刻退出
        private CancellationToken _lifetimeToken = CancellationToken.None;

        /// <summary>共享采样等待的超时（超时后按既有行为降级：照常序列化缓存值，不挂死请求线程）。</summary>
        public static readonly TimeSpan SamplingWaitTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// 读完整个请求头的期限：从这里开始计时，超时直接断开连接。
        /// </summary>
        /// <remarks>
        /// ★ 这个期限**不能**用 <c>NetworkStream.ReadTimeout</c> 实现。
        ///   那个属性只作用于**同步** <c>Read</c>；本类的读取全部是 <c>ReadAsync</c>，
        ///   异步路径根本不看它 —— 设置之后看起来"有防护"，实际一行防护也没有。
        /// ★ 没有真实期限时，慢速连接（slowloris：每次只发 1 个字节，永不发完 CRLFCRLF）
        ///   可以让读取循环无限期挂着，每条这样的连接长期占住一个任务 + 一块 8KB 池化缓冲。
        /// ★ 用取消令牌施加期限：链接服务级令牌，所以 Stop 也能立刻中断它。
        /// </remarks>
        private static readonly TimeSpan DefaultHeaderReadTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 读完整个请求头的期限（默认 <see cref="DefaultHeaderReadTimeout"/>）。
        /// </summary>
        /// <remarks>
        /// ★ 内部接缝（<c>internal</c>，**不属于公开 API**）：只为让测试把期限压到几百毫秒，
        ///   否则每条相关用例都得真的空等 5 秒。默认值就是生产行为。
        /// </remarks>
        internal TimeSpan HeaderReadTimeout { get; set; } = DefaultHeaderReadTimeout;

        /// <summary>
        /// 同步写入（HTTP 响应 / WebSocket 握手）的期限。
        /// </summary>
        /// <remarks>
        /// 与读取相反，<c>WriteTimeout</c> 对**同步** <c>Write</c> 是有效的，
        /// 而 <see cref="SendHttpResponse"/> / <see cref="Handshake"/> 用的正是同步写。
        /// 客户端只建连不读（接收窗口打满）时，没有这个期限就会一直阻塞在写调用里。
        /// </remarks>
        private const int StreamWriteTimeoutMs = 5000;

        /// <summary>
        /// 主会话的**绝对** TTL：30 分钟（到期即失效，与是否活跃无关）。
        /// </summary>
        /// <remarks>
        /// ★ 理由：会话凭据在浏览器里最长停留 30 分钟（Cookie 的 Max-Age 同值），
        ///   绝对上限让"被偷走的 Cookie"最多被用半小时。
        /// </remarks>
        internal static readonly TimeSpan SessionAbsoluteTtl = TimeSpan.FromMinutes(30);

        /// <summary>
        /// 主会话的**空闲** TTL：2 分钟（超过 2 分钟没有任何使用就失效）。
        /// </summary>
        /// <remarks>
        /// ★ 理由：网页版的正常节奏是"打开页面 → 一直看着"，快照每秒轮询一次，
        ///   每次轮询都会刷新空闲计时 ⇒ 只要有人在看就不会掉线；
        ///   而"登录完就走"的会话 2 分钟后就没了，缩小了凭据暴露窗口。
        ///   它比绝对 TTL 先到 ⇒ 先到者过期。
        /// </remarks>
        internal static readonly TimeSpan SessionIdleTtl = TimeSpan.FromMinutes(2);

        /// <summary>
        /// 一次性票据的 TTL：30 秒。
        /// </summary>
        /// <remarks>
        /// ★ 理由：票据的唯一用途是"紧接着的一次 WebSocket 握手"。
        ///   30 秒足够浏览器在本地完成握手（毫秒级），又足够短 ——
        ///   即使它出现在 URL / Referer / 代理日志里，也几乎不可能被复用来建第二条连接。
        /// </remarks>
        internal static readonly TimeSpan TicketTtl = TimeSpan.FromSeconds(30);

        /// <summary>主会话 Cookie 名。</summary>
        internal const string SessionCookieName = "lmsid";

        /// <summary>CSRF 头名。</summary>
        internal const string CsrfHeaderName = "X-LiteMonitor-CSRF";

        /// <summary>CSRF 令牌的固定消息串（契约 W13.5 冻结，前后端必须一致）。</summary>
        internal const string CsrfMessage = "liteMonitor-csrf-v1";

        /// <summary>请求体大小上限：4 KB（契约 W7）。</summary>
        internal const int MaxRequestBodyBytes = 4096;

        /// <summary>没有同步序列化回调时的占位实现（此时只会走可等待形态）。</summary>
        private static readonly Action<Utf8JsonWriter> NoSnapshotProvider = writer => { };

        /// <param name="dataProvider">快照序列化回调。</param>
        /// <param name="demandState">需求状态实例（默认新建一个）。</param>
        /// <param name="timeProvider">需求租期使用的单调时钟（默认 <see cref="TimeProvider.System"/>）。</param>
        public WebSessionManager(Action<Utf8JsonWriter> dataProvider, WebDemandState? demandState = null, TimeProvider? timeProvider = null)
            : this(dataProvider, null, demandState, timeProvider, null)
        {
        }

        /// <param name="asyncDataProvider">可等待的快照序列化回调（只负责序列化，不负责触发采集）。</param>
        /// <param name="demandState">需求状态实例（默认新建一个）。</param>
        /// <param name="timeProvider">需求租期与采样超时使用的单调时钟（默认 <see cref="TimeProvider.System"/>）。</param>
        /// <param name="samplingRequester">共享采样请求入口（默认 null = 不采样）。</param>
        public WebSessionManager(
            Func<Utf8JsonWriter, CancellationToken, Task> asyncDataProvider,
            WebDemandState? demandState = null,
            TimeProvider? timeProvider = null,
            WebSamplingRequester? samplingRequester = null)
            : this(null, asyncDataProvider, demandState, timeProvider, samplingRequester)
        {
        }

        private WebSessionManager(
            Action<Utf8JsonWriter>? dataProvider,
            Func<Utf8JsonWriter, CancellationToken, Task>? asyncDataProvider,
            WebDemandState? demandState,
            TimeProvider? timeProvider,
            WebSamplingRequester? samplingRequester)
        {
            if (dataProvider == null && asyncDataProvider == null)
            {
                throw new ArgumentException("必须提供快照序列化回调（同步或可等待形态任选其一）。", nameof(dataProvider));
            }

            _dataProvider = dataProvider ?? NoSnapshotProvider;
            _asyncDataProvider = asyncDataProvider;
            _samplingRequester = samplingRequester;
            _time = timeProvider ?? TimeProvider.System;
            _demand = demandState ?? new WebDemandState(timeProvider);

            // ★ TTL 换算成**单调时钟刻度**：会话 / 票据的到期判定与墙上时间无关，
            //   测试可以注入假时钟做到完全确定性（不靠 Thread.Sleep）。
            _sessionAbsoluteTtlTicks = ToTicks(SessionAbsoluteTtl);
            _sessionIdleTtlTicks = ToTicks(SessionIdleTtl);
            _ticketTtlTicks = ToTicks(TicketTtl);
        }

        /// <summary>把时长换算成当前单调时钟的刻度数。</summary>
        private long ToTicks(TimeSpan span)
        {
            if (span <= TimeSpan.Zero) return 0;
            double ticks = _time.TimestampFrequency * span.TotalSeconds;
            if (ticks <= 0) return 0;
            if (ticks >= long.MaxValue) return long.MaxValue;
            return (long)ticks;
        }

        /// <summary>Web 有效数据需求状态（线程安全）。</summary>
        public WebDemandState Demand => _demand;

        /// <summary>已鉴权且尚未关闭的 WebSocket 连接数。</summary>
        public int AuthenticatedConnectionCount => _demand.AuthenticatedConnectionCount;

        /// <summary>已鉴权的 HTTP 快照租期是否有效。</summary>
        public bool HasHttpLease => _demand.HasHttpLease;

        /// <summary>当前是否存在合法的 Web 数据需求（已鉴权连接或未过期的 HTTP 租期）。</summary>
        public bool HasWebDataDemand => _demand.HasAnyDemand;

        /// <summary>一次性读取需求状态（连接数 + 租期，不撕裂）。</summary>
        public WebDemandSnapshot GetDemandSnapshot() => _demand.GetSnapshot();

        /// <summary>
        /// 需求状态变化通知（连接增删 / HTTP 租期授予 / 停止清空）。
        ///
        /// ★ 订阅者必须**重新读取** <see cref="HasWebDataDemand"/>（唯一真值来源）再同步到采集层，
        ///   不要用通知里携带的旧值 —— 通知只表示"状态可能变了"。
        /// ★ 通知在内部锁之外触发，订阅者可以安全地调用本对象的任何读取接口。
        /// </summary>
        public Action? DemandChanged { get; set; }

        // Test-only synchronization points for HTTP lease lifecycle regressions.
        internal Action? HttpLeaseCheckTestHook { get; set; }
        internal Action? HttpLeaseGrantTestHook { get; set; }

        /// <summary>
        /// 当前运行代次（每次 Start / Stop 都会 +1）。
        /// 用来识别"跨越 Stop 边界"的连接与请求：它们不再登记为需求。
        /// </summary>
        public int Generation => Volatile.Read(ref _generation);

        public void Start()
        {
            if (_isRunning) return;

            // ★ 代次自增与"开始运行"必须在同一把锁里完成（B1 复审的顺序窗口）：
            //   连接登记（AddClient）也持有同一把锁。
            //   如果先置 _isRunning 再自增代次，中间窗口里到达的连接会带着**旧代次**被登记成功，
            //   于是"Stop 之后才连上的客户端"被当成有效需求（需求永久泄漏）。
            //   反过来（先自增代次再置 _isRunning）同样有窗口，只是方向相反。
            //   两者放进同一把锁后，连接登记要么整体发生在 Start 之前（被拒），
            //   要么整体发生在 Start 之后（代次一致，被接受），不再有中间态。
            int generation;
            lock (_clientsGate)
            {
                if (_isRunning) return;
                _isRunning = true;
                generation = Interlocked.Increment(ref _generation);
            }

            // ★ 用代次 + 取消令牌区分 Stop 前后的广播任务：
            //   快速 Stop/Start 后，旧的广播循环必须自己退出，不能复活成第二个广播者。
            var cts = new CancellationTokenSource();
            // 上一次的 CTS 已经被 Cancel 过（延迟任务已结束），这里才安全释放
            var old = Interlocked.Exchange(ref _broadcastCts, cts);
            try { old?.Dispose(); } catch { }

            // ★ 服务级取消令牌：Stop 时让在途的 HTTP 采样等待立刻退出
            var lifetime = new CancellationTokenSource();
            var oldLifetime = Interlocked.Exchange(ref _lifetimeCts, lifetime);
            // ★ 只 Cancel，**不 Dispose**（次要#6）：
            //   **事实描述**：退役的 CTS 不释放 —— 在途请求还持有 oldLifetime.Token
            //   （HandleClientAsync 入口捕获），不释放可以保证这个 token 不指向一个已释放的对象；
            //   释放交给 GC（CancellationTokenSource 无终结器，本路径也不碰 token.WaitHandle，
            //   不占非托管句柄）。
            //   与 _broadcastCts 在 Stop 路径上的处理一致：只取消，不释放。
            // ⚠ 不要在这里写"Dispose 会抛 ObjectDisposedException"之类的断言：
            //   .NET 8 上（目标框架 net8.0-windows / SDK 8.0.426）实测 4 种组合
            //   （Cancel→Dispose + CreateLinkedTokenSource / Dispose only + CreateLinkedTokenSource /
            //     Dispose only + token.Register / Cancel→Dispose + token.Register）
            //   **全部不抛**（CTS 先 Cancel → token 已取消 → 注册走"立即回调"分支）。
            //   所以"不释放"的收益是"不指向已释放对象"这个确定性，而不是防 ODE。
            try { oldLifetime?.Cancel(); } catch { }
            _lifetimeToken = lifetime.Token;

            // ★ 先把 token 取出来再进任务体：
            //   cts.Token 是在 lambda **执行时**才求值的，
            //   如果任务体在 Start() 释放旧 CTS 之后才启动，就会读到已释放的 CTS -> ObjectDisposedException。
            var token = cts.Token;
            Task.Run(() => BroadcastLoop(generation, token));
        }

        public void Stop()
        {
            // ★ 取消在途的采样等待（不等它自己超时）
            try { _lifetimeCts?.Cancel(); } catch { }
            _lifetimeToken = CancellationToken.None;

            lock (_clientsGate)
            {
                _isRunning = false;

                // 让旧广播循环立刻退出（代次 + 取消）
                Interlocked.Increment(ref _generation);

                foreach (var client in _wsClients.Keys)
                {
                    try { client.Close(); } catch { }
                }
                _wsClients.Clear();

                // ★ 回收全部旧需求：连接与 HTTP 租期都不留给下一次启动
                _demand.Clear();
            }

            // ★ 主会话与票据是**内存态**：服务停止 ⇒ 全部作废（契约 W2「服务重启即全部失效」）。
            //   否则一次重启前的 Cookie 还能继续用 —— 那与"重启即失效"的承诺不符。
            lock (_authGate)
            {
                _sessions.Clear();
                _tickets.Clear();
            }

            // 旧广播循环可能正拿着这个 token 收尾，交给下一次 Start() 释放，避免 ObjectDisposedException。
            try { _broadcastCts?.Cancel(); } catch { }

            // ★ 需求已归零：通知订阅者把 false 送达采集层
            NotifyDemandChanged();
        }

        /// <summary>
        /// 统一处理客户端连接（自动识别 WebSocket 或 HTTP）
        /// </summary>
        public async Task HandleClientAsync(TcpClient client)
        {
            // ★ 捕获本次连接的代次。
            //   Stop 之后才完成的握手 / 才到达的请求一律不再登记需求（见 AddClient / HandleHttpRequest）。
            int connectionGeneration = Volatile.Read(ref _generation);

            // ★ 服务级取消令牌：Stop 时取消，在途的采样等待立刻退出
            CancellationToken lifetimeToken = _lifetimeToken;

            byte[] buffer = null;
            try
            {
                var stream = client.GetStream();

                // ★ 写超时对**同步** Write 有效，而 SendHttpResponse / Handshake 正是同步写：
                //   客户端只建连不读、把发送窗口打满时，靠它结束阻塞。
                stream.WriteTimeout = StreamWriteTimeoutMs;

                // ★ 这里刻意**不**设置 stream.ReadTimeout：
                //   它只作用于同步 Read，对下面的 ReadAsync 完全无效。
                //   留着这行会让人误以为"已经有读取期限了"，真正的期限见 HeaderReadTimeout。

                // 1. 读取请求头 (最多 8KB)
                buffer = ArrayPool<byte>.Shared.Rent(8192);

                // ★ 真实读取期限：CancelAfter 到点即放弃，防 slowloris。
                //   作用域收紧到"读完请求头"为止：握手成功转入长连接后不再受这个期限约束
                //   （WebSocket 连接的寿命本来就由客户端决定，不该被握手期限杀掉）。
                // ★ 这里**刻意不**链接 lifetimeToken（服务级令牌）。
                //   Stop 的既有契约是"不再授予需求"，不是"掐断在途连接的响应" ——
                //   在途请求读完头之后由代次检查拒绝授予租期，但仍然得到完整响应；
                //   两条回归用例锁着它：`跨越Stop边界的快照请求不再授予租期`、
                //   `快速重启后在途HTTP请求仍能得到响应`。
                //   把期限挂在生命周期上会让 Stop 直接关连接，把"不该续租"错做成"不该回应"。
                //   期限只负责对抗慢速连接，与运行代次正交。
                int bytesRead;
                int headerEnd;
                using (var headerReadCts = new CancellationTokenSource(HeaderReadTimeout))
                {
                    (bytesRead, headerEnd) = await ReadHeaderAsync(stream, buffer, headerReadCts.Token);
                }

                if (bytesRead == 0) { client.Close(); return; }

                // ★ 请求头与请求体可能在**同一次读**里到达：头之后的字节属于 body，
                //   必须留给请求体读取路径（否则 body 会被丢掉，随后读取只能等到超时）。
                int headerLength = headerEnd > 0 ? headerEnd : bytesRead;
                string requestStr = Encoding.UTF8.GetString(buffer, 0, headerLength);
                byte[] leftoverBody = headerEnd > 0 && bytesRead > headerEnd
                    ? SliceOf(buffer, headerEnd, bytesRead - headerEnd)
                    : Array.Empty<byte>();

                // 2. 协议分流
                if (IsWebSocketUpgrade(requestStr))
                {
                    if (Handshake(stream, requestStr))
                    {
                        // 握手成功 = 已鉴权，登记为有效 Web 需求
                        AddClient(client, connectionGeneration);
                        await ReceiveLoop(client); // 进入 WS 循环
                    }
                    else
                    {
                        // ★ 被拒绝的握手**必须关闭连接**（LM-034）。
                        //   被拒的原因都在 <see cref="Handshake"/> 内部：Host/Origin 校验失败（403）、
                        //   旧 ?pwd= 协议（401）、票据无效（401）、Sec-WebSocket-Key 非法（400），
                        //   以及写响应时抛异常（Handshake 的 catch 分支）。
                        //   错误响应里已经带了 "Connection: close"，但服务端**不发 FIN** ——
                        //   连接就一直悬挂：实测 10 次被拒握手 ⇒ 10 条连接一直停在 ESTABLISHED，
                        //   服务端套接字不回收（连接额度被白占），客户端只能一直等到进程结束。
                        //   顺序不能变：**先写回错误响应（客户端要能看到 401/403/400），再关闭**。
                        //   关闭失败要留下痕迹（不能静默吞掉），否则这类泄漏又变成查不到原因的现象。
                        try { client.Close(); }
                        catch (Exception ex) { Debug.WriteLine("关闭被拒绝的 WebSocket 连接失败: " + ex.Message); }
                    }
                }
                else
                {
                    // ★ HTTP 路径改成可等待：鉴权 → 授予租期 → 共享采样 → 序列化 → 响应
                    await HandleHttpRequestAsync(stream, requestStr, leftoverBody, connectionGeneration, lifetimeToken).ConfigureAwait(false);
                    client.Close(); // HTTP 是短连接
                }
            }
            catch (OperationCanceledException)
            {
                // ★ 请求头读取超过 HeaderReadTimeout（slowloris / 半开连接）。
                //   这是**预期**的防御路径，不是故障 —— 刻意不让它落进下面的 catch(Exception)，
                //   否则每次超时都会打一条 "Client Error"，真实故障会被这种噪声淹没。
                RemoveClient(client);
                try { client.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Client Error: {ex.Message}");
                // 异常路径：连接可能已经登记过，必须回收需求
                RemoveClient(client);
                try { client.Close(); } catch { }
            }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // ==================================================================
        //  主会话 / 一次性票据 / CSRF（契约 W2 / W3 / W4）
        // ==================================================================

        /// <summary>
        /// 生成 32 字节 CSPRNG 的 base64url 令牌（无填充）。
        /// </summary>
        /// <remarks>
        /// ★ 会话 ID 与票据都走这里：不可预测（不是时间戳、不是自增、不是口令派生）。
        /// </remarks>
        private static string GenerateSecureToken() =>
            Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        /// <summary>base64url 编码（无填充，RFC 4648 §5）。</summary>
        private static string Base64UrlEncode(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>
        /// 由会话 ID 派生 CSRF 令牌：HMAC-SHA256，键 = 会话 ID，消息 = 固定串。
        /// </summary>
        /// <remarks>
        /// ★ 契约 W13.5 冻结的派生方式，服务端与前端必须一致。
        ///   键就是会话 ID ⇒ 攻击者拿不到会话 ID 就算不出令牌；
        ///   而会话 ID 在 HttpOnly Cookie 里，前端 JS 读不到 —— 所以令牌由登录响应一并下发。
        /// </remarks>
        internal static string ComputeCsrfToken(string sessionId)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sessionId ?? ""));
            return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(CsrfMessage)));
        }

        /// <summary>签发一个主会话。</summary>
        private WebSessionRecord CreateSession(string passwordFingerprint)
        {
            long now = _time.GetTimestamp();
            var record = new WebSessionRecord
            {
                Id = GenerateSecureToken(),
                CreatedTicks = now,
                LastUsedTicks = now,
                PasswordFingerprint = passwordFingerprint ?? ""
            };

            lock (_authGate)
            {
                _sessions[record.Id] = record;
                PruneSessionsLocked(now);
            }
            return record;
        }

        /// <summary>
        /// 取出一个仍然有效的主会话；无效时返回 false。
        /// </summary>
        /// <remarks>
        /// ★ 判定口径（HTTP 与 WS 完全一致）：撤销标记 / 绝对 TTL / 空闲 TTL / 口令指纹。
        /// ★ 口令指纹对不上 ⇒ <b>撤销全部</b>会话与票据（契约 W2「改密 ⇒ 全部撤销」）。
        /// </remarks>
        private bool TryGetSession(string sessionId, string passwordFingerprint, out WebSessionRecord record)
        {
            record = null!;
            if (string.IsNullOrEmpty(sessionId)) return false;

            bool revokeAll = false;
            lock (_authGate)
            {
                if (!_sessions.TryGetValue(sessionId, out var s)) return false;

                if (s.Revoked) { _sessions.Remove(sessionId); return false; }

                if (!string.Equals(s.PasswordFingerprint, passwordFingerprint ?? "", StringComparison.Ordinal))
                {
                    // ★ 口令变了（或被清空）⇒ 全部会话与票据立即作废
                    revokeAll = true;
                }
                else
                {
                    long now = _time.GetTimestamp();
                    if (_sessionAbsoluteTtlTicks > 0 && now - s.CreatedTicks >= _sessionAbsoluteTtlTicks)
                    {
                        _sessions.Remove(sessionId);
                        return false;      // 绝对 TTL 到期
                    }
                    if (_sessionIdleTtlTicks > 0 && now - s.LastUsedTicks >= _sessionIdleTtlTicks)
                    {
                        _sessions.Remove(sessionId);
                        return false;      // 空闲 TTL 到期
                    }

                    s.LastUsedTicks = now;    // 刷新空闲计时
                    record = s;
                }
            }

            if (revokeAll)
            {
                RevokeAllAuthState();
                return false;
            }
            return record != null;
        }

        /// <summary>显式注销一个会话。</summary>
        private void RevokeSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            lock (_authGate)
            {
                if (_sessions.TryGetValue(sessionId, out var s))
                {
                    s.Revoked = true;
                    _sessions.Remove(sessionId);
                }
            }
        }

        /// <summary>
        /// 撤销<b>全部</b>会话与票据，并断开所有已鉴权的 WebSocket 连接。
        /// </summary>
        /// <remarks>
        /// ★ 触发时机：口令变化（指纹对不上）或服务停止。
        /// ★ WS 连接必须一起断：否则"改密"之后旧连接还能继续收数据 —— 撤销就形同虚设。
        /// </remarks>
        private void RevokeAllAuthState()
        {
            lock (_authGate)
            {
                _sessions.Clear();
                _tickets.Clear();
            }

            // 断开已鉴权的 WS 连接（放在锁外：关闭 socket 可能触发回调）
            foreach (var client in _wsClients.Keys)
            {
                try { client.Close(); } catch { }
            }
            _wsClients.Clear();
            _demand.Clear();
            NotifyDemandChanged();
        }

        /// <summary>清理过期 / 已撤销的会话记录。</summary>
        private void PruneSessionsLocked(long now)
        {
            var expired = new List<string>();
            foreach (var kv in _sessions)
            {
                if (kv.Value.Revoked) { expired.Add(kv.Key); continue; }
                if (_sessionAbsoluteTtlTicks > 0 && now - kv.Value.CreatedTicks >= _sessionAbsoluteTtlTicks) { expired.Add(kv.Key); continue; }
                if (_sessionIdleTtlTicks > 0 && now - kv.Value.LastUsedTicks >= _sessionIdleTtlTicks) { expired.Add(kv.Key); }
            }
            foreach (string id in expired) _sessions.Remove(id);
        }

        /// <summary>签发一次性票据（30 秒有效、仅限 WS 握手、用过即废）。</summary>
        private string CreateTicket(string sessionId, string passwordFingerprint)
        {
            long now = _time.GetTimestamp();
            var ticket = new WebTicketRecord
            {
                Id = GenerateSecureToken(),
                ExpiryTicks = now + _ticketTtlTicks,
                SessionId = sessionId ?? "",
                PasswordFingerprint = passwordFingerprint ?? ""
            };

            lock (_authGate)
            {
                _tickets[ticket.Id] = ticket;

                // 顺手清掉过期票据
                var expired = new List<string>();
                foreach (var kv in _tickets)
                {
                    if (now >= kv.Value.ExpiryTicks) expired.Add(kv.Key);
                }
                foreach (string id in expired) _tickets.Remove(id);
            }
            return ticket.Id;
        }

        /// <summary>
        /// 消费一张票据（一次性）：有效则返回 true，并把票据<b>立即作废</b>。
        /// </summary>
        /// <remarks>
        /// ★ 过期 / 已用 / 口令指纹不匹配 / 申请它的会话已失效 ⇒ 一律拒绝。
        /// ★ 票据只用于 WS 握手；HTTP API 不接受它（路由层根本不看票据）。
        /// </remarks>
        private bool TryConsumeTicket(string ticketId, string passwordFingerprint)
        {
            if (string.IsNullOrEmpty(ticketId)) return false;

            lock (_authGate)
            {
                if (!_tickets.TryGetValue(ticketId, out var t)) return false;

                // ★ 一次性：查到即从表里移除 —— 同一个票据第二次使用必然查不到
                _tickets.Remove(ticketId);

                if (t.Used) return false;
                if (_time.GetTimestamp() >= t.ExpiryTicks) return false;
                if (!string.Equals(t.PasswordFingerprint, passwordFingerprint ?? "", StringComparison.Ordinal)) return false;

                // 申请它的主会话必须仍然有效（会话被注销 / 过期 ⇒ 票据一并作废）
                if (!string.IsNullOrEmpty(t.SessionId) && !SessionStillValidLocked(t.SessionId, passwordFingerprint))
                {
                    return false;
                }
                return true;
            }
        }

        /// <summary>（调用方必须已持有 _authGate）会话是否仍然有效。</summary>
        private bool SessionStillValidLocked(string sessionId, string passwordFingerprint)
        {
            if (!_sessions.TryGetValue(sessionId, out var s)) return false;
            if (s.Revoked) return false;
            if (!string.Equals(s.PasswordFingerprint, passwordFingerprint ?? "", StringComparison.Ordinal)) return false;
            long now = _time.GetTimestamp();
            if (_sessionAbsoluteTtlTicks > 0 && now - s.CreatedTicks >= _sessionAbsoluteTtlTicks) return false;
            if (_sessionIdleTtlTicks > 0 && now - s.LastUsedTicks >= _sessionIdleTtlTicks) return false;
            return true;
        }

        /// <summary>校验请求头里的 CSRF 令牌（恒时比较）。</summary>
        private static bool CheckCsrfHeader(HttpRequestInfo request, string sessionId)
        {
            string supplied = request.GetSingleHeader(CsrfHeaderName);
            if (string.IsNullOrEmpty(supplied)) return false;
            string expected = ComputeCsrfToken(sessionId);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(supplied),
                Encoding.UTF8.GetBytes(expected));
        }

        // --- 连接与需求的联动管理 ---

        /// <summary>
        /// 登记一个已鉴权的 WebSocket 连接（同时计入需求）。
        /// 与 <see cref="Stop"/> 的清空动作互斥，避免"刚 Clear 又被加回来"的脏状态。
        /// </summary>
        /// <param name="client">要登记的连接。</param>
        /// <param name="connectionGeneration">该连接建立时的运行代次（由 HandleClientAsync 捕获）。</param>
        private void AddClient(TcpClient client, int connectionGeneration)
        {
            bool registered = false;
            lock (_clientsGate)
            {
                // ★ Stop 之后不再登记连接。
                //   Stop 已经清空过集合，不会再遍历一次去 Close 这个客户端：
                //   如果这里仍然把它加回来，客户端会一直挂在 ReceiveLoop 上，
                //   需求计数长期为 1，硬件采集永远进不了空闲停止（§4.4「Stop 清理全部旧需求」）。
                if (!_isRunning || connectionGeneration != Volatile.Read(ref _generation))
                {
                    try { client.Close(); } catch { }
                    return;
                }

                // ★ 登记时把**该连接的运行代次**一起记下来：
                //   广播时用它过滤收件人（旧代次的广播不得发给新代次的连接）。
                if (_wsClients.TryAdd(client, connectionGeneration))
                {
                    _demand.AddAuthenticatedConnection();
                    registered = true;
                }
            }

            // ★ 通知在锁之外触发：订阅者要重新读取需求状态，不能被这里的锁挡住
            if (registered) NotifyDemandChanged();
        }

        /// <summary>
        /// 移除一个 WebSocket 连接并回收它的需求。
        /// 只有真正从集合里移除成功才减计数，重复移除不会把计数减成负数。
        /// </summary>
        private void RemoveClient(TcpClient client)
        {
            bool removed = false;
            lock (_clientsGate)
            {
                if (_wsClients.TryRemove(client, out _))
                {
                    _demand.RemoveAuthenticatedConnection();
                    removed = true;
                }
            }

            // ★ 需求减少时立刻通知（毫秒级回收，不等广播周期）
            if (removed) NotifyDemandChanged();
        }

        /// <summary>触发需求变化通知（异常不会影响需求状态本身）。</summary>
        private void NotifyDemandChanged()
        {
            try
            {
                DemandChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Web 需求变化通知失败: " + ex.Message);
            }
        }

        // --- 内部网络辅助方法 ---

        /// <summary>
        /// 逐段读取直到拿到完整的请求头（以空行结束），或缓冲写满、对端关闭。
        /// </summary>
        /// <param name="stream">连接流。</param>
        /// <param name="buffer">接收缓冲。</param>
        /// <param name="cancellationToken">
        /// 读取期限令牌。**必须**传：<c>ReadAsync</c> 会响应它，
        /// 而 <c>NetworkStream.ReadTimeout</c> 对异步读没有任何作用。
        /// </param>
        /// <returns>已读到的总字节数；0 表示对端在发任何数据之前就关闭了。</returns>
        private async Task<(int BytesRead, int HeaderEnd)> ReadHeaderAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            int totalBytes = 0;
            int headerEnd = -1;
            while (totalBytes < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, totalBytes, buffer.Length - totalBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;

                totalBytes += read;
                int found = FindHeaderEnd(buffer, totalBytes);
                if (found >= 0)
                {
                    // ★ 记录请求头长度：**头之后已经读到的字节属于请求体**，要留给 body 读取路径
                    headerEnd = found;
                    return (totalBytes, headerEnd);
                }
            }
            return (totalBytes, headerEnd);
        }

        /// <summary>截取一段字节（请求体残留）。</summary>
        private static byte[] SliceOf(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i < length - 3; i++)
            {
                if (buffer[i] == 13 && buffer[i+1] == 10 && buffer[i+2] == 13 && buffer[i+3] == 10)
                    return i + 4; // 返回包含结束符的长度
            }
            return -1;
        }

        private bool IsWebSocketUpgrade(string req)
        {
            return Regex.IsMatch(req, "GET", RegexOptions.IgnoreCase) && 
                   Regex.IsMatch(req, "Upgrade: websocket", RegexOptions.IgnoreCase);
        }

        // ==================================================================
        //  HTTP 路由（契约 W13.5 冻结的端点形态）
        // ==================================================================

        /// <summary>
        /// 处理一个 HTTP 请求。
        /// </summary>
        /// <remarks>
        /// ★ 处理顺序（契约 W8）：<b>先读完请求头 → 校验 Host/Origin → 完成鉴权 → 才决定要不要读请求体</b>。
        ///   未鉴权的请求**绝不**触发请求体读取（否则可被当作带宽 / 内存消耗放大器）。
        /// </remarks>
        private async Task HandleHttpRequestAsync(NetworkStream stream, string request, byte[] leftoverBody,
            int connectionGeneration, CancellationToken cancellationToken)
        {
            HttpRequestInfo info = ParseRequest(request);
            if (string.IsNullOrEmpty(info.Method)) return;

            WebAccessPolicy policy = BuildAccessPolicy();

            // ① 旧协议已移除：查询串里的 pwd 一律不再被接受（契约 W13 / W13.5）
            if (HasLegacyPasswordQuery(info))
            {
                SendErrorResponse(stream, 401, "查询串口令（?pwd=）已移除，请改用主会话登录。", info, policy);
                return;
            }

            // ② Host / Origin 校验（防 DNS Rebinding / 跨源读取）
            if (!IsHostAllowed(info, policy) || !IsOriginAllowed(info, policy))
            {
                SendErrorResponse(stream, 403, "Host / Origin 校验未通过。", info, policy);
                return;
            }

            // ③ OPTIONS 预检：走**同一套** Host / Origin 校验（上面已经过了）
            if (string.Equals(info.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                SendPreflightResponse(stream, info, policy);
                return;
            }

            switch (info.Path)
            {
                case "/api/auth/login":
                    await HandleLoginAsync(stream, info, policy, leftoverBody, cancellationToken).ConfigureAwait(false);
                    return;

                case "/api/auth/ticket":
                    await HandleTicketAsync(stream, info, policy).ConfigureAwait(false);
                    return;

                case "/api/auth/logout":
                    await HandleLogoutAsync(stream, info, policy).ConfigureAwait(false);
                    return;
            }

            string path = info.Path;
            if (path.StartsWith("/api/snapshot"))
            {
                await HandleSnapshotAsync(stream, info, policy, connectionGeneration, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/favicon.ico")
            {
                // 仅取图标：不产生硬件需求
                SendHttpResponse(stream, 404, "text/html", Array.Empty<byte>(), 0, info, policy);
                return;
            }

            // 默认返回首页（仅取 HTML：不产生硬件需求）
            SendIndexHtml(stream, info, policy);
        }

        /// <summary>首页（登录页）：不要求鉴权，但要过 Host / Origin 校验。</summary>
        private void SendIndexHtml(NetworkStream stream, HttpRequestInfo info, WebAccessPolicy policy)
        {
            string iconBase64 = WebPageContent.GetAppIconBase64();

            // ★ "是否需要口令"现在看**验证材料**（哈希 / 盐）而不是明文 ——
            //   迁移之后明文已不在内存里，但口令仍然有效，页面必须照样要求登录。
            bool authRequired = Settings.Load().HasWebPasswordConfigured();

            // ★ A11 挂载点：主题颜色快照 → CSS 变量。
            //   · 只**消费**UI 层发布的不可变快照（WebThemeColorPublisher.Current）：
            //     这里不解析主题、不读主题文件、不碰硬件；
            //   · 未发布快照时用写死的回退配色 → 页面外观与改造前一致；
            //   · 刻意**不推送、不轮询**：换主题后刷新浏览器即取到新配色。
            string themeCss = WebThemeCss.BuildCssVariables(WebThemeColorPublisher.Current);

            string html = WebPageContent.IndexHtml
                .Replace("{{FAVICON}}", !string.IsNullOrEmpty(iconBase64) ? $"<link rel='icon' type='image/png' href='data:image/png;base64,{iconBase64}'>" : "")
                .Replace("{{AUTH_REQUIRED}}", authRequired.ToString().ToLower())
                .Replace(WebPageContent.ThemeCssPlaceholder, themeCss);

            byte[] data = Encoding.UTF8.GetBytes(html);
            SendHttpResponse(stream, 200, "text/html; charset=utf-8", data, data.Length, info, policy);
        }

        /// <summary>
        /// <c>POST /api/auth/login</c>：口令换主会话。
        /// </summary>
        /// <remarks>
        /// ★ 成功：200 + <c>Set-Cookie: lmsid=…; HttpOnly; SameSite=Strict; Path=/; Max-Age=1800</c>
        ///   + 体 <c>{"csrf":"…"}</c>（CSRF 令牌由登录响应下发 —— 前端读不到 HttpOnly Cookie）。
        /// ★ 失败：401（口令错误）/ 400（体不合规）/ 403（Host、Origin 不通过，已在上面处理）。
        /// </remarks>
        private async Task HandleLoginAsync(NetworkStream stream, HttpRequestInfo info, WebAccessPolicy policy, byte[] leftoverBody, CancellationToken cancellationToken)
        {
            if (!string.Equals(info.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                SendErrorResponse(stream, 405, "只接受 POST。", info, policy);
                return;
            }

            var cfg = Settings.Load();

            // ★ L0（未配置口令）：**没有可校验的凭据**，但协议口径保持统一 ——
            //   仍然签发主会话与 CSRF 令牌，后续的票据 / 注销流程与 L1 完全一致。
            //   （契约所有者裁定：L0 下以 {"password":""} 登录必须成功 ——
            //     "空口令"就是"无口令"，没有可拒绝的理由；而若 L0 让 HTTP API 无会话直通，
            //     就会形成第二套鉴权口径。）
            if (!cfg.HasWebPasswordConfigured())
            {
                WebSessionRecord l0Session = CreateSession(cfg.GetWebPasswordFingerprint());
                SendLoginSuccess(stream, l0Session, info, policy);
                return;
            }

            // ★ 请求体规则（契约 W7）—— 鉴权已完成（Host/Origin 通过），此时才允许读 body
            byte[]? body = await ReadJsonBodyAsync(stream, info, leftoverBody, cancellationToken).ConfigureAwait(false);
            if (body == null) return;   // 错误响应已发出

            if (!TryParseLoginBody(body, out string password))
            {
                SendErrorResponse(stream, 400, "请求体不合法（必须是 {\"password\":\"…\"}）。", info, policy);
                return;
            }

            // ★ 惰性迁移：旧配置里还有明文 ⇒ 首次校验时把哈希 + 盐写进磁盘，并清掉备份里的明文。
            //   迁移失败 ⇒ **明确报错**，绝不降级成"空口令"（那等于把服务敞开）。
            if (!string.IsNullOrEmpty(cfg.WebServerPassword) && !cfg.EnsureWebPasswordMigrated())
            {
                SendErrorResponse(stream, 500, "口令迁移失败（无法写入新配置），请检查配置目录权限。", info, policy);
                return;
            }

            if (!cfg.VerifyWebPassword(password))
            {
                SendErrorResponse(stream, 401, "口令错误。", info, policy);
                return;
            }

            WebSessionRecord session = CreateSession(cfg.GetWebPasswordFingerprint());
            SendLoginSuccess(stream, session, info, policy);
        }

        /// <summary>
        /// 登录成功：签发主会话 Cookie + 下发 CSRF 令牌。
        /// </summary>
        /// <remarks>
        /// ★ <c>Set-Cookie: lmsid=…; HttpOnly; SameSite=Strict; Path=/; Max-Age=1800</c>
        ///   —— 不加 <c>Secure</c>：本机是 HTTP，加了浏览器就不回传（功能直接坏掉）。
        /// ★ CSRF 令牌必须随登录响应下发：HttpOnly 的 Cookie 前端 JS 读不到。
        /// </remarks>
        private void SendLoginSuccess(NetworkStream stream, WebSessionRecord session, HttpRequestInfo info, WebAccessPolicy policy)
        {
            string csrf = ComputeCsrfToken(session.Id);
            string cookie = $"{SessionCookieName}={session.Id}; HttpOnly; SameSite=Strict; Path=/; Max-Age={SessionAbsoluteTtl.TotalSeconds:0}";
            byte[] payload = Encoding.UTF8.GetBytes("{\"csrf\":\"" + csrf + "\"}");
            SendHttpResponse(stream, 200, "application/json; charset=utf-8", payload, payload.Length, info, policy,
                new List<string> { "Set-Cookie: " + cookie });
        }

        /// <summary>
        /// <c>POST /api/auth/ticket</c>：用主会话换一次性票据（仅可用于 WS 握手）。
        /// </summary>
        private async Task HandleTicketAsync(NetworkStream stream, HttpRequestInfo info, WebAccessPolicy policy)
        {
            if (!string.Equals(info.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                SendErrorResponse(stream, 405, "只接受 POST。", info, policy);
                return;
            }

            string? sessionId = GetSessionCookie(info);
            if (sessionId == null)
            {
                SendErrorResponse(stream, 401, "需要有效的主会话。", info, policy);
                return;
            }

            if (!TryGetSession(sessionId, policy.PasswordFingerprint, out WebSessionRecord session))
            {
                SendErrorResponse(stream, 401, "主会话无效或已过期。", info, policy);
                return;
            }

            if (!CheckCsrfHeader(info, session.Id))
            {
                SendErrorResponse(stream, 403, "CSRF 校验失败。", info, policy);
                return;
            }

            // ★ 该端点不接受请求体：这里**不读** body（也省掉一次读期限）
            string ticket = CreateTicket(session.Id, policy.PasswordFingerprint);
            byte[] payload = Encoding.UTF8.GetBytes("{\"ticket\":\"" + ticket + "\"}");
            SendHttpResponse(stream, 200, "application/json; charset=utf-8", payload, payload.Length, info, policy);
        }

        /// <summary>
        /// <c>POST /api/auth/logout</c>：显式注销当前主会话。
        /// </summary>
        private async Task HandleLogoutAsync(NetworkStream stream, HttpRequestInfo info, WebAccessPolicy policy)
        {
            if (!string.Equals(info.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                SendErrorResponse(stream, 405, "只接受 POST。", info, policy);
                return;
            }

            string? sessionId = GetSessionCookie(info);
            if (sessionId == null)
            {
                SendErrorResponse(stream, 401, "需要有效的主会话。", info, policy);
                return;
            }

            if (!TryGetSession(sessionId, policy.PasswordFingerprint, out WebSessionRecord session))
            {
                SendErrorResponse(stream, 401, "主会话无效或已过期。", info, policy);
                return;
            }

            if (!CheckCsrfHeader(info, session.Id))
            {
                SendErrorResponse(stream, 403, "CSRF 校验失败。", info, policy);
                return;
            }

            RevokeSession(session.Id);

            // ★ 清 Cookie：Max-Age=0 让浏览器立即丢弃（Path 必须与签发时一致）
            string clear = $"{SessionCookieName}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict";
            SendHttpResponse(stream, 204, "application/json; charset=utf-8", Array.Empty<byte>(), 0, info, policy,
                new List<string> { "Set-Cookie: " + clear });
        }

        /// <summary>
        /// <c>GET /api/snapshot</c>：需要主会话（L0 模式除外）。
        /// </summary>
        private async Task HandleSnapshotAsync(NetworkStream stream, HttpRequestInfo info, WebAccessPolicy policy, int connectionGeneration, CancellationToken cancellationToken)
        {
            if (!string.Equals(info.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                SendErrorResponse(stream, 405, "只接受 GET。", info, policy);
                return;
            }

            // ★ 主会话在**任何模式**下都是必需的（L0 也一样）：
            //   "没有口令"不等于"没有会话" —— 否则 HTTP API 会出现第二套鉴权口径。
            string? sessionId = GetSessionCookie(info);
            if (sessionId == null)
            {
                SendErrorResponse(stream, 401, "需要有效的主会话。", info, policy);
                return;
            }
            if (!TryGetSession(sessionId, policy.PasswordFingerprint, out _))
            {
                SendErrorResponse(stream, 401, "主会话无效或已过期。", info, policy);
                return;
            }

            // ★ 已鉴权的快照请求获得一个短需求租期（默认 2 秒）。
            //   连续 HTTP 轮询会不断延长租期；未鉴权请求 / HTML / favicon 都不会走到这里。
            //   跨越 Stop 边界的在途请求不再授予租期：Stop 已经清空过全部需求，
            //   再补一个 2 秒租期等于把已停止的服务又变成"有需求"。
            //   ★ 授权点必须在**采样之前**：等待采样期间不能没有需求。
            HttpLeaseCheckTestHook?.Invoke();

            bool leaseGranted = false;
            lock (_clientsGate)
            {
                if (_isRunning && connectionGeneration == Volatile.Read(ref _generation))
                {
                    HttpLeaseGrantTestHook?.Invoke();
                    _demand.GrantHttpLease();
                    leaseGranted = true;
                }
            }

            // Keep notifications outside the lifecycle lock.
            if (leaseGranted) NotifyDemandChanged();

            // ★ 共享采样（限频）：HTTP 轮询与网页广播走**同一条**限频采样路径，
            //   协调器保证 1 秒 1 次 + 并发合并 → 多个网页 + 轮询不会叠加真实采集。
            //   快照本身**不执行** hw.Update()，这里只是请求一次共享采样。
            await RequestSharedSamplingWithTimeoutAsync(cancellationToken).ConfigureAwait(false);

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            await WriteSnapshotAsync(writer, cancellationToken).ConfigureAwait(false);
            writer.Flush();
            SendHttpResponse(stream, 200, "application/json", ms.GetBuffer(), (int)ms.Length, info, policy);
        }

        // ==================================================================
        //  请求解析 / 访问边界（契约 W5 / W6 / W7）
        // ==================================================================

        /// <summary>
        /// 解析请求行与请求头。
        /// </summary>
        private static HttpRequestInfo ParseRequest(string raw)
        {
            var info = new HttpRequestInfo();
            string[] lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            string first = lines.Length > 0 ? lines[0].TrimEnd('\r') : "";
            if (first.Length == 0) return info;

            string[] parts = first.Split(' ');
            if (parts.Length >= 2)
            {
                info.Method = parts[0];
                info.Target = parts[1];
            }

            int q = info.Target.IndexOf('?');
            if (q >= 0)
            {
                info.Path = info.Target.Substring(0, q);
                info.Query = info.Target.Substring(q + 1);
            }
            else
            {
                info.Path = info.Target;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0) break;          // 空行 = 头块结束
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;             // 畸形行：忽略（不猜）

                string name = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                string value = lines[i].Substring(colon + 1).Trim();
                if (name.Length == 0) continue;

                if (!info.Headers.TryGetValue(name, out var list))
                {
                    list = new List<string>();
                    info.Headers[name] = list;
                }
                list.Add(value);
            }

            return info;
        }

        /// <summary>查询串里是否还带着旧协议的 pwd 参数（已移除 ⇒ 一律拒绝）。</summary>
        private static bool HasLegacyPasswordQuery(HttpRequestInfo info)
        {
            foreach (var pair in ParseQueryPairs(info.Query))
            {
                if (string.Equals(pair.Key, "pwd", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>解析查询串（键值都做 URL 解码；同名参数全部保留）。</summary>
        private static List<KeyValuePair<string, string>> ParseQueryPairs(string query)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(query)) return result;

            foreach (string part in query.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string rawName = eq >= 0 ? part.Substring(0, eq) : part;
                string rawValue = eq >= 0 ? part.Substring(eq + 1) : "";
                result.Add(new KeyValuePair<string, string>(UrlDecode(rawName), UrlDecode(rawValue)));
            }
            return result;
        }

        /// <summary>URL 解码（沿用既有约定：<c>Uri.UnescapeDataString</c>，裸 '+' 不当空格）。</summary>
        private static string UrlDecode(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try { return Uri.UnescapeDataString(value); }
            catch (ArgumentException) { return ""; }
            catch (UriFormatException) { return ""; }
        }

        /// <summary>
        /// 取查询串里某个参数的**唯一**取值；缺失或重复都返回 null。
        /// </summary>
        /// <remarks>
        /// ★ 重复参数一律拒绝（参数污染）：不猜"取第一个还是最后一个"。
        /// </remarks>
        private static string? GetSingleQueryValue(HttpRequestInfo info, string name)
        {
            string? found = null;
            foreach (var pair in ParseQueryPairs(info.Query))
            {
                if (!string.Equals(pair.Key, name, StringComparison.Ordinal)) continue;
                if (found != null) return null;      // ★ 重复 ⇒ 拒绝
                found = pair.Value;
            }
            return found;
        }

        /// <summary>
        /// 取出会话 Cookie 的值；**重复**的 lmsid 一律视为非法（返回 null）。
        /// </summary>
        /// <remarks>
        /// ★ 契约 W2：重复参数一律拒绝，不猜"取第一个还是最后一个"（参数污染）。
        /// </remarks>
        private static string? GetSessionCookie(HttpRequestInfo info)
        {
            string? cookieHeader = info.GetSingleHeader("cookie");
            if (string.IsNullOrEmpty(cookieHeader)) return null;

            string? found = null;
            foreach (string part in cookieHeader.Split(';'))
            {
                string item = part.Trim();
                if (item.Length == 0) continue;
                int eq = item.IndexOf('=');
                string name = eq >= 0 ? item.Substring(0, eq).Trim() : item;
                if (!string.Equals(name, SessionCookieName, StringComparison.Ordinal)) continue;

                if (found != null) return null;      // ★ 重复 lmsid ⇒ 拒绝
                found = eq >= 0 ? item.Substring(eq + 1).Trim() : "";
            }
            return found;
        }

        /// <summary>
        /// 读取并校验请求体（契约 W7）。
        /// </summary>
        /// <remarks>
        /// ★ 规则：必须有 Content-Length（没有 ⇒ chunked ⇒ 拒绝）；超过 4 KB ⇒ **直接拒绝且不读完**；
        ///   Content-Type 必须是 application/json（charset 必须是 UTF-8）；
        ///   读取期限**复用** 5 秒请求头期限（不是新值）。
        /// ★ 失败时把错误响应发出并返回 null。
        /// </remarks>
        private async Task<byte[]?> ReadJsonBodyAsync(NetworkStream stream, HttpRequestInfo info, byte[] leftoverBody, CancellationToken cancellationToken)
        {
            // ① Transfer-Encoding: chunked ⇒ 不允许流式上传
            if (info.HasHeader("transfer-encoding"))
            {
                SendErrorResponse(stream, 400, "不支持分块传输（必须有 Content-Length）。", info, BuildAccessPolicy());
                return null;
            }

            string? contentLengthRaw = info.GetSingleHeader("content-length");
            if (contentLengthRaw == null)
            {
                SendErrorResponse(stream, 400, "缺少 Content-Length。", info, BuildAccessPolicy());
                return null;
            }
            if (!int.TryParse(contentLengthRaw, out int contentLength) || contentLength < 0)
            {
                SendErrorResponse(stream, 400, "Content-Length 非法。", info, BuildAccessPolicy());
                return null;
            }

            // ② 超过上限 ⇒ 直接拒绝，**不读** body（避免被当作放大器）
            if (contentLength > MaxRequestBodyBytes)
            {
                SendErrorResponse(stream, 400, $"请求体超过上限（{MaxRequestBodyBytes} 字节）。", info, BuildAccessPolicy());
                return null;
            }

            // ③ Content-Type 必须是 application/json（charset 只能是 UTF-8）
            string? contentType = info.GetSingleHeader("content-type");
            if (!IsJsonContentType(contentType))
            {
                SendErrorResponse(stream, 400, "Content-Type 必须是 application/json（charset=utf-8）。", info, BuildAccessPolicy());
                return null;
            }

            if (contentLength == 0)
            {
                SendErrorResponse(stream, 400, "请求体为空。", info, BuildAccessPolicy());
                return null;
            }

            // ④ 读取：期限复用请求头期限（5 秒）
            //    ★ 先用"读请求头时顺带读到的残留字节"填满开头 —— 客户端常常把头与体
            //      一次写出去，那些字节已经在缓冲里了，不能再去 socket 里等（等不到）。
            byte[] buffer = new byte[contentLength];
            int total = 0;
            if (leftoverBody != null && leftoverBody.Length > 0)
            {
                int take = Math.Min(leftoverBody.Length, contentLength);
                Buffer.BlockCopy(leftoverBody, 0, buffer, 0, take);
                total = take;
            }

            using (var readCts = new CancellationTokenSource(HeaderReadTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(readCts.Token, cancellationToken))
            {
                try
                {
                    while (total < contentLength)
                    {
                        int read = await stream.ReadAsync(buffer, total, contentLength - total, linked.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        total += read;
                    }
                }
                catch (OperationCanceledException)
                {
                    SendErrorResponse(stream, 408, "读取请求体超时。", info, BuildAccessPolicy());
                    return null;
                }
                catch (IOException)
                {
                    return null;   // 连接已断：不用再回响应
                }
            }

            if (total != contentLength)
            {
                SendErrorResponse(stream, 400, "请求体长度与 Content-Length 不一致。", info, BuildAccessPolicy());
                return null;
            }

            return buffer;
        }

        /// <summary>Content-Type 是否为 application/json（UTF-8）。</summary>
        private static bool IsJsonContentType(string? contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return false;

            string[] parts = contentType.Split(';');
            string media = parts[0].Trim().ToLowerInvariant();
            if (media != "application/json") return false;

            for (int i = 1; i < parts.Length; i++)
            {
                string param = parts[i].Trim();
                int eq = param.IndexOf('=');
                if (eq <= 0) continue;
                string name = param.Substring(0, eq).Trim().ToLowerInvariant();
                string value = param.Substring(eq + 1).Trim().Trim('"').ToLowerInvariant();
                if (name == "charset" && value != "utf-8" && value != "utf8") return false;
            }
            return true;
        }

        /// <summary>
        /// 严格解析登录体：只接受 <c>{"password":"…"}</c>，重复字段一律拒绝。
        /// </summary>
        /// <remarks>
        /// ★ 重复 JSON 字段必须拒绝（契约 W7）：取"第一个"还是"最后一个"都会产生歧义，
        ///   而解析器与校验器取到不同的那个，就等于"用一个口令过校验、用另一个口令干活"。
        /// ★ 解析失败**不**把异常细节原样回给客户端。
        /// </remarks>
        private static bool TryParseLoginBody(byte[] body, out string password)
        {
            password = "";
            try
            {
                var reader = new Utf8JsonReader(body);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

                bool seen = false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) return seen;

                    if (reader.TokenType != JsonTokenType.PropertyName) return false;
                    string name = reader.GetString() ?? "";
                    if (!reader.Read()) return false;

                    if (string.Equals(name, "password", StringComparison.Ordinal))
                    {
                        if (seen) return false;                 // ★ 重复字段 ⇒ 拒绝
                        if (reader.TokenType != JsonTokenType.String) return false;
                        password = reader.GetString() ?? "";
                        seen = true;
                    }
                    else
                    {
                        // 其它字段：跳过（不猜其结构），但同名重复仍然致命
                        if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
                        {
                            reader.Skip();
                        }
                    }
                }
                return false;
            }
            catch (JsonException)
            {
                return false;   // ★ 不把异常细节回给客户端
            }
        }

        /// <summary>
        /// 构造当前的访问边界策略（Host / Origin 白名单 + 口令状态）。
        /// </summary>
        private WebAccessPolicy BuildAccessPolicy()
        {
            Settings cfg = Settings.Load();
            var policy = new WebAccessPolicy();

            policy.PasswordRequired = cfg.HasWebPasswordConfigured();
            policy.PasswordFingerprint = cfg.GetWebPasswordFingerprint();
            policy.TrustProxyHeaders = cfg.WebServerTrustProxyHeaders;

            // 本机地址始终允许（契约 W5）
            policy.AllowedHosts.Add("127.0.0.1");
            policy.AllowedHosts.Add("localhost");
            policy.AllowedHosts.Add("::1");

            // 局域网模式：额外允许用户配置的地址
            if (cfg.WebServerBindMode == WebServerBindMode.Lan)
            {
                foreach (string addr in SplitConfigList(cfg.WebServerLanAddresses))
                {
                    string host = NormalizeHost(addr);
                    if (host.Length > 0) policy.AllowedHosts.Add(host);
                }
            }

            // 显式配置的跨源（精确匹配）
            foreach (string origin in SplitConfigList(cfg.WebServerAllowedOrigins))
            {
                string o = origin.Trim().TrimEnd('/');
                if (o.Length > 0) policy.AllowedOrigins.Add(o);
            }

            return policy;
        }

        /// <summary>Host 头是否在允许集合内（防 DNS Rebinding）。</summary>
        private bool IsHostAllowed(HttpRequestInfo info, WebAccessPolicy policy)
        {
            string? hostHeader = info.GetSingleHeader("host");
            if (string.IsNullOrEmpty(hostHeader)) return false;      // 没有 Host ⇒ 拒绝

            SplitHostPort(hostHeader, out string host, out int? port);
            host = NormalizeHost(host);
            if (host.Length == 0) return false;
            if (!policy.AllowedHosts.Contains(host)) return false;

            // 端口必须与实际监听端口一致（端口未知时不校验端口，只校验主机名）
            if (port.HasValue && ListenPort.HasValue && port.Value != ListenPort.Value) return false;

            return true;
        }

        /// <summary>Origin 头是否允许（缺失 ⇒ 允许；"null" ⇒ 拒绝；不在集合 ⇒ 拒绝）。</summary>
        private bool IsOriginAllowed(HttpRequestInfo info, WebAccessPolicy policy)
        {
            string? origin = info.GetSingleHeader("origin");
            if (string.IsNullOrEmpty(origin)) return true;      // 非浏览器客户端 ⇒ 允许（Host 仍须通过）
            if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase)) return false;

            // 显式配置的跨源：精确匹配
            if (policy.AllowedOrigins.Contains(origin.Trim().TrimEnd('/'))) return true;

            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)) return false;
            string scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https") return false;

            string host = NormalizeHost(uri.Host);
            if (host.Length == 0) return false;
            if (!policy.AllowedHosts.Contains(host)) return false;

            if (ListenPort.HasValue && uri.Port != ListenPort.Value) return false;
            return true;
        }

        /// <summary>拆分 Host 头的主机名与端口。</summary>
        private static void SplitHostPort(string value, out string host, out int? port)
        {
            host = "";
            port = null;
            string v = (value ?? "").Trim();
            if (v.Length == 0) return;

            if (v.StartsWith("[", StringComparison.Ordinal))
            {
                int close = v.IndexOf(']');
                if (close > 0)
                {
                    host = v.Substring(1, close - 1);
                    if (close + 1 < v.Length && v[close + 1] == ':' &&
                        int.TryParse(v.Substring(close + 2), out int p))
                    {
                        port = p;
                    }
                    return;
                }
            }

            int colon = v.LastIndexOf(':');
            if (colon > 0 && v.IndexOf(':') == colon)
            {
                host = v.Substring(0, colon);
                if (int.TryParse(v.Substring(colon + 1), out int p2)) port = p2;
                return;
            }

            host = v;
        }

        /// <summary>主机名归一化（小写、去 IPv6 方括号、去掉端口）。</summary>
        private static string NormalizeHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return "";
            string h = host.Trim().ToLowerInvariant();
            if (h.StartsWith("[", StringComparison.Ordinal) && h.EndsWith("]", StringComparison.Ordinal))
            {
                h = h.Substring(1, h.Length - 2);
            }
            return h.Trim();
        }

        /// <summary>按逗号 / 分号 / 换行拆分配置列表。</summary>
        private static IEnumerable<string> SplitConfigList(string? value)
        {
            if (string.IsNullOrEmpty(value)) yield break;
            foreach (string part in value.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (p.Length > 0) yield return p;
            }
        }

        /// <summary>
        /// 访问边界策略（一次请求的快照）。
        /// </summary>
        private sealed class WebAccessPolicy
        {
            /// <summary>是否配置了口令（false = L0 无凭据模式）。</summary>
            public bool PasswordRequired;

            /// <summary>当前口令指纹（改密 ⇒ 全部会话 / 票据撤销）。</summary>
            public string PasswordFingerprint = "";

            /// <summary>允许的主机名集合。</summary>
            public readonly HashSet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>显式允许的跨源（精确匹配）。</summary>
            public readonly HashSet<string> AllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>是否信任转发头（降级开关，默认 false）。</summary>
            public bool TrustProxyHeaders;
        }

        /// <summary>
        /// 以恒定时间比较两个字符串的 UTF-8 编码。
        /// </summary>
        /// <remarks>
        /// <see cref="CryptographicOperations.FixedTimeEquals"/> 的耗时只取决于长度，
        /// 与"从第几个字节开始不同"无关 —— 这正是逐位试错所需的信息。
        /// </remarks>
        private static bool FixedTimeEqualsUtf8(string left, string right)
        {
            byte[] leftBytes = Encoding.UTF8.GetBytes(left);
            byte[] rightBytes = Encoding.UTF8.GetBytes(right);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        /// <summary>
        /// 发送 HTTP 响应。
        /// </summary>
        /// <remarks>
        /// ★ **不再**返回 <c>Access-Control-Allow-Origin: *</c>（契约 W6）：
        ///   默认不返回任何 CORS 头；只有请求的 Origin 命中用户显式配置的跨源白名单时，
        ///   才回显**具体的**那个源并附带 <c>Access-Control-Allow-Credentials: true</c>。
        /// </remarks>
        private void SendHttpResponse(NetworkStream stream, int code, string type, byte[] body, int len,
                                      HttpRequestInfo? request = null, WebAccessPolicy? policy = null,
                                      List<string>? extraHeaders = null)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(GetReasonPhrase(code)).Append("\r\n");
            sb.Append("Content-Type: ").Append(type).Append("\r\n");
            sb.Append("Content-Length: ").Append(len).Append("\r\n");
            if (extraHeaders != null)
            {
                foreach (string h in extraHeaders) sb.Append(h).Append("\r\n");
            }
            AppendCorsHeaders(sb, request, policy);
            sb.Append("Connection: close\r\n\r\n");

            byte[] hBytes = Encoding.UTF8.GetBytes(sb.ToString());
            stream.Write(hBytes, 0, hBytes.Length);
            if (len > 0) stream.Write(body, 0, len);
        }

        /// <summary>错误响应（统一形态：短文本 + 明确状态码）。</summary>
        private void SendErrorResponse(NetworkStream stream, int code, string message,
                                       HttpRequestInfo? request = null, WebAccessPolicy? policy = null)
        {
            byte[] data = Encoding.UTF8.GetBytes(message ?? "");
            SendHttpResponse(stream, code, "text/plain; charset=utf-8", data, data.Length, request, policy);
        }

        /// <summary>
        /// OPTIONS 预检响应：走与普通请求**同一套** Host / Origin 校验（已在路由前完成）。
        /// </summary>
        private void SendPreflightResponse(NetworkStream stream, HttpRequestInfo request, WebAccessPolicy policy)
        {
            var extra = new List<string>
            {
                "Access-Control-Allow-Methods: GET, POST, OPTIONS",
                "Access-Control-Allow-Headers: Content-Type, X-LiteMonitor-CSRF"
            };
            SendHttpResponse(stream, 204, "text/plain; charset=utf-8", Array.Empty<byte>(), 0, request, policy, extra);
        }

        /// <summary>
        /// 追加 CORS 响应头：**默认一个都不加**。
        /// </summary>
        /// <remarks>
        /// ★ 只有"用户显式配置的跨源白名单"命中时才回显**具体的**源；
        ///   绝不使用 <c>*</c>（带凭据时 <c>*</c> 本身就是非法的）。
        /// </remarks>
        private void AppendCorsHeaders(StringBuilder sb, HttpRequestInfo? request, WebAccessPolicy? policy)
        {
            if (request == null || policy == null) return;

            string? origin = request.GetSingleHeader("origin");
            if (string.IsNullOrEmpty(origin)) return;
            if (policy.AllowedOrigins.Count == 0) return;

            string normalized = origin.Trim().TrimEnd('/');
            if (!policy.AllowedOrigins.Contains(normalized)) return;

            sb.Append("Access-Control-Allow-Origin: ").Append(normalized).Append("\r\n");
            sb.Append("Access-Control-Allow-Credentials: true\r\n");
            sb.Append("Vary: Origin\r\n");
        }

        /// <summary>状态码对应的原因短语。</summary>
        private static string GetReasonPhrase(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 500: return "Internal Server Error";
                default: return "OK";
            }
        }

        // --- 共享采样（限频） ---

        /// <summary>
        /// 判断广播循环所属代次是否仍然是"当前代次"（没有跨过 Stop → Start 边界）。
        /// </summary>
        /// <remarks>
        /// ★ 为什么必须**自己查**，不能只看取消令牌：
        ///   共享采样是"限频 + 并发合并"的，**采集方不响应取消令牌**；
        ///   而等待包装（<see cref="RequestSharedSamplingWithTimeoutAsync"/>）又把取消
        ///   **转成 false**（不抛异常）→ 旧循环会从 await **正常恢复**。
        ///   所以每个 await 之后都要重新核对代次，否则旧代次会继续序列化并发送。
        /// ★ 同时看令牌与代次：令牌覆盖"Stop 但还没自增代次"的瞬间，
        ///   代次覆盖"Stop → Start 之后令牌已换新"的场景。
        /// </remarks>
        private bool IsGenerationCurrent(int generation, CancellationToken token)
        {
            return _isRunning
                && !token.IsCancellationRequested
                && generation == Volatile.Read(ref _generation);
        }

        /// <summary>
        /// 产出一次快照载荷：**先共享采样，再序列化**。
        /// HTTP 快照与网页广播都走这里 → 共用同一条限频采样路径。
        /// </summary>
        /// <param name="writer">快照写入器。</param>
        /// <param name="generation">调用方（广播循环）所属的服务代次。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>
        /// true = 载荷仍然有效（代次未变，可以发送）；
        /// false = 已经跨过 Stop 边界（调用方必须**放弃本轮发送**，直接退出）。
        /// </returns>
        private async Task<bool> ProducePayloadAsync(Utf8JsonWriter writer, int generation, CancellationToken cancellationToken)
        {
            await RequestSharedSamplingWithTimeoutAsync(cancellationToken).ConfigureAwait(false);

            // ★ 采样等待结束 → 重新校验代次。
            //   采集方不响应取消，等待包装又把取消转成 false（不抛异常），
            //   旧循环会从这里"正常"恢复 —— 必须在**序列化之前**自己查代次。
            if (!IsGenerationCurrent(generation, cancellationToken)) return false;

            await WriteSnapshotAsync(writer, cancellationToken).ConfigureAwait(false);

            // ★ 序列化等待结束 → 再校验一次（序列化本身也可能是异步的，期间同样可能 Stop → Start）。
            return IsGenerationCurrent(generation, cancellationToken);
        }

        /// <summary>
        /// 请求一次共享采样（带超时与取消）。
        /// ★ 超时后按既有行为**降级**：照常序列化当前缓存值，不挂死请求线程。
        /// </summary>
        private async Task<bool> RequestSharedSamplingWithTimeoutAsync(CancellationToken cancellationToken)
        {
            if (_samplingRequester == null) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // ★ 采样与超时并行：采样先完成就用采样结果，超时先到就降级。
            //   超时用注入的单调时钟计时（默认 TimeProvider.System）。
            Task<bool> sampling = RequestSharedSamplingAsync(cts.Token);
            Task timeout = Task.Delay(SamplingWaitTimeout, _time);

            Task finished = await Task.WhenAny(sampling, timeout).ConfigureAwait(false);
            if (finished != sampling)
            {
                // 超时：取消等待并按既有行为**降级** —— 照常序列化当前缓存值，不挂死请求线程
                try { cts.Cancel(); } catch { }
                return false;
            }

            return await sampling.ConfigureAwait(false);
        }

        /// <summary>执行共享采样请求（异常不外泄：采样失败也要照常返回快照）。</summary>
        private async Task<bool> RequestSharedSamplingAsync(CancellationToken cancellationToken)
        {
            var requester = _samplingRequester;
            if (requester == null) return false;
            try
            {
                return await requester(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Web 共享采样请求失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 写出快照内容（**只做序列化**）。
        /// ★ 这里不允许触发采集：采集统一由 <see cref="RequestSharedSamplingAsync"/> 在序列化之前完成。
        /// </summary>
        private Task WriteSnapshotAsync(Utf8JsonWriter writer, CancellationToken cancellationToken)
        {
            if (_asyncDataProvider != null)
            {
                return _asyncDataProvider(writer, cancellationToken);
            }
            _dataProvider(writer);
            return Task.CompletedTask;
        }

        // --- WebSocket core logic ---

        private async Task BroadcastLoop(int generation, CancellationToken token)
        {
            // ★ 复用同一个载荷流（次要#7）：每帧只重置长度，**不重新分配内部缓冲**。
            //   原来每帧 `new MemoryStream(capacity)` → 每帧 new byte[32768] → 广播 1 帧/秒 ≈ 32KB/s Gen0 垃圾。
            //   取舍：没有采用"capacity 传 0 按需增长"——那会让每帧从 0 开始倍增扩容（约 17 次 realloc + 拷贝），
            //   比现在的一次性 32KB 分配**更差**；这里改成"一次分配、长期复用"：
            //   稳态 0 分配、无 realloc 抖动，且保留"可扩容"语义（>32KB 的帧不会丢）。
            //   ⚠ 这个流必须保持**可扩容**：最早的写法是 new MemoryStream(buffer)（不可扩容的固定缓冲），
            //     快照 JSON 一旦超过 32 KB 就会抛 NotSupportedException（整帧丢失）——
            //     复用的 MemoryStream(int) 是可扩容的，SetLength(0) 只重置长度、保留已扩大的容量。
            using var payloadStream = new MemoryStream(32 * 1024);
            try
            {
                // ★ 代次校验：Stop 之后（或再次 Start 之后）本循环必须退出，
                //   防止快速 Stop/Start 复活出第二个广播者。
                //   ★ 这个校验**不能只看循环入口**：循环体里有多个 await
                //     （等待共享采样 / 等待序列化 / 等待发送），
                //     等待期间完全可能发生 Stop → Start。
                //     所以每个 await 之后都要**重新**校验（见 IsGenerationCurrent）。
                while (IsGenerationCurrent(generation, token))
                {
                    // [Optimization] 如果没有任何客户端连接，降低循环频率以节省资源
                    if (_wsClients.IsEmpty)
                    {
                        await Task.Delay(2000, token).ConfigureAwait(false);
                        continue;
                    }

                    byte[] buffer = null;
                    try
                    {
                        int capacity = 32 * 1024;
                        buffer = ArrayPool<byte>.Shared.Rent(capacity);

                        int payloadLen;
                        // ★ 重置长度（位置归 0，内部缓冲保留 → 不再每帧分配 32KB）
                        payloadStream.SetLength(0);
                        using (var writer = new Utf8JsonWriter(payloadStream))
                        {
                            // ★ 先共享采样（限频），再序列化 —— 与 HTTP 路径同一条采样入口
                            //   ProducePayloadAsync 在"采样等待结束"与"序列化等待结束"两处
                            //   各自校验代次；返回 false = 本循环已过期 → 放弃本轮，绝不发送。
                            bool payloadValid = await ProducePayloadAsync(writer, generation, token).ConfigureAwait(false);
                            if (!payloadValid)
                            {
                                // ★ 旧代次：直接退出本轮（不能把这一帧发给重启后的新连接）
                                break;
                            }
                            writer.Flush();
                        }
                        payloadLen = (int)payloadStream.Length;

                        // 载荷超过租用缓冲时换一块更大的（保留 ArrayPool 复用思路，不做每帧大分配）
                        if (payloadLen > buffer.Length)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            buffer = ArrayPool<byte>.Shared.Rent(payloadLen);
                        }
                        Buffer.BlockCopy(payloadStream.GetBuffer(), 0, buffer, 0, payloadLen);

                        int headerLen = 2;
                        if (payloadLen >= 65536) headerLen += 8;
                        else if (payloadLen >= 126) headerLen += 2;
                        int totalLen = headerLen + payloadLen;

                        // ★ 发送前再校验一次（构建帧的这段代码里也可能发生 Stop → Start）
                        if (!IsGenerationCurrent(generation, token)) break;

                        // Rent a target buffer for the frame
                        byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(totalLen);
                        try
                        {
                            int payloadOffset = EncodeFrameHeader(payloadLen, frameBuffer);
                            Buffer.BlockCopy(buffer, 0, frameBuffer, payloadOffset, payloadLen);

                            // Now we can return the "JSON Scratchpad" buffer IMMEDIATELY!
                            ArrayPool<byte>.Shared.Return(buffer);
                            buffer = null; // Prevent double return in finally

                            // Broadcast using frameBuffer
                            var tasks = new List<Task>();
                            // Capture the buffer and length for the closure
                            var bufToSend = frameBuffer;
                            var lenToSend = totalLen;

                            foreach (var entry in _wsClients)
                            {
                                // ★ 收件人必须与广播循环所属的服务代次绑定：
                                //   旧代次的广播不能把新代次（Stop → Start 之后才连上）的连接当收件人。
                                //   这一层防的是"最后一次代次校验与真正枚举/发送之间"的窄窗口。
                                if (entry.Value != generation) continue;

                                var client = entry.Key;
                                if (!client.Connected)
                                {
                                    // 已断开的连接：回收需求
                                    RemoveClient(client);
                                    continue;
                                }
                                // We must NOT return frameBuffer until ALL sends are done.
                                // But since we await Task.WhenAll, it's fine.
                                tasks.Add(SendFrameSafeAsync(client, bufToSend, lenToSend));
                            }

                            if (tasks.Count > 0)
                            {
                                await Task.WhenAll(tasks);
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(frameBuffer);
                        }

                        // ★ 等待发送结束 → 再校验：旧代次不再进入下一轮
                        if (!IsGenerationCurrent(generation, token)) break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Broadcast Error: {ex.Message}");
                    }
                    finally
                    {
                        if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
                    }

                    // 正常广播频率：每秒一次
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Stop 时的正常退出
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Broadcast Loop Error: {ex.Message}");
            }
        }

        private int EncodeFrameHeader(int payloadLen, byte[] buffer)
        {
            int offset = 0;

            // FIN(1) + RSV(000) + OpCode(1=Text) => 10000001 => 0x81
            buffer[offset++] = 0x81;

            // Mask(0) + Length
            if (payloadLen < 126)
            {
                buffer[offset++] = (byte)payloadLen;
            }
            else if (payloadLen <= 65535)
            {
                buffer[offset++] = 126;
                buffer[offset++] = (byte)((payloadLen >> 8) & 0xFF);
                buffer[offset++] = (byte)(payloadLen & 0xFF);
            }
            else
            {
                buffer[offset++] = 127;
                // 64-bit length
                buffer[offset++] = 0; buffer[offset++] = 0; buffer[offset++] = 0; buffer[offset++] = 0;
                buffer[offset++] = (byte)((payloadLen >> 24) & 0xFF);
                buffer[offset++] = (byte)((payloadLen >> 16) & 0xFF);
                buffer[offset++] = (byte)((payloadLen >> 8) & 0xFF);
                buffer[offset++] = (byte)(payloadLen & 0xFF);
            }
            return offset;
        }

        private async Task SendFrameSafeAsync(TcpClient client, byte[] buffer, int length)
        {
            try
            {
                var stream = client.GetStream();
                // [Fix] Add timeout to prevent memory leak from stuck tasks
                using var cts = new CancellationTokenSource(2000); // 2s timeout
                await stream.WriteAsync(buffer, 0, length, cts.Token);
            }
            catch
            {
                // Remove dead client (写失败：回收需求)
                RemoveClient(client);
                try { client.Close(); } catch { }
            }
        }


        private async Task ReceiveLoop(TcpClient client)
        {
            byte[] buffer = null;
            try
            {
                var stream = client.GetStream();
                // [Optimization] Use ArrayPool for receive buffer
                buffer = ArrayPool<byte>.Shared.Rent(1024);

                while (client.Connected && _wsClients.ContainsKey(client))
                {
                    // 阻塞读取，如果断开会抛出异常或返回0
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break; // 客户端关闭连接

                    // 解析帧头判断是否为 Close 帧 (OpCode = 8)
                    // 0x88 = 1000 1000 = FIN + Close
                    if ((buffer[0] & 0x0F) == 0x08)
                    {
                        break; // 收到关闭帧
                    }
                }
            }
            catch { }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
                // 正常断开 / 异常退出：回收需求
                RemoveClient(client);
                try { client.Close(); } catch { }
            }
        }

        /// <summary>
        /// WebSocket 握手（契约 W3 / W5 / W13.5）。
        /// </summary>
        /// <remarks>
        /// ★ 凭据 = <b>一次性票据</b>（<c>?ticket=</c>），不再接受 <c>?pwd=</c>：
        ///   浏览器 WebSocket API 无法自定义 Authorization 头，票据短 TTL + 一次性 + 仅限 WS 三者叠加。
        /// ★ Host / Origin 校验与 HTTP 完全一致（同一套判定口径）。
        /// </remarks>
        private bool Handshake(NetworkStream stream, string request)
        {
            try
            {
                HttpRequestInfo info = ParseRequest(request);
                WebAccessPolicy policy = BuildAccessPolicy();

                // ① Host / Origin（防 DNS Rebinding / 跨源建连）
                if (!IsHostAllowed(info, policy) || !IsOriginAllowed(info, policy))
                {
                    SendErrorResponse(stream, 403, "Host / Origin 校验未通过。", info, policy);
                    return false;
                }

                // ② 旧协议已移除
                if (HasLegacyPasswordQuery(info))
                {
                    SendErrorResponse(stream, 401, "查询串口令（?pwd=）已移除，请先登录换取一次性票据。", info, policy);
                    return false;
                }

                // ③ 票据：一次性、30 秒、仅限 WS。
                //   ★ L0 下同样必须走票据流程（协议口径统一，不开第二套口子）。
                string? ticket = GetSingleQueryValue(info, "ticket");
                if (ticket == null || !TryConsumeTicket(ticket, policy.PasswordFingerprint))
                {
                    SendErrorResponse(stream, 401, "票据无效、已过期或已被使用。", info, policy);
                    return false;
                }

                // 1. 提取 Sec-WebSocket-Key 并做 RFC 6455 形态校验
                if (!TryGetWebSocketKey(request, out string key))
                {
                    SendErrorResponse(stream, 400, "Sec-WebSocket-Key 缺失 / 重复 / 形态非法。", info, policy);
                    return false;
                }

                // 2. 生成 Accept Key (Magic String: 258EAFA5-E914-47DA-95CA-C5AB0DC85B11)
                string magic = key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                byte[] hash;
                using (var sha1 = SHA1.Create())
                {
                    hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(magic));
                }
                string acceptKey = Convert.ToBase64String(hash);

                // 3. 发送握手响应
                var response = new StringBuilder();
                response.Append("HTTP/1.1 101 Switching Protocols\r\n");
                response.Append("Connection: Upgrade\r\n");
                response.Append("Upgrade: websocket\r\n");
                response.Append($"Sec-WebSocket-Accept: {acceptKey}\r\n");
                response.Append("\r\n");

                byte[] respBytes = Encoding.UTF8.GetBytes(response.ToString());
                stream.Write(respBytes, 0, respBytes.Length);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 从请求头块里取出 <c>Sec-WebSocket-Key</c>，并做 RFC 6455 要求的形态校验。
        /// </summary>
        /// <returns>头缺失、重复、或取值不是「16 字节随机数的 Base64」时返回 <c>false</c>。</returns>
        /// <remarks>
        /// ★ 原先是一句 <c>Regex.Match(request, "Sec-WebSocket-Key: (.*)")</c>，四点都不对：
        ///   <list type="number">
        ///   <item>
        ///   <b>头名大小写不敏感。</b>固定写法会漏掉 <c>sec-websocket-key:</c>，那是合法请求。
        ///   </item>
        ///   <item>
        ///   <b>正则在整段请求里找第一次出现。</b>它同样会命中别的头的**值**里恰好拼出的字样，
        ///   取到的根本不是头。
        ///   </item>
        ///   <item>
        ///   <b>重复键无从发现。</b>同一头出现两次时，取第一个还是最后一个决定了 Accept 基于谁计算，
        ///   这是与请求走私同型的解析歧义 —— 直接拒绝，不猜。
        ///   </item>
        ///   <item>
        ///   <b>不校验取值形态。</b>RFC 6455 规定它是 16 字节随机数的 Base64；
        ///   形态不对说明对面不是 WebSocket 客户端，此时回 101 只是把错误推迟到帧解析阶段，
        ///   还会让它占用一个已登记的有效连接名额。
        ///   </item>
        ///   </list>
        /// </remarks>
        private static bool TryGetWebSocketKey(string request, out string key)
        {
            key = "";
            bool found = false;

            // 只扫请求行 + 头行，遇空行即止（真正的头块边界，比"扫全文"严格）
            int lineStart = 0;
            bool isRequestLine = true;
            while (lineStart < request.Length)
            {
                int lineEnd = request.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = request.Length;

                string line = request.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r');

                if (isRequestLine)
                {
                    isRequestLine = false;      // 请求行没有头名，跳过
                }
                else if (line.Length == 0)
                {
                    break;                      // 空行 = 头块结束
                }
                else
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        string name = line.Substring(0, colon).Trim();
                        if (string.Equals(name, "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                        {
                            if (found) return false;        // 重复键：拒绝

                            string value = line.Substring(colon + 1).Trim();
                            if (!IsWebSocketKeyShapeValid(value)) return false;

                            key = value;
                            found = true;
                        }
                    }
                }

                lineStart = lineEnd + 1;
            }

            return found;
        }

        /// <summary>
        /// 校验 <c>Sec-WebSocket-Key</c> 取值是否为「16 字节的 Base64」。
        /// </summary>
        /// <remarks>
        /// 16 字节的 Base64 定长 24 字符（末尾带 <c>=</c>），这里不必按长度预筛：
        /// <see cref="Convert.TryFromBase64String(string, Span{byte}, out int)"/> 已在不合法时返回 false，
        /// 再用解码后字节数卡住"长度对但内容随便是任意字符串"的情形。
        /// </remarks>
        private static bool IsWebSocketKeyShapeValid(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            Span<byte> decoded = stackalloc byte[32];   // 16 字节 Base64 解码后正好 16，留余量
            return Convert.TryFromBase64String(value, decoded, out int written) && written == 16;
        }
    }
}
