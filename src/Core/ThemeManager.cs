using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiteMonitor.Rendering.Configuration;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 布局配置：仅保留当前代码实际用到的字段
    /// - width:            窗体宽度（最终由 Settings.PanelWidth 覆盖）
    /// - rowHeight:        行高（各监控项基准高度）
    /// - padding:          画布外边距
    /// - cornerRadius:     窗体圆角（MainForm 应用）
    /// - groupRadius:      组块圆角（UIRenderer 应用）
    /// - groupPadding:     组块内边距
    /// - groupSpacing:     组块之间的垂直间距
    /// - groupBottom:      组块额外底部留白
    /// - itemGap:          监控项之间的垂直间距
    /// - groupTitleOffset: 组标题与块体的垂直微调
    ///
    /// ★ 机能风新增开关（缺键与 0 等价，0 = 关闭该效果 / 走旧路径）：
    /// - frameStyle:   0=旧路径 1=切角 2=圆角描边 3=静态全息边框
    /// - frameWidth:   描边宽度，96 DPI 逻辑尺寸（0–8）
    /// - frameLength:  切角边长，96 DPI 逻辑尺寸（0–64），仅 frameStyle=1 有意义
    /// - scanline:     扫描线强度（0–100）
    /// - glow:         辉光强度（0–100）
    /// - grid:         网格强度（0–100）
    /// - barStyle:     0=旧条 1=渐变 2=纹理
    ///
    /// ★ 缩放合同：新增加的 frameWidth / frameLength 是 96 DPI 逻辑尺寸，
    ///   **不参与本类的 Scale()**；由 ThemeAppearanceOptions 统一换算成物理像素，
    ///   保证"每个新尺寸只缩放一次"。scanline / glow / grid / barStyle / frameStyle
    ///   是强度或枚举，同样不缩放。
    /// </summary>
    public class LayoutConfig
    {
        public int Width { get; set; } = 240;//不会被实际使用，运行时由 Settings.PanelWidth 覆盖
        public float LayoutScale { get; set; } = 1.0f;//不会被实际使用，运行时由 Settings.PanelWidth 覆盖
        public int RowHeight { get; set; } = 40;
        public int Padding { get; set; } = 12;

        public int CornerRadius { get; set; } = 12;
        public int GroupRadius { get; set; } = 10;

        public int GroupPadding { get; set; } = 8;
        public int GroupSpacing { get; set; } = 30;
        public int GroupBottom { get; set; } = 0;

        public int ItemGap { get; set; } = 6;
        public int GroupTitleOffset { get; set; } = 6;

        // ============================================================
        // 机能风新增开关（整数型新属性：缺键与 0 等价，写出时忽略默认值）
        // ============================================================

        /// <summary>外框样式：0=旧路径 1=切角 2=圆角描边 3=静态全息边框。未知值按 0 处理（关闭该效果）。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int FrameStyle { get; set; } = 0;

        /// <summary>外框描边宽度，96 DPI 逻辑尺寸（0–8）。由 ThemeAppearanceOptions 换算物理像素。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int FrameWidth { get; set; } = 0;

        /// <summary>切角边长，96 DPI 逻辑尺寸（0–64）。由 ThemeAppearanceOptions 换算物理像素。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int FrameLength { get; set; } = 0;

        /// <summary>扫描线强度（0–100），不缩放。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Scanline { get; set; } = 0;

        /// <summary>辉光强度（0–100），不缩放。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Glow { get; set; } = 0;

        /// <summary>网格强度（0–100），不缩放。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Grid { get; set; } = 0;

        /// <summary>进度条样式：0=旧条 1=渐变 2=纹理。未知值按 0 处理（走旧条）。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int BarStyle { get; set; } = 0;

        /// <summary>
        /// 本层未知键扩展槽：保留 JSON 中未被类型化识别的键
        /// （含对象 / 数组 / 布尔 / null 全部形态）。
        /// </summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; } = new();

        /// <summary>
        /// 复制用的属性清单（一次性缓存）。用反射按属性逐个复制，
        /// 避免"类里一份字段清单 + Clone 里又一份字段清单"的两份手写清单，
        /// 以后新增 Layout 属性不会漏拷贝。
        /// </summary>
        private static readonly PropertyInfo[] _copyableProperties = typeof(LayoutConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead
                        && p.SetMethod != null && p.SetMethod.IsPublic
                        && !string.Equals(p.Name, nameof(ExtensionData), StringComparison.Ordinal))
            .ToArray();

        public void Scale(float s)
        {
            if (s <= 0f || Math.Abs(s - 1f) < 0.01f)
                return;

            Width = (int)(Width * s);
            RowHeight = (int)(RowHeight * s);
            Padding = (int)(Padding * s);
            CornerRadius = (int)(CornerRadius * s);
            GroupRadius = (int)(GroupRadius * s);
            GroupPadding = (int)(GroupPadding * s);
            GroupSpacing = (int)(GroupSpacing * s);
            GroupBottom = (int)(GroupBottom * s);
            GroupTitleOffset = (int)(GroupTitleOffset * s);
            ItemGap = (int)(ItemGap * s);

            // ★ 新增的 frameWidth / frameLength / scanline / glow / grid / barStyle / frameStyle
            //   故意不在这里缩放：它们要么是强度/枚举，要么是逻辑尺寸，
            //   由 ThemeAppearanceOptions 统一换算，避免"缩放两次"。
        }

        /// <summary>
        /// 创建布局副本：复制所有配置字段与扩展数据。
        /// 用于预览等需要独立副本的场景，避免改动原主题。
        /// </summary>
        public LayoutConfig Clone()
        {
            var copy = new LayoutConfig();
            copy.CopyLayoutFrom(this);
            return copy;
        }

        /// <summary>
        /// 把源布局的全部配置字段与扩展数据复制到本实例。
        /// </summary>
        public void CopyLayoutFrom(LayoutConfig src)
        {
            if (src == null) return;

            foreach (var p in _copyableProperties)
            {
                p.SetValue(this, p.GetValue(src));
            }

            // 扩展数据复制为新的字典：改副本不影响原主题
            ExtensionData = src.ExtensionData != null
                ? new Dictionary<string, JsonElement>(src.ExtensionData)
                : new Dictionary<string, JsonElement>();
        }

        /// <summary>
        /// 生成预览用布局副本（复制全部字段与扩展数据），并保证"只缩放一次"：
        /// - 源布局未缩放过（LayoutScale ≈ 1）：按 scale 缩放一次；
        /// - 源布局已缩放过：不再重复缩放，只复制结果（避免缩放累计）。
        /// </summary>
        public LayoutConfig CreatePreviewCopy(float scale)
        {
            var copy = Clone();

            bool alreadyScaled = Math.Abs(copy.LayoutScale - 1f) >= 0.01f;
            if (alreadyScaled) return copy;

            float s = scale <= 0f ? 1.0f : scale;
            copy.Scale(s);
            copy.LayoutScale = s;
            return copy;
        }
    }

    /// <summary>
    /// 字体配置：
    /// - family       : 文本主字体
    /// - valueFamily  : 数值字段单独字体（等宽可读性更好）
    /// - title/group/item/value: 四类字号
    /// - bold         : 是否加粗（四类统一按该值生效）
    /// - scale        : DPI/喜好缩放系数（0.5~3.0）
    ///
    /// ★ 本次不新增 Font.Scale 属性：现有主题 JSON 里的 "font.scale" 只作为扩展数据保留，
    ///   不改变字号算法。
    /// </summary>
    public class FontConfig
    {
        public string Family { get; set; } = "Microsoft YaHei UI";
        public string ValueFamily { get; set; } = "Consolas";

        public double Title { get; set; } = 11.5;
        public double Group { get; set; } = 10.5;
        public double Item { get; set; } = 10.0;
        public double Value { get; set; } = 10.5;

        public bool Bold { get; set; } = true;

        /// <summary>运行期字段（不参与主题 JSON），避免每次保存都写出 "itemSize"。</summary>
        [JsonIgnore]
        public int ItemSize { get; internal set; }

        /// <summary>本层未知键扩展槽：保留 "font.scale" 之类的未知键。</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; } = new();
    }

    /// <summary>
    /// 阈值定义（warn/crit），渲染中用于切换颜色。
    /// </summary>
    public class ThresholdSet
    {
        public double Warn { get; set; } = 70;
        public double Crit { get; set; } = 90;

        /// <summary>本层未知键扩展槽。</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; } = new();
    }

    /// <summary>
    /// 各类指标的阈值配置（按当前 UIRenderer 的使用保留）。
    /// </summary>
    public class ThresholdConfig
    {
        public ThresholdSet Load { get; set; } = new() { Warn = 65, Crit = 85 };
        public ThresholdSet Temp { get; set; } = new() { Warn = 50, Crit = 70 };
        public ThresholdSet Vram { get; set; } = new() { Warn = 65, Crit = 85 };
        public ThresholdSet Mem { get; set; } = new() { Warn = 65, Crit = 85 };
        public ThresholdSet NetKBps { get; set; } = new() { Warn = 2048 * 1024, Crit = 8192 * 1024 };

        /// <summary>本层未知键扩展槽。</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; } = new();
    }

    /// <summary>
    /// 颜色配置：只保留实际使用的颜色键
    /// - Background / GroupBackground
    /// - TextTitle / TextGroup / TextPrimary
    /// - ValueSafe / ValueWarn / ValueCrit
    /// - BarBackground / BarLow / BarMid / BarHigh
    ///
    /// ★ 新增用途色（可空，缺省不写出）：
    /// - Border        : 描边色，仅在外框样式显式开启时消费
    /// - Shadow        : 辉光/阴影色，仅在辉光显式开启时消费
    /// - TextSecondary : 次级文字色，仅在需要次级文字时消费
    ///   （旧主题里已经存在这些键，但不会因此自动出现描边/辉光）
    /// </summary>
    public class ColorConfig
    {
        public string Background { get; set; } = "#202225";

        public string TextTitle { get; set; } = "#FFFFFF";
        public string TextGroup { get; set; } = "#B0B0B0";
        public string TextPrimary { get; set; } = "#EAEAEA";

        public string ValueSafe { get; set; } = "#66FF99";
        public string ValueWarn { get; set; } = "#FFD666";
        public string ValueCrit { get; set; } = "#FF6666";

        public string BarBackground { get; set; } = "#1C1C1C";
        public string BarLow { get; set; } = "#00C853";
        public string BarMid { get; set; } = "#FFAB00";
        public string BarHigh { get; set; } = "#D50000";

        public string GroupBackground { get; set; } = "#2B2D31";

        /// <summary>描边色（可空）。仅当外框样式显式开启时才消费该值。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Border { get; set; }

        /// <summary>辉光/阴影色（可空）。仅当辉光显式开启时才消费该值。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Shadow { get; set; }

        /// <summary>次级文字色（可空）。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TextSecondary { get; set; }

        /// <summary>本层未知键扩展槽。</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; } = new();
    }

    /// <summary>
    /// 背景配置（可空对象，缺失 = null = 走旧背景路径）：
    /// - Mode          : solid（默认，旧背景）/ gradient（静态渐变）/ glass（请求实时材质）
    /// - Opacity       : 背景 tint 层的额外 alpha 系数（0–1），与 Tint 自身 alpha 相乘
    /// - Blur          : 逻辑像素模糊半径（0–32），只约束捕获滤波后端
    /// - Tint          : 背景染色色（缺省取 Background）
    /// - GradientStart : 渐变起始色（缺省取 Background）
    /// - GradientEnd   : 渐变结束色（缺省取 GroupBackground）
    /// - GradientAngle : 渐变角度（度）
    /// - Noise         : 噪点强度（0–100）
    /// - EdgeGlow      : 边缘辉光强度（0–100）
    /// - Fps           : 捕获帧率（1–6）
    /// </summary>
    public class BackdropConfig
    {
        public string Mode { get; set; } = "solid";
        public double Opacity { get; set; } = 0.75;
        public double Blur { get; set; } = 12;

        /// <summary>背景染色色（可空，缺省取 Background）。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Tint { get; set; }

        /// <summary>渐变起始色（可空，缺省取 Background）。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GradientStart { get; set; }

        /// <summary>渐变结束色（可空，缺省取 GroupBackground）。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GradientEnd { get; set; }

        public double GradientAngle { get; set; } = 90;

        /// <summary>噪点强度（0–100），0 = 关闭。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Noise { get; set; } = 0;

        /// <summary>边缘辉光强度（0–100），0 = 关闭。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int EdgeGlow { get; set; } = 0;

        /// <summary>捕获帧率（1–6），只对实时捕获后端有意义。</summary>
        public int Fps { get; set; } = 2;

        /// <summary>本层未知键扩展槽。</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; } = new();
    }

    /// <summary>
    /// Theme 主对象：聚合 Layout / Font / Threshold / Color / Backdrop。
    /// 运行期还会构建 4 类 Font 对象供渲染使用。
    /// </summary>
    public class Theme
    {
        public string Name { get; set; } = "Default";
        public int Version { get; set; } = 3;

        public LayoutConfig Layout { get; set; } = new();
        public FontConfig Font { get; set; } = new();
        public ThresholdConfig Thresholds { get; set; } = new();
        public ColorConfig Color { get; set; } = new();

        /// <summary>
        /// 背景配置（可空）：缺失即 null，表示使用旧背景路径。
        /// 刻意不加 "= new()" 初始化器 —— 缺失时不会自动保存一个默认实例。
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BackdropConfig? Backdrop { get; set; }

        /// <summary>
        /// Optional versioned GPU rendering route. Missing means the legacy
        /// renderer and preserves compatibility with every existing theme.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ThemeRenderingConfig? Rendering { get; set; }

        /// <summary>本层未知键扩展槽。</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; } = new();

        // 运行期字体（Json 忽略）
        [JsonIgnore] public Font FontTitle = SystemFonts.CaptionFont;
        [JsonIgnore] public Font FontGroup = SystemFonts.CaptionFont;
        [JsonIgnore] public Font FontItem = SystemFonts.CaptionFont;
        [JsonIgnore] public Font FontValue = SystemFonts.CaptionFont;
        [JsonIgnore] public Font FontTaskbar = SystemFonts.CaptionFont;

        // ===== 任务栏字体(写死硬编码，用来被调用) =====

        /// <summary>
        /// 构建 4 类字体。bold 对四类统一生效；scale 做软限制（0.5~3.0）。
        /// </summary>
        public void BuildFonts()
        {
            try
            {
                var style = Font.Bold ? FontStyle.Bold : FontStyle.Regular;
                FontTitle = new Font(Font.Family, (float)Font.Title, style);
                FontGroup = new Font(Font.Family, (float)Font.Group, style);
                FontItem = new Font(Font.Family, (float)Font.Item, style);

                var valueFamily = string.IsNullOrWhiteSpace(Font.ValueFamily) ? Font.Family : Font.ValueFamily;
                FontValue = new Font(valueFamily, (float)Font.Value, style);
            }
            catch
            {
                FontTitle = FontGroup = FontItem = FontValue = SystemFonts.CaptionFont;
            }
        }
        public void Scale(float dpiScale, float userScale)
        {
            Layout.LayoutScale = dpiScale * userScale;
            Layout.Scale(Layout.LayoutScale);

            var style = Font.Bold ? FontStyle.Bold : FontStyle.Regular;
            var valueFamily = string.IsNullOrWhiteSpace(Font.ValueFamily) ? Font.Family : Font.ValueFamily;
            float f = userScale <= 0 ? 1.0f : userScale;

            // ★★★ 关键：先销毁旧的缩放字体，防止句柄泄露 ★★★
            DisposeFonts();

            // 创建新比例的字体
            FontTitle = new Font(Font.Family, (float)Font.Title * f, style);
            FontGroup = new Font(Font.Family, (float)Font.Group * f, style);
            FontItem = new Font(Font.Family, (float)Font.Item * f, style);
            FontValue = new Font(valueFamily, (float)Font.Value * f, style);
        }
        public void DisposeFonts()
        {
            // 只有非系统字体才需要 Dispose
            if (FontTitle != null && !FontTitle.IsSystemFont) FontTitle.Dispose();
            if (FontGroup != null && !FontGroup.IsSystemFont) FontGroup.Dispose();
            if (FontItem != null && !FontItem.IsSystemFont) FontItem.Dispose();
            if (FontValue != null && !FontValue.IsSystemFont) FontValue.Dispose();
        }

    }


    /// <summary>
    /// 主题管理器：负责读取 JSON、反序列化、构建字体、暴露 Current。
    /// 注意：不在此处做清缓存；清缓存应在 UIController.ApplyTheme() 统一处理。
    /// </summary>
    public static class ThemeManager
    {
        public static Theme Current { get; private set; } = new Theme();

        // ★★★ 修改：移除 _stringPool，只保留 _colorCache ★★★
        // private static readonly Dictionary<string, string> _stringPool ... (删除)
        private static readonly Dictionary<string, Color> _colorCache = new(32);
        private static readonly object _lock = new object();

        public static string ThemeDir
        {
            get
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "resources/themes");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// 列出可用主题文件名（不含扩展名）
        /// </summary>
        public static IEnumerable<string> GetAvailableThemes()
        {
            try
            {
                return Directory.EnumerateFiles(ThemeDir, "*.json")
                                .Select(Path.GetFileNameWithoutExtension)
                                .OrderBy(n => n)
                                .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 统一的主题序列化选项：读写共用同一套选项，
        /// 保证"已知键"与"扩展键"的匹配规则一致（大小写不敏感），
        /// 避免同一份文件用不同选项读写时出现同名键重复写出。
        /// </summary>
        public static JsonSerializerOptions CreateSerializerOptions(bool writeIndented = true)
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                WriteIndented = writeIndented
            };
        }

        /// <summary>
        /// 加载指定主题：读取 JSON → 反序列化 → 构建字体 → 设置 Current。
        /// </summary>
        public static Theme Load(string name)
        {
            try
            {
                var path = Path.Combine(ThemeDir, $"{name}.json");
                if (!File.Exists(path))
                    throw new FileNotFoundException("Theme json not found", path);

                var json = File.ReadAllText(path);
                var theme = JsonSerializer.Deserialize<Theme>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    IgnoreReadOnlyProperties = true,
                    AllowTrailingCommas = true
                });

                if (theme == null)
                    throw new Exception("Theme parse failed.");

                // 兜底：扩展键不得与已知属性同名（否则序列化会重复写出同名键）
                PruneExtensionDataConflicts(theme);

                if (theme.Font != null)
                {
                    theme.Font.Family = UIUtils.Intern(theme.Font.Family);
                    if (!string.IsNullOrEmpty(theme.Font.ValueFamily))
                    {
                        theme.Font.ValueFamily = UIUtils.Intern(theme.Font.ValueFamily);
                    }
                }
                // 构建运行期字体
                theme.BuildFonts();

                Current = theme;
                Console.WriteLine($"[ThemeManager] Loaded theme: {theme.Name} (v{theme.Version})");

                return theme;
            }
            catch (Exception ex)
            {
                // 兜底主题，保证程序可继续运行
                Console.WriteLine($"[ThemeManager] Load error: {ex.Message}");
                var fallback = new Theme();
                fallback.BuildFonts();
                Current = fallback;
                return fallback;
            }
        }

        // ★★★ 修改：删除私有 Intern 方法 ★★★
        // private static string Intern(string str) ... (删除)

        /// <summary>
        /// 清理扩展数据中与本层已知属性同名的键（大小写不敏感比较）。
        /// 正常加载路径（大小写不敏感）下已知属性优先、不会命中；
        /// 此方法用于兜住"用非大小写不敏感选项加载"的场景，
        /// 避免序列化时同名已知键与扩展键重复写出。
        /// </summary>
        public static void PruneExtensionDataConflicts(Theme theme)
        {
            if (theme == null) return;

            PruneNode(theme, typeof(Theme));
            PruneNode(theme.Layout, typeof(LayoutConfig));
            PruneNode(theme.Font, typeof(FontConfig));
            PruneNode(theme.Color, typeof(ColorConfig));
            PruneNode(theme.Thresholds, typeof(ThresholdConfig));
            if (theme.Thresholds != null)
            {
                PruneNode(theme.Thresholds.Load, typeof(ThresholdSet));
                PruneNode(theme.Thresholds.Temp, typeof(ThresholdSet));
                PruneNode(theme.Thresholds.Vram, typeof(ThresholdSet));
                PruneNode(theme.Thresholds.Mem, typeof(ThresholdSet));
                PruneNode(theme.Thresholds.NetKBps, typeof(ThresholdSet));
            }
            PruneNode(theme.Backdrop, typeof(BackdropConfig));
            PruneNode(theme.Rendering, typeof(ThemeRenderingConfig));
        }

        private static void PruneNode(object? node, Type type)
        {
            if (node == null) return;

            var extProp = type.GetProperty("ExtensionData", BindingFlags.Public | BindingFlags.Instance);
            if (extProp?.GetValue(node) is not Dictionary<string, JsonElement> ext) return;
            if (ext.Count == 0) return;

            var known = KnownPropertyNames(type);

            List<string>? toRemove = null;
            foreach (var key in ext.Keys)
            {
                if (known.Contains(key))
                    (toRemove ??= new List<string>()).Add(key);
            }
            if (toRemove == null) return;

            foreach (var key in toRemove) ext.Remove(key);
        }

        private static HashSet<string> KnownPropertyNames(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                       .Where(p => p.CanRead)
                       .Select(p => p.Name)
                       .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name))
                       .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public static Color ParseColor(string colorStr)
        {
            if (string.IsNullOrWhiteSpace(colorStr))
                return Color.Transparent;

            // ★★★ 修改：使用 UIUtils 的全局池 ★★★
            string key = UIUtils.Intern(colorStr);

            lock (_lock)
            {
                if (_colorCache.TryGetValue(key, out var cached))
                    return cached;
            }

            Color color;
            if (colorStr.StartsWith('#'))
            {
                ReadOnlySpan<char> hex = colorStr.AsSpan(1);
                try {
                    if (hex.Length == 6)
                    {
                        int r = Convert.ToInt32(hex.Slice(0, 2).ToString(), 16);
                        int g = Convert.ToInt32(hex.Slice(2, 2).ToString(), 16);
                        int b = Convert.ToInt32(hex.Slice(4, 2).ToString(), 16);
                        color = Color.FromArgb(r, g, b);
                    }
                    else if (hex.Length == 8)
                    {
                        int a = Convert.ToInt32(hex.Slice(0, 2).ToString(), 16);
                        int r = Convert.ToInt32(hex.Slice(2, 2).ToString(), 16);
                        int g = Convert.ToInt32(hex.Slice(4, 2).ToString(), 16);
                        int b = Convert.ToInt32(hex.Slice(6, 2).ToString(), 16);
                        color = Color.FromArgb(a, r, g, b);
                    }
                    else
                    {
                        color = Color.Transparent;
                    }
                } catch { color = Color.Transparent; }
            }
            else if (colorStr.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase)
                     && colorStr.EndsWith(")", StringComparison.Ordinal))
            {
                // rgba(r,g,b,a)：r/g/b 为 0–255 整数，a 统一为 0–1（1 = 不透明）
                color = ParseRgbaColor(colorStr);
            }
            else
            {
                color = Color.FromName(colorStr);
            }

            // 🔒 写缓存加锁
            lock (_lock)
            {
                _colorCache[key] = color;
            }
            return color;
        }

        /// <summary>
        /// 解析 rgba(r,g,b,a)：
        /// - r/g/b：0–255 整数（越界拒绝）
        /// - a：0–1（1 = 不透明；越界 / NaN / Infinity 拒绝）
        /// - alpha×255 按 AwayFromZero 舍入
        /// - 使用 InvariantCulture，避免受系统区域设置影响
        /// 非法值一律回退到全透明色（与旧行为一致，不抛异常）。
        /// </summary>
        private static Color ParseRgbaColor(string colorStr)
        {
            int open = colorStr.IndexOf('(');
            int close = colorStr.LastIndexOf(')');
            if (open < 0 || close <= open) return Color.Transparent;

            string body = colorStr.Substring(open + 1, close - open - 1);
            string[] parts = body.Split(',');
            if (parts.Length != 4) return Color.Transparent;

            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ||
                !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int g) ||
                !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
            {
                return Color.Transparent;
            }
            if (r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255)
                return Color.Transparent;

            if (!double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double a))
                return Color.Transparent;
            if (double.IsNaN(a) || double.IsInfinity(a)) return Color.Transparent;
            if (a < 0d || a > 1d) return Color.Transparent;

            int alpha = (int)Math.Round(a * 255d, MidpointRounding.AwayFromZero);
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;

            return Color.FromArgb(alpha, r, g, b);
        }

        public static void ClearCaches()
        {
            lock (_lock) // 🔒 加锁
            {
                _colorCache.Clear();
                // _stringPool.Clear(); (删除，这里不需要清理全局字符串，因为主题切换不影响硬件Key)
            }
            // ★★★ 新增：清理 UIUtils 的画刷缓存 (配合主题切换) ★★★
            UIUtils.ClearBrushCache();
        }
    }
}
