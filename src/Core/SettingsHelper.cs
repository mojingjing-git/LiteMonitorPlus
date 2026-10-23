using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using LiteMonitor.src.Core;

namespace LiteMonitor
{
    public static class SettingsHelper
    {
        // Cache path
        private static readonly string _cachedPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        private static readonly string _backupPath = Path.Combine(AppContext.BaseDirectory, "settings.json.bak");
        private static readonly string _tempPath = Path.Combine(AppContext.BaseDirectory, "settings.json.tmp");
        private static readonly object _ioLock = new object();
        public static string FilePath => _cachedPath;

        // Global block save lock
        private static bool _globalBlockSave;
        private static SaveBlockOwner _saveBlockOwner = SaveBlockOwner.None;

        /// <summary>
        /// 谁把「禁止保存」置上的。
        ///
        /// <see cref="GlobalBlockSave"/> 有两个使用方：本类在「配置暂时读不到」时置真，
        /// 以及 <c>SettingsForm</c> 的「重置配置」在删掉配置文件后、进程退出前置真（防止刚删的配置被内存值写回）。
        /// 读取成功时只能撤销**自己置的那一次**，否则一次成功的读取会把重置兜底一起撤销。
        ///
        /// 不能只用一个「我置过没有」的 bool：外部置真之后那个 bool 仍可能为真
        /// （此前有过一次瞬时 IO 故障且尚未被成功读取清掉），一次成功读取就会连外部的禁写一起解除。
        /// 因此记录的是当前持有者。
        /// </summary>
        private enum SaveBlockOwner
        {
            None,

            /// <summary>本类因 IO 故障置上，读取成功后自动解除。</summary>
            IoFailure,

            /// <summary>外部（如「重置配置」）置上，本类不得解除。</summary>
            External,
        }

        public static bool GlobalBlockSave
        {
            get => _globalBlockSave;
            set
            {
                _globalBlockSave = value;
                // 外部赋值一律记为 External：本类自己置真走 BlockSaveForIoFailure()。
                _saveBlockOwner = value ? SaveBlockOwner.External : SaveBlockOwner.None;
            }
        }

        private static void BlockSaveForIoFailure()
        {
            _globalBlockSave = true;
            _saveBlockOwner = SaveBlockOwner.IoFailure;
        }

        private static void ReleaseSaveBlockFromIoFailure()
        {
            // 只解除「本类因 IO 故障置上的」禁写。外部（「重置配置」）置上的强制禁写不在解除范围内。
            if (_saveBlockOwner != SaveBlockOwner.IoFailure) return;

            _globalBlockSave = false;
            _saveBlockOwner = SaveBlockOwner.None;
        }

        public static Settings Load(bool forceReload = false)
        {
            // Note: The singleton instance management is kept in Settings.Load() facade
            // or handled by the caller. This method strictly loads from disk/creates default.
            
            Settings? s;
            LoadFailure primaryFailure;
            lock (_ioLock)
            {
                s = TryLoadFile(FilePath, out primaryFailure);

                // 主文件内容损坏：立刻改名留证。
                // 这样后续保存不会把它当作「当前主文件」复制进备份（见 AtomicWrite 第 3 步），
                // 备份里那份有效配置才保得住。
                if (primaryFailure == LoadFailure.Corrupt)
                {
                    QuarantineCorruptFile(FilePath);
                }

                // 只有「不存在」或「损坏」才回落到备份。
                // I/O 故障时不回落：另一个文件很可能锁在同一原因上，
                // 而真正的危险是把默认值当成用户配置、解锁后再写回去覆盖。
                if (s == null && primaryFailure != LoadFailure.IoFailure)
                {
                    LoadFailure backupFailure;
                    s = TryLoadFile(_backupPath, out backupFailure);

                    if (backupFailure == LoadFailure.Corrupt)
                    {
                        QuarantineCorruptFile(_backupPath);
                    }
                    else if (backupFailure == LoadFailure.IoFailure)
                    {
                        primaryFailure = LoadFailure.IoFailure;
                    }
                }
            }

            // 配置暂时读不到时：本次会话用默认配置继续运行，但禁止任何自动落盘，
            // 否则用户一改设置就会用默认值把磁盘上那份完好的配置覆盖掉。
            if (primaryFailure == LoadFailure.IoFailure)
            {
                BlockSaveForIoFailure();
                System.Diagnostics.Debug.WriteLine(
                    "[Settings] 配置暂时无法读取（IO 故障），本次会话已禁止自动保存，避免用默认值覆盖用户配置。");
            }
            else
            {
                // 读取成功后解除本类此前置上的禁写 —— 否则一次瞬时故障会让本次会话永久存不了盘。
                ReleaseSaveBlockFromIoFailure();
            }

            s ??= new Settings();

            if (s.GroupAliases == null) s.GroupAliases = new Dictionary<string, string>();

            // 1. Check if new install
            if (s.MonitorItems == null || s.MonitorItems.Count == 0)
            {
                s.InitDefaultItems();
                // Ensure TaskbarSortIndex has initial value
                foreach (var item in s.MonitorItems)
                {
                    if (item.TaskbarSortIndex == 0)
                        item.TaskbarSortIndex = item.SortIndex;
                }
            }
            else
            {
                // 2. Version check
                bool isLegacyConfig = s.MonitorItems.All(x => x.TaskbarSortIndex == 0);

                if (isLegacyConfig)
                {
                    s.RebuildAndMigrateSettings();
                }
                else
                {
                    s.CheckAndAppendMissingItems();
                }
            }

            // [Fix] Removed redundant SyncToLanguage call. 
            // Caller (AppActions.ApplyAllSettings) is responsible for syncing language state.
            // s.SyncToLanguage();
            
            // [Migration] 兼容老用户：如果未设置 PresetStyle (-1)，根据旧的 FontSize 推断  
            // 未来1.2.9几个版本后，将TaskbarPresetStyle 默认配置改成1即可
            if (s.TaskbarPresetStyle == -1)
            {
                // 9.0pt => 小字模式(0), 其他 => 大字模式(1)
                s.TaskbarPresetStyle = (Math.Abs(s.TaskbarFontSize - Settings.DEFAULT_TB_SIZE_REGULAR) < 0.1f) ? 0 : 1;
            }

            s.InternAllStrings();

            // 3. [多显卡] 同步显卡监控项。
            //    ★ 必须在 InternAllStrings 之后 ★ (见上方说明)
            //    也必须在 CheckAndAppendMissingItems 之后 —— 那一步会把缺失的默认项
            //    (含旧的 "GPU.Load" 单卡项) 补回来, 这里再统一迁移成 "GPU.{序号}.{指标}"。
            //    注意: 此刻硬件可能还没枚举完, 拿不到实际卡数, 这里先按上限夹紧;
            //    真实卡数会在保存设置时 (Task 5.5) 与运行时 (Task 12.3) 再次夹紧。
            GpuItemSync.Ensure(s);
            
            return s;
        }

        /// <summary>
        /// 保存配置，并**如实报告**是否真的写成功。
        /// </summary>
        /// <remarks>
        /// ★ 为什么要**新增**这个入口，而不是把 <see cref="Save"/> 改成返回 bool：
        ///   <see cref="Save"/> 是**扩展方法**且被**多处**调用（UI / 插件 / 菜单 / 自动保存 …）。
        ///   改它的返回类型会动到既有 public 成员的签名；把它改成会抛异常的版本，
        ///   还会把"原来静默失败"的调用点悄悄变成"现在会抛异常"—— 那会造成回归。
        ///   这里新增一个**能报告结果**的入口，把"写没写成"这条信息交给真正需要它的调用方
        ///   （如 <c>Settings.EnsureWebPasswordMigrated</c>）；
        ///   <see cref="Save"/> 保持原有语义（静默失败、不抛异常）**逐位不变**。
        /// ★ <see cref="GlobalBlockSave"/> 为真（禁止保存）⇒ 返回 **false**（不是 true）。
        /// ★ 任何 IO / 序列化异常 ⇒ 返回 **false**（不是"吞掉异常后假装成功"）。
        /// ★ 可见性：**internal**（不是 public）—— 本仓约定"测试接缝收为 internal + InternalsVisibleTo"，
        ///   不新增对外公开的保存入口；消费方只有同程序集内的
        ///   <c>Settings.EnsureWebPasswordMigrated</c>。
        /// </remarks>
        internal static bool TrySave(this Settings settings)
        {
            if (GlobalBlockSave) return false;

            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                lock (_ioLock)
                {
                    AtomicWrite(json);
                }
                return true;
            }
            catch
            {
                CleanupTempFile();
                return false;
            }
        }

        /// <summary>
        /// 保存配置（既有入口）：失败时**静默**，不抛异常。
        /// </summary>
        /// <remarks>
        /// ★ 语义与改造前**逐位一致**：禁止保存 ⇒ 直接返回；IO 失败 ⇒ 清理临时文件后返回。
        ///   需要知道"写没写成"的调用方请改用 <see cref="TrySave"/>。
        /// </remarks>
        public static void Save(this Settings settings)
        {
            TrySave(settings);
        }

        public static void DeleteStoredSettings()
        {
            lock (_ioLock)
            {
                DeleteFileIfExists(FilePath);
                DeleteFileIfExists(_backupPath);
                DeleteFileIfExists(_tempPath);
            }
        }

        /// <summary>
        /// 单个配置文件加载失败的原因分类。
        ///
        /// 必须把「没有这个文件」（新装）与「暂时读不到」（被独占锁 / 权限不足 / 磁盘错误）区分开：
        /// 后者如果按新装处理，随后任何一次自动保存都会用默认值把用户的真实配置覆盖掉。
        /// </summary>
        internal enum LoadFailure
        {
            /// <summary>加载成功。</summary>
            None,

            /// <summary>文件不存在 —— 可以按新装处理。</summary>
            NotFound,

            /// <summary>内容无法解析 —— 文件本身已损坏，可留证后弃用并回落到备份。</summary>
            Corrupt,

            /// <summary>临时 I/O 故障（被独占锁、权限不足、磁盘错误）—— 绝不可当作新装。</summary>
            IoFailure
        }

        /// <summary>
        /// 把无法解析的配置文件改名留证。
        ///
        /// 两个作用：一是保留现场供排查；二是让它不再占据主文件位置 ——
        /// 否则下一次保存会把它当作「当前主文件」复制进备份，把备份里那份有效配置一起毁掉。
        /// </summary>
        private static void QuarantineCorruptFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;

                string quarantine = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                if (File.Exists(quarantine))
                    quarantine += "_" + Guid.NewGuid().ToString("N").Substring(0, 8);

                File.Move(path, quarantine);
            }
            catch
            {
                // 留证失败不能阻断启动；主文件仍是坏的，但 Load 会继续走备份回落
            }
        }

        private static Settings? TryLoadFile(string path, out LoadFailure failure)
        {
            failure = LoadFailure.None;

            try
            {
                // 注意：File.Exists 在权限不足时也返回 false 而不抛异常，
                // 因此「无权限」在这个判定上会被归为 NotFound。这是已知取舍：
                // 要区分它必须真的去打开文件，那会让每次启动都产生一次失败的 IO。
                if (!File.Exists(path))
                {
                    failure = LoadFailure.NotFound;
                    return null;
                }

                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (loaded == null)
                {
                    failure = LoadFailure.Corrupt;
                    return null;
                }

                return loaded;
            }
            catch (JsonException)
            {
                // 内容不是合法 JSON（或字段类型对不上）：文件本身是坏的，可以留证后弃用
                failure = LoadFailure.Corrupt;
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 被独占锁 / 权限不足 / 路径异常：文件可能完好，只是此刻读不到。
                // 绝不能当作「新装」处理，否则随后的自动保存会用默认值覆盖用户的真实配置。
                failure = LoadFailure.IoFailure;
                return null;
            }
            catch
            {
                // 其余异常保守归入 IO 故障：宁可本次会话不落盘，也不要用默认值覆盖用户配置
                failure = LoadFailure.IoFailure;
                return null;
            }
        }

        private static void AtomicWrite(string json)
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 1. 先写临时文件
            File.WriteAllText(_tempPath, json);

            // 2. 回读校验：落盘内容必须还能解析回配置对象。
            //    校验放在「刷新备份」之前，这样半截内容既不会顶替主文件，也不会污染备份。
            string verifyJson = File.ReadAllText(_tempPath);
            if (JsonSerializer.Deserialize<Settings>(verifyJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) == null)
            {
                throw new InvalidOperationException("临时配置文件回读校验失败，已放弃本次保存");
            }

            // 3. 校验通过后才刷新备份。顺序很关键：
            //    备份的语义是「上一份有效内容」，所以要在替换主文件**之前**复制当前主文件。
            //    但只有当当前主文件本身可解析时它才算一份有效内容 ——
            //    主文件已损坏还去复制它，会把备份里那份好配置一起毁掉。
            if (File.Exists(FilePath) && TryLoadFile(FilePath, out _) != null)
            {
                // ★★ LM-085：备份**不得**是旧主文件的「原样副本」——
                //   原样复制会把**加密前**的明文凭据（插件凭据 / Web 口令）留在磁盘上的
                //   第二个文件里，备份的泄露面比主文件还大。
                //   这里改成：读回旧主文件 → 过一遍脱敏/加密 → 再写进备份，
                //   于是备份内容与主文件**同级**（凭据一律是 DPAPI 密文、口令一律为空）。
                //   ★ 脱敏失败 ⇒ **不写备份**（绝不退回「原样复制」）：宁可备份陈旧，
                //   也不能让明文凭据继续留在磁盘上。
                try { WriteSanitizedBackup(FilePath, _backupPath); }
                catch { }
            }

            // 4. 最后替换主文件
            File.Move(_tempPath, FilePath, true);
        }

        /// <summary>
        /// 写备份（LM-085）：备份内容必须**已经过密 / 脱敏处理**。
        /// <para>
        /// ★ 失败时**不退回原样复制** —— 原样复制 = 明文凭据继续留在磁盘上。
        ///   写不出备份 ⇒ 保持旧备份不动（宁可备份陈旧，也不留明文）。
        /// </para>
        /// </summary>
        private static void WriteSanitizedBackup(string sourcePath, string backupPath)
        {
            string raw = File.ReadAllText(sourcePath);
            string sanitized = Settings.SanitizeSettingsJsonForBackup(raw);
            File.WriteAllText(backupPath, sanitized);
        }

        private static void CleanupTempFile()
        {
            try
            {
                if (File.Exists(_tempPath)) File.Delete(_tempPath);
            }
            catch { }
        }

        private static void DeleteFileIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public static void InitDefaultItems(this Settings settings)
        {
            settings.MonitorItems = new List<MonitorItemConfig>
            {
                // [1xx] Dashboard Items
                // [Taskbar] DASH items should appear at the END by default (SortIndex > 800)
                new MonitorItemConfig { Key = "DASH.HOST", SortIndex = 101, TaskbarSortIndex = 1100, VisibleInPanel = false, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.Time", SortIndex = 102, TaskbarSortIndex = 1200, VisibleInPanel = false, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.Uptime", SortIndex = 103, TaskbarSortIndex = 1300, VisibleInPanel = true, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.IP",   SortIndex = 104, TaskbarSortIndex = 1400, VisibleInPanel = false, TaskbarLabel = " " },
               
                // [2xx] CPU
                new MonitorItemConfig { Key = "CPU.Load",  SortIndex = 201, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "CPU.Temp",  SortIndex = 202, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "CPU.Clock", SortIndex = 203, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Power", SortIndex = 204, VisibleInPanel = false },
                // [New] CPU Voltage
                new MonitorItemConfig { Key = "CPU.Voltage", SortIndex = 205, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Fan",   SortIndex = 206, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Pump",  SortIndex = 207, VisibleInPanel = false },

                // [3xx] GPU
                new MonitorItemConfig { Key = "GPU.Load",  SortIndex = 301, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "GPU.Temp",  SortIndex = 302, VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.Clock", SortIndex = 303, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.Power", SortIndex = 304, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.Fan",   SortIndex = 305, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.VRAM",  SortIndex = 306, VisibleInPanel = true },

                // [4xx] HOST (MEM, FPS, MOBO, DISK Temp, CASE Fan)
                new MonitorItemConfig { Key = "MEM.Load",  SortIndex = 401, VisibleInPanel = true, VisibleInTaskbar = true },
                // ★ [Fix #137] 提交内存（#137）：默认**隐藏**（面板与任务栏都不显示），用户可自行开启。
                //   它是**独立**监控项，不替换 MEM.Load（后者仍然是物理内存占用率，语义完全不变）。
                //   排序紧挨 MEM.Load，与既有内存项相邻（同 HOST 分组）。
                new MonitorItemConfig { Key = MetricUtils.CommittedMemoryKey, SortIndex = 402, VisibleInPanel = false, VisibleInTaskbar = false },
                new MonitorItemConfig { Key = "FPS",       SortIndex = 403, VisibleInPanel = false },
                new MonitorItemConfig { Key = "MOBO.Temp", SortIndex = 404, VisibleInPanel = false },
                new MonitorItemConfig { Key = "DISK.Temp", SortIndex = 405, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CASE.Fan",  SortIndex = 406, VisibleInPanel = false },
                
                // [5xx] BATTERY (New Group)
                new MonitorItemConfig { Key = "BAT.Percent", SortIndex = 501, VisibleInPanel = false, VisibleInTaskbar = false },
                new MonitorItemConfig { Key = "BAT.Power",   SortIndex = 502, VisibleInPanel = false },
                new MonitorItemConfig { Key = "BAT.Voltage", SortIndex = 503, VisibleInPanel = false },
                new MonitorItemConfig { Key = "BAT.Current", SortIndex = 504, VisibleInPanel = false },

                // [6xx] DISK IO
                new MonitorItemConfig { Key = "DISK.Read", SortIndex = 601, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DISK.Write",SortIndex = 602, VisibleInPanel = true },

                // [7xx] NET
                new MonitorItemConfig { Key = "NET.Up",    SortIndex = 701, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "NET.Down",  SortIndex = 702, VisibleInPanel = true, VisibleInTaskbar = true },

                // [8xx] DATA
                new MonitorItemConfig { Key = "DATA.DayUp",  SortIndex = 801, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DATA.DayDown",SortIndex = 802, VisibleInPanel = true },
            };
        }
        // [Sync] 同步到语言设置
        // 作用：将配置中的组别名和监控项标签同步到语言管理器
        // 注意：这会清除所有当前的语言覆盖
        public static void SyncToLanguage(this Settings settings)
        {
            LanguageManager.ClearOverrides();
            if (settings.GroupAliases != null)
            {
                foreach (var kv in settings.GroupAliases)
                    LanguageManager.SetOverride(UIUtils.Intern("Groups." + kv.Key), kv.Value);
            }
            if (settings.MonitorItems != null)
            {
                foreach (var item in settings.MonitorItems)
                {
                    if (!string.IsNullOrEmpty(item.UserLabel))
                        LanguageManager.SetOverride(UIUtils.Intern("Items." + item.Key), item.UserLabel);
                    if (!string.IsNullOrEmpty(item.TaskbarLabel))
                        LanguageManager.SetOverride(UIUtils.Intern("Short." + item.Key), item.TaskbarLabel);
                }
            }
        }

        // Cache for standard keys to avoid repeated allocations
        private static readonly Lazy<HashSet<string>> _standardKeys = new Lazy<HashSet<string>>(() => 
        {
            var s = new Settings();
            s.InitDefaultItems();
            return new HashSet<string>(s.MonitorItems.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
        });

        public static HashSet<string> GetStandardKeys() => _standardKeys.Value;

        public static void InternAllStrings(this Settings settings)
        {
            if (settings.MonitorItems != null)
            {
                // [Cleanup] Remove Orphaned Items
                // Optimization: Use StandardKeys whitelist to avoid heavy allocations
                var whitelist = GetStandardKeys();
                var keysToRemove = new List<MonitorItemConfig>();

                foreach (var item in settings.MonitorItems)
                {
                    if (item == null) continue;
                    
                    item.Key = UIUtils.Intern(item.Key);
                    
                    // 1. Is it a standard item?
                    if (whitelist.Contains(item.Key)) continue;

                    // 2. Is it a valid Plugin item? 
                    // [Safety] 只要是 DASH. 开头，一律保留，防止误删用户自定义的简单插件
                    if (item.Key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // 保留所有 DASH.*
                    }

                    // 3. ★★★ [多显卡] 带序号的显卡 Key ("GPU.0.Temp") 一律保留 ★★★
                    // 白名单 GetStandardKeys() 里只有旧格式的 "GPU.Temp",
                    // 不加这一句的话, 多显卡监控项会被当成"孤儿项"全部删光,
                    // 用户对它们做的自定义 (可见性 / 排序 / 用户名) 也会一起丢。
                    if (GpuKey.TryParse(item.Key, out _, out _))
                    {
                        continue;
                    }

                    keysToRemove.Add(item);
                }
                
                foreach (var orphan in keysToRemove)
                {
                    settings.MonitorItems.Remove(orphan);
                }
            }

            settings.PreferredDisk = UIUtils.Intern(settings.PreferredDisk);
            settings.LastAutoDisk = UIUtils.Intern(settings.LastAutoDisk);
            settings.PreferredNetwork = UIUtils.Intern(settings.PreferredNetwork);
            settings.LastAutoNetwork = UIUtils.Intern(settings.LastAutoNetwork);
            settings.PreferredGpu = UIUtils.Intern(settings.PreferredGpu);
            
            settings.PreferredCpuFan = UIUtils.Intern(settings.PreferredCpuFan);
            settings.PreferredCpuPump = UIUtils.Intern(settings.PreferredCpuPump);
            settings.PreferredCaseFan = UIUtils.Intern(settings.PreferredCaseFan);
            settings.PreferredMoboTemp = UIUtils.Intern(settings.PreferredMoboTemp);
            
            settings.TaskbarFontFamily = UIUtils.Intern(settings.TaskbarFontFamily);
        }

        public static void RebuildAndMigrateSettings(this Settings settings)
        {
            // 1. 获取最新版本的标准清单 (确保包含新版本增加的监控项)
            var temp = new Settings();
            temp.InitDefaultItems();
            var migratedList = temp.MonitorItems;

            // 2. 遍历新标准清单，回填用户的个性化设置
            // 同时构建标准键集合，用于后续过滤
            var standardKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var newItem in migratedList)
            {
                standardKeys.Add(newItem.Key);

                var oldItem = settings.MonitorItems.FirstOrDefault(x => x.Key.Equals(newItem.Key, StringComparison.OrdinalIgnoreCase));
                if (oldItem != null)
                {
                    newItem.VisibleInPanel = oldItem.VisibleInPanel;
                    newItem.VisibleInTaskbar = oldItem.VisibleInTaskbar;
                    newItem.UserLabel = oldItem.UserLabel;
                    newItem.TaskbarLabel = oldItem.TaskbarLabel;
                    newItem.UnitPanel = oldItem.UnitPanel;
                    newItem.UnitTaskbar = oldItem.UnitTaskbar;
                }
                // 数据修复：老版本没有 TaskbarSortIndex，默认初始化
                if (newItem.TaskbarSortIndex == 0) newItem.TaskbarSortIndex = newItem.SortIndex;
            }

            // 3. [Fix] 找回插件项：追加 DASH 开头且不在标准列表中的配置
            // 必须过滤掉 DASH.HOST/DASH.Time 等标准项，否则会重复！
            migratedList.AddRange(settings.MonitorItems
                .Where(x => x.Key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase) && !standardKeys.Contains(x.Key))
                .Select(x => { 
                    if (x.TaskbarSortIndex == 0) x.TaskbarSortIndex = x.SortIndex; 
                    return x; 
                }));

            // 3.5 ★★★ [多显卡] 同样找回带序号的显卡 Key ("GPU.0.Custom") ★★★
            //     这一步跑在 InternAllStrings / GpuItemSync.Ensure **之前**,
            //     不找回的话, 老配置 (所有项 TaskbarSortIndex == 0) 走迁移分支时,
            //     用户的自定义显卡项在到达 Ensure 之前就被丢掉了 (实测会丢)。
            //     越界的序号 (超出 MonitoredGpuCount) 由 GpuItemSync.Ensure 的 4.5 段再过滤。
            migratedList.AddRange(settings.MonitorItems
                .Where(x => GpuKey.TryParse(x.Key, out _, out _) && !standardKeys.Contains(x.Key))
                .Select(x => {
                    if (x.TaskbarSortIndex == 0) x.TaskbarSortIndex = x.SortIndex;
                    return x;
                }));

            settings.MonitorItems = migratedList;
        }

        public static void CheckAndAppendMissingItems(this Settings settings)
        {
            var temp = new Settings();
            temp.InitDefaultItems();
            var defaultList = temp.MonitorItems;
            
            // 找出缺失项 (保持 DefaultList 的相对顺序)
            var newItems = defaultList
                .Where(std => !settings.MonitorItems.Any(usr => usr.Key.Equals(std.Key, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (newItems.Count == 0) return;

            bool listChanged = false;

            foreach (var newItem in newItems)
            {
                // [智能锚点逻辑]
                // 不使用硬编码的 SortIndex，而是寻找该项在 DefaultList 中的"前一个邻居" (Anchor)
                // 如果用户把邻居移到了别处，新项会紧随其后。
                
                MonitorItemConfig? anchor = null;
                int myDefIdx = defaultList.FindIndex(x => x.Key == newItem.Key);
                
                // 往前回溯寻找最近的有效锚点
                for (int k = myDefIdx - 1; k >= 0; k--)
                {
                    var prevKey = defaultList[k].Key;
                    var existing = settings.MonitorItems.FirstOrDefault(x => x.Key.Equals(prevKey, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        anchor = existing;
                        break;
                    }
                }

                // === A. Panel SortIndex ===
                int targetIndex;
                if (anchor != null)
                {
                    targetIndex = anchor.SortIndex + 1;
                }
                else
                {
                    // 没锚点 (说明是队首)，插在当前最小值前面
                    int min = settings.MonitorItems.Count > 0 ? settings.MonitorItems.Min(x => x.SortIndex) : 0;
                    targetIndex = min - 1;
                }

                // 挤开后续项
                foreach (var item in settings.MonitorItems.Where(x => x.SortIndex >= targetIndex))
                    item.SortIndex++;
                
                newItem.SortIndex = targetIndex;

                // === B. Taskbar SortIndex ===
                int targetTbIndex;
                if (anchor != null)
                {
                    targetTbIndex = anchor.TaskbarSortIndex + 1;
                }
                else
                {
                    // [Fix] 没锚点 (如 DASH 这种排在队首的) 或锚点未开启任务栏
                    // 策略：看该项在 Default 中的原始意图。
                    // 如果 Default 中 TaskbarSortIndex 很大 (>1000)，说明意图是放后面 -> 插在 Max + 1
                    // 如果 Default 中 TaskbarSortIndex 很小 (<1000)，说明意图是放前面 -> 插在 Min - 1
                    
                    var validItems = settings.MonitorItems.Where(x => x.TaskbarSortIndex != 0).ToList();
                    
                    if (newItem.TaskbarSortIndex > 1000)
                    {
                        // 意图：放后面
                        int max = validItems.Count > 0 ? validItems.Max(x => x.TaskbarSortIndex) : 0;
                        targetTbIndex = max + 1;
                    }
                    else
                    {
                        // 意图：放前面
                        int min = validItems.Count > 0 ? validItems.Min(x => x.TaskbarSortIndex) : 1;
                        targetTbIndex = min - 1;
                        if (targetTbIndex == 0) targetTbIndex = -1; // 避开 0
                    }
                }

                foreach (var item in settings.MonitorItems.Where(x => x.TaskbarSortIndex >= targetTbIndex))
                    item.TaskbarSortIndex++;

                newItem.TaskbarSortIndex = targetTbIndex;

                settings.MonitorItems.Add(newItem);
                listChanged = true;
            }

            if (listChanged)
            {
                settings.MonitorItems = settings.MonitorItems.OrderBy(x => x.SortIndex).ToList();
            }
        }

        public static Settings.TBStyle GetStyle(this Settings settings)
        {
            // 如果开启了自定义布局，完全使用自定义参数
            if (settings.TaskbarCustomLayout) return new Settings.TBStyle {
                Font = settings.TaskbarFontFamily, Size = settings.TaskbarFontSize, Bold = settings.TaskbarFontBold,
                Gap = settings.TaskbarItemSpacing, Inner = settings.TaskbarInnerSpacing, VOff = settings.TaskbarVerticalPadding
            };

            // 如果未开启自定义布局，使用标准布局参数
            bool isSmall = settings.TaskbarPresetStyle == 0; // 0 = Regular/Small, 1 = Bold/Big

            return new Settings.TBStyle {
                Font = Settings.DEFAULT_TB_FONT, 
                Size = isSmall ? Settings.DEFAULT_TB_SIZE_REGULAR : Settings.DEFAULT_TB_SIZE_BOLD,
                Bold = !isSmall,
                Gap = Settings.DEFAULT_TB_GAP, 
                Inner = isSmall ? Settings.DEFAULT_TB_INNER_REGULAR : Settings.DEFAULT_TB_INNER_BOLD, 
                VOff = Settings.DEFAULT_TB_VOFF 
            };
        }

        public static bool IsAnyEnabled(this Settings settings, string keyPrefix)
        {
            return settings.MonitorItems.Any(x => x.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && (x.VisibleInPanel || x.VisibleInTaskbar));
        }

        /// <summary>
        /// 是否启用了**物理内存**相关的监控项。
        ///
        /// ★ 为什么要和 <see cref="IsAnyEnabled"/> 分开（#137）：
        ///   <see cref="IsAnyEnabled"/> 是**前缀匹配**，而 "MEM.Commit.Load"（提交内存）
        ///   也以 "MEM" 开头。只开启提交内存、关闭物理内存时，前缀匹配仍然返回 true，
        ///   于是提交内存会被**误绑**到物理内存的采集链路上：
        ///     - 开关性能计数器时会把 LHM 内存传感器也一起拉起来（多余的轮询）；
        ///     - 计数器关闭时又会把提交内存的可用性绑死在 LHM 内存是否可读上。
        ///   提交内存走的是**独立的** GetPerformanceInfo 通路（不依赖性能计数器、不依赖 LHM），
        ///   因此这里必须把它排除掉，两者的采集需求彻底解耦。
        /// </summary>
        public static bool IsAnyPhysicalMemoryEnabled(this Settings settings)
        {
            return settings.MonitorItems.Any(x =>
                !x.Key.StartsWith(CommittedMemoryKeyPrefix, StringComparison.OrdinalIgnoreCase) &&
                x.Key.StartsWith("MEM", StringComparison.OrdinalIgnoreCase) &&
                (x.VisibleInPanel || x.VisibleInTaskbar));
        }

        /// <summary>提交内存相关 Key 的公共前缀（"MEM.Commit."）。</summary>
        public const string CommittedMemoryKeyPrefix = "MEM.Commit.";

        public static void UpdateMaxRecord(this Settings settings, string key, float val)
        {
            bool changed = false;
            if (val <= 0 || float.IsNaN(val) || float.IsInfinity(val)) return;
            
            if (key.Contains("Clock") && val > 10000) return; 
            if (key.Contains("Power") && val > 1000) return;
            if ((key.Contains("Fan") || key.Contains("Pump")) && val > 10000) return;

            // ★★★ [多显卡] 带序号的 Key ("GPU.1.Power") 归一化成基础 Key ("GPU.Power") 后再比对 ★★★
            string baseKey = GpuKey.ToBaseKey(key);

            if (baseKey == "CPU.Power" && val > settings.RecordedMaxCpuPower) { settings.RecordedMaxCpuPower = val; changed = true; }
            else if (baseKey == "CPU.Clock" && val > settings.RecordedMaxCpuClock) { settings.RecordedMaxCpuClock = val; changed = true; }
            else if (baseKey == "GPU.Power" && val > settings.RecordedMaxGpuPower) { settings.RecordedMaxGpuPower = val; changed = true; }
            else if (baseKey == "GPU.Clock" && val > settings.RecordedMaxGpuClock) { settings.RecordedMaxGpuClock = val; changed = true; }
            
            else if (baseKey == "CPU.Fan" && val > settings.RecordedMaxCpuFan) { settings.RecordedMaxCpuFan = val; changed = true; }
            else if (baseKey == "CPU.Pump" && val > settings.RecordedMaxCpuPump) { settings.RecordedMaxCpuPump = val; changed = true; }
            else if (baseKey == "GPU.Fan" && val > settings.RecordedMaxGpuFan) { settings.RecordedMaxGpuFan = val; changed = true; }
            else if (baseKey == "CASE.Fan" && val > settings.RecordedMaxChassisFan) { settings.RecordedMaxChassisFan = val; changed = true; }

            // ★★★ [多显卡] 每张卡单独记一份峰值, 让各自的进度条独立自适应 ★★★
            if (key != baseKey)
            {
                if (!settings.RecordedMaxGpuDynamic.TryGetValue(key, out float cur) || val > cur)
                {
                    settings.RecordedMaxGpuDynamic[key] = val;
                    changed = true;
                }
            }

            if (changed && (DateTime.Now - settings.LastAutoSaveTime).TotalSeconds > 30)
            {
                settings.Save();
                settings.LastAutoSaveTime = DateTime.Now;
            }
        }
    }
}
