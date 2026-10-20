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

        public string CurrentPassword { get;set; }

        private readonly Settings _cfg;
        
        // [Refactor] Decoupled WebSocket Logic
        private readonly WebSessionManager _sessionManager;
        
        public static LiteWebServer? Instance { get; private set; }

        public bool IsRunning => _isRunning;
        public int CurrentRunningPort => _isRunning ? _currentRunningPort : -1;

        /// <summary>当前是否存在合法的 Web 数据需求（已鉴权连接或未过期的 HTTP 快照租期）。</summary>
        public bool HasWebDataDemand => _sessionManager.HasWebDataDemand;

        /// <summary>已鉴权且尚未关闭的 WebSocket 连接数。</summary>
        public int AuthenticatedConnectionCount => _sessionManager.AuthenticatedConnectionCount;

        /// <summary>Web 需求状态快照（连接数 + HTTP 租期，一次性读取，不撕裂）。</summary>
        public WebDemandSnapshot GetDemandSnapshot() => _sessionManager.GetDemandSnapshot();

        public LiteWebServer(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;
            // Initialize WebSocket Manager with the data provider delegate
            _sessionManager = new WebSessionManager(WriteSnapshotJson);
        }

        public bool Start(out string errorMsg)
        {
            errorMsg = "";
            if (_isRunning) return true;
            if (!_cfg.WebServerEnabled) return true;

            try
            {
                int port = _cfg.WebServerPort;

                try
                {
                    // [Fix] 优先尝试绑定 IPv6Any 并开启 DualMode，以同时支持 IPv4 和 IPv6
                    _listener = new TcpListener(IPAddress.IPv6Any, port);
                    _listener.Server.DualMode = true;
                    _listener.Start();
                }
                catch (Exception)
                {
                    // 如果系统不支持 IPv6，回退到 IPv4
                    _listener = new TcpListener(IPAddress.Any, port);
                    _listener.Start();
                }

                _isRunning = true;
                _currentRunningPort = port;
                CurrentPassword = _cfg.WebServerPassword;

                // 1. 启动监听连接的循环
                // ★ 代次：Stop 之后旧的监听循环必须自己退出，不能在重启后复活
                int generation = Interlocked.Increment(ref _generation);
                Task.Run(() => ListenLoop(generation));
                
                // 2. 启动 WebSocket 管理器 (广播循环在内部)
                _sessionManager.Start();
                
                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                Debug.WriteLine("WebServer Start Error: " + ex.Message);
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
            
            // 停止 WebSocket 管理器（同时清空全部 Web 需求：连接 + HTTP 租期）
            _sessionManager.Stop();
        }

        public void Dispose()
        {
            Stop();
        }

        private async Task ListenLoop(int generation)
        {
            // ★ 代次校验：Stop 之后本循环必须退出，避免重启后出现两个监听者
            while (_isRunning && generation == Volatile.Read(ref _generation) && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    // [Refactor] All connection handling logic moved to WebSessionManager (now serving as unified ConnectionManager)
                    _ = _sessionManager.HandleClientAsync(client);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }
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
            bool fpsDemand = WebFpsDemand.IsFpsDemand(_cfg, _sessionManager.HasWebDataDemand);

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
                    var valueState = WebSnapshotValues.ResolveValue(item.Key, fpsDemand, k => hw.Get(k), out string valueStr, out float? rawValue);

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
            value = NotEnabled;
            raw = null;

            // ★ FPS 未启用：明确占位，不读数值（读 FPS 会触发外部采集服务的惰性启动）
            if (!fpsDemand && string.Equals(key, "FPS", StringComparison.OrdinalIgnoreCase))
            {
                return WebSnapshotValueState.NotEnabled;
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
