using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json; // [Fix] Added missing using
using System.Buffers; // [Optimization] Use ArrayPool
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.InfoService;
using System.Diagnostics;

namespace LiteMonitor.src.WebServer
{
    public class LiteWebServer : IDisposable
    {
        private TcpListener? _listener;
        private volatile bool _isRunning = false;
        private int _currentRunningPort = -1;
        private int _generation = 0;   // Start/Stop 代次：用来区分 Stop 前后的监听任务

        // 正在处理中的连接数（含已经建好的 WebSocket 长连接）。
        // 只由 Interlocked 读写：ListenLoop 与各连接自己的收尾路径分别在不同线程上跑。
        private int _activeConnections = 0;

        public string CurrentPassword { get;set; }

        private readonly Settings _cfg;
        
        // [Refactor] Decoupled WebSocket Logic
        private readonly WebSessionManager _sessionManager;

        // ★ 共享采样请求入口（限频）：生产实现走 HardwareMonitor 的采样协调器
        private readonly Func<CancellationToken, Task<bool>> _samplingRequester;

        // ★ 把"是否有 Web 需求"写入采集层（默认写 HardwareMonitor.SetWebDataDemand）
        private readonly Func<bool, bool> _demandApplier;

        // ★ Web 需求 → 采集需求的同步器（事件驱动 + 周期监视）
        private readonly WebDemandSynchronizer _demandSync;
        
        public static LiteWebServer? Instance { get; private set; }

        /// <summary>
        /// 监听地址接缝（<c>internal</c>，**不属于公开 API**）。
        /// </summary>
        /// <remarks>
        /// ★ null（默认）= 生产行为：<c>IPAddress.IPv6Any</c> + DualMode，系统不支持 IPv6 时回退 IPv4。
        /// ★ 单元测试把它设成 <see cref="IPAddress.Loopback"/>：测试期间**只监听回环**，
        ///   不在局域网上暴露一个带口令的服务（测试窗口里被外部扫到是不可接受的卫生问题）。
        /// ★ 默认值保持生产语义不变 —— 这是一个**只给测试用**的接缝，不改变任何生产行为。
        /// </remarks>
        internal IPAddress? ListenAddress { get; set; }

        /// <summary>
        /// 同时在处理中的连接上限，超出直接断开。
        /// </summary>
        /// <remarks>
        /// ★ 内部接缝（<c>internal</c>，**不属于公开 API**）：只为让测试能把上限压到很小，
        ///   从而在不建 64 条连接的前提下验证"超限即拒"。默认值就是生产行为。
        /// ★ 语义是"在途连接"：WebSocket 握手成功后，长连接会一直占着一个名额
        ///   （直到连接断开，收尾路径在 finally 里归还）。
        /// ★ 默认 64 的理由：浏览器对同一源头的并发连接上限是 6，几个标签页也远低于此；
        ///   而一个脚本开几千条连接时，这里把"无上限累积任务 + 缓冲"降级成"大部分被立刻拒绝"。
        /// ★ 这是**背压**而不是准入控制：正常情况下不会触顶，触顶时拒绝比耗尽资源更可接受。
        /// </remarks>
        internal int MaxConcurrentConnections { get; set; } = 64;

        public bool IsRunning => _isRunning;
        public int CurrentRunningPort => _isRunning ? _currentRunningPort : -1;

        /// <summary>当前是否存在合法的 Web 数据需求（已鉴权连接或未过期的 HTTP 快照租期）。</summary>
        public bool HasWebDataDemand => _sessionManager.HasWebDataDemand;

        /// <summary>已鉴权且尚未关闭的 WebSocket 连接数。</summary>
        public int AuthenticatedConnectionCount => _sessionManager.AuthenticatedConnectionCount;

        /// <summary>Web 需求状态快照（连接数 + HTTP 租期，一次性读取，不撕裂）。</summary>
        public WebDemandSnapshot GetDemandSnapshot() => _sessionManager.GetDemandSnapshot();

        /// <summary>
        /// 既有构造函数（**原样保留**，保证二进制兼容）。
        /// </summary>
        /// <remarks>
        /// ★ 简报要求"公开 API 只许新增、不许改既有成员签名"：
        ///   本构造函数必须保留 —— 否则 1 参 ctor 的元数据签名会消失（源码兼容 ✓、二进制兼容 ✘）。
        /// ★ 既有 3 个调用点（<c>MainForm_Transparent</c> / <c>FpsStateProbe</c> / <c>ProbeEnvironment</c>）
        ///   都写的是 <c>new LiteWebServer(cfg)</c>，会绑定到本版本，
        ///   行为与改造前完全一致（默认实现即生产实现）。
        /// ★ B2 修复轮 3（F1）：委派改成**显式转发**生产默认实现，而不是"传 null 再走 ?? 兜底"。
        ///   <para>★ <b>行为等价性（零行为变化）</b>：3 参 ctor 里是
        ///   <c>samplingRequester ?? DefaultSamplingRequester</c> /
        ///   <c>demandApplier ?? ApplyWebDataDemandToHardware</c>；
        ///   本构造函数传的就是这两个 <c>private static</c> 方法本身，
        ///   与"传 null 再走 ??"最终得到的委托**目标与方法签名完全相同**
        ///   （static 方法 → 委托的 Target 为 null）→ 运行时行为逐位一致。</para>
        ///   <para>★ 3 参 ctor 的 <c>??</c> 兜底**必须保留**：直接传 null 的调用方仍要拿到默认实现。</para>
        ///   <para>★ 为什么要显式转发：只有显式转发，"生产默认需求传播路径"才在源码里一眼可见，
        ///   源码级护栏也才能锁住它 —— 之前"传 null"时，1 参 ctor 的委派链是<b>零覆盖</b>的
        ///   （把它改成 no-op 委派，全部用例仍然全绿）。</para>
        /// </remarks>
        public LiteWebServer(Settings cfg)
            : this(cfg, DefaultSamplingRequester, ApplyWebDataDemandToHardware)
        {
        }

        /// <param name="cfg">当前设置。</param>
        /// <param name="samplingRequester">
        /// 共享采样请求入口（默认走 <c>HardwareMonitor.Instance?.UpdateIfDueAsync</c>）。
        /// 传 null 表示使用默认实现。
        /// </param>
        /// <param name="demandApplier">
        /// 把 Web 需求写入采集层的回调（默认写 <c>HardwareMonitor.Instance.SetWebDataDemand</c>）。
        /// 返回 false 表示"暂时写不进去"（例如采集层实例还不存在），同步器会在下一轮重试。
        /// </param>
        public LiteWebServer(
            Settings cfg,
            Func<CancellationToken, Task<bool>>? samplingRequester = null,
            Func<bool, bool>? demandApplier = null)
        {
            _cfg = cfg;
            Instance = this;
            _samplingRequester = samplingRequester ?? DefaultSamplingRequester;
            _demandApplier = demandApplier ?? ApplyWebDataDemandToHardware;

            // Initialize WebSocket Manager with the data provider delegate
            // ★ 序列化回调只负责写 JSON；采样由共享采样入口在序列化**之前**完成。
            _sessionManager = new WebSessionManager(
                (writer, ct) => { WriteSnapshotJson(writer); return Task.CompletedTask; },
                demandState: null,
                timeProvider: null,
                samplingRequester: (ct) => _samplingRequester(ct));

            // ★ 需求同步器：需求真值来源与网页占位判断**同源**（都是 _sessionManager 的需求状态）
            _demandSync = new WebDemandSynchronizer(
                demandProvider: () => _sessionManager.HasWebDataDemand,
                applyDemand: _demandApplier,
                targetProvider: () => HardwareMonitor.Instance);

            // ★ 需求变化（连接增删 / HTTP 租期授予 / 停止清空）立刻同步，不等广播周期
            _sessionManager.DemandChanged = () => _demandSync.SyncNow();
        }

        /// <summary>
        /// 默认共享采样入口：走采集协调器（1 秒 1 次 + 并发合并）。
        /// ★ <see cref="HardwareMonitor.Instance"/> **每次取值时再取**，不在构造时缓存：
        ///   硬件层可能比 Web 服务更晚创建。
        /// </summary>
        private static Task<bool> DefaultSamplingRequester(CancellationToken cancellationToken)
        {
            var hw = HardwareMonitor.Instance;
            if (hw == null) return Task.FromResult(false);
            return hw.UpdateIfDueAsync(cancellationToken);
        }

        /// <summary>
        /// 默认需求写入：把"是否有 Web 需求"写进采集层。
        /// ★ 采集层实例还不存在时返回 false → 同步器下一轮监视会重试（不会丢需求）。
        /// </summary>
        private static bool ApplyWebDataDemandToHardware(bool hasDemand)
        {
            var hw = HardwareMonitor.Instance;
            if (hw == null) return false;
            hw.SetWebDataDemand(hasDemand);
            return true;
        }

        public bool Start(out string errorMsg)
        {
            errorMsg = "";
            if (_isRunning) return true;
            if (!_cfg.WebServerEnabled) return true;

            TcpListener? listener = null;
            try
            {
                int port = _cfg.WebServerPort;

                IPAddress? listenAddress = ListenAddress;
                if (listenAddress != null)
                {
                    // ★ 测试接缝：只监听指定地址（不开 DualMode → 不会把 IPv4 客户端也带进来）
                    listener = new TcpListener(listenAddress, port);
                    listener.Start();
                }
                else
                {
                    try
                    {
                        // [Fix] 优先尝试绑定 IPv6Any 并开启 DualMode，以同时支持 IPv4 和 IPv6
                        listener = new TcpListener(IPAddress.IPv6Any, port);
                        listener.Server.DualMode = true;
                        listener.Start();
                    }
                    catch (Exception)
                    {
                        // 如果系统不支持 IPv6，回退到 IPv4
                        listener = new TcpListener(IPAddress.Any, port);
                        listener.Start();
                    }
                }

                _isRunning = true;
                _currentRunningPort = port;
                CurrentPassword = _cfg.WebServerPassword;

                // 2. 先启动 WebSocket 管理器 (广播循环在内部)
                //    ★ 必须在开始监听之前：会话管理器没启动时不会登记任何连接需求，
                //      否则"刚连上就 Stop"的客户端会被当成僵尸连接，需求永远卡住。
                _sessionManager.Start();

                // 1. 启动监听连接的循环
                // ★ 代次：Stop 之后旧的监听循环必须自己退出，不能在重启后复活
                // ★ 监听器用局部变量传进去：循环体不再读可变的 _listener 字段，
                //   避免"条件检查通过之后 Stop+Start"导致旧循环 accept 到新监听器上。
                int generation = Interlocked.Increment(ref _generation);
                Task.Run(() => ListenLoop(generation, listener!));

                // ★ _listener 只在**真正启动成功之后**才赋值（B1 复审）：
                //   启动失败时字段不应该指向一个仍在监听的套接字。
                _listener = listener;

                // ★ 启动需求监视：HTTP 租期到期没有事件可挂靠，只能靠周期监视发现。
                //   间隔 200 毫秒 << 1 个采集周期，满足"≤1 采集周期 + 宽限"。
                _demandSync.Start();
                _demandSync.SyncNow();

                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                Debug.WriteLine("WebServer Start Error: " + ex.Message);
                // 启动失败：不要留下一个还在监听的套接字
                _isRunning = false;
                _currentRunningPort = -1;
                try { listener?.Stop(); } catch { }
                _listener = null;

                // ★ 启动失败必须把**已经起来的**部件全部停掉（次要#5）。
                //   异常可能发生在 _sessionManager.Start() 之后（例如 _demandSync.Start() 抛错）：
                //   那样会留下一个**仍在跑的广播循环**，而需求监视又没启动
                //   → HTTP 租期到期没人发现 → 采集层需求**永久停在 true**（永久白采集，本轮优化全白做）。
                //   顺序：先停会话管理器（它会清空全部需求并通知 → 同步器把 false 送达采集层），
                //   再停需求监视（否则监视线程可能在停掉之后又写一次）。
                try { _sessionManager.Stop(); } catch { }
                try { _demandSync.Stop(); } catch { }
                return false;
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _currentRunningPort = -1;

            // 让旧监听循环退出（代次失效）
            Interlocked.Increment(ref _generation);

            try { _listener?.Stop(); } catch { }
            _listener = null;
            
            // 停止 WebSocket 管理器（同时清空全部 Web 需求：连接 + HTTP 租期）
            _sessionManager.Stop();

            // ★ 需求已归零：把 false 送达采集层（不依赖广播周期）
            _demandSync.SyncNow();

            // 停止需求监视
            _demandSync.Stop();
        }

        public void Dispose()
        {
            Stop();
        }

        private async Task ListenLoop(int generation, TcpListener listener)
        {
            // ★ 代次校验：Stop 之后本循环必须退出，避免重启后出现两个监听者
            while (_isRunning && generation == Volatile.Read(ref _generation) && listener != null)
            {
                // ★ 捕获"接收前"的会话代次：
                //   如果接收期间发生过 Stop，这次连接属于旧一代，不能再登记成需求。
                int sessionGeneration = _sessionManager.Generation;

                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Debug.WriteLine(ex.Message); continue; }

                if (sessionGeneration != _sessionManager.Generation)
                {
                    // 跨越 Stop 边界的连接：直接丢弃，不交给会话管理器
                    try { client.Close(); } catch { }
                    continue;
                }

                // ★ 并发上限（背压）：没有这道闸时，一个脚本可以建立起任意多条连接，
                //   每条都会拉起一个连接处理任务并占住自己的缓冲与读取期限；
                //   进程内存和线程池排队长度随之无上界增长。
                //   超限时直接断开 —— 此时还没读请求，无法区分 HTTP/WebSocket，也就无法回 503。
                if (Interlocked.Increment(ref _activeConnections) > MaxConcurrentConnections)
                {
                    Interlocked.Decrement(ref _activeConnections);
                    Debug.WriteLine($"Web 在途连接数已达上限 {MaxConcurrentConnections}，拒绝新连接");
                    try { client.Close(); } catch { }
                    continue;
                }

                // [Refactor] All connection handling logic moved to WebSessionManager (now serving as unified ConnectionManager)
                // ★ 走包装方法而不是直接 `_ = _sessionManager.HandleClientAsync(client)`：
                //   fire-and-forget 的 Task 一旦抛出未处理异常，异常只会在 GC 时才以
                //   UnobservedTaskException 冒出来，且没人负责归还并发名额。
                _ = HandleClientGuardedAsync(client);
            }
        }

        /// <summary>
        /// 调用连接处理器，并保证无论成功、失败还是取消都会归还一个并发名额。
        /// </summary>
        /// <param name="client">已接受的连接（其关闭由被调用方负责）。</param>
        /// <remarks>
        /// ★ 这里必须吞掉异常而不是让它逃逸：
        ///   本方法是 <c>_ = ...</c> 的 fire-and-forget 调用，异常一旦逃逸就没有任何代码观察它，
        ///   最终只会在 GC 时以 <c>UnobservedTaskException</c> 出现（默认还会被静默忽略）。
        ///   记录到 Debug 是这条路径上唯一能留下痕迹的地方。
        /// ★ 名额在 finally 里归还：这是"计数不会永久泄漏"的唯一保证 ——
        ///   进程内 Stop → Start 之后，上一轮的连接都会因为 socket 被关闭而在
        ///   HandleClientAsync 里退出，从而把名额还回来。
        /// </remarks>
        private async Task HandleClientGuardedAsync(TcpClient client)
        {
            try
            {
                await _sessionManager.HandleClientAsync(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("HandleClient Error: " + ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _activeConnections);
            }
        }

        private void WriteSnapshotJson(Utf8JsonWriter writer)
        {
            var hw = HardwareMonitor.Instance;
            if (hw == null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
                return;
            }

            // ★★★ 修复：创建列表副本并加锁，防止遍历时 UI 线程修改集合导致崩溃 ★★★
            List<MonitorItemConfig> itemsCopy;
            Dictionary<string, int> groupIndices = new Dictionary<string, int>();
            
            lock (_cfg.MonitorItems)
            {
                // ★★★ 修复：复用主界面 (Panel模式) 的排序逻辑 ★★★
                itemsCopy = _cfg.MonitorItems
                    .GroupBy(x => x.UIGroup)
                    .OrderBy(g => g.Min(x => x.SortIndex))
                    .SelectMany(g => g.OrderBy(x => x.SortIndex))
                    .ToList();

                // 预计算组顺序索引 (基于排序后的列表)
                int gIdx = 0;
                foreach(var item in itemsCopy)
                {
                    if (!groupIndices.ContainsKey(item.UIGroup))
                    {
                        groupIndices[item.UIGroup] = gIdx++;
                    }
                }
            }

            string localIp = hw.GetNetworkIP() ?? "127.0.0.1";

            // ★ FPS 需求规则 (计划 §4.4)：本地启用 或 (有合法 Web 需求 且 显式开启网页版帧率)
            //   没有需求时 Web 端输出 "--" 占位，不读取数值、不伪造 0。
            //   ★ 需求值与同步器写入采集层的值**同源**（都是 _sessionManager 的需求状态）。
            bool fpsDemand = WebFpsDemand.IsFpsDemand(_cfg, _sessionManager.HasWebDataDemand);

            // ★ 当前是否有**可信**的 FPS 数据：
            //   有需求但服务刚启动、首帧未到时，hw.Get("FPS") 会返回 0f —— 那是"没有数据"而不是"真的是 0"。
            //   用 HasFpsData 区分两者，前者输出 "--"，后者输出真实的 0。
            bool hasFpsData = hw.HasFpsData;

            writer.WriteStartObject();

            // "sys" object
            writer.WriteStartObject("sys");
            writer.WriteString("ip", localIp);
            writer.WriteNumber("port", _currentRunningPort);
            writer.WriteString("uptime", (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"hh\:mm\:ss"));
            writer.WriteEndObject();

            // "items" array
            writer.WriteStartArray("items");

            foreach (var item in itemsCopy)
            {
                string displayName;
                // [Fix] WebUI should also use MetricLabelResolver to show dynamic names
                string resolved = MetricLabelResolver.ResolveLabel(item);
                if (!string.IsNullOrEmpty(resolved))
                {
                    displayName = resolved;
                }
                else
                {
                    // [Optimization] Use cached keys from MonitorItemConfig
                    string itemsKey = !string.IsNullOrEmpty(item.CachedItemsKey) ? item.CachedItemsKey : UIUtils.Intern("Items." + item.Key);
                    displayName = LanguageManager.T(itemsKey);
                    // 如果翻译失败 (fallback to key suffix)
                    if (displayName.StartsWith("Items.")) displayName = displayName.Split('.')[1];
                }
                
                // ★★★ [按卡拆组] gid 会被前端拼进 DOM id 和 querySelector 选择器,
                //   "GPU.0" 里的点号是非法 CSS 选择器字符 (会被当成 class, 而 class 不能以数字开头),
                //   querySelector 会抛 DOMException 导致整个网页端白屏 -> 必须先消毒成 "GPU_0"。
                //   (已用 headless Chrome 实测: '#rw-GPU.0' 抛 SyntaxError, render() 中断。)
                string groupId = GroupTitleResolver.ToSafeGroupId(item.UIGroup);
                // 分组标题统一走 GroupTitleResolver, 保证与桌面端一致 (别名 / 显卡简称)。
                string groupDisplay = GroupTitleResolver.Resolve(_cfg, item.UIGroup);

                string valStr = "";
                string unit = "";
                double pct = 0;
                int status = 0;
                bool isPrimary = false;

                if (item.Key.StartsWith("DASH."))
                {
                    // 特殊处理 DASH.HOST 等纯信息项 (以及插件项)
                    string dashKey = item.Key.Substring(5);
                    valStr = InfoService.Instance.GetValue(dashKey);
                    
                    // ★★★ 修复：读取插件写入的颜色状态 (.Color) ★★★
                    // [Optimization] Use cached Dash keys
                    string colorKey = !string.IsNullOrEmpty(item.CachedDashColorKey) ? item.CachedDashColorKey : dashKey + ".Color";
                    string colorStr = InfoService.Instance.GetValue(colorKey);
                    if (int.TryParse(colorStr, out int cVal)) status = cVal;

                    // 读取插件写入的单位 (.Unit)
                    string unitKey = !string.IsNullOrEmpty(item.CachedDashUnitKey) ? item.CachedDashUnitKey : dashKey + ".Unit";
                    string unitStr = InfoService.Instance.GetValue(unitKey);
                    if (!string.IsNullOrEmpty(unitStr)) unit = unitStr;
                }
                else
                {
                    // 硬件监控项
                    // ★ FPS 需求规则 (计划 §4.4)：
                    //   localFpsDemand = 本地 FPS 在面板或任务栏启用
                    //   webFpsDemand    = 有合法 Web 数据需求 && WebFpsEnabled
                    //   没有需求时：既不读取 FPS 数值（读取会触发外部 FPS 采集服务的惰性启动），
                    //   也不把"未启动"伪报成 0，而是明确输出 "--"。
                    //   有需求但首帧未到（hasFpsData == false）时同样输出 "--"，不是 0。
                    var valueState = WebSnapshotValues.ResolveValue(item.Key, fpsDemand, hasFpsData, k => hw.Get(k), out string valueStr, out float? rawValue);

                    // [Fix] 恢复过滤逻辑：跳过没有数据的监控项（如台式机的电池），以符合业务屏蔽需求
                    // 布局顺序问题已由 CSS order (gidx) 机制解决，即使数据晚到也会插入正确位置
                    if (valueState == WebSnapshotValueState.NoData) continue;

                    valStr = valueStr;
                    if (valueState == WebSnapshotValueState.HasData && rawValue.HasValue)
                    {
                        // [Refactor] 使用 MetricUtils 统一格式化逻辑，确保与桌面端完全一致 (包括单位换算、颜色阈值、内存显示模式)
                        unit = MetricUtils.GetUnitStr(item.Key, rawValue.Value, MetricUtils.UnitContext.Panel);
                        
                        // GetProgressValue 返回 0.0-1.0，Web端通常需要 0-100
                        pct = MetricUtils.GetProgressValue(item.Key, rawValue.Value) * 100.0;
                        
                        // 状态判断 (0=Safe, 1=Warn, 2=Crit)
                        status = MetricUtils.GetState(item.Key, rawValue.Value);
                    }
                    else
                    {
                        // 明确未启用：单位与正常路径保持一致，数值是 "--" 占位
                        unit = MetricUtils.GetUnitStr(item.Key, null, MetricUtils.UnitContext.Panel);
                    }

                    // Primary Logic
                    if (item.Key == "BAT.Percent") isPrimary = true;
                    else if (item.Key == "CPU.Load") isPrimary = true;
                    else if (item.Key == "MEM.Load") isPrimary = true;
                    // ★ [多显卡] 用基础 Key 归一化, 否则 "GPU.0.Load" / "GPU.1.Load" 匹配不上,
                    //   GPU 分组就再也拿不到负载状态 (分组环/状态色会失真)。
                    else if (item.Key == "GPU.Core.Load" || GpuKey.ToBaseKey(item.Key) == "GPU.Load") isPrimary = true;
                    else isPrimary = false;
                }

                if (string.IsNullOrEmpty(valStr)) valStr = "--";
                
                writer.WriteStartObject();
                writer.WriteString("k", item.Key);
                writer.WriteString("n", displayName);
                writer.WriteString("gid", groupId);
                writer.WriteString("gn", groupDisplay);
                // ★★★ 新增：发送组顺序索引，供前端 CSS order 使用 ★★★
                writer.WriteNumber("gidx", groupIndices.TryGetValue(item.UIGroup, out int idx) ? idx : 999);
                writer.WriteString("v", valStr);
                writer.WriteString("u", unit);
                writer.WriteNumber("pct", pct);
                writer.WriteNumber("sts", status);
                writer.WriteBoolean("primary", isPrimary);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// 快照里某个监控项的取值状态。
    /// </summary>
    public enum WebSnapshotValueState
    {
        /// <summary>没有数据：调用方应当跳过这一行（保持既有的"跳过无数据项"行为）。</summary>
        NoData,

        /// <summary>有数据：按取到的数值写出。</summary>
        HasData,

        /// <summary>明确未启用：写出占位值 <see cref="WebSnapshotValues.NotEnabled"/>，不伪报 0。</summary>
        NotEnabled
    }

    /// <summary>
    /// 快照取值的统一入口（保持既有字符串字段契约）。
    ///
    /// 关键约定：
    ///  - FPS 未启用时 **不读取数值**（读取 FPS 会触发外部采集服务的惰性启动），
    ///    输出明确的 "--" 占位，绝不把"未启动"伪报成 0；
    ///  - 其它监控项没有数据时返回 <see cref="WebSnapshotValueState.NoData"/>，由调用方跳过该行
    ///    （与改动前的过滤行为一致）。
    /// </summary>
    public static class WebSnapshotValues
    {
        /// <summary>未启用时的占位值（与快照里既有的 "--" 占位一致）。</summary>
        public const string NotEnabled = "--";

        /// <summary>
        /// 计算某个硬件监控项在快照里的取值。
        /// </summary>
        /// <param name="key">监控项 Key（如 "CPU.Load" / "FPS"）。</param>
        /// <param name="fpsDemand">当前是否存在 FPS 采集需求（见 <see cref="WebFpsDemand"/>）。</param>
        /// <param name="readValue">读取实际数值的委托；FPS 未启用时**不会**被调用。</param>
        /// <param name="value">写出的数值字符串（无值/未启用时为 "--"）。</param>
        /// <param name="raw">读到的原始数值（无值/未启用时为 null）。</param>
        public static WebSnapshotValueState ResolveValue(
            string key,
            bool fpsDemand,
            Func<string, float?> readValue,
            out string value,
            out float? raw)
        {
            // 既有行为保持不变：FPS 有需求时按真实值输出（含"真 0 FPS"）。
            return ResolveValue(key, fpsDemand, hasFpsData: true, readValue, out value, out raw);
        }

        /// <summary>
        /// 计算某个硬件监控项在快照里的取值（**带 FPS 数据可信度**的形态）。
        /// </summary>
        /// <remarks>
        /// ★ 取值顺序固定为：**先判需求 → 再判 HasFpsData → 最后才读数值**。
        ///   - 没有 FPS 需求 → <see cref="WebSnapshotValueState.NotEnabled"/>（"--"），
        ///     且 **readValue 不会被调用**（读 FPS 会触发外部采集服务的惰性启动）；
        ///   - 有需求但 <paramref name="hasFpsData"/> 为 false（服务刚启动、首帧未到）
        ///     → 同样是"尚无可信数据"，输出 "--" 而**不是** 0（"真 0 FPS"必须能显示 0，
        ///     所以不能用数值本身来区分）；
        ///   - 有需求且有可信数据 → 读真实值（真实的 0 就是 0）。
        /// </remarks>
        /// <param name="key">监控项 Key（如 "CPU.Load" / "FPS"）。</param>
        /// <param name="fpsDemand">当前是否存在 FPS 采集需求（见 <see cref="WebFpsDemand"/>）。</param>
        /// <param name="hasFpsData">当前是否有**可信**的 FPS 数据（见 <c>HardwareMonitor.HasFpsData</c>）。</param>
        /// <param name="readValue">读取实际数值的委托；FPS 不满足条件时**不会**被调用。</param>
        /// <param name="value">写出的数值字符串（无值/未启用时为 "--"）。</param>
        /// <param name="raw">读到的原始数值（无值/未启用时为 null）。</param>
        public static WebSnapshotValueState ResolveValue(
            string key,
            bool fpsDemand,
            bool hasFpsData,
            Func<string, float?> readValue,
            out string value,
            out float? raw)
        {
            value = NotEnabled;
            raw = null;

            if (string.Equals(key, "FPS", StringComparison.OrdinalIgnoreCase))
            {
                // ① 先判需求：没有需求 → 明确占位，且**不读取数值**
                //    （读 FPS 会触发外部采集服务的惰性启动）
                if (!fpsDemand) return WebSnapshotValueState.NotEnabled;

                // ② 再判数据可信度：服务刚启动、首帧未到 → 也是"尚无可信数据"，
                //    输出 "--" 而不是把"没数据"伪报成 0
                if (!hasFpsData) return WebSnapshotValueState.NotEnabled;
            }

            float? v = readValue(key);
            if (!v.HasValue) return WebSnapshotValueState.NoData;

            raw = v;
            value = MetricUtils.GetValueStr(key, v.Value, false); // false = Panel Context (Standard)
            return WebSnapshotValueState.HasData;
        }
    }

    /// <summary>
    /// FPS 需求规则（计划 §4.4）。
    ///
    /// 这是**唯一**的判定入口：网页快照的 "--" 占位判断与外部 FPS 采集服务的启停
    /// 都必须用同一套规则，否则会出现"网页说有需求但采集服务没跑"（报 0）
    /// 或"网页说没需求但采集服务在跑"（白跑）的不一致。
    ///
    /// <code>
    /// localFpsDemand = 本地 FPS 在面板或任务栏启用
    /// webFpsDemand    = 当前有合法 Web 数据需求 &amp;&amp; WebFpsEnabled
    /// fpsDemand       = localFpsDemand || webFpsDemand
    /// </code>
    /// </summary>
    public static class WebFpsDemand
    {
        /// <summary>本地 FPS 需求：FPS 监控项在面板或任务栏可见（已启用）。</summary>
        public static bool IsLocalFpsDemand(Settings? cfg)
        {
            if (cfg == null) return false;
            return cfg.IsAnyEnabled("FPS");
        }

        /// <summary>Web FPS 需求：有合法的 Web 数据需求，并且用户显式开启了网页版帧率。</summary>
        public static bool IsWebFpsDemand(Settings? cfg, bool hasWebDataDemand)
        {
            if (cfg == null) return false;
            return cfg.WebFpsEnabled && hasWebDataDemand;
        }

        /// <summary>总需求：本地或 Web 任一成立就需要采集 FPS。</summary>
        public static bool IsFpsDemand(Settings? cfg, bool hasWebDataDemand)
        {
            return IsLocalFpsDemand(cfg) || IsWebFpsDemand(cfg, hasWebDataDemand);
        }
    }
}
