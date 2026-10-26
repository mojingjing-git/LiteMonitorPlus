using System;
using System.Drawing;
using LiteMonitor.src.Core; // Theme/MonitorItemConfig/UIUtils（现状命名空间；B4-1 仅移除 InfoService）
using LiteMonitor.Core;     // B4-1：纯格式化编排器（不依赖全局服务）

namespace LiteMonitor
{
    public enum MetricRenderStyle
    {
        StandardBar, 
        TwoColumn,   
        TextOnly     
    }

    public class MetricItem
    {
        // [新增] 绑定原始配置对象，实现动态 Label
        // ★ 声明为可空：ThemeEditor 预览项 / 未接线项本就没有绑定配置
        //   （见 ThemePreviewViews 的类注释与 ThemeStudioPreviewFormsTests 的 Assert.Null(m.BoundConfig)）。
        //   运行时确为 null，不是"类型声明撒了谎"。
        public MonitorItemConfig? BoundConfig { get; set; }

        // ── B4-1：组合根接线点（应用层持有全局查询，MetricItem 自身不再引用 InfoService/Settings）──
        /// <summary>插件色解析（组合根设置，如 k =&gt; InfoService.Instance.GetValue(k)）；ThemeEditor 等场景自动继承。</summary>
        public static Func<string, string?>? DefaultPluginColorResolver { get; set; }
        /// <summary>BoundConfig 缺失时的配置兜底查找（组合根设置）；保持 ThemeEditor 预览项沿用用户单位格式的历史行为。</summary>
        public static Func<string, MonitorItemConfig?>? DefaultConfigLookup { get; set; }

        /// <summary>实例级覆盖；未设置时回落到 <see cref="DefaultPluginColorResolver"/>。</summary>
        public Func<string, string?>? PluginColorResolver { get; set; }
        /// <summary>实例级覆盖；未设置时回落到 <see cref="DefaultConfigLookup"/>。</summary>
        public Func<string, MonitorItemConfig?>? ConfigLookup { get; set; }

        private string _key = "";
        
        // [Optimization] 缓存 InfoService 查找键
        private string? _dashColorKey;
        private const string PluginPrefix = "DASH.";

        public string Key 
        { 
            get => _key;
            set 
            {
                _key = UIUtils.Intern(value);
                // 预计算查找键
                // [Fix] 使用 OrdinalIgnoreCase 并正确处理可空性
                if (_key.StartsWith(PluginPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // 移除 "DASH." 前缀 (长度=5) 并追加 ".Color"
                    // 使用 Span 避免中间子字符串分配 (CA1845)
                    _dashColorKey = UIUtils.Intern(string.Concat(_key.AsSpan(PluginPrefix.Length), ".Color".AsSpan()));
                }
                else
                {
                    _dashColorKey = null;
                }
            } 
        }

        private string _label = "";
        public string Label 
        {
            get 
            {
                // [Refactor] 使用统一的 Label 解析器
                string labelResolved = MetricLabelResolver.ResolveLabel(BoundConfig);
                if (!string.IsNullOrEmpty(labelResolved)) return labelResolved;

                return _label;
            }
            set => _label = UIUtils.Intern(value);
        }
        
        private string _shortLabel = "";
        public string ShortLabel 
        {
            get 
            {
                // [Refactor] 使用统一的 Label 解析器
                string shortResolved = MetricLabelResolver.ResolveShortLabel(BoundConfig);
                if (!string.IsNullOrEmpty(shortResolved)) return shortResolved;

                return _shortLabel;
            }
            set => _shortLabel = UIUtils.Intern(value);
        }
        
        public float? Value { get; set; } = null;
        public float DisplayValue { get; set; } = 0f;
        // ★ 声明为可空：Dashboard 文本项才有值，数值项恒为 null。
        //   ★ null 与 "" 语义不同 —— MetricItemFormatter 用 `textValue != null` 区分
        //   「文本项」与「数值项」，改成 `= string.Empty` 会让所有数值项走进文本分支（行为回归）。
        public string? TextValue { get; set; } = null;

        // =============================
        // 格式化缓存（B4-1：纯数据状态委托给 MetricFormatterState；UI 独有状态仍在下方）
        // =============================
        private MetricFormatterState _fmt = new();

        public string CachedValueText => _fmt.CachedValueText;
        public string CachedUnitText => _fmt.CachedUnitText;
        public bool HasCustomUnit => _fmt.HasCustomUnit;
        public int CachedColorState => _fmt.CachedColorState;
        public double CachedPercent => _fmt.CachedPercent;

        public Color GetTextColor(Theme t)
        {
            return UIUtils.GetStateColor(CachedColorState, t, true);
        }

        /// <summary>解析插件色 override：插件项返回状态值（无覆盖=0），非插件项返回 null。</summary>
        private int? ResolvePluginColorOverride()
        {
            if (_dashColorKey == null) return null;
            var resolver = PluginColorResolver ?? DefaultPluginColorResolver;
            string? colorVal = resolver?.Invoke(_dashColorKey);
            if (!string.IsNullOrEmpty(colorVal) && int.TryParse(colorVal, out int state))
            {
                return state;
            }
            return 0; // 插件项但无有效覆盖 → 安全默认
        }

        public string GetFormattedText(bool isHorizontal)
        {
            // B4-1：配置解析与全局读取在此收口为显式输入；决策逻辑全部在 MetricItemFormatter（纯）。
            var cfg = BoundConfig ?? (ConfigLookup ?? DefaultConfigLookup)?.Invoke(Key);

            var result = MetricItemFormatter.Format(
                key: Key,
                displayValue: DisplayValue,
                textValue: TextValue,
                isHorizontal: isHorizontal,
                userFormatPanel: cfg?.UnitPanel,
                userFormatTaskbar: cfg?.UnitTaskbar,
                pluginColorOverride: ResolvePluginColorOverride(),
                currentPower: MetricUtils.GetPowerStatus(),
                prev: _fmt,
                r: _resolvers);

            _fmt = result.State;
            return result.Text;
        }

        /// <summary>解析委托（适配层固定指向 MetricUtils 方法组）。</summary>
        private readonly MetricFormatResolvers _resolvers = new(
            GetValueStr: (key, value, compact, userFormat) => MetricUtils.GetValueStr(key, value, compact, userFormat),
            GetUnitStr: (key, value, ctx) => MetricUtils.GetUnitStr(key, value, ctx == MetricFormatContext.Taskbar ? MetricUtils.UnitContext.Taskbar : MetricUtils.UnitContext.Panel),
            GetDisplayUnit: (key, unit, fmt) => MetricUtils.GetDisplayUnit(key, unit, fmt),
            GetState: (key, value) => MetricUtils.GetState(key, value),
            GetProgressValue: (key, value) => MetricUtils.GetProgressValue(key, value));

        public MetricRenderStyle Style { get; set; } = MetricRenderStyle.StandardBar;
        public Rectangle Bounds { get; set; } = Rectangle.Empty;

        public Rectangle LabelRect;   
        public Rectangle ValueRect;   
        public Rectangle BarRect;     
        public Rectangle BackRect;    

        public void TickSmooth(double speed)
        {
            if (!Value.HasValue) return;
            float target = Value.Value;
            float diff = Math.Abs(target - DisplayValue);
            
            // ★★★ [Fix] 核心修复：微小差异直接吸附，而不是跳过 ★★★
            if (diff < 0.05f) 
            {
                DisplayValue = target;
                return;
            }

            // 大幅跳变或高速模式直接更新
            if (diff > 15f || speed >= 0.9) DisplayValue = target;
            else DisplayValue += (float)((target - DisplayValue) * speed);
        }
    }
}