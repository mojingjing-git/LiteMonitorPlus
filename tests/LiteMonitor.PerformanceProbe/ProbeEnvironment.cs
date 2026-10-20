using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.PerformanceProbe;

/// <summary>
/// 探针运行上下文：负责隔离配置、启动/停止被测应用实例、收集环境元数据。
///
/// 隔离要点：
/// - Settings 由探针自己 new 出来并注入 Settings 单例（Settings._instance），
///   因此不会读取、也不会写回用户常用实例的 settings.json。
/// - SettingsHelper / TrafficLogger / HardwareHistoryLogger / HardwareErrorLog
///   都以 AppContext.BaseDirectory 为根，探针的 BaseDirectory 是本项目 output\ 目录，
///   运行期文件不会落到用户的安装目录。
/// </summary>
internal sealed class ProbeEnvironment : IDisposable
{
    private readonly List<CountingHardware> _proxies = new();
    private LiteWebServerHandle? _web;

    public ProbeEnvironment(string outputDir)
    {
        OutputDir = outputDir;
        Directory.CreateDirectory(OutputDir);

        Cfg = BuildIsolatedSettings();
        InstallSettingsSingleton(Cfg);
        LanguageManager.Load("en");
        ThemeManager.Load(Cfg.Skin);

        Monitor = new HardwareMonitor(Cfg);
    }

    public string OutputDir { get; }

    public Settings Cfg { get; }

    public HardwareMonitor Monitor { get; }

    public IReadOnlyList<CountingHardware> Proxies => _proxies;

    /// <summary>硬件层是否已就绪（初始化完成且枚举到硬件）。</summary>
    public bool HardwareReady { get; private set; }

    /// <summary>就绪等待过程中的说明（失败时如实记录，不编造）。</summary>
    public string ReadyNote { get; private set; } = "";

    // ------------------------------------------------------------------
    // 配置隔离
    // ------------------------------------------------------------------

    private static Settings BuildIsolatedSettings()
    {
        var cfg = new Settings
        {
            Language = "en",
            Skin = "DarkFlat_Classic",
            AlertTempEnabled = false,
            ShowTaskbar = false,
            AutoCheckUpdate = false,
            RefreshMs = 1000,
            WebServerEnabled = false,
            WebServerPort = FindFreeTcpPort(),
            WebServerPassword = "probe-password",
            UseWinPerCounters = true,
        };
        cfg.InitDefaultItems();
        return cfg;
    }

    /// <summary>
    /// 把探针配置注入 Settings 单例。Settings.Load() 命中单例后直接返回内存对象，
    /// 因此 WebSessionManager.CheckAuth() 等内部调用也不会去读磁盘上的 settings.json。
    /// </summary>
    private static void InstallSettingsSingleton(Settings cfg)
    {
        var field = typeof(Settings).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException("Settings._instance 字段不存在，无法注入探针隔离配置。");
        field.SetValue(null, cfg);
    }

    public static int FindFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ------------------------------------------------------------------
    // 就绪等待
    // ------------------------------------------------------------------

    /// <summary>
    /// 等待硬件层初始化完成。超时或失败时如实返回 false，并把原因写进 ReadyNote。
    /// 绝不因为测不到就填 0 值冒充实测。
    /// </summary>
    public bool WaitForHardwareReady(TimeSpan timeout)
    {
        var openingField = typeof(HardwareMonitor).GetField("_isOpening", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            Thread.Sleep(200);
            bool opening = (bool)openingField.GetValue(Monitor)!;
            int count = 0;
            try { count = Monitor.ComputerInstance.Hardware.Count; } catch { }
            if (!opening && count > 0)
            {
                HardwareReady = true;
                ReadyNote = $"初始化完成，用时 {clock.Elapsed.TotalSeconds:F1}s，枚举到 {count} 个硬件对象。";
                // 主程序在启动 3 秒后还会做一次历史禁用 + GC + 工作集裁剪，等它做完再测。
                Thread.Sleep(3500);
                return true;
            }
        }
        HardwareReady = false;
        ReadyNote = $"初始化在 {timeout.TotalSeconds:F0}s 内未完成（_isOpening={openingField.GetValue(Monitor)}）。";
        return false;
    }

    // ------------------------------------------------------------------
    // LHM Update 计数
    // ------------------------------------------------------------------

    /// <summary>
    /// 把 Computer.Hardware 中的真实硬件对象换成计数代理，并强制 SensorMap 重建一次，
    /// 使 CachedGpu / CachedGpus 持有的也是代理对象（否则 HardwareMonitor 的
    /// ReferenceEquals 判断会把显卡跳过，测量就失真了）。
    ///
    /// 返回是否安装成功。失败时由调用方把"每卡 Update 次数"标记为未验证。
    /// </summary>
    public bool InstallUpdateCounters()
    {
        try
        {
            var computer = Monitor.ComputerInstance;
            var groups = GetLiveGroups(computer);
            Diagnostics["groupCount"] = groups.Count;
            Diagnostics["hardwareListIsLive"] = false;  // Computer.Hardware 是按分组现算的副本

            var settings = new ProbeSettings();
            var injected = new List<string>();
            var notInjectable = new List<string>();

            foreach (var group in groups)
            {
                var field = FindInjectableHardwareListField(group!);
                if (field == null)
                {
                    notInjectable.Add(group!.GetType().Name);
                    continue;
                }
                var list = (System.Collections.IList)field.GetValue(group)!;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is CountingHardware) continue;
                    var inner = (IHardware)list[i]!;
                    var proxy = new CountingHardware(inner, settings);
                    list[i] = proxy;
                    _proxies.Add(proxy);
                    injected.Add(proxy.Label);
                }
            }
            Diagnostics["injectedDevices"] = new System.Text.Json.Nodes.JsonArray(
                injected.Select(x => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(x)!).ToArray());
            Diagnostics["notInjectableGroups"] = new System.Text.Json.Nodes.JsonArray(
                notInjectable.Select(x => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(x)!).ToArray());
            Diagnostics["proxyCount"] = _proxies.Count;

            // 触发一次 SensorMap 重建：OnUpdateTickStarted() 检测到 MonitoredGpuCount 变化就会重建。
            int original = Cfg.MonitoredGpuCount;
            try
            {
                Cfg.MonitoredGpuCount = original == 1 ? 2 : 1;
                Monitor.UpdateAll();
                Cfg.MonitoredGpuCount = original;
                Monitor.UpdateAll();
            }
            finally
            {
                Cfg.MonitoredGpuCount = original;
            }

            // 验证：应用自己的采集循环是否真的调用到了代理。
            // ★ 生产侧现在有采集门限（HardwareSamplingCoordinator）：未到期时 UpdateAll() 会被跳过，
            //   所以这里必须等到**真实采集**发生（CollectionRounds 增长）再判断，
            //   否则会把"被门限跳过"误判成"计数代理失效"。
            ResetCounters();
            var stats = Monitor.SamplingStatistics;
            long roundsBefore = stats.CollectionRounds;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && stats.CollectionRounds == roundsBefore)
            {
                try { Monitor.UpdateIfDueAsync().GetAwaiter().GetResult(); } catch { }
                Thread.Sleep(50);
            }
            long observed = _proxies.Sum(p => p.UpdateCount);
            Diagnostics["collectionRoundsObservedDuringInstallCheck"] = stats.CollectionRounds - roundsBefore;
            Diagnostics["updateCallsObservedAfterInstall"] = observed;
            Diagnostics["countersWorking"] = observed > 0;
            return observed > 0;
        }
        catch (Exception ex)
        {
            ReadyNote += $" 计数代理安装失败：{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Computer.Hardware 是按分组现算出来的副本，真正持有硬件对象的是 Computer._groups。
    /// </summary>
    private static System.Collections.IList GetLiveGroups(IComputer computer)
    {
        var field = typeof(Computer).GetField("_groups", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("找不到 Computer._groups，无法注入计数代理。");
        return (System.Collections.IList)field.GetValue(computer)!;
    }

    /// <summary>
    /// 找出分组里存放硬件对象的集合。只有元素类型恰好是基类 Hardware 的集合才能放入代理对象。
    /// </summary>
    private static FieldInfo? FindInjectableHardwareListField(object group)
    {
        foreach (var f in group.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
        {
            if (!f.FieldType.IsGenericType) continue;
            if (f.FieldType.GetGenericTypeDefinition() != typeof(List<>)) continue;
            if (f.FieldType.GetGenericArguments()[0] != typeof(Hardware)) continue;
            return f;
        }
        return null;
    }

    /// <summary>安装过程的诊断信息（原样写进结果 JSON，便于复现时判断计数是否可信）。</summary>
    public System.Text.Json.Nodes.JsonObject Diagnostics { get; } = new();

    /// <summary>
    /// 确认代理仍在分组里（硬件重载/热插拔后可能丢失）。
    ///
    /// 这是"每卡 Update 次数"这个核心证据的运行时有效性守卫：
    /// HardwareMonitor.ReloadComputerSafe() 会重建分组集合并换掉硬件对象，
    /// 之后 deviceUpdateCounters 会变成 0 或残缺，而 JSON 看起来仍是一份正常基线。
    /// 所以除了"没有非代理对象"，还要核对**可注入分组里的硬件数量**与代理数量一致
    /// （硬件整个消失时也必须判为不可信）。
    /// </summary>
    public bool CountersStillInstalled()
    {
        try
        {
            if (_proxies.Count == 0) return false;
            int liveHardware = 0;
            var groups = GetLiveGroups(Monitor.ComputerInstance);
            foreach (var group in groups)
            {
                var field = FindInjectableHardwareListField(group!);
                if (field == null) continue;
                var list = (System.Collections.IList)field.GetValue(group)!;
                foreach (var item in list)
                {
                    liveHardware++;
                    if (item is not CountingHardware) return false;
                }
            }
            return liveHardware == _proxies.Count;
        }
        catch
        {
            return false;
        }
    }

    public void ResetCounters()
    {
        foreach (var p in _proxies) p.ResetCounters();
    }

    public JsonArray SnapshotDeviceCounters(int collectionRounds)
    {
        var array = new JsonArray();
        foreach (var p in _proxies)
        {
            array.Add(new JsonObject
            {
                ["device"] = p.Label,
                ["updateCount"] = p.UpdateCount,
                ["updatesPerCollectionRound"] = collectionRounds > 0
                    ? Math.Round((double)p.UpdateCount / collectionRounds, 4)
                    : 0,
                ["updateWallMs"] = Math.Round(p.UpdateWallMs, 3),
                ["updateWallMsPerCall"] = p.UpdateCount > 0
                    ? Math.Round(p.UpdateWallMs / p.UpdateCount, 3)
                    : 0,
            });
        }
        return array;
    }

    // ------------------------------------------------------------------
    // Web
    // ------------------------------------------------------------------

    public LiteWebServerHandle? Web => _web;

    public LiteWebServerHandle StartWeb()
    {
        if (_web != null) return _web;
        var server = new LiteMonitor.src.WebServer.LiteWebServer(Cfg);
        if (!server.Start(out string error))
        {
            ReadyNote += $" Web 服务启动失败：{error}";
            return null!;
        }
        _web = new LiteWebServerHandle(server);
        return _web;
    }

    public void StopWeb()
    {
        _web?.Dispose();
        _web = null;
    }

    // ------------------------------------------------------------------
    // 元数据
    // ------------------------------------------------------------------

    public JsonObject BuildMetadata()
    {
        var meta = new JsonObject
        {
            ["probeUtc"] = DateTime.UtcNow.ToString("o"),
            ["machineName"] = Environment.MachineName,
            ["os"] = Environment.OSVersion.ToString(),
            ["processorCount"] = Environment.ProcessorCount,
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["runtimeIdentifier"] = RuntimeInformation.RuntimeIdentifier,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["baseDirectory"] = AppContext.BaseDirectory,
            ["outputDirectory"] = OutputDir,
            ["gitHead"] = GitHeadResolver.Resolve(),
            ["mainAssemblyPath"] = typeof(Settings).Assembly.Location,
            ["mainAssemblySha256"] = ComputeSha256(typeof(Settings).Assembly.Location),
            ["probeAssemblySha256"] = ComputeSha256(typeof(ProbeEnvironment).Assembly.Location),
            ["isAdministrator"] = FpsCounter.IsAdministrator(),
            ["readyNote"] = ReadyNote,
        };

        try
        {
            var hardware = Monitor.ComputerInstance.Hardware;
            var devices = new JsonArray();
            foreach (var hw in hardware)
            {
                devices.Add(new JsonObject
                {
                    ["name"] = hw.Name,
                    ["type"] = hw.HardwareType.ToString(),
                    ["sensorCount"] = hw.Sensors.Length,
                    ["subHardwareCount"] = hw.SubHardware.Length,
                });
            }
            meta["hardwareInventory"] = devices;
        }
        catch (Exception ex)
        {
            meta["hardwareInventoryError"] = $"{ex.GetType().Name}: {ex.Message}";
        }

        // 温度传感器可用性：普通权限下 CPU / GPU 温度是否真的读得到数
        try
        {
            var temps = new JsonArray();
            foreach (var hw in Monitor.ComputerInstance.Hardware)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != LibreHardwareMonitor.Hardware.SensorType.Temperature) continue;
                    temps.Add(new JsonObject
                    {
                        ["device"] = hw.Name,
                        ["sensor"] = s.Name,
                        ["value"] = s.Value,
                    });
                }
            }
            meta["temperatureSensors"] = temps;
        }
        catch (Exception ex)
        {
            meta["temperatureSensorsError"] = $"{ex.GetType().Name}: {ex.Message}";
        }

        var items = new JsonArray();
        foreach (var item in Cfg.MonitorItems)
        {
            items.Add(new JsonObject
            {
                ["key"] = item.Key,
                ["visibleInPanel"] = item.VisibleInPanel,
                ["visibleInTaskbar"] = item.VisibleInTaskbar,
            });
        }
        meta["settingsSummary"] = new JsonObject
        {
            ["refreshMs"] = Cfg.RefreshMs,
            ["webServerEnabled"] = Cfg.WebServerEnabled,
            ["webServerPort"] = Cfg.WebServerPort,
            ["monitoredGpuCount"] = Cfg.MonitoredGpuCount,
            ["useWinPerCounters"] = Cfg.UseWinPerCounters,
            ["alertTempEnabled"] = Cfg.AlertTempEnabled,
            ["monitorItems"] = items,
        };

        return meta;
    }

    public static string ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception ex)
        {
            return $"unavailable: {ex.GetType().Name}";
        }
    }

    public void Dispose()
    {
        StopWeb();
        try { Monitor.Dispose(); } catch { }
    }
}

/// <summary>轻量包装，便于统一释放 Web 服务。</summary>
internal sealed class LiteWebServerHandle : IDisposable
{
    public LiteWebServerHandle(LiteMonitor.src.WebServer.LiteWebServer server) => Server = server;

    public LiteMonitor.src.WebServer.LiteWebServer Server { get; }

    public int Port => Server.CurrentRunningPort;

    public void Dispose() => Server.Dispose();
}

/// <summary>
/// 解析当前工作区的 git HEAD（支持普通仓库与 worktree 的 .git 文件两种形态）。
/// 解析不到时返回空字符串，绝不猜测。
/// </summary>
internal static class GitHeadResolver
{
    public static string Resolve()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");
                if (File.Exists(dotGit))
                {
                    var text = File.ReadAllText(dotGit).Trim();
                    if (text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    {
                        string gitDir = text["gitdir:".Length..].Trim();
                        if (!Path.IsPathRooted(gitDir))
                            gitDir = Path.GetFullPath(Path.Combine(dir.FullName, gitDir));
                        return ReadHead(gitDir);
                    }
                }
                else if (Directory.Exists(dotGit))
                {
                    return ReadHead(dotGit);
                }
                dir = dir.Parent;
            }
        }
        catch
        {
            // 解析失败就返回空串
        }
        return "";
    }

    private static string ReadHead(string gitDir)
    {
        string headPath = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(headPath)) return "";
        string head = File.ReadAllText(headPath).Trim();
        if (!head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase)) return head;
        string refPath = head["ref:".Length..].Trim();

        // 普通仓库：refs 在 gitDir 下；linked worktree：refs 在 common dir 下。
        string commonDir = ResolveCommonDir(gitDir);
        foreach (string root in new[] { gitDir, commonDir })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string refFile = Path.Combine(root, refPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refFile)) return File.ReadAllText(refFile).Trim();

            string packed = Path.Combine(root, "packed-refs");
            if (File.Exists(packed))
            {
                foreach (string line in File.ReadAllLines(packed))
                {
                    if (line.StartsWith("#") || line.StartsWith("^")) continue;
                    int sp = line.IndexOf(' ');
                    if (sp <= 0) continue;
                    if (line[(sp + 1)..].Trim() == refPath) return line[..sp].Trim();
                }
            }
        }
        return "";
    }

    /// <summary>linked worktree 的 gitDir 里只有 HEAD/index，refs 放在 common dir。</summary>
    private static string ResolveCommonDir(string gitDir)
    {
        try
        {
            string commondir = Path.Combine(gitDir, "commondir");
            if (!File.Exists(commondir)) return gitDir;
            string text = File.ReadAllText(commondir).Trim();
            if (text.Length == 0) return gitDir;
            return Path.IsPathRooted(text) ? text : Path.GetFullPath(Path.Combine(gitDir, text));
        }
        catch
        {
            return gitDir;
        }
    }
}
