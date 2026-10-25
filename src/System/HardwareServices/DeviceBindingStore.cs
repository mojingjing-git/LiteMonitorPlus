using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// [F07] 设备绑定仓库 —— **持久化"展示槽位 ↔ 设备身份"的映射**。
    ///
    /// <para>★ 为什么不写进 settings.json：本包**禁改** <c>src/Core/Settings.cs</c>（共享热点，F08a 已动过），
    /// 而"槽位 → 稳定设备 ID"需要的是**一组**映射（每卡一条），塞进 Settings 会把共享热点继续做大。
    /// 因此这里用**旁挂文件**（与 settings.json 同目录）：<c>device-bindings.json</c> + <c>.bak</c>。</para>
    ///
    /// <para>★ 写入语义：<b>原子</b>（tmp + 覆盖移动）、<b>带备份</b>（覆盖前把旧版留成 .bak）、<b>幂等</b>（内容不变不重写）。</para>
    /// </summary>
    public sealed class DeviceBindingStore
    {
        /// <summary>绑定文件名。</summary>
        public const string FileName = "device-bindings.json";

        /// <summary>绑定备份文件名。</summary>
        public const string BackupFileName = "device-bindings.json.bak";

        /// <summary>"用户手动首选"的槽位名（非数字）。</summary>
        public const string PreferredSlot = "preferred";

        /// <summary>一条槽位绑定。</summary>
        public sealed class DeviceBinding
        {
            /// <summary>设备种类名（<see cref="DeviceKind"/> 的名字）。</summary>
            public string Kind { get; set; } = "";

            /// <summary>槽位名（"preferred" 或 "0" / "1" ...）。</summary>
            public string Slot { get; set; } = "";

            /// <summary>稳定设备 ID（空 = 当初就没能证明）。</summary>
            public string StableId { get; set; } = "";

            /// <summary>上次见到的运行时标识。</summary>
            public string RuntimeId { get; set; } = "";

            /// <summary>上次见到的显示名。</summary>
            public string DisplayName { get; set; } = "";

            /// <summary>是否是用户手动指定（false = 自动选择）。</summary>
            public bool Manual { get; set; }

            /// <summary>佐证属性（判定"仍是同一台设备"时用）。</summary>
            public Dictionary<string, string> Attributes { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);

            /// <summary>最后更新时刻（单调时钟时间戳，可注入）。</summary>
            public long UpdatedTimestamp { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        private readonly object _gate = new();
        private readonly TimeProvider _time;
        private readonly List<DeviceBinding> _bindings = new();
        private string? _pathOverride;

        /// <param name="path">绑定文件路径（null = 与 settings.json 同目录）。</param>
        /// <param name="timeProvider">可注入时钟（默认系统单调时钟）。</param>
        public DeviceBindingStore(string? path = null, TimeProvider? timeProvider = null)
        {
            _pathOverride = path;
            _time = timeProvider ?? TimeProvider.System;
        }

        /// <summary>产品默认实例（与 settings.json 同目录）。</summary>
        public static DeviceBindingStore Shared { get; } = new DeviceBindingStore();

        /// <summary>绑定文件路径。</summary>
        public string FilePath => _pathOverride ?? Path.Combine(AppContext.BaseDirectory, FileName);

        /// <summary>备份文件路径。</summary>
        public string BackupPath => _pathOverride != null
            ? _pathOverride + ".bak"
            : Path.Combine(AppContext.BaseDirectory, BackupFileName);

        /// <summary>当前全部绑定（副本）。</summary>
        public IReadOnlyList<DeviceBinding> Bindings
        {
            get { lock (_gate) { return _bindings.Select(Clone).ToList(); } }
        }

        /// <summary>按种类 + 槽位查找绑定（找不到返回 null）。</summary>
        public DeviceBinding? Find(DeviceKind kind, string slot)
        {
            lock (_gate)
            {
                return _bindings.FirstOrDefault(b =>
                    string.Equals(b.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(b.Slot, slot, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>写入（或覆盖）一条绑定，返回 true 表示内容发生了变化。</summary>
        public bool Upsert(DeviceBinding binding)
        {
            ArgumentNullException.ThrowIfNull(binding);
            bool changed = false;
            lock (_gate)
            {
                var existing = _bindings.FirstOrDefault(b =>
                    string.Equals(b.Kind, binding.Kind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(b.Slot, binding.Slot, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    _bindings.Add(Clone(binding));
                    changed = true;
                }
                else if (!SameContent(existing, binding))
                {
                    string kind = existing.Kind;
                    string slot = existing.Slot;
                    int index = _bindings.IndexOf(existing);
                    _bindings[index] = Clone(binding);
                    _bindings[index].Kind = kind;
                    _bindings[index].Slot = slot;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>删除一条绑定，返回 true 表示确实删掉了。</summary>
        public bool Remove(DeviceKind kind, string slot)
        {
            lock (_gate)
            {
                int removed = _bindings.RemoveAll(b =>
                    string.Equals(b.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(b.Slot, slot, StringComparison.OrdinalIgnoreCase));
                return removed > 0;
            }
        }

        /// <summary>当前单调时间戳（写绑定时用）。</summary>
        public long NowTimestamp => _time.GetTimestamp();

        /// <summary>
        /// 落盘：**原子写 + 备份 + 幂等**（内容没变就不重写，也不刷新备份）。
        /// <para>★ 写失败**不抛异常**（设备身份属于"锦上添花"的辅助信息，绝不能因为它把启动流程搞崩），
        /// 但会如实返回 false，调用方可以记日志。</para>
        /// </summary>
        public bool Save()
        {
            string json;
            lock (_gate)
            {
                json = JsonSerializer.Serialize(_bindings, JsonOptions);
            }

            try
            {
                string path = FilePath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // 幂等：内容一致就不动文件（避免无谓地刷新备份）。
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path);
                    if (string.Equals(existing, json, StringComparison.Ordinal)) return true;
                }

                // 备份：覆盖前把当前版本留成 .bak。
                if (File.Exists(path))
                {
                    try { File.Copy(path, BackupPath, true); } catch { /* 备份失败不阻断主写入 */ }
                }

                // 原子写：先写 tmp，再覆盖移动。
                string tmp = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从磁盘加载（主文件损坏时回落到备份）。
        /// <para>★ 加载失败一律得到"空绑定"，绝不抛异常。</para>
        /// </summary>
        public bool Load()
        {
            foreach (string path in new[] { FilePath, BackupPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    string json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<List<DeviceBinding>>(json, JsonOptions);
                    if (loaded == null) continue;
                    lock (_gate)
                    {
                        _bindings.Clear();
                        foreach (DeviceBinding b in loaded)
                        {
                            if (b == null) continue;
                            b.Attributes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            _bindings.Add(b);
                        }
                    }
                    return true;
                }
                catch
                {
                    // 主文件坏了就试备份。
                }
            }
            return false;
        }

        private static bool SameContent(DeviceBinding a, DeviceBinding b)
        {
            if (!string.Equals(a.StableId, b.StableId, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.RuntimeId, b.RuntimeId, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)) return false;
            if (a.Manual != b.Manual) return false;
            if (a.Attributes.Count != b.Attributes.Count) return false;
            foreach (var kv in a.Attributes)
            {
                if (!b.Attributes.TryGetValue(kv.Key, out string? v) ||
                    !string.Equals(kv.Value, v, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static DeviceBinding Clone(DeviceBinding source)
        {
            return new DeviceBinding
            {
                Kind = source.Kind,
                Slot = source.Slot,
                StableId = source.StableId,
                RuntimeId = source.RuntimeId,
                DisplayName = source.DisplayName,
                Manual = source.Manual,
                UpdatedTimestamp = source.UpdatedTimestamp,
                Attributes = new Dictionary<string, string>(source.Attributes, StringComparer.OrdinalIgnoreCase),
            };
        }
    }
}
