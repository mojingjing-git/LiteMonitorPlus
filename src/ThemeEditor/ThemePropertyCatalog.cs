using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>属性失效级别：决定编辑后需要刷新的预览范围（重做计划 §4.2 TE00 矩阵）。</summary>
    public enum ThemePropertyInvalidation
    {
        /// <summary>仅颜色：现有预览会话可直接更新。</summary>
        Color,
        /// <summary>布局／阈值：重建经典布局，GPU 更新场景。</summary>
        Layout,
        /// <summary>字体：重建会话拥有的字体资源。</summary>
        Font,
        /// <summary>渲染配置（backend/material/parameters）：按需分级重建 GPU 会话。</summary>
        Rendering
    }

    /// <summary>属性作用域：该字段属于哪个编辑目标（重做计划 §4.1）。</summary>
    [Flags]
    public enum ThemePropertyScope
    {
        None = 0,
        /// <summary>经典外观（经典主题的全部字段）。</summary>
        Classic = 1,
        /// <summary>GPU 主效果。</summary>
        GpuMain = 2,
        /// <summary>GPU 兼容外观（fallbackStyle 六槽）。</summary>
        GpuFallback = 4,
        /// <summary>主效果与兼容外观共享（字体、布局、圆角、阈值等主题主体字段）。</summary>
        Shared = 8
    }

    /// <summary>属性编辑控件形态。</summary>
    public enum ThemePropertyEditorKind
    {
        Text,
        Color,
        Number,
        Boolean,
        Choice
    }

    /// <summary>
    /// 属性描述：UI 与预览共用的单一事实来源（重做计划 §4.2"先冻结字段映射，再制作控件"）。
    /// 范围与默认值以生产解析器为准（TE00 能力矩阵冻结输入）。
    /// </summary>
    public sealed record ThemePropertyDescriptor
    {
        /// <summary>检查器显示名（接入语言资源前先给中文默认）。</summary>
        public required string DisplayName { get; init; }

        /// <summary>JSON 路径（点分规范名，如 <c>color.background</c>）。</summary>
        public required string JsonPath { get; init; }

        /// <summary>分组（外观／文字／布局／阈值…）。</summary>
        public required string Group { get; init; }

        /// <summary>作用域。</summary>
        public required ThemePropertyScope Scope { get; init; }

        /// <summary>编辑控件形态。</summary>
        public required ThemePropertyEditorKind EditorKind { get; init; }

        /// <summary>失效级别（预览刷新决策）。</summary>
        public required ThemePropertyInvalidation Invalidation { get; init; }

        /// <summary>数值范围（Number 形态）。</summary>
        public double? Minimum { get; init; }
        public double? Maximum { get; init; }

        /// <summary>Choice 形态的候选项。</summary>
        public IReadOnlyList<string>? Choices { get; init; }

        /// <summary>读取草稿当前值（缺省用模型默认值兜底）。</summary>
        public required Func<Theme, object?> Getter { get; init; }

        /// <summary>把值写入草稿（null = 恢复缺省语义，由目录统一处理）。</summary>
        public required Action<Theme, object?> Setter { get; init; }

        /// <summary>该字段在模型层的缺省值（"恢复默认"与缺省显示用）。</summary>
        public required Func<object?> DefaultGetter { get; init; }

        /// <summary>把值序列化为 JSON 节点（null 表示删除键）。</summary>
        public required Func<object?, JsonNode?> ToJson { get; init; }
    }

    /// <summary>
    /// 属性目录（重做计划 §8 ThemePropertyCatalog）。
    /// 首批冻结经典主题字段闭环（TE03 第一批）：配色、字体、布局、阈值。
    /// GPU 字段（parameters/fallbackStyle）按 TE00 矩阵在 M2 批次补充。
    /// </summary>
    public static partial class ThemePropertyCatalog
    {
        private static readonly List<ThemePropertyDescriptor> _classic = BuildClassic();

        /// <summary>经典主题属性（M1 批次冻结集）。</summary>
        public static IReadOnlyList<ThemePropertyDescriptor> ClassicProperties => _classic;

        /// <summary>按文档类型与编辑目标取属性集（GPU 主题在 M2 扩展）。</summary>
        public static IReadOnlyList<ThemePropertyDescriptor> GetProperties(bool isGpuTheme)
        {
            // M1：经典编辑闭环。GPU 主题在 M2 接入主效果／兼容外观视图。
            return _classic;
        }

        private static string S(object? value) => value as string ?? string.Empty;

        private static JsonNode? JsonString(object? value) =>
            value is string s ? JsonValue.Create(s) : null;

        private static JsonNode? JsonNumber(object? value) => value switch
        {
            int i => JsonValue.Create(i),
            double d => JsonValue.Create(d),
            float f => JsonValue.Create(f),
            _ => null
        };

        private static JsonNode? JsonBool(object? value) =>
            value is bool b ? JsonValue.Create(b) : null;

        private static double D(object? value) => Convert.ToDouble(value);

        private static List<ThemePropertyDescriptor> BuildClassic() => new()
        {
            // ---------- 配色 ----------
            Color("页面底色", "color.background", t => t.Color.Background, (t, v) => t.Color.Background = S(v)),
            Color("分组底色", "color.groupBackground", t => t.Color.GroupBackground, (t, v) => t.Color.GroupBackground = S(v)),
            Color("标题文字", "color.textTitle", t => t.Color.TextTitle, (t, v) => t.Color.TextTitle = S(v)),
            Color("分组标题", "color.textGroup", t => t.Color.TextGroup, (t, v) => t.Color.TextGroup = S(v)),
            Color("正文文字", "color.textPrimary", t => t.Color.TextPrimary, (t, v) => t.Color.TextPrimary = S(v)),
            Color("次级文字", "color.textSecondary", t => t.Color.TextSecondary, (t, v) => t.Color.TextSecondary = v as string),
            Color("描边色", "color.border", t => t.Color.Border, (t, v) => t.Color.Border = v as string),
            Color("辉光阴影", "color.shadow", t => t.Color.Shadow, (t, v) => t.Color.Shadow = v as string),
            Color("安全值", "color.valueSafe", t => t.Color.ValueSafe, (t, v) => t.Color.ValueSafe = S(v)),
            Color("警告值", "color.valueWarn", t => t.Color.ValueWarn, (t, v) => t.Color.ValueWarn = S(v)),
            Color("危险值", "color.valueCrit", t => t.Color.ValueCrit, (t, v) => t.Color.ValueCrit = S(v)),
            Color("条形底", "color.barBackground", t => t.Color.BarBackground, (t, v) => t.Color.BarBackground = S(v)),
            Color("条形低", "color.barLow", t => t.Color.BarLow, (t, v) => t.Color.BarLow = S(v)),
            Color("条形中", "color.barMid", t => t.Color.BarMid, (t, v) => t.Color.BarMid = S(v)),
            Color("条形高", "color.barHigh", t => t.Color.BarHigh, (t, v) => t.Color.BarHigh = S(v)),

            // ---------- 文字（共享设置：GPU 主题下同时影响兼容外观字体） ----------
            Text("界面字体", "font.family", t => t.Font.Family, (t, v) => t.Font.Family = S(v), shared: true),
            Text("数值字体", "font.valueFamily", t => t.Font.ValueFamily, (t, v) => t.Font.ValueFamily = S(v), shared: true),
            Num("标题字号", "font.title", t => t.Font.Title, (t, v) => t.Font.Title = D(v), 1, 128, shared: true),
            Num("分组字号", "font.group", t => t.Font.Group, (t, v) => t.Font.Group = D(v), 1, 128, shared: true),
            Num("条目字号", "font.item", t => t.Font.Item, (t, v) => t.Font.Item = D(v), 1, 128, shared: true),
            Num("数值字号", "font.value", t => t.Font.Value, (t, v) => t.Font.Value = D(v), 1, 128, shared: true),
            Bool("加粗", "font.bold", t => t.Font.Bold, (t, v) => t.Font.Bold = v is true, shared: true),

            // ---------- 布局 ----------
            Num("行高", "layout.rowHeight", t => t.Layout.RowHeight, (t, v) => t.Layout.RowHeight = (int)D(v), 16, 200),
            Num("内边距", "layout.padding", t => t.Layout.Padding, (t, v) => t.Layout.Padding = (int)D(v), 0, 64),
            Num("面板圆角", "layout.cornerRadius", t => t.Layout.CornerRadius, (t, v) => t.Layout.CornerRadius = (int)D(v), 0, 48),
            Num("分组圆角", "layout.groupRadius", t => t.Layout.GroupRadius, (t, v) => t.Layout.GroupRadius = (int)D(v), 0, 48),
            Num("分组内距", "layout.groupPadding", t => t.Layout.GroupPadding, (t, v) => t.Layout.GroupPadding = (int)D(v), 0, 64),
            Num("分组间距", "layout.groupSpacing", t => t.Layout.GroupSpacing, (t, v) => t.Layout.GroupSpacing = (int)D(v), 0, 200),
            Num("条目间距", "layout.itemGap", t => t.Layout.ItemGap, (t, v) => t.Layout.ItemGap = (int)D(v), 0, 64),
            Num("分组标题偏移", "layout.groupTitleOffset", t => t.Layout.GroupTitleOffset, (t, v) => t.Layout.GroupTitleOffset = (int)D(v), 0, 64),
            Choice("外框样式", "layout.frameStyle", t => t.Layout.FrameStyle, (t, v) => t.Layout.FrameStyle = (int)D(v),
                ["0", "1", "2", "3"]),
            Num("外框描边宽", "layout.frameWidth", t => t.Layout.FrameWidth, (t, v) => t.Layout.FrameWidth = (int)D(v), 0, 8),
            Num("切角边长", "layout.frameLength", t => t.Layout.FrameLength, (t, v) => t.Layout.FrameLength = (int)D(v), 0, 64),
            Num("扫描线强度", "layout.scanline", t => t.Layout.Scanline, (t, v) => t.Layout.Scanline = (int)D(v), 0, 100),
            Num("辉光强度", "layout.glow", t => t.Layout.Glow, (t, v) => t.Layout.Glow = (int)D(v), 0, 100),
            Num("网格强度", "layout.grid", t => t.Layout.Grid, (t, v) => t.Layout.Grid = (int)D(v), 0, 100),
            Choice("进度条样式", "layout.barStyle", t => t.Layout.BarStyle, (t, v) => t.Layout.BarStyle = (int)D(v),
                ["0", "1", "2"]),

            // ---------- 阈值 ----------
            Num("负载警告", "thresholds.load.warn", t => t.Thresholds.Load.Warn, (t, v) => t.Thresholds.Load.Warn = D(v), 0, 100),
            Num("负载危险", "thresholds.load.crit", t => t.Thresholds.Load.Crit, (t, v) => t.Thresholds.Load.Crit = D(v), 0, 100),
            Num("温度警告", "thresholds.temp.warn", t => t.Thresholds.Temp.Warn, (t, v) => t.Thresholds.Temp.Warn = D(v), 0, 150),
            Num("温度危险", "thresholds.temp.crit", t => t.Thresholds.Temp.Crit, (t, v) => t.Thresholds.Temp.Crit = D(v), 0, 150),
            Num("显存警告", "thresholds.vram.warn", t => t.Thresholds.Vram.Warn, (t, v) => t.Thresholds.Vram.Warn = D(v), 0, 100),
            Num("显存危险", "thresholds.vram.crit", t => t.Thresholds.Vram.Crit, (t, v) => t.Thresholds.Vram.Crit = D(v), 0, 100),
            Num("内存警告", "thresholds.mem.warn", t => t.Thresholds.Mem.Warn, (t, v) => t.Thresholds.Mem.Warn = D(v), 0, 100),
            Num("内存危险", "thresholds.mem.crit", t => t.Thresholds.Mem.Crit, (t, v) => t.Thresholds.Mem.Crit = D(v), 0, 100),
        };

        private static ThemePropertyDescriptor Color(
            string display, string path,
            Func<Theme, object?> get, Action<Theme, object?> set) =>
            new()
            {
                DisplayName = display,
                JsonPath = path,
                Group = "配色",
                Scope = ThemePropertyScope.Classic,
                EditorKind = ThemePropertyEditorKind.Color,
                Invalidation = ThemePropertyInvalidation.Color,
                Getter = get,
                Setter = set,
                DefaultGetter = () => GetModelDefault(path),
                ToJson = JsonString
            };

        private static ThemePropertyDescriptor Text(
            string display, string path,
            Func<Theme, object?> get, Action<Theme, object?> set,
            bool shared = false) =>
            new()
            {
                DisplayName = display,
                JsonPath = path,
                Group = "文字",
                Scope = shared ? ThemePropertyScope.Shared : ThemePropertyScope.Classic,
                EditorKind = ThemePropertyEditorKind.Text,
                Invalidation = ThemePropertyInvalidation.Font,
                Getter = get,
                Setter = set,
                DefaultGetter = () => GetModelDefault(path),
                ToJson = JsonString
            };

        private static ThemePropertyDescriptor Num(
            string display, string path,
            Func<Theme, object?> get, Action<Theme, object?> set,
            double min, double max,
            bool shared = false) =>
            new()
            {
                DisplayName = display,
                JsonPath = path,
                Group = path.StartsWith("thresholds", StringComparison.Ordinal) ? "阈值" :
                    path.StartsWith("font", StringComparison.Ordinal) ? "文字" : "布局",
                Scope = shared ? ThemePropertyScope.Shared : ThemePropertyScope.Classic,
                EditorKind = ThemePropertyEditorKind.Number,
                Invalidation = path.StartsWith("font", StringComparison.Ordinal)
                    ? ThemePropertyInvalidation.Font
                    : ThemePropertyInvalidation.Layout,
                Minimum = min,
                Maximum = max,
                Getter = get,
                Setter = set,
                DefaultGetter = () => GetModelDefault(path),
                ToJson = JsonNumber
            };

        private static ThemePropertyDescriptor Bool(
            string display, string path,
            Func<Theme, object?> get, Action<Theme, object?> set,
            bool shared = false) =>
            new()
            {
                DisplayName = display,
                JsonPath = path,
                Group = "文字",
                Scope = shared ? ThemePropertyScope.Shared : ThemePropertyScope.Classic,
                EditorKind = ThemePropertyEditorKind.Boolean,
                Invalidation = ThemePropertyInvalidation.Font,
                Getter = get,
                Setter = set,
                DefaultGetter = () => GetModelDefault(path),
                ToJson = JsonBool
            };

        private static ThemePropertyDescriptor Choice(
            string display, string path,
            Func<Theme, object?> get, Action<Theme, object?> set,
            string[] choices) =>
            new()
            {
                DisplayName = display,
                JsonPath = path,
                Group = "布局",
                Scope = ThemePropertyScope.Classic,
                EditorKind = ThemePropertyEditorKind.Choice,
                Invalidation = ThemePropertyInvalidation.Layout,
                Choices = choices,
                Getter = get,
                Setter = set,
                DefaultGetter = () => GetModelDefault(path),
                ToJson = JsonNumber
            };

        /// <summary>
        /// 模型层缺省值：新建对应分节的干净实例读同一路径。
        /// 依赖描述符路径与模型属性的既有对应关系；运行期字段（Font.*）不含在内。
        /// </summary>
        private static object? GetModelDefault(string path)
        {
            Theme fresh = new();
            string[] segments = path.Split('.');
            object current = segments[0] switch
            {
                "color" => fresh.Color,
                "font" => fresh.Font,
                "layout" => fresh.Layout,
                "thresholds" => ResolveThreshold(fresh, segments[1]),
                _ => fresh
            };

            if (ReferenceEquals(current, fresh))
            {
                return null;
            }

            var property = current.GetType().GetProperty(
                segments[^1],
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.IgnoreCase);
            return property?.GetValue(current);
        }

        private static object ResolveThreshold(Theme fresh, string kind) => kind switch
        {
            "load" => fresh.Thresholds.Load,
            "temp" => fresh.Thresholds.Temp,
            "vram" => fresh.Thresholds.Vram,
            "mem" => fresh.Thresholds.Mem,
            _ => fresh.Thresholds.NetKBps
        };
    }
}
