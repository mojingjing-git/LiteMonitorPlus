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

        private volatile bool _isRunning = false;
        private int _generation = 0;                       // Start/Stop 代次：用来区分 Stop 前后的广播任务
        private CancellationTokenSource? _broadcastCts;
        private CancellationTokenSource? _lifetimeCts;     // 服务级取消令牌：Stop 时让在途采样等待立刻退出
        private CancellationToken _lifetimeToken = CancellationToken.None;

        /// <summary>共享采样等待的超时（超时后按既有行为降级：照常序列化缓存值，不挂死请求线程）。</summary>
        public static readonly TimeSpan SamplingWaitTimeout = TimeSpan.FromSeconds(2);

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
                stream.ReadTimeout = 5000; // 防恶意连接

                // 1. 读取请求头 (最多 8KB)
                buffer = ArrayPool<byte>.Shared.Rent(8192);
                int bytesRead = await ReadHeaderAsync(stream, buffer);
                
                if (bytesRead == 0) { client.Close(); return; }

                string requestStr = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // 2. 协议分流
                if (IsWebSocketUpgrade(requestStr))
                {
                    if (Handshake(stream, requestStr))
                    {
                        // 握手成功 = 已鉴权，登记为有效 Web 需求
                        AddClient(client, connectionGeneration);
                        await ReceiveLoop(client); // 进入 WS 循环
                    }
                }
                else
                {
                    // ★ HTTP 路径改成可等待：鉴权 → 授予租期 → 共享采样 → 序列化 → 响应
                    await HandleHttpRequestAsync(stream, requestStr, connectionGeneration, lifetimeToken).ConfigureAwait(false);
                    client.Close(); // HTTP 是短连接
                }
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

        private async Task<int> ReadHeaderAsync(NetworkStream stream, byte[] buffer)
        {
            int totalBytes = 0;
            while (totalBytes < buffer.Length)
            {
                // 预留空间检查
                if (totalBytes >= buffer.Length) break;

                int read = await stream.ReadAsync(buffer, totalBytes, buffer.Length - totalBytes);
                if (read == 0) break;
                
                totalBytes += read;
                if (FindHeaderEnd(buffer, totalBytes) >= 0) return totalBytes;
            }
            return totalBytes;
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

        private async Task HandleHttpRequestAsync(NetworkStream stream, string request, int connectionGeneration, CancellationToken cancellationToken)
        {
            string[] lines = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            
            string path = lines[0].Split(' ').Length > 1 ? lines[0].Split(' ')[1] : "/";
            
            if (path.StartsWith("/api/snapshot"))
            {
                if (!CheckAuth(request))
                {
                    // 未鉴权：不产生任何硬件需求
                    SendHttpResponse(stream, 401, "text/plain", Encoding.UTF8.GetBytes("Unauthorized"), 12);
                    return;
                }

                // ★ 已鉴权的快照请求获得一个短需求租期（默认 2 秒）。
                //   连续 HTTP 轮询会不断延长租期；未鉴权请求 / HTML / favicon 都不会走到这里。
                //   跨越 Stop 边界的在途请求不再授予租期：Stop 已经清空过全部需求，
                //   再补一个 2 秒租期等于把已停止的服务又变成"有需求"。
                //   ★ 授权点必须在**采样之前**：等待采样期间不能没有需求。
                if (connectionGeneration == Volatile.Read(ref _generation))
                {
                    _demand.GrantHttpLease();
                    NotifyDemandChanged();
                }

                // ★ 共享采样（限频）：HTTP 轮询与网页广播走**同一条**限频采样路径，
                //   协调器保证 1 秒 1 次 + 并发合并 → 多个网页 + 轮询不会叠加真实采集。
                //   快照本身**不执行** hw.Update()，这里只是请求一次共享采样。
                await RequestSharedSamplingWithTimeoutAsync(cancellationToken).ConfigureAwait(false);

                using var ms = new MemoryStream();
                using var writer = new Utf8JsonWriter(ms);
                await WriteSnapshotAsync(writer, cancellationToken).ConfigureAwait(false);
                writer.Flush();
                SendHttpResponse(stream, 200, "application/json", ms.GetBuffer(), (int)ms.Length);
            }
            else if (path == "/favicon.ico")
            {
                // 仅取图标：不产生硬件需求
                SendHttpResponse(stream, 404, "text/html", Array.Empty<byte>(), 0);
            }
            else
            {
                // 默认返回首页（仅取 HTML：不产生硬件需求）
                string iconBase64 = WebPageContent.GetAppIconBase64();
                bool authRequired = !string.IsNullOrEmpty(Settings.Load().WebServerPassword);
                
                string html = WebPageContent.IndexHtml
                    .Replace("{{FAVICON}}", !string.IsNullOrEmpty(iconBase64) ? $"<link rel='icon' type='image/png' href='data:image/png;base64,{iconBase64}'>" : "")
                    .Replace("{{AUTH_REQUIRED}}", authRequired.ToString().ToLower());
                
                byte[] data = Encoding.UTF8.GetBytes(html);
                SendHttpResponse(stream, 200, "text/html; charset=utf-8", data, data.Length);
            }
        }

        private bool CheckAuth(string requestHeader)
        {
            var pwd = Settings.Load().WebServerPassword;
            if (string.IsNullOrEmpty(pwd)) return true;

            var firstLine = requestHeader.Split('\n')[0]; 
            var match = Regex.Match(firstLine, @"[?&]pwd=([^&\s]+)");
            if (match.Success)
            {
                return match.Groups[1].Value == pwd;
            }
            return false;
        }

        private void SendHttpResponse(NetworkStream stream, int code, string type, byte[] body, int len)
        {
            string headers = $"HTTP/1.1 {code} OK\r\nContent-Type: {type}\r\nContent-Length: {len}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            byte[] hBytes = Encoding.UTF8.GetBytes(headers);
            stream.Write(hBytes, 0, hBytes.Length);
            if (len > 0) stream.Write(body, 0, len);
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

        private bool Handshake(NetworkStream stream, string request)
        {
            try
            {
                if (!CheckAuth(request))
                {
                    SendHttpResponse(stream, 401, "text/plain", Encoding.UTF8.GetBytes("Unauthorized"), 12);
                    return false;
                }

                // 1. 提取 Sec-WebSocket-Key
                var match = Regex.Match(request, "Sec-WebSocket-Key: (.*)");
                if (!match.Success) return false;

                string key = match.Groups[1].Value.Trim();

                // 2. 生成 Accept Key (Magic String: 258EAFA5-E914-47DA-95CA-C5AB0DC85B11)
                string magic = key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                byte[] hash = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(magic));
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
    }
}
