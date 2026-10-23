using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
namespace LiteMonitor
{
    public class Settings
    {
        // ====== 基础设置 ======
        public string Skin { get; set; } = "DarkFlat_Classic";
        public bool TopMost { get; set; } = true;
        public bool AutoStart { get; set; } = false;
        public int RefreshMs { get; set; } = 1000;
        public double AnimationSpeed { get; set; } = 0.35;
        public Point Position { get; set; } = new Point(-1, -1);

        // ====== 界面与行为 ======
        public bool AutoCheckUpdate { get; set; } = true; // ★★★ [新增] 自动检查更新 ★★★
        public bool HorizontalMode { get; set; } = false;
        public double Opacity { get; set; } = 0.85;
        public string Language { get; set; } = "";
        public bool ClickThrough { get; set; } = false;
        public bool AutoHide { get; set; } = true;
        public bool ClampToScreen { get; set; } = true;
        public int PanelWidth { get; set; } = 240;
        public double UIScale { get; set; } = 1.0;

        // ====== 实时玻璃（M2 / A16）======
        // ★ 实时玻璃**默认关闭**（M2 合同 §8.1）：开启需要用户显式操作，
        //   并且首次开启时必须提示"捕获瞬间面板可能从录屏/截图中短暂消失或闪动"。
        //   旧版 settings.json 没有这个字段时反序列化为 false（System.Text.Json 的缺省行为）⇒ 保持关闭。
        public bool LiveGlassEnabled { get; set; } = false;
        // ★ 是否已经就"实时玻璃的副作用"提示过用户（首次开启时弹一次，之后不再打扰）。
        public bool LiveGlassHintShown { get; set; } = false;

        // ====== GPU 材质主题（独立于旧 LiveGlassEnabled）======
        // 旧配置没有这些字段时保持 false：启动仍走原 GDI+，不创建 GPU 设备或捕获会话。
        public bool GpuThemeEffectsEnabled { get; set; } = false;
        public bool GpuLiveBackgroundEnabled { get; set; } = false;
        public bool GpuWdaCapturePolicyAccepted { get; set; } = false;

        // ====== 硬件相关 ======
        public string PreferredNetwork { get; set; } = "";
        public string LastAutoNetwork { get; set; } = "";
        public string PreferredDisk { get; set; } = "";
        public string LastAutoDisk { get; set; } = "";
        public string PreferredGpu { get; set; } = "";
        
        // ★★★ [新增] 首选风扇 ★★★
        public string PreferredCpuFan { get; set; } = "";
        public string PreferredCpuPump { get; set; } = ""; // 保存用户选的水冷接口
        public string PreferredCaseFan { get; set; } = "";
        public string PreferredMoboTemp { get; set; } = "";

        // ★ [#166] 首选 CPU 电压来源（保存 LHM 的**稳定传感器标识**，形如 "/amdcpu/0/voltage/0"）
        //   "" = 自动（按来源可信度自动挑选）。
        //   ★ 旧配置没有这个字段时反序列化为 ""（System.Text.Json 的缺省行为）⇒ 视为自动，不报错。
        public string PreferredCpuVoltageSensor { get; set; } = "";

        // ====== [#610] 按指定 IP 排除实时网速 ======
        // ★ 默认关闭。关闭或没有有效规则时**不启动任何额外事件采集**（零新增会话/逐包处理）。
        public bool IpTrafficExclusionEnabled { get; set; } = false;

        // ★ 逐行填写**精确** IPv4/IPv6（首版不支持 CIDR / 域名 / 通配符 / 端口）。
        //   错误行会在设置页逐条标出原因，绝不静默忽略；上限 256 条。
        public List<string> ExcludedTrafficIps { get; set; } = new List<string>();
        
        // 主窗体所在的屏幕设备名 (用于记忆上次位置)
        public string ScreenDevice { get; set; } = "";

        // ====== 任务栏 ======
        public bool ShowTaskbar { get; set; } = true;
        // ★★★ 新增：横条模式是否跟随任务栏布局？ ★★★
        public bool HorizontalFollowsTaskbar { get; set; } = false;
        public bool HideMainForm { get; set; } = false;
        public bool HideTrayIcon { get; set; } = false;
        public bool TaskbarAlignLeft { get; set; } = true;
        
        // ★★★ 任务栏：预设模式选择 (1=Bold/Big, 0=Regular/Small) ★★★
        // Default = -1 用于检测老用户迁移
        public int TaskbarPresetStyle { get; set; } = -1;

        // ★★★ 任务栏：自定义布局参数 ★★★
        // 开启后，将忽略预设的"粗体/细体"逻辑，强制使用以下参数
        public bool TaskbarCustomLayout { get; set; } = false; 

        public string TaskbarFontFamily { get; set; } = DEFAULT_TB_FONT;
        public float TaskbarFontSize { get; set; } = DEFAULT_TB_SIZE_BOLD;
        public bool TaskbarFontBold { get; set; } = true;
        
        // 间距配置 (单位: px, 会自动随 DPI 缩放)
        public int TaskbarItemSpacing { get; set; } = DEFAULT_TB_GAP;      // 组与组之间的间距
        public int TaskbarInnerSpacing { get; set; } = DEFAULT_TB_INNER_BOLD;     // 标签与数值之间的间距
        public int TaskbarVerticalPadding { get; set; } = DEFAULT_TB_VOFF;  // 垂直方向的微调/行间距
        
        // ★★★ [新增] 横条/桌面模式 间距配置 ★★★
        public int HorizontalItemSpacing { get; set; } = 12;  // 组间距 (默认 12)
        public int HorizontalInnerSpacing { get; set; } = 8;  // 标签与值间距 (默认 8)
        public bool HorizontalSingleLine { get; set; } = false; // [新增] 横条模式单行显示

        // ★★★ 常量定义：用于 GetStyle 中的默认策略 ★★★
        public const string DEFAULT_TB_FONT = "Microsoft YaHei UI"; // 任务栏默认字体（兼容多语言环境，系统自带）
        public const float DEFAULT_TB_SIZE_BOLD = 10f; // 粗字模式默认字号（提升可读性）
        public const float DEFAULT_TB_SIZE_REGULAR = 9f; // 细字模式默认字号（节省空间，更精致）
        public const int DEFAULT_TB_GAP = 6; // 任务栏组间距默认值（不同监控项之间的距离）
        public const int DEFAULT_TB_INNER_BOLD = 8; // 粗字模式标签与值间距默认值（更宽的间距适配粗体）
        public const int DEFAULT_TB_INNER_REGULAR = 6; // 细字模式标签与值间距默认值（紧凑布局）
        public const int DEFAULT_TB_VOFF = 2; // 任务栏垂直内边距默认值（用于垂直居中微调）

        
        // ★★★ 新增：指定任务栏显示的屏幕设备名 ("" = 自动/主屏) ★★★
        public string TaskbarMonitorDevice { get; set; } = "";

        // 任务栏行为配置
        public bool TaskbarClickThrough { get; set; } = false;     // 鼠标穿透
        public bool TaskbarSingleLine { get; set; } = false;// 单行显示
        public bool TaskbarHoverShowAll { get; set; } = true; // [新增] 悬浮显示所有监控项
        public int TaskbarManualOffset { get; set; } = 0;// 手动偏移量 (像素)

        // ====== 任务栏：高级自定义外观 ======
        public bool TaskbarCustomStyle { get; set; } = false; // 总开关
        public string TaskbarColorLabel { get; set; } = "#141414"; // 标签颜色
        public string TaskbarColorSafe { get; set; } = "#008040";  // 正常 (淡绿)
        public string TaskbarColorWarn { get; set; } = "#B57500";  // 警告 (金黄)
        public string TaskbarColorCrit { get; set; } = "#C03030";  // 严重 (橙红)
        public string TaskbarColorBg { get; set; } = "#D2D2D2";    // 防杂边背景色 (透明键)

        // 双击动作配置
        public int MainFormDoubleClickAction { get; set; } = 0;
        public int TaskbarDoubleClickAction { get; set; } = 0;

        // 内存/显存显示模式
        public int MemoryDisplayMode { get; set; } = 1;

        // ★ 2. 运行时缓存：存储探测到的总容量 (GB)
        [JsonIgnore] public static float DetectedRamTotalGB { get; set; } = 0;
        [JsonIgnore] public static float DetectedGpuVramTotalGB { get; set; } = 0;

        // 开启后：CPU使用率、CPU频率、内存占用、磁盘读写 将优先从 Windows 计数器读取
        public bool UseWinPerCounters { get; set; } = true;

        // ★★★ [新增] 忽略 SMB (局域网文件传输) 流量 ★★★
        public bool IgnoreSmbTraffic { get; set; } = true;
        
        // ====== 记录与报警 ======
        public float RecordedMaxCpuPower { get; set; } = 65.0f;
        public float RecordedMaxCpuClock { get; set; } = 4200.0f;
        public float RecordedMaxGpuPower { get; set; } = 100.0f;
        public float RecordedMaxGpuClock { get; set; } = 1800.0f;
        
        // ★★★ [新增] FPS 固定最大值 (用于进度条上限，推荐 144) ★★★
        public float RecordedMaxFps { get; set; } = 144.0f;

        // ★★★ [新增] 风扇最大值记录 ★★★
        public float RecordedMaxCpuFan { get; set; } = 4000;
        public float RecordedMaxCpuPump { get; set; } = 5000; // 水冷最大转速 (用于百分比计算)
        public float RecordedMaxGpuFan { get; set; } = 3500;
        public float RecordedMaxChassisFan { get; set; } = 3000;

        public bool MaxLimitTipShown { get; set; } = false;
        
        public bool AlertTempEnabled { get; set; } = true;
        public int AlertTempThreshold { get; set; } = 80;

        public bool WebServerEnabled { get; set; } = false;
        public int WebServerPort { get; set; } = 5000; // 默认端口

        // ================================================================
        // Web 凭据与监听范围（SEC-001 / SEC-002 / LM-005 / LM-006 / LM-023）
        // ================================================================

        /// <summary>
        /// 访问口令（**明文**）。
        /// </summary>
        /// <remarks>
        /// ★ 这个字段**只用于读取旧配置**与"当前会话内的编辑"，**永不落盘**：
        ///   写盘时由 <see cref="WebPasswordJsonConverter"/> 强制写成空串，
        ///   真正用来校验的是 <see cref="WebServerPasswordHash"/> + <see cref="WebServerPasswordSalt"/>。
        /// ★ 之所以保留明文字段：设置界面要显示/编辑它。
        ///   ★ 但"口令是否变了"**不再**拿明文比（R03）：迁移后的生产形态里明文恒为空串，
        ///   拿明文比较永远发现不了"哈希被清掉 / 被换掉"。
        ///   现在比的是**有效凭据指纹**：<c>AppActions.ApplyWebServer</c> 用
        ///   <see cref="GetWebPasswordFingerprint"/> 与
        ///   <c>LiteWebServer.CurrentCredentialFingerprint</c> 比较。
        /// </remarks>
        [JsonConverter(typeof(WebPasswordJsonConverter))]
        public string WebServerPassword { get; set; } = ""; // 访问密码（内存态）

        /// <summary>
        /// 访问口令的 PBKDF2-SHA256 哈希（base64）—— 落盘的验证材料。
        /// </summary>
        /// <remarks>
        /// getter 返回的是**生效**哈希：内存里还有明文时按明文现算（带缓存），
        /// 否则用落盘的那份。这样"用户刚在设置里改了口令、还没保存"也能立刻生效，
        /// 而旧配置（只有明文）在首次校验时会走惰性迁移把哈希写进磁盘。
        /// </remarks>
        public string WebServerPasswordHash
        {
            get => GetEffectiveWebPasswordHash();
            set => _webPasswordHash = value ?? "";
        }

        /// <summary>访问口令的盐（base64）。</summary>
        public string WebServerPasswordSalt { get; set; } = "";

        /// <summary>
        /// 监听范围（契约 W12）。
        /// </summary>
        /// <remarks>
        /// ★ <see cref="WebServerBindMode.Unset"/> 是"旧配置占位"：
        ///   老版本没有这个字段，反序列化后就是 Unset，由 <see cref="Load"/> 按 W12 的规则解析：
        ///   <list type="bullet">
        ///   <item>存量配置（WebServerEnabled=true 且此前版本没有该字段）⇒ <b>Lan</b>（保持旧行为，不静默变更监听范围），并由设置界面提示收敛；</item>
        ///   <item>其余（未启用 / 新安装）⇒ <b>Loopback</b>（仅本机）。</item>
        ///   </list>
        /// ★ 解析结果会在下一次保存时落盘 ⇒ 之后不再反复解析（一次性、可回退）。
        /// </remarks>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WebServerBindMode WebServerBindMode { get; set; } = WebServerBindMode.Unset;

        /// <summary>
        /// 局域网模式下额外允许的主机名 / IP（逗号或分号分隔）。
        /// </summary>
        /// <remarks>
        /// ★ 这些地址进入 Host / Origin 白名单；为空时局域网模式只放行本机地址。
        /// </remarks>
        public string WebServerLanAddresses { get; set; } = "";

        /// <summary>
        /// 显式允许的跨源 Origin（逗号分隔）。为空 ⇒ **不返回任何 CORS 响应头**（默认同源）。
        /// </summary>
        /// <remarks>
        /// ★ 只回显**匹配的具体源**，绝不使用 <c>*</c>；带凭据时同时给
        /// <c>Access-Control-Allow-Credentials: true</c>。
        /// </remarks>
        public string WebServerAllowedOrigins { get; set; } = "";

        /// <summary>
        /// 是否信任 <c>X-Forwarded-For</c> / <c>X-Real-IP</c> / <c>Forwarded</c>。
        /// </summary>
        /// <remarks>
        /// ★ **默认不信任**（这是降级开关：打开它等于信任任意来源伪造的转发头）。
        ///   只有用户明确知道自己前面有可信反向代理时才打开。
        /// </remarks>
        public bool WebServerTrustProxyHeaders { get; set; } = false;

        /// <summary>
        /// 运行时标记：监听范围是"为兼容存量配置而保留的 Lan"，设置界面据此提示用户收敛到仅本机。
        /// </summary>
        [JsonIgnore]
        public bool WebServerBindModeNeedsConvergence { get; internal set; } = false;

        // ★★★ [新增] 网页版是否显示帧率 (FPS) ★★★
        // 网页版默认**不**采集 FPS：帧率需要额外的外部采集服务，开着会带来额外采集开销。
        // 只有用户显式打开时，网页端才会产生 FPS 采集需求（与本地 FPS 需求合并计算）。
        // 旧设置 JSON 里没有这个字段时，反序列化后为 false（System.Text.Json 的缺省行为）。
        public bool WebFpsEnabled { get; set; } = false;
        
        public ThresholdsSet Thresholds { get; set; } = new ThresholdsSet();

        [JsonIgnore] public DateTime LastAlertTime { get; set; } = DateTime.MinValue;
        [JsonIgnore] public long SessionUploadBytes { get; set; } = 0;
        [JsonIgnore] public long SessionDownloadBytes { get; set; } = 0;
        [JsonIgnore] public DateTime LastAutoSaveTime { get; set; } = DateTime.MinValue;

        public Dictionary<string, string> GroupAliases { get; set; } = new Dictionary<string, string>();
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();

        // ★★★ [多显卡] 各显卡各自的峰值记录 (key = 完整 Key, 如 "GPU.1.Power"), 供进度条按卡自适应 ★★★
        public Dictionary<string, float> RecordedMaxGpuDynamic { get; set; } = new Dictionary<string, float>();

        // ★★★ [多显卡] 运行时缓存: 各显卡的显存总容量 (GB), key = "GPU" / "GPU.0" / "GPU.1" ★★★
        // 用 ConcurrentDictionary: 硬件线程 (HardwareValueProvider.GetGpuVramPercent) 写,
        // UI 线程 (MetricUtils.GetGpuVramTotalGB) 读 —— 普通 Dictionary 并发写会损坏内部结构。
        [JsonIgnore]
        public static ConcurrentDictionary<string, float> DetectedGpuVramTotalGBByKey { get; } = new ConcurrentDictionary<string, float>();

        // ★★★ [多显卡] 监控显卡数量: 1 = 只监控 1 张 (沿用旧行为), N = 按优先级监控前 N 张 ★★★
        public int MonitoredGpuCount { get; set; } = 1;
        public List<PluginInstanceConfig> PluginInstances { get; set; } = new List<PluginInstanceConfig>();

        // ★★★ 插件市场：市场安装的插件记录。卸载所有权判定与更新检测的唯一依据 ★★★
        // 旧版 settings.json 没有这个字段，反序列化为 null，由 Load() 兜底成空列表
        public List<MarketPluginRecord> MarketPlugins { get; set; } = new List<MarketPluginRecord>();

        // ★★★ [新增] 极简样式封装（复制到 Settings 类里） ★★★
        public struct TBStyle { 
            public string Font; public float Size; public bool Bold; 
            public int Gap; public int Inner; public int VOff; 
        }

        // ★★★ 核心修复：添加全局保存锁，防止重置时被自动保存覆盖 ★★★
        [JsonIgnore]
        public static bool GlobalBlockSave 
        { 
            get => SettingsHelper.GlobalBlockSave; 
            set => SettingsHelper.GlobalBlockSave = value; 
        }

        // ★★★ 优化 2：全局单例引用 ★★★
        private static Settings _instance;

        // ★★★ 优化 3：改造 Load 方法为单例模式 ★★★
        public static Settings Load(bool forceReload = false)
        {
            // 如果单例已存在且不强制刷新，直接返回内存对象 (0 IO, 0 GC)
            if (_instance != null && !forceReload) return _instance;

            // 委托给 SettingsHelper 加载
            _instance = SettingsHelper.Load(forceReload);
            
            // 兼容旧配置：如果 WebServerPassword 为 null，初始化为空字符串
            if (_instance.WebServerPassword == null)
            {
                _instance.WebServerPassword = "";
            }

            // 兼容旧配置：如果 MarketPlugins 为 null，初始化为空列表
            if (_instance.MarketPlugins == null)
            {
                _instance.MarketPlugins = new List<MarketPluginRecord>();
            }

            // ★ W12：解析监听范围（存量配置保持旧行为，新装默认仅环回）
            _instance.ResolveWebServerBindMode();

            // ★ W9：插件凭据解密失败的显式处理（禁用受影响实例 + 记录错误，绝不静默变空）
            _instance.ApplyPluginCredentialState();

            return _instance;
        }

        /// <summary>
        /// 解析监听范围（契约 W12）。只在 <see cref="WebServerBindMode.Unset"/> 时生效一次。
        /// </summary>
        /// <remarks>
        /// ★ 存量配置（WebServerEnabled=true 且此前版本没有该字段）⇒ <b>Lan</b>：
        ///   保持旧行为，**不静默变更监听范围**；设置界面会提示用户收敛到「仅本机」。
        /// ★ 未启用 / 新安装 ⇒ <b>Loopback</b>：默认仅监听 127.0.0.1。
        /// ★ 解析结果随下一次保存落盘 ⇒ 之后不再反复解析（一次性、可回退：用户随时能改回来）。
        /// </remarks>
        private void ResolveWebServerBindMode()
        {
            if (WebServerBindMode != WebServerBindMode.Unset) return;

            if (WebServerEnabled)
            {
                WebServerBindMode = WebServerBindMode.Lan;
                WebServerBindModeNeedsConvergence = true;
            }
            else
            {
                WebServerBindMode = WebServerBindMode.Loopback;
            }
        }

        /// <summary>
        /// 插件凭据的显式失败处理（契约 W9 / LM-023）。
        /// </summary>
        /// <remarks>
        /// ★ 触发条件：某个受 DPAPI 保护的凭据**解不开**（换机器 / 换用户 / 数据损坏）。
        /// ★ 处置：<b>停用该插件实例</b> + 记录一条**明确错误**（设置界面可见）。
        ///   绝**不**把凭据静默变成空值、绝**不**删除磁盘上的原密文。
        /// ★ 与监听范围无关：凭据解密失败**不会**让 Web 服务"因为没口令"而扩大监听范围。
        /// </remarks>
        private void ApplyPluginCredentialState()
        {
            _credentialErrors.Clear();

            // ① 转换器在读取阶段记下的解密失败
            foreach (string failure in PluginCredentialState.TakeFailures())
            {
                _credentialErrors.Add(failure);
            }

            if (PluginInstances == null) return;

            // ② 仍然是"受保护形态"的值 = 解密失败（转换器已把失败的密文原样留下）
            foreach (var inst in PluginInstances)
            {
                if (inst == null) continue;

                bool failed = false;
                if (inst.InputValues != null)
                {
                    foreach (var kv in inst.InputValues)
                    {
                        if (ProtectedSecret.IsProtected(kv.Value)) { failed = true; break; }
                    }
                }
                if (!failed && inst.Targets != null)
                {
                    foreach (var target in inst.Targets)
                    {
                        if (target == null) continue;
                        foreach (var kv in target)
                        {
                            if (ProtectedSecret.IsProtected(kv.Value)) { failed = true; break; }
                        }
                        if (failed) break;
                    }
                }

                if (!failed) continue;

                inst.Enabled = false;
                _credentialErrors.Add(
                    $"插件实例 {inst.Id}（模板 {inst.TemplateId}）的受保护凭据无法解密" +
                    "（可能换了机器或换了 Windows 用户）；该实例已停用，原凭据未被删除，请重新填写。");
            }

            foreach (string err in _credentialErrors)
            {
                Debug.WriteLine("[Settings] 插件凭据错误：" + err);
            }
        }

        /// <summary>
        /// 本次加载发现的凭据错误（明文、可在设置界面展示）。
        /// </summary>
        [JsonIgnore]
        public IReadOnlyList<string> CredentialErrors => _credentialErrors;

        private readonly List<string> _credentialErrors = new List<string>();

        // Helper method to get taskbar style moved to SettingsHelper

        public Settings DeepClone()
        {
            var json = System.Text.Json.JsonSerializer.Serialize(this);
            return System.Text.Json.JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }

        // ================================================================
        // Web 凭据：哈希 / 校验 / 惰性迁移（契约 W9）
        // ================================================================

        /// <summary>PBKDF2-SHA256 的迭代次数。</summary>
        /// <remarks>
        /// ★ 100k 次是"本地口令"的合理量级：单次校验约几十毫秒（登录是低频操作），
        ///   离线爆破成本被显著抬高。**不要**为了界面流畅把它降到几千次 ——
        ///   哈希只在"口令真的变了"时重算，界面输入路径不受影响（见 ResolveWebServerBindMode 注释）。
        /// </remarks>
        private const int WebPasswordIterations = 100_000;
        private const int WebPasswordSaltBytes = 16;
        private const int WebPasswordHashBytes = 32;

        private string _webPasswordHash = "";        // 落盘的那份哈希
        private string _webPasswordHashCache = "";   // 按明文现算的缓存
        private string _webPasswordHashCacheKey = "";
        private static readonly object _webPasswordGate = new object();

        /// <summary>备份文件路径（与 SettingsHelper 内部保持一致）。</summary>
        private static string BackupFilePath =>
            Path.Combine(AppContext.BaseDirectory, "settings.json.bak");

        /// <summary>
        /// 当前生效的口令哈希：内存里还有明文时按明文现算（带缓存），否则用落盘的那份。
        /// </summary>
        private string GetEffectiveWebPasswordHash()
        {
            string plain = WebServerPassword ?? "";
            if (string.IsNullOrEmpty(plain)) return _webPasswordHash ?? "";

            // 明文在场 ⇒ 需要一份盐（落盘的盐来自旧配置；没有就现生成一份）
            if (string.IsNullOrEmpty(WebServerPasswordSalt)) WebServerPasswordSalt = CreateWebPasswordSalt();

            string key = WebServerPasswordSalt + "\u0001" + plain;
            if (!string.Equals(_webPasswordHashCacheKey, key, StringComparison.Ordinal))
            {
                _webPasswordHashCache = ComputeWebPasswordHash(plain, WebServerPasswordSalt);
                _webPasswordHashCacheKey = key;
            }
            return _webPasswordHashCache;
        }

        /// <summary>是否已经配置了访问口令。</summary>
        public bool HasWebPasswordConfigured() => !string.IsNullOrEmpty(GetEffectiveWebPasswordHash());

        /// <summary>
        /// 校验候选口令（恒时比较）。
        /// </summary>
        /// <remarks>
        /// ★ 未配置口令时**一律返回 false**：调用方据此进入 L0（无凭据）分支，
        ///   绝不能因为"没有口令"就把任何候选口令判成正确。
        /// </remarks>
        public bool VerifyWebPassword(string candidate)
        {
            string expected = GetEffectiveWebPasswordHash();
            if (string.IsNullOrEmpty(expected)) return false;
            if (candidate == null) return false;

            string actual = ComputeWebPasswordHash(candidate, WebServerPasswordSalt);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(actual),
                Encoding.UTF8.GetBytes(expected));
        }

        /// <summary>
        /// 口令指纹：口令一变它就变（会话表用它发现"改密 ⇒ 全部会话与票据撤销"）。
        /// </summary>
        /// <remarks>
        /// ★ 只用一次 SHA-256（不是 PBKDF2）—— 它在**每个请求**上都会被计算，
        ///   必须便宜。它不需要抗爆破（攻击者拿不到它）。
        /// </remarks>
        public string GetWebPasswordFingerprint()
        {
            string hash = GetEffectiveWebPasswordHash();
            if (string.IsNullOrEmpty(hash)) return "";
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("liteMonitor-pwd-fp-v1|" + hash));
            return Convert.ToBase64String(digest);
        }

        /// <summary>
        /// 惰性迁移：把仍以明文存在的旧口令转成"哈希 + 盐"落盘，并清掉备份里的明文。
        /// </summary>
        /// <remarks>
        /// ★ 触发时机：<b>首次需要校验口令时</b>（而不是启动时）——
        ///   启动就写盘会在"配置暂时读不到"时把默认值写回磁盘。
        /// ★ 迁移失败（写盘失败）<b>不</b>静默降级成"空口令"：
        ///   返回 false，调用方据此**拒绝登录**（见 WebSessionManager 的登录路径）。
        /// </remarks>
        public bool EnsureWebPasswordMigrated()
        {
            lock (_webPasswordGate)
            {
                if (string.IsNullOrEmpty(WebServerPassword)) return false;   // 没有明文 ⇒ 无需迁移

                try
                {
                    if (string.IsNullOrEmpty(WebServerPasswordSalt))
                        WebServerPasswordSalt = CreateWebPasswordSalt();

                    // 触发一次哈希计算（缓存住）
                    string hash = GetEffectiveWebPasswordHash();
                    if (string.IsNullOrEmpty(hash)) return false;

                    // 落盘：写盘时明文被转换器强制写成 ""（只有哈希 + 盐进磁盘）
                    // ★ 必须看**真实的写盘结果**：以前 Save 返回 void 且吞掉 IO 异常，
                    //   写盘失败也会一路走到 "return true" ⇒
                    //   ① 旧明文口令仍然留在 settings.json 上（凭据泄露方向），
                    //   ② 调用方（登录路径）还以为迁移成功 ⇒ "迁移失败 ⇒ 拒绝登录"的承诺落空。
                    if (!SettingsHelper.TrySave(this))
                    {
                        Debug.WriteLine("[Settings] Web 口令迁移失败：配置未能写入磁盘（保存被禁止或 IO 失败）。");
                        return false;
                    }

                    // 备份里同样有明文 ⇒ 一并处理（契约 W9 / P2）
                    // ★ 备份清理失败同样必须**如实反映**：备份里留着旧明文 = 凭据仍留在磁盘上。
                    if (!SanitizeBackupFile())
                    {
                        Debug.WriteLine("[Settings] Web 口令迁移失败：备份中的旧明文口令未能清除。");
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Settings] Web 口令迁移失败: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// 清理备份文件（settings.json.bak）里的明文口令。
        /// </summary>
        /// <remarks>
        /// ★ 备份是"上一份有效内容"，它同样含明文 —— 只清主文件等于没清。
        /// ★ 处理策略：能解析 ⇒ 删掉 <c>WebServerPassword</c> 字段后写回；
        ///   解析不了（已损坏）⇒ 这份备份已经不能作为回滚材料，直接删除，
        ///   避免明文继续留在磁盘上（契约允许"重新生成或删除"）。
        /// ★ 返回值：**true** = 备份里已没有明文（或本来就没有）；**false** = 明文仍可能留在磁盘上。
        ///   调用方必须把它如实反映出去（见 <see cref="EnsureWebPasswordMigrated"/>）。
        /// </remarks>
        private static bool SanitizeBackupFile()
        {
            string path = BackupFilePath;
            try
            {
                if (!File.Exists(path)) return true;                      // 没有备份 ⇒ 无明文可清
                string text = File.ReadAllText(path);
                if (!text.Contains("WebServerPassword")) return true;     // 没有该字段 ⇒ 无需处理

                JsonNode root = JsonNode.Parse(text);
                if (root is JsonObject obj)
                {
                    if (obj.ContainsKey("WebServerPassword")) obj.Remove("WebServerPassword");
                    File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    File.Delete(path);   // 不是 JSON 对象 ⇒ 不可用 ⇒ 删除
                }
                return true;
            }
            catch (JsonException)
            {
                // 备份已损坏 ⇒ 删掉它（明文不得继续留在磁盘上）；删不掉就是失败，如实返回 false
                try { File.Delete(path); return true; }
                catch { return false; }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>
        /// 把配置 JSON 处理成**可以安全落进备份**的内容（LM-085）。
        /// <para>
        /// ★ 背景：<c>SettingsHelper.AtomicWrite</c> 过去把**旧主文件原样**复制成备份。
        ///   而旧主文件可能是**加密前**的形态 —— 插件凭据是明文、Web 口令是明文 ——
        ///   于是"明文凭据"被原样留在磁盘上的**第二个文件**里，
        ///   备份的泄露面比主文件还大（fail-open）。
        /// </para>
        /// <para>
        /// ★ 这里只做两件事，**其余字段逐字保留**（备份仍然是"上一份有效内容"，仍是可回滚材料）：
        ///   <list type="number">
        ///   <item>插件凭据（<c>PluginInstances[].InputValues</c> 与 <c>Targets[]</c> 里
        ///         "看起来是凭据"的参数）⇒ 明文一律用 DPAPI 加密；已是密文的原样保留；</item>
        ///   <item>Web 口令（<c>WebServerPassword</c>）⇒ 脱敏成空串
        ///         （主文件的契约本就是"口令永不落盘"，备份必须与主文件同级）。</item>
        ///   </list>
        /// </para>
        /// <para>
        /// ★ DPAPI 失败时**绝不写明文**：宁可把这个值从备份里去掉（备份少一个凭据），
        ///   也不能把明文再落到磁盘上。
        /// </para>
        /// </summary>
        internal static string SanitizeSettingsJsonForBackup(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;

            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonObject obj) return json;   // 不是 JSON 对象 => 原样返回（由调用方裁决）

            // ① Web 口令：脱敏成空串（与主文件 WebPasswordJsonConverter 的契约一致）
            if (obj.TryGetPropertyValue("WebServerPassword", out JsonNode? pwdNode) &&
                pwdNode is JsonValue pwdValue &&
                pwdValue.TryGetValue<string>(out string? plainPwd) &&
                !string.IsNullOrEmpty(plainPwd))
            {
                obj["WebServerPassword"] = "";
            }

            // ② 插件凭据：明文一律 DPAPI 加密
            if (obj.TryGetPropertyValue("PluginInstances", out JsonNode? instancesNode) &&
                instancesNode is JsonArray instances)
            {
                foreach (JsonNode? instance in instances)
                {
                    if (instance is not JsonObject instanceObj) continue;

                    SanitizeSecretValues(instanceObj, "InputValues");

                    if (instanceObj.TryGetPropertyValue("Targets", out JsonNode? targetsNode) &&
                        targetsNode is JsonArray targets)
                    {
                        foreach (JsonNode? target in targets)
                        {
                            if (target is JsonObject targetObj) SanitizeSecretValues(targetObj, null);
                        }
                    }
                }
            }

            return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// 把一个"参数字典"里的凭据值改成 DPAPI 密文（LM-085）。
        /// <para>
        /// ★ <paramref name="propertyName"/> 为 null 时就地处理该对象本身（<c>Targets</c> 的每个元素），
        ///   否则处理它下面的那个属性（<c>InputValues</c>）。
        /// </para>
        /// </summary>
        private static void SanitizeSecretValues(JsonObject owner, string? propertyName)
        {
            JsonObject map;
            if (propertyName == null)
            {
                map = owner;
            }
            else
            {
                if (!owner.TryGetPropertyValue(propertyName, out JsonNode? node) || node is not JsonObject obj) return;
                map = obj;
            }

            // 先把键抄出来：下面要改 map 本身，不能一边遍历一边改
            var keys = new List<string>();
            foreach (KeyValuePair<string, JsonNode?> kv in map) keys.Add(kv.Key);

            foreach (string key in keys)
            {
                if (!PluginSecretDictionaryConverter.IsSecretKey(key)) continue;

                JsonNode? node = map[key];
                if (node is not JsonValue value) continue;
                if (!value.TryGetValue<string>(out string? text) || string.IsNullOrEmpty(text)) continue;
                if (ProtectedSecret.IsProtected(text)) continue;   // 已经是密文 => 原样保留

                try
                {
                    map[key] = ProtectedSecret.Protect(text);
                }
                catch (Exception)
                {
                    // DPAPI 失败 ⇒ 绝不能把明文写进备份：宁可这个值不备份
                    map.Remove(key);
                }
            }
        }

        /// <summary>生成一份随机的盐（base64）。</summary>
        internal static string CreateWebPasswordSalt()
        {
            byte[] salt = RandomNumberGenerator.GetBytes(WebPasswordSaltBytes);
            return Convert.ToBase64String(salt);
        }

        /// <summary>PBKDF2-SHA256（固定迭代次数，输出定长 base64）。</summary>
        internal static string ComputeWebPasswordHash(string plain, string salt)
        {
            byte[] saltBytes;
            try { saltBytes = string.IsNullOrEmpty(salt) ? Array.Empty<byte>() : Convert.FromBase64String(salt); }
            catch (FormatException) { saltBytes = Array.Empty<byte>(); }

            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(plain ?? ""),
                saltBytes,
                WebPasswordIterations,
                HashAlgorithmName.SHA256,
                WebPasswordHashBytes);
            return Convert.ToBase64String(hash);
        }
    }

    public class MonitorItemConfig
    {
        // ★★★ 核心优化：使用字符串驻留池解决内存浪费 ★★★
        private string _key = "";

        // 极简缓存：复用首个实例作为原型 (0 GC)
        private static readonly ConcurrentDictionary<string, MonitorItemConfig> _protoCache = new ConcurrentDictionary<string, MonitorItemConfig>();

        // [Optimization] Cache keys to avoid string concatenation in hot paths
        [JsonIgnore] public string CachedPropLabelKey { get; private set; } = "";
        [JsonIgnore] public string CachedPropShortLabelKey { get; private set; } = "";
        [JsonIgnore] public string CachedUIGroup { get; private set; } = "";
        [JsonIgnore] public string CachedItemsKey { get; private set; } = ""; // Items.Key
        [JsonIgnore] public string CachedGroupsKey { get; private set; } = ""; // Groups.UIGroup
        [JsonIgnore] public string CachedDashColorKey { get; private set; } = "";
        [JsonIgnore] public string CachedDashUnitKey { get; private set; } = "";

        public string Key 
        { 
            get => _key; 
            set 
            {
                string k = UIUtils.Intern(value ?? "");
                if (_key == k) return;
                _key = k;

                // 极简模式：直接从缓存的原型对象拷贝所有属性，无需额外对象分配
                if (_protoCache.TryGetValue(k, out var p))
                {
                    CachedPropLabelKey = p.CachedPropLabelKey;
                    CachedPropShortLabelKey = p.CachedPropShortLabelKey;
                    CachedItemsKey = p.CachedItemsKey;
                    CachedUIGroup = p.CachedUIGroup;
                    CachedGroupsKey = p.CachedGroupsKey;
                    CachedDashColorKey = p.CachedDashColorKey;
                    CachedDashUnitKey = p.CachedDashUnitKey;
                    return;
                }

                // 首次计算
                CachedPropLabelKey = UIUtils.Intern("PROP.Label." + _key);
                CachedPropShortLabelKey = UIUtils.Intern("PROP.ShortLabel." + _key);
                CachedItemsKey = UIUtils.Intern("Items." + _key);

                // ★ "MEM.Commit.Load"（内存已提交）与 "MEM.Load"（物理内存）同属 HOST，
                //   两条路径必须同步，否则新项会掉进一个标题为 "MEM" 的独立分组。
                if (_key == "MEM.Load" || _key == "MEM.Commit.Load" || _key == "MOBO.Temp" || _key == "DISK.Temp" || _key == "CASE.Fan"|| _key == "FPS")
                    CachedUIGroup = "HOST";
                else if (_key.StartsWith("DASH."))
                {
                    CachedUIGroup = "DASH";
                    string sub = _key.Substring(5);
                    CachedDashColorKey = UIUtils.Intern(sub + ".Color");
                    CachedDashUnitKey = UIUtils.Intern(sub + ".Unit");
                }
                else
                {
                    // ★★★ [按卡拆组] 带序号的显卡 Key 归到「按卡分组」("GPU.0" / "GPU.1"),
                    //   这样每张卡的 6 个指标会被包在自己的小标题下面, 面板上不再并排
                    //   出现多组重复的显卡型号。
                    //   非显卡项 (CPU / MEM / HOST / DASH / ...) 保持原逻辑不变。
                    if (GpuGroupPolicy.TryGetPerCardGroupKey(_key, out string gpuGroup))
                        CachedUIGroup = UIUtils.Intern(gpuGroup);
                    else
                        CachedUIGroup = UIUtils.Intern(_key.Split('.')[0]);
                }

                CachedGroupsKey = UIUtils.Intern("Groups." + CachedUIGroup);
                
                // 存入缓存
                _protoCache[k] = this;
            }
        }
        // ★★★ [优化] 分离用户配置与系统动态值 ★★★
        // UserLabel: 用户手动设置的名称 (持久化)。为空表示跟随系统。
        public string UserLabel { get; set; } = ""; 
        public string TaskbarLabel { get; set; } = "";

        // DynamicLabel: 插件运行时计算的名称 (不持久化)。
        [JsonIgnore]
        public string DynamicLabel { get; set; } = "";
        
        [JsonIgnore]
        public string DynamicTaskbarLabel { get; set; } = "";

        // DisplayLabel: 最终显示名称 (优先显示用户设置，否则显示动态值)
        [JsonIgnore]
        public string DisplayLabel => !string.IsNullOrEmpty(UserLabel) ? UserLabel : DynamicLabel;
        
        [JsonIgnore]
        public string DisplayTaskbarLabel => !string.IsNullOrEmpty(TaskbarLabel) ? TaskbarLabel : DynamicTaskbarLabel;

        // ★★★ [新增] 自定义单位配置 ★★★
        // null/"Auto" = 自动(默认), "" = 不显示, "{u}/s" = 自定义格式
        public string UnitPanel { get; set; } = null; 
        public string UnitTaskbar { get; set; } = null;
        public bool VisibleInPanel { get; set; } = true;
        public bool VisibleInTaskbar { get; set; } = false;
        
        public int SortIndex { get; set; } = 0;
        // ★★★ 新增：任务栏独立排序索引 ★★★
        public int TaskbarSortIndex { get; set; } = 0;
       

        // ★★★ 新增：统一的分组属性 ★★★
        // 所有界面（主界面、设置页、菜单）都统一调用这个属性来决定它属于哪个组
        // 从而避免了在 UI 代码里到处写 if else
        [JsonIgnore]
        public string UIGroup 
        {
            get 
            {
                // [Optimization] Return cached value if available
                if (!string.IsNullOrEmpty(CachedUIGroup)) return CachedUIGroup;

                // Fallback for uninitialized state (should be rare)
                // ★ 与上面 CachedUIGroup 的判断保持一致（"MEM.Commit.Load" 同属 HOST）。
                if (Key == "MEM.Load" ||
                    Key == "MEM.Commit.Load" ||
                    Key == "MOBO.Temp" || 
                    Key == "DISK.Temp" || 
                    Key == "CASE.Fan"|| 
                    Key == "FPS") 
                { 
                    return "HOST"; 
                } 
                
                if (Key.StartsWith("DASH."))
                {
                    return "DASH";
                }

                // ★★★ [按卡拆组] 必须与 Key setter 里的推导规则保持完全一致 ★★★
                if (GpuGroupPolicy.TryGetPerCardGroupKey(Key, out string gpuGroup))
                    return gpuGroup;

                return Key.Split('.')[0]; 
            }
        }
    }

    public class ThresholdsSet
    {
        public ValueRange Load { get; set; } = new ValueRange { Warn = 60, Crit = 85 };
        public ValueRange Temp { get; set; } = new ValueRange { Warn = 50, Crit = 70 };
        public ValueRange DiskIOMB { get; set; } = new ValueRange { Warn = 2, Crit = 8 };
        public ValueRange NetUpMB { get; set; } = new ValueRange { Warn = 1, Crit = 2 };
        public ValueRange NetDownMB { get; set; } = new ValueRange { Warn = 2, Crit = 8 };
        public ValueRange DataUpMB { get; set; } = new ValueRange { Warn = 512, Crit = 1024 };
        public ValueRange DataDownMB { get; set; } = new ValueRange { Warn = 2048, Crit = 5096 };
    }

    public class ValueRange
    {
        public double Warn { get; set; } = 0;
        public double Crit { get; set; } = 0;
    }

    public class PluginInstanceConfig
    {
        public string Id { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public bool Enabled { get; set; } = false;
        public int CustomInterval { get; set; } = 0; // 自定义刷新频率 (0 = 使用模版默认)
        
        // 全局参数 (Scope="global")
        // ★ 转换器负责把"必须恢复原文"的凭据（API Key / Token / 口令…）落盘成 DPAPI 密文，
        //   读取时再解开 —— 内存里始终是明文，插件执行路径完全不受影响。
        [JsonConverter(typeof(PluginSecretDictionaryConverter))]
        public Dictionary<string, string> InputValues { get; set; } = new Dictionary<string, string>();
        
        // 目标列表 (Scope="target")
        // 每个元素是一个 Dictionary，存储该目标的所有 target 参数
        [JsonConverter(typeof(PluginSecretListConverter))]
        public List<Dictionary<string, string>> Targets { get; set; } = new List<Dictionary<string, string>>();

        // [Optimization] Runtime key cache to avoid redundant string generation in PluginExecutor
        // Map: TargetIndex_OutputKey -> KeysBundle
        [JsonIgnore] 
        public ConcurrentDictionary<string, PluginOutputKeys> KeyCache { get; } = new ConcurrentDictionary<string, PluginOutputKeys>();
    }

    // ==================================================================
    //  Web 服务的监听范围模式（契约 W12）
    // ==================================================================

    /// <summary>
    /// Web 服务的监听范围。
    /// </summary>
    public enum WebServerBindMode
    {
        /// <summary>
        /// 旧配置占位：<c>settings.json</c> 里没有该字段时反序列化得到它，
        /// 由 <see cref="Settings.Load"/> 按 W12 规则解析成 <see cref="Loopback"/> 或 <see cref="Lan"/>。
        /// </summary>
        Unset = 0,

        /// <summary>仅本机：只监听 127.0.0.1（默认，新安装一律如此）。</summary>
        Loopback = 1,

        /// <summary>局域网：监听全部网卡，**必须**配置非空访问口令。</summary>
        Lan = 2,
    }

    // ==================================================================
    //  口令字段的落盘转换器（明文永不落盘）
    // ==================================================================

    /// <summary>
    /// 访问口令字段的 JSON 转换器：<b>读</b>得到旧配置里的明文（供惰性迁移），<b>写</b>永远只写空串。
    /// </summary>
    /// <remarks>
    /// ★ 这是"明文不得再写回 settings.json"的**结构性**保证：
    ///   无论调用方怎么设置 <c>WebServerPassword</c>，序列化出来的永远是 <c>""</c>。
    /// ★ 反序列化仍然读得到明文 —— 否则旧配置升级后口令直接丢失（用户登不进去）。
    /// </remarks>
    internal sealed class WebPasswordJsonConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return "";
            if (reader.TokenType != JsonTokenType.String) return "";   // 形态不对 ⇒ 当作没有口令（不抛，避免整份配置被判损坏）
            return reader.GetString() ?? "";
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            // ★ 明文永不落盘
            writer.WriteStringValue("");
        }
    }

    // ==================================================================
    //  DPAPI 保护的凭据（契约 W9 / LM-023）
    // ==================================================================

    /// <summary>
    /// 需要**恢复原文**的凭据的容器：DPAPI **CurrentUser** 作用域。
    /// </summary>
    /// <remarks>
    /// ★ 作用域定义（LM-023 要求的"DPAPI 作用域未定义"在此闭合）：
    ///   <list type="bullet">
    ///   <item><b>CurrentUser</b>（本实现）：密文只能用**当前 Windows 用户**解开；
    ///         换用户 / 换机器都解不开 —— 这正是我们要的用户隔离。</item>
    ///   <item><b>LocalMachine</b>（禁止使用）：任何本机用户都能解开 ⇒ 等于没有隔离。</item>
    ///   </list>
    ///   实现上对应 Win32 <c>CryptProtectData</c> 且**不**置 <c>CRYPTPROTECT_LOCAL_MACHINE</c>。
    /// ★ 解密失败（换机器 / 换用户 / 数据损坏）必须**明确报错**：
    ///   调用方拿到 false + 错误文本，**不得**静默变成空值、不得删除原凭据。
    /// </remarks>
    public static class ProtectedSecret
    {
        /// <summary>受保护值的前缀（用于识别与版本演进）。</summary>
        public const string Prefix = "dpapi:v1:";

        /// <summary>CRYPTPROTECT_UI_FORBIDDEN：不弹 UI（无人值守场景必须）。</summary>
        private const int CryptProtectUiForbidden = 0x1;

        // ★ 刻意**不**使用 CRYPTPROTECT_LOCAL_MACHINE（0x4）：那会让任何本机用户都能解密。
        private const int CryptProtectLocalMachine = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DataBlob pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>明文 → 密文（CurrentUser 作用域）。</summary>
        public static string Protect(string plaintext)
        {
            if (plaintext == null) return "";
            byte[] data = Encoding.UTF8.GetBytes(plaintext);

            IntPtr inBuf = Marshal.AllocHGlobal(Math.Max(data.Length, 1));
            try
            {
                Marshal.Copy(data, 0, inBuf, data.Length);
                var inBlob = new DataBlob { cbData = data.Length, pbData = inBuf };

                // ★ 作用域：只给 UI_FORBIDDEN，**不给** LOCAL_MACHINE ⇒ CurrentUser。
                int flags = CryptProtectUiForbidden;
                if ((flags & CryptProtectLocalMachine) != 0)
                    throw new InvalidOperationException("DPAPI 作用域必须是 CurrentUser（禁止 LocalMachine）");

                if (!CryptProtectData(ref inBlob, "LiteMonitor", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out var outBlob))
                    throw new CryptographicException("DPAPI 保护失败（Win32 " + Marshal.GetLastWin32Error() + "）");

                try
                {
                    byte[] cipher = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, cipher, 0, outBlob.cbData);
                    return Prefix + Convert.ToBase64String(cipher);
                }
                finally { FreeBlob(outBlob); }
            }
            finally { Marshal.FreeHGlobal(inBuf); }
        }

        /// <summary>
        /// 密文 → 明文。失败时返回 false 并给出**明确**错误原因。
        /// </summary>
        /// <remarks>
        /// ★ 调用方**不得**在失败时把凭据当成空值继续用，也**不得**删掉原文：
        ///   必须把错误显式暴露给用户（换机器 / 换用户 → 需要重新填写）。
        /// </remarks>
        public static bool TryUnprotect(string value, out string plaintext, out string error)
        {
            plaintext = "";
            error = "";

            if (!IsProtected(value))
            {
                error = "该值不是受 DPAPI 保护的凭据";
                return false;
            }

            byte[] cipher;
            try
            {
                cipher = Convert.FromBase64String(value.Substring(Prefix.Length));
            }
            catch (FormatException ex)
            {
                error = "受保护的凭据已损坏（不是合法 Base64）：" + ex.Message;
                return false;
            }

            IntPtr inBuf = Marshal.AllocHGlobal(Math.Max(cipher.Length, 1));
            try
            {
                Marshal.Copy(cipher, 0, inBuf, cipher.Length);
                var inBlob = new DataBlob { cbData = cipher.Length, pbData = inBuf };

                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                       CryptProtectUiForbidden, out var outBlob))
                {
                    int win32 = Marshal.GetLastWin32Error();
                    error = "无法解密受保护的凭据（可能换了机器或换了 Windows 用户，Win32 " + win32 + "）";
                    return false;
                }

                try
                {
                    byte[] plain = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, plain, 0, outBlob.cbData);
                    plaintext = Encoding.UTF8.GetString(plain);
                    return true;
                }
                finally { FreeBlob(outBlob); }
            }
            catch (Exception ex)
            {
                error = "无法解密受保护的凭据：" + ex.Message;
                return false;
            }
            finally { Marshal.FreeHGlobal(inBuf); }
        }

        /// <summary>该值是否是受保护的凭据。</summary>
        public static bool IsProtected(string value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

        private static void FreeBlob(DataBlob blob)
        {
            if (blob.pbData != IntPtr.Zero)
            {
                try { LocalFree(blob.pbData); } catch { }
            }
        }
    }

    // ==================================================================
    //  插件凭据的落盘转换器（透明加解密）
    // ==================================================================

    /// <summary>
    /// 插件参数字典的转换器：把"看起来是凭据"的参数用 DPAPI（CurrentUser）加密后落盘。
    /// </summary>
    /// <remarks>
    /// ★ 内存里的值**始终是明文**（读的时候解、写的过程中加），
    ///   插件执行路径（<c>PluginExecutor</c>）完全不受影响。
    /// ★ 解密失败时**保留原密文**并向上层暴露（由 <c>Settings.ApplyPluginCredentialState</c>
    ///   停用该实例并报错），绝不静默变成空值。
    /// </remarks>
    internal sealed class PluginSecretDictionaryConverter : JsonConverter<Dictionary<string, string>>
    {
        /// <summary>会被当作凭据加密的参数名（小写比较）。</summary>
        internal static readonly string[] SecretKeyNames =
        {
            "api_key", "apikey", "api-key", "access_key", "accesskey", "access-token", "access_token",
            "accesstoken", "token", "secret", "client_secret", "clientsecret", "password", "passwd",
            "authorization", "auth_token", "authtoken", "private_key", "privatekey", "credential"
        };

        /// <summary>参数名是否属于"必须恢复原文的凭据"。</summary>
        internal static bool IsSecretKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim().ToLowerInvariant();
            foreach (var k in SecretKeyNames)
            {
                if (n == k) return true;
            }
            return false;
        }

        public override Dictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return ReadDict(ref reader);
        }

        /// <summary>读取一个参数字典（供列表转换器复用）。</summary>
        internal static Dictionary<string, string> ReadDict(ref Utf8JsonReader reader)
        {
            var result = new Dictionary<string, string>();
            if (reader.TokenType == JsonTokenType.Null) return result;
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("插件参数必须是 JSON 对象");

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return result;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("插件参数 JSON 结构非法");

                string name = reader.GetString() ?? "";
                if (!reader.Read()) throw new JsonException("插件参数 JSON 结构非法");

                string raw = ReadScalar(ref reader);
                string value = raw;

                if (ProtectedSecret.IsProtected(raw))
                {
                    if (ProtectedSecret.TryUnprotect(raw, out string plain, out string err))
                    {
                        value = plain;
                    }
                    else
                    {
                        // ★ 解密失败：保留密文原样（不删除原凭据），把错误暴露给上层
                        value = raw;
                        PluginCredentialState.RecordDecryptFailure(name, err);
                    }
                }

                result[name] = value;
            }

            throw new JsonException("插件参数 JSON 结构非法");
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
        {
            WriteDict(writer, value);
        }

        /// <summary>写出一个参数字典（供列表转换器复用）。</summary>
        internal static void WriteDict(Utf8JsonWriter writer, Dictionary<string, string> value)
        {
            writer.WriteStartObject();
            if (value != null)
            {
                foreach (var kv in value)
                {
                    writer.WritePropertyName(kv.Key);
                    string v = kv.Value ?? "";
                    if (IsSecretKey(kv.Key) && !ProtectedSecret.IsProtected(v) && v.Length > 0)
                    {
                        try { writer.WriteStringValue(ProtectedSecret.Protect(v)); }
                        catch (Exception) { writer.WriteStringValue(v); }   // DPAPI 失败 ⇒ 原样写（不丢数据）
                    }
                    else
                    {
                        writer.WriteStringValue(v);
                    }
                }
            }
            writer.WriteEndObject();
        }

        /// <summary>读取一个标量值（其它类型按原始文本保留）。</summary>
        internal static string ReadScalar(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String: return reader.GetString() ?? "";
                case JsonTokenType.Null: return "";
                case JsonTokenType.True: return "true";
                case JsonTokenType.False: return "false";
                case JsonTokenType.Number: return reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                default: return "";
            }
        }
    }

    /// <summary>插件目标列表的转换器（逐项交给字典转换器处理）。</summary>
    internal sealed class PluginSecretListConverter : JsonConverter<List<Dictionary<string, string>>>
    {
        public override List<Dictionary<string, string>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var list = new List<Dictionary<string, string>>();
            if (reader.TokenType == JsonTokenType.Null) return list;
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("插件目标列表必须是 JSON 数组");

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return list;
                list.Add(PluginSecretDictionaryConverter.ReadDict(ref reader));
            }

            throw new JsonException("插件目标列表 JSON 结构非法");
        }

        public override void Write(Utf8JsonWriter writer, List<Dictionary<string, string>> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            if (value != null)
            {
                foreach (var d in value)
                {
                    PluginSecretDictionaryConverter.WriteDict(writer, d);
                }
            }
            writer.WriteEndArray();
        }
    }

    /// <summary>插件凭据解密失败的收集处（进程内，仅用于把错误显式暴露出来）。</summary>
    internal static class PluginCredentialState
    {
        private static readonly List<string> _failures = new List<string>();
        private static readonly object _gate = new object();

        internal static void RecordDecryptFailure(string name, string error)
        {
            lock (_gate)
            {
                _failures.Add((string.IsNullOrEmpty(name) ? "(未命名参数)" : name) + "：" + error);
            }
        }

        internal static string[] TakeFailures()
        {
            lock (_gate)
            {
                var copy = _failures.ToArray();
                _failures.Clear();
                return copy;
            }
        }
    }
}
