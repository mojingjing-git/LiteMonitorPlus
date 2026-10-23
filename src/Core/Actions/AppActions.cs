using System;
using System.Drawing;
using System.Net;
using System.Windows.Forms;
using LiteMonitor.src.UI;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer; // ★★★ 引用 WebServer 命名空间 ★★★
using LiteMonitor.src.Plugins;
using System.Linq;
using System.Collections.Generic;

namespace LiteMonitor.src.Core.Actions
{
    /// <summary>
    /// 全局动作执行器
    /// 封装所有“修改配置后需要立即生效”的业务逻辑
    /// 供 MenuManager (右键菜单) 和 SettingsForm (设置中心) 共同调用
    /// </summary>
    public static class AppActions
    {

        // ★★★ 新增：全局应用入口 ★★★
        public static void ApplyAllSettings(Settings cfg, MainForm mainForm, UIController ui, bool applyAutoStart = false)
        {
            // 0. [核心修复] 在任何逻辑执行前，记录当前的渲染模式
            // 防止 ApplyLanguage -> ApplyTheme 提前重置了 ui 的状态导致无法判断是否切换了模式
            bool wasHorizontal = ui.IsLayoutHorizontal;

            // 1. 语言变更 (注意：传 null 给 ui，防止它在此提前触发 ApplyTheme)
            ApplyLanguage(cfg, null, mainForm); 

            // 2. 系统级设置
            // 开机启动会调用 schtasks 写计划任务，只能在开关变化时执行，避免每次应用设置都触发杀软拦截。
            if (applyAutoStart) ApplyAutoStart(cfg);
            ApplyWindowAttributes(cfg, mainForm); // 基础窗口属性

            // 3. 界面布局与主题 (传入原始状态以判断是否需要居中)
            ApplyThemeAndLayout(cfg, ui, mainForm, wasHorizontal); 
            
            // 4. 子模块特定设置
            ApplyMonitorLayout(cfg, ui, mainForm); // 监控项、硬件源变更
            ApplyTaskbarStyle(cfg, ui);       // 任务栏样式

            // ★★★ 5. [新增] 应用网页服务设置 (重启服务以应用端口变更) ★★★
            ApplyWebServer(cfg);

            // 6. 应用插件设置 (重载实例并清除缓存)
            PluginManager.Instance.Reload(cfg);

            // 7. 可见性 (最后执行，避免闪烁)
            ApplyVisibility(cfg, mainForm);
        }

        
        // =============================================================
        // 1. 核心系统动作 (语言、开机自启)
        // =============================================================

        public static void ApplyLanguage(Settings cfg, UIController? ui, MainForm form)
        {
            // 1. 加载语言资源
            LanguageManager.Load(cfg.Language);
            
            // 2. 同步自定义名称 (防止语言包覆盖了用户的自定义重命名)
            cfg.SyncToLanguage();

            // 3. 刷新主题（这也同时刷新了字体、布局计算、Timer间隔等）
            ui?.ApplyTheme(cfg.Skin);
            
            // 4. 重建右键菜单（更新文字）
            form.RebuildMenus();
            
            // 5. 刷新任务栏窗口（如果有）
            ReloadTaskbarWindows();
        }

        public static void ApplyAutoStart(Settings cfg)
        {
            AutoStart.Set(cfg.AutoStart);
        }

        // =============================================================
        // 2. 窗口行为与属性 (置顶、穿透、自动隐藏、透明度)
        // =============================================================

        public static void ApplyWindowAttributes(Settings cfg, MainForm form)
        {
            // 置顶
            if (form.TopMost != cfg.TopMost) form.TopMost = cfg.TopMost;
            form.RefreshTopMost(forceReinsert: true);
            
            // 鼠标穿透
            form.SetClickThrough(cfg.ClickThrough);
            
            // 自动隐藏 (需要启动或停止 Timer)
            if (cfg.AutoHide) form.InitAutoHideTimer();
            else form.StopAutoHideTimer();

            // 透明度
            if (Math.Abs(form.Opacity - cfg.Opacity) > 0.01)
                form.Opacity = Math.Clamp(cfg.Opacity, 0.1, 1.0);

            // 5. 刷新菜单 (确保透明度、置顶等勾选状态同步更新)
            form.RebuildMenus();
        }

        // =============================================================
        // 3. 窗口可见性管理 (主界面、托盘、任务栏) - 含防呆
        // =============================================================

        public static void ApplyVisibility(Settings cfg, MainForm form)
        {
            // --- 防呆逻辑 ---
            // 检查三者（任务栏显示、隐藏界面、托盘图标）是否至少保留一个
            if (!cfg.ShowTaskbar && cfg.HideMainForm && cfg.HideTrayIcon)
            {
                // 如果全关了，强制打开托盘图标
                cfg.HideTrayIcon = false; 
                // (注意：配置值的持久化Save由调用方负责，或者在这里Save也可以，但为了逻辑分离通常由调用方Save)
            }

            // --- 执行动作 ---
            
            // 1. 托盘
            if (cfg.HideTrayIcon) form.HideTrayIcon();
            else form.ShowTrayIcon();

            // 2. 主窗口
            // HideMainForm = true 意味着我们要执行 Hide()
            if (cfg.HideMainForm) form.Hide();
            else form.Show();

            // 3. 任务栏窗口
            form.ToggleTaskbar(cfg.ShowTaskbar);
            
            // 4. 刷新菜单
            // 因为可见性改变可能影响菜单项的勾选状态（尤其是防呆逻辑修正后），也可能影响“任务栏显示”等选项的状态
            form.RebuildMenus(); 
        }

        // =============================================================
        // 4. 外观与布局 (主题、缩放、宽度、刷新率、显示模式)
        // =============================================================

        public static void ApplyThemeAndLayout(Settings cfg, UIController? ui, MainForm form, bool? wasHorizontal = null, bool retainData = false)
        {
            // 1. 确定是否发生了模式切换
            // 如果外部没传 wasHorizontal，则尝试从 ui 获取当前渲染状态
            bool oldMode = wasHorizontal ?? (ui?.IsLayoutHorizontal ?? cfg.HorizontalMode);
            bool modeChanged = (oldMode != cfg.HorizontalMode);

            Point? center = null;
            if (modeChanged && form.Visible && form.WindowState == FormWindowState.Normal)
            {
                center = new Point(form.Left + form.Width / 2, form.Top + form.Height / 2);
            }

            // 2. 应用主题
            ui?.ApplyTheme(cfg.Skin, retainData);
            
            // 3. 强制立即刷新布局以获得正确的新尺寸 (关键：否则 form.Width 还是旧的)
            //ui?.RebuildLayout();
            
            // 如果切换了横竖屏模式，菜单结构会变，需要重建
            form.RebuildMenus();
            ReloadTaskbarWindows();

            // 4. 执行居中重定位
            if (center.HasValue)
            {
                form.ApplyRoundedCorners();
                form.Location = new Point(center.Value.X - form.Width / 2, center.Value.Y - form.Height / 2);
                form.EnsureVisibleAndSavePos();
            }
        }

        // =============================================================
        // 5. 数据源与监控项 (磁盘/网络源、监控开关)
        // =============================================================

        public static void ApplyMonitorLayout(Settings cfg, UIController? ui, MainForm form, bool rebuildMenus = true)
        {
            // ★★★ [多显卡] 同步显卡监控项 ★★★
            // 1) 把 MonitoredGpuCount 落到 MonitorItems 上 (生成 GPU.{序号}.{指标})
            // 2) 按实际检测到的显卡数夹紧 —— 避免选 8 张但机器只有 2 张时产生空白监控项
            // 必须在本方法重建布局之前执行, 否则 UI 读到的还是旧的 MonitorItems。
            if (cfg != null)
            {
                // ★ 非阻塞取实际卡数 (见 Step 1.5): 绝不 lock 硬件锁 ★
                int detectedGpuCount = HardwareMonitor.TryGetDetectedGpuCount();

                // 探测不到时 (0) 不做夹紧, 但仍然要按配置数量重新生成监控项 ——
                // 否则用户改了「监控显卡数量」保存后不会生效。
                int cap = detectedGpuCount > 0
                    ? detectedGpuCount
                    : Math.Max(1, cfg.MonitoredGpuCount);

                GpuItemSync.Ensure(cfg, availableGpuCount: cap);
            }

            // ★★★ [新增] 动态检查硬件开启需求 (热切换) ★★★
            // 如果用户开启了风扇/水泵，自动开启 USB 控制器；反之关闭
            HardwareMonitor.Instance?.RefreshHardwareConfig();

            // 重新计算哪些格子要显示 (主界面和任务栏的数据列都会重建)
            ui?.RebuildLayout();
            
            // 因为监控项变了（比如开启了GPU），菜单里的勾选状态也得变
            // 优化：在右键菜单直接操作时，无需重建菜单，避免冗余
            if (rebuildMenus)
            {
                form.RebuildMenus();
            }
            
            // 任务栏窗口的内容也取决于监控项配置，必须刷新
            ReloadTaskbarWindows();
        }

        // =============================================================
        // 6. 任务栏样式 (字体、对齐、紧凑模式)
        // =============================================================
        
        public static void ApplyTaskbarStyle(Settings cfg, UIController? ui)
        {
            // 1. 刷新所有任务栏窗口
            // 这一步会触发 TaskbarForm.ReloadLayout()，进而自动读取 cfg 中的新颜色和穿透设置
            ReloadTaskbarWindows();
            
            // 如果样式影响了主程序计算（极少情况），可解开下面注释
            ui?.ApplyTheme(cfg.Skin); 
        }

        // =============================================================
        // 7. 网页服务 (新增逻辑)
        // =============================================================

        /// <summary>
        /// 应用网页服务设置（启停 / 端口 / 凭据）。
        /// </summary>
        /// <remarks>
        /// ★ 保留无返回值的老签名给既有调用点；需要结果（提示 / 断言）用
        ///   <see cref="ApplyWebServerWithResult"/>。
        /// </remarks>
        public static void ApplyWebServer(Settings cfg)
        {
            ApplyWebServerWithResult(cfg);
        }

        /// <summary>
        /// 应用网页服务设置，并返回**确定的结果**（停止 / 重启 / 拒绝 / 失败）。
        /// </summary>
        /// <remarks>
        /// ★ R03：判定"凭据是否变了"必须用**有效凭据指纹**
        ///   （<see cref="Settings.GetWebPasswordFingerprint"/>）而不是明文 ——
        ///   迁移后的生产形态里明文恒为空串，拿明文比较**永远发现不了**
        ///   "哈希被清掉 / 哈希被换掉"这类变更。
        /// ★ R03：「局域网暴露 + 没有有效凭据」⇒ **停止服务**（并给出明确结果），
        ///   绝不"无口令继续监听"；判定与冷启动守卫共用
        ///   <see cref="WebServerAccessPolicy"/>，运行中的变更绕不过冷启动约束。
        /// ★ 停止 / 重启都会连带撤销全部主会话、票据与已建立的 WebSocket 连接
        ///   （见 <c>WebSessionManager.Stop()</c>）—— 凭据变了还留着旧连接等于撤销形同虚设。
        /// </remarks>
        public static WebServerApplyResult ApplyWebServerWithResult(Settings cfg)
        {
            var result = new WebServerApplyResult();
            var server = LiteWebServer.Instance;
            if (server == null || cfg == null)
            {
                result.ServiceRunning = false;
                return result;
            }

            bool shouldRun = cfg.WebServerEnabled;
            bool isRunning = server.IsRunning;
            int targetPort = cfg.WebServerPort;
            int currentPort = server.CurrentRunningPort;

            // ★ 有效凭据指纹："" = 没有有效凭据
            string targetFingerprint = cfg.GetWebPasswordFingerprint();
            string currentFingerprint = server.CurrentCredentialFingerprint;
            bool credentialChanged = !string.Equals(targetFingerprint, currentFingerprint, StringComparison.Ordinal);

            // ★ 监听范围是否变了：比的是"配置要的"与"运行中**实际生效**的"。
            //   只改监听范围（不动端口、不动凭据）同样必须重绑 ——
            //   否则用户把「局域网」收回「仅本机」，socket 仍绑在 0.0.0.0 上
            //   （对外暴露状态与界面显示相反：界面写着 127.0.0.1，实际却是全网卡）。
            //   反向（仅本机 → 局域网）同样要重绑，否则服务仍旧只监听 127.0.0.1。
            // ★ 归一化口径与 LiteWebServer.Start 完全一致：只有 Lan 才是局域网，其余（含 Unset）按仅本机。
            WebServerBindMode targetBindMode = cfg.WebServerBindMode == WebServerBindMode.Lan
                ? WebServerBindMode.Lan
                : WebServerBindMode.Loopback;
            bool bindModeChanged = server.CurrentBindMode.HasValue
                && server.CurrentBindMode.Value != targetBindMode;

            // ★ 运行时监听策略：配置要求监听局域网，**或**运行时实际绑在非回环地址上
            bool runtimeLanExposed = server.IsLanExposed;

            // ★ 注意：WebFpsEnabled 不需要重启服务。
            //   它只在"网页版帧率"开关里参与 FPS 需求判定（WebFpsDemand），
            //   而需求判定是每次实时读取配置的，改完立即生效。
            //   反过来，服务 Stop/重启会清空全部 Web 需求（连接 + HTTP 租期），见 WebSessionManager.Stop()。

            // ★ 「局域网暴露 + 没有有效凭据」⇒ 停止服务（确定行为，不是"继续监听"）
            if (shouldRun && WebServerAccessPolicy.IsLanAccessForbidden(cfg, runtimeLanExposed))
            {
                if (isRunning)
                {
                    // ★ Stop 会连带撤销全部会话 / 票据 / 已建立的 WS 连接
                    server.Stop();
                }
                result.RejectedLanWithoutPassword = true;
                result.ServiceRunning = server.IsRunning;
                result.ErrorMessage = "局域网模式必须设置访问口令：网页服务已停止" +
                                      "（不会以无口令方式继续对局域网监听）。";
                System.Diagnostics.Debug.WriteLine("[WebServer] " + result.ErrorMessage);
                return result;
            }

            if (shouldRun)
            {
                if (!isRunning)
                {
                    if (!server.Start(out string err))
                    {
                        result.ErrorMessage = err;
                        System.Diagnostics.Debug.WriteLine("WebServer start failed: " + err);
                    }
                }
                else if (targetPort != currentPort || credentialChanged || bindModeChanged)
                {
                    // ★ 端口变更 / **凭据变更**（含"凭据被清掉"）/ **监听范围变更** 均触发重启
                    //   ⇒ 重启会撤销全部会话 / 票据 / WS 连接
                    server.Stop();
                    if (!server.Start(out string err))
                    {
                        result.ErrorMessage = err;
                        System.Diagnostics.Debug.WriteLine("WebServer restart failed: " + err);
                    }
                    else
                    {
                        result.Restarted = true;
                    }
                }
                // else：端口、凭据与监听范围都没变 ⇒ **不**重启（避免每次"应用设置"都断连）
            }
            else
            {
                // 如果需要关闭，且当前正在运行 -> 关闭
                if (isRunning)
                {
                    server.Stop();
                }
            }

            result.ServiceRunning = server.IsRunning;
            return result;
        }

        /// <summary>
        /// 构造"实际监听地址"的显示文本（契约 W11：设置界面必须显示**真实**监听地址与端口）。
        /// </summary>
        /// <param name="endpoint">
        /// **真实监听端点**（<c>LiteWebServer.CurrentListenEndpoint</c>）；null ⇒ 没有可显示的端点。
        /// </param>
        /// <param name="running">服务当前是否在运行。</param>
        /// <param name="configuredPort">配置里的端口（仅在**没有真实端点**时作为端口显示值）。</param>
        /// <param name="isChinese">界面文案用中文还是英文。</param>
        /// <remarks>
        /// ★ 事实来源**只有端点一个**：地址与端口都来自
        ///   <see cref="LiteWebServer.CurrentListenEndpoint"/>（真实绑定结果）。
        ///   以前界面把地址取自**配置草稿** <c>Settings.WebServerBindMode</c>、端口却取自运行态，
        ///   口径不一致 ⇒ "用户改了但还没点应用"时界面会按草稿显示：
        ///   LAN → 仅本机 时界面写着 127.0.0.1，而 socket 其实还绑在 0.0.0.0 上
        ///   ⇒ **对外暴露状态与界面显示相反**（安全向）。
        /// ★ 没有真实端点（未运行 / 运行中但端点不可得）⇒ 地址显示 "-"，
        ///   **不**拿配置草稿冒充事实。
        /// ★ 抽成纯函数是为了能脱离 WinForms 控件单测这条规则；
        ///   界面侧（<c>MainPanelPage.BuildListenAddressText</c>）只做取端点与转发的薄壳。
        /// </remarks>
        public static string DescribeListenAddress(IPEndPoint? endpoint, bool running, int configuredPort, bool isChinese)
        {
            string address = endpoint != null ? endpoint.Address.ToString() : "-";
            int port = endpoint != null ? endpoint.Port : configuredPort;
            string state = running
                ? (isChinese ? "运行中" : "running")
                : (isChinese ? "未运行" : "not running");
            return $"{address}:{port} ({state})";
        }

        // --- 内部辅助 ---
        private static void ReloadTaskbarWindows()
        {
            // [Fix] 使用 ToList() 创建副本，防止 ReloadLayout -> TooltipHelper -> Dispose 修改 OpenForms 集合导致枚举异常
            var targets = Application.OpenForms.OfType<TaskbarForm>().ToList();
            foreach (var tf in targets)
            {
                tf.ReloadLayout();
            }
        }
    }

    /// <summary>
    /// 网页服务设置的**应用结果**（供设置界面提示与回归测试断言）。
    /// </summary>
    /// <remarks>
    /// ★ 存在的意义：把"应用设置"对网页服务的影响变成**可断言的事实**，
    ///   而不是只有一行 <c>Debug.WriteLine</c>（R03）。
    /// </remarks>
    public sealed class WebServerApplyResult
    {
        /// <summary>应用完成后服务是否在运行。</summary>
        public bool ServiceRunning { get; set; }

        /// <summary>本次应用是否**重启**了服务（端口、凭据或监听范围变化）。</summary>
        public bool Restarted { get; set; }

        /// <summary>本次应用是否拒绝了「局域网模式 + 无有效凭据」（服务已被停止）。</summary>
        public bool RejectedLanWithoutPassword { get; set; }

        /// <summary>失败 / 拒绝的原因（成功时为空串）。</summary>
        public string ErrorMessage { get; set; } = "";
    }
}
