using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
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
        [JsonIgnore] public Font FontTitle = ThemeManager.CaptionFontOrDefault;
        [JsonIgnore] public Font FontGroup = ThemeManager.CaptionFontOrDefault;
        [JsonIgnore] public Font FontItem = ThemeManager.CaptionFontOrDefault;
        [JsonIgnore] public Font FontValue = ThemeManager.CaptionFontOrDefault;
        [JsonIgnore] public Font FontTaskbar = ThemeManager.CaptionFontOrDefault;

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
                FontTitle = FontGroup = FontItem = FontValue = ThemeManager.CaptionFontOrDefault;
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
        /// <summary>
        /// 系统题注字体。
        /// <para>★ <see cref="SystemFonts.CaptionFont"/> 在缺少对应系统字体的环境
        /// （精简系统 / 容器 / 部分服务端会话）下会返回 <c>null</c>，直接赋给非空
        /// <see cref="Font"/> 字段会产生 CS8601 并在后续解引用处炸掉。此处统一回退到
        /// <see cref="SystemFonts.DefaultFont"/>（.NET 8 标注为非空）。</para>
        /// </summary>
        public static Font CaptionFontOrDefault => SystemFonts.CaptionFont ?? SystemFonts.DefaultFont;

        public static Theme Current { get; private set; } = new Theme();

        // [S04] 颜色缓存与解析迁移到 ThemeColorParser（统一校验器，成败分离缓存）。

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
                                .Select(p => Path.GetFileNameWithoutExtension(p) ?? string.Empty)
                                .OrderBy(n => n)
                                .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 轻量判定主题是否声明了 GPU 渲染路线（读 <c>rendering.backend</c>）。
        /// <para>★ 刻意用 <see cref="JsonDocument"/> 只读 <c>rendering</c> 块，
        /// **不走** <see cref="Load"/>（那会设 <see cref="Current"/>、构建字体、产生全局副作用
        /// ——设置页/托盘逐主题标注绝不能换掉当前主题，R3 §4.2-2.1）。</para>
        /// <para>★ 容错（R3 §4.2-2.2）：缺文件 / 坏 JSON / 缺 rendering 或 backend 键
        /// 一律归类为 legacy（false），不得抛到 UI 层。</para>
        /// </summary>
        public static bool IsGpuTheme(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                // 防御纵深：主题名来自本机 settings.json，可能被手工改成 "..\.." 这类
                // 路径拼接形式；先消毒，非法名直接按 legacy 处理（走下方 catch）。
                LiteMonitor.ThemeEditor.ThemeFileService.ValidateThemeName(name);
                string path = Path.Combine(ThemeDir, $"{name}.json");
                if (!File.Exists(path)) return false;

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("rendering", out JsonElement rendering))
                    return false;
                if (rendering.ValueKind != JsonValueKind.Object) return false;
                if (!rendering.TryGetProperty("backend", out JsonElement backend))
                    return false;
                if (backend.ValueKind != JsonValueKind.String) return false;

                return string.Equals(
                    backend.GetString()?.Trim(),
                    ThemeRenderingConfig.GpuBackend,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
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
        /// 主题加载的如实结果（R3/F02）：请求身份、源文件摘要、实际主题、是否用了缺省主题。
        /// 加载失败时 Theme 为缺省主题，Success=false，调用方不得把它当作请求主题。
        /// </summary>
        public sealed record ThemeLoadResult(
            string RequestedName,
            string? SourcePath,
            string? SourceHash,
            Theme? Theme,
            bool Success,
            bool UsedDefault,
            string? Error);

        /// <summary>最近一次 LoadWithResult 的结果；供应用编排确认“实际加载的是谁”。</summary>
        public static ThemeLoadResult? LastLoad { get; private set; }

        /// <summary>
        /// 只读检查主题文件，不修改 Current。供工作室在持久化前预检，
        /// 避免坏 JSON 先落盘再静默套缺省主题。
        /// </summary>
        public static ThemeLoadResult Inspect(string path, string? requestedName = null)
        {
            string name = requestedName ?? Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            try
            {
                if (!File.Exists(path))
                {
                    return new ThemeLoadResult(name, path, null, null, false, true, "Theme json not found");
                }

                byte[] bytes = File.ReadAllBytes(path);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                // 摘要必须按原始字节计算，但文本解码必须识别 BOM：
                // Encoding.UTF8.GetString 会把 UTF-8 BOM 保留为 U+FEFF，导致 JSON 解析失败；
                // 也会破坏旧版可读取的 UTF-16 BOM 主题。File.ReadAllText 保留旧行为。
                string json = File.ReadAllText(path);
                var theme = JsonSerializer.Deserialize<Theme>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    IgnoreReadOnlyProperties = true,
                    AllowTrailingCommas = true
                });

                if (theme == null)
                {
                    return new ThemeLoadResult(name, path, hash, null, false, true, "Theme parse failed.");
                }

                PruneExtensionDataConflicts(theme);
                if (theme.Font != null)
                {
                    theme.Font.Family = UIUtils.Intern(theme.Font.Family);
                    if (!string.IsNullOrEmpty(theme.Font.ValueFamily))
                    {
                        theme.Font.ValueFamily = UIUtils.Intern(theme.Font.ValueFamily);
                    }
                }
                theme.BuildFonts();
                return new ThemeLoadResult(name, path, hash, theme, true, false, null);
            }
            catch (Exception ex)
            {
                return new ThemeLoadResult(name, path, null, null, false, true, ex.Message);
            }
        }

        /// <summary>加载并发布结果；保留旧 <see cref="Load"/> 的容错行为。</summary>
        public static ThemeLoadResult LoadWithResult(string name)
        {
            string path = Path.Combine(ThemeDir, $"{name}.json");
            try
            {
                // 防御纵深：Skin 字段可能被手工改成路径拼接形式；加载的容错契约是
                // 「失败回缺省主题不抛」，这里同样不抛、也绝不读任意路径的 JSON。
                LiteMonitor.ThemeEditor.ThemeFileService.ValidateThemeName(name);
            }
            catch (ArgumentException)
            {
                var invalidNameFallback = new Theme();
                invalidNameFallback.BuildFonts();
                Current = invalidNameFallback;
                LastLoad = new ThemeLoadResult(name, path, null, null, false, true, "invalid theme name");
                return LastLoad;
            }

            ThemeLoadResult inspected = Inspect(path, name);
            if (inspected.Success && inspected.Theme != null)
            {
                Current = inspected.Theme;
                LastLoad = inspected;
                Debug.WriteLine($"[ThemeManager] Loaded theme: {inspected.Theme.Name} (v{inspected.Theme.Version})");
                return inspected;
            }

            Debug.WriteLine($"[ThemeManager] Load error: {inspected.Error}");
            var fallback = new Theme();
            fallback.BuildFonts();
            Current = fallback;
            LastLoad = inspected with { Theme = fallback, UsedDefault = true };
            return LastLoad;
        }

        /// <summary>
        /// 加载指定主题：读取 JSON → 反序列化 → 构建字体 → 设置 Current。
        /// 失败时返回缺省主题，保证程序可继续运行；需要知道失败原因用 <see cref="LoadWithResult"/>。
        /// </summary>
        public static Theme Load(string name) => LoadWithResult(name).Theme ?? new Theme();

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

            // 【GPU T05】新增兄弟块 rendering.fallbackStyle 的字段保留路径：
            //   1. 该块是 ThemeRenderingConfig 的**已声明属性** ⇒ 不会被顶层裁剪丢掉；
            //   2. 块内未知键同样按"已知属性名"口径清理，避免保存时与已知键同名重复写出；
            //      未知键本身原样保留（不裁），保存后不丢。
            if (theme.Rendering?.FallbackStyle != null)
            {
                PruneNode(theme.Rendering.FallbackStyle, typeof(ThemeFallbackStyle));
            }
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

        /// <param name="colorStr">颜色字面量。★ 允许为 null —— 主题 DTO 的 Border / Shadow / TextSecondary
        /// 本身就可空，null 与空白一律回落为 <see cref="Color.Transparent"/>（下方 IsNullOrWhiteSpace 已覆盖）。</param>
        /// <remarks>
        /// [S04] 运行时兜底语义保持不变：任何解析失败仍回透明、不抛异常（渲染路径不因坏颜色崩溃）。
        /// 但解析与缓存全部委托 <see cref="ThemeColorParser.TryParse"/>（成功/失败独立状态 + 失败诊断），
        /// 工作室与主题市场用同一校验器拿到明确结论；此处只保留"失败→透明"的渲染侧回退。
        /// </remarks>
        public static Color ParseColor(string? colorStr)
        {
            var result = ThemeColorParser.TryParse(colorStr);
            if (!result.Success)
            {
                // 可定位诊断：不抛异常，渲染侧按既有约定回退透明。
                System.Diagnostics.Debug.WriteLine("[ThemeColor] 解析失败，回退透明：" + result.Diagnostic);
                return Color.Transparent;
            }
            return result.Color;
        }

        public static void ClearCaches()
        {
            ThemeColorParser.ClearCache();
            // ★★★ 新增：清理 UIUtils 的画刷缓存 (配合主题切换) ★★★
            UIUtils.ClearBrushCache();
        }
    }
}
