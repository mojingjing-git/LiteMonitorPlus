using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 效果图导出的**实际渲染路线**（TE07 / 重做计划 §5.3）。
    ///
    /// ★ 诚实性硬规则：只有真正由 GPU 渲染器产出的图才允许是 <see cref="Gpu"/>；
    ///   GPU 主题在 GPU 不可用时用兼容外观渲染器出的图一律是 <see cref="Fallback"/>，
    ///   绝不允许"把经典图标成 GPU"。
    /// </summary>
    public enum ThemeExportImageRoute
    {
        /// <summary>经典（GDI+ / UIRenderer）路线：主题本身就是 legacy 主题。</summary>
        Classic = 0,

        /// <summary>GPU 路线：图由 GPU 渲染器（<c>GpuSceneRenderer</c>）真实产出。</summary>
        Gpu = 1,

        /// <summary>兼容外观路线：主题声明 GPU，但本次导出实际走的是经典渲染器。</summary>
        Fallback = 2
    }

    /// <summary>效果图导出的路线偏好。</summary>
    public enum ImageRoutePreference
    {
        /// <summary>自动：GPU 主题优先走 GPU 路线，不可用时明确回退到兼容外观路线。</summary>
        Auto = 0,

        /// <summary>强制经典路线（不尝试 GPU）。</summary>
        Classic = 1,

        /// <summary>强制 GPU 路线：不可用时导出**失败**（不产出经典冒充的图）。</summary>
        Gpu = 2
    }

    /// <summary>效果图画幅（固定尺寸 ⇒ 导出结果可复现）。</summary>
    public enum ThemeExportImageOrientation
    {
        /// <summary>竖向主面板：240×420。</summary>
        Vertical = 0,

        /// <summary>横向面板：400×120。</summary>
        Horizontal = 1
    }

    /// <summary>
    /// 效果图形态。与 <see cref="ThemeExportImageOrientation"/> 分开：
    /// 旧调用继续按 Vertical/Horizontal；Taskbar 是新形态，不能静默映射成竖向。
    /// </summary>
    public enum ThemeExportImageFormFactor
    {
        Vertical = 0,
        Horizontal = 1,
        Taskbar = 2
    }

    /// <summary>JSON 导出结果。</summary>
    public sealed record ThemeExportResult
    {
        /// <summary>是否成功写出文件。</summary>
        public bool Success { get; init; }

        /// <summary>失败原因（成功时为 null）。</summary>
        public string? Error { get; init; }

        /// <summary>写出的文件绝对路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>**写入文件字节**的 SHA-256（十六进制小写）。</summary>
        public string Sha256 { get; init; } = string.Empty;

        /// <summary>写入文件的字节数。</summary>
        public long ByteCount { get; init; }

        /// <summary>诊断（渲染校验问题、载荷问题等；不改变"文件已写出"的事实）。</summary>
        public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    }

    /// <summary>效果图导出结果。</summary>
    public sealed record ThemeImageExportResult
    {
        /// <summary>是否成功产出图片文件。</summary>
        public bool Success { get; init; }

        /// <summary>失败原因（成功时为 null）。</summary>
        public string? Error { get; init; }

        /// <summary>图片文件绝对路径（失败时可能为空）。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>图片文件字节的 SHA-256。</summary>
        public string Sha256 { get; init; } = string.Empty;

        /// <summary>图片文件字节数。</summary>
        public long ByteCount { get; init; }

        /// <summary>本次图片的**实际**渲染路线。</summary>
        public ThemeExportImageRoute Route { get; init; } = ThemeExportImageRoute.Classic;

        /// <summary>是否为兼容外观回退图（GPU 主题但本次未产出真实 GPU 图）。</summary>
        public bool IsFallback => Route == ThemeExportImageRoute.Fallback;

        /// <summary>诊断（路线说明、GPU 不可用原因等）。</summary>
        public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// TM02：市场投稿元数据（与主题格式 <c>version</c> 分开）。
    /// 发布版本是市场条目字符串；主题格式版本仍是整数，不能互相推导。
    /// </summary>
    public sealed record ThemeSubmissionMetadata
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string NameEn { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string DescriptionEn { get; init; } = string.Empty;
        public string Author { get; init; } = string.Empty;
        public string MarketVersion { get; init; } = "1.0.0";
        public string License { get; init; } = "MIT";
    }

    /// <summary>投稿资料准备结果。</summary>
    public sealed record ThemeSubmissionResult
    {
        /// <summary>本地材料是否齐备（**不代表市场校验通过**）。</summary>
        public bool Success { get; init; }

        /// <summary>失败原因。</summary>
        public string? Error { get; init; }

        /// <summary>资料目录绝对路径。</summary>
        public string Directory { get; init; } = string.Empty;

        /// <summary>最终主题载荷文件（theme.json）路径。</summary>
        public string ThemeJsonPath { get; init; } = string.Empty;

        /// <summary>资料摘要文件（submission.json）路径。</summary>
        public string SubmissionJsonPath { get; init; } = string.Empty;

        /// <summary>投稿入口说明（README.txt）路径。</summary>
        public string ReadmePath { get; init; } = string.Empty;

        /// <summary>效果图路径（若生成失败则为空）。</summary>
        public string? PreviewImagePath { get; init; }

        /// <summary>效果图的**实际**渲染路线（未生成时为 null）。</summary>
        public ThemeExportImageRoute? PreviewRoute { get; init; }

        /// <summary>主题声明的渲染路线（gpu / legacy）。</summary>
        public string ThemeRoute { get; init; } = ThemeRenderingConfig.LegacyBackend;

        /// <summary>最终主题载荷的 SHA-256（对 theme.json 实际文件字节计算）。</summary>
        public string Sha256 { get; init; } = string.Empty;

        /// <summary>最终主题载荷的字节数。</summary>
        public long ByteCount { get; init; }

        /// <summary>需要人工确认的事项（远程引用等）；非空即表示不能只看本地结论。</summary>
        public IReadOnlyList<string> ReviewRequired { get; init; } = Array.Empty<string>();

        /// <summary>诊断（渲染校验诊断、结构问题等）。</summary>
        public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 主题导出与投稿准备服务（TE07）。
    ///
    /// ★ 合同（重做计划 §5.2/§5.3、并行计划 Lane B）：
    ///   1. **只读会话**：本服务从不写入、不保存、不改写会话身份
    ///      （不用 <c>SaveAs</c> / <c>Save</c>），只读 <see cref="ThemeDocumentSession"/> 的载荷。
    ///   2. **载荷同源**：JSON 导出直接调用同程序集的 <c>internal BuildPayload()</c>，
    ///      导出结果与会话当前修订**逐字节一致**（含未知扩展键、缺省不被物化）。
    ///   3. **原子写盘共用核心**：写文件一律走 <see cref="ThemeFileService.WriteThemeJsonToPath"/>
    ///      （Lane I 提供的共用 SafeWrite 核心），本服务不另写替换逻辑。
    ///   4. **路线诚实**：效果图必须带 <see cref="ThemeExportImageRoute"/>；
    ///      GPU 不可用时返回失败或**明确标注**的兼容外观图，绝不把经典图标成 GPU。
    ///   5. **不伪造市场校验**：<c>ThemeMarket.ValidateThemeContent</c> 是 private 且下载侧专用，
    ///      本地只做结构与 <c>rendering.Validate()</c> 校验，并声明"待市场 CI / 维护者审核"。
    /// </summary>
    public static class ThemeExportService
    {
        internal sealed record GpuRenderAttempt(bool Success, string? Error);

        internal delegate GpuRenderAttempt GpuRenderAttemptForTests(
            Theme theme,
            string path,
            Size size,
            ThemeExportImageOrientation orientation);

        /// <summary>竖向效果图宽度（固定）。</summary>
        public const int VerticalImageWidth = 240;

        /// <summary>竖向效果图高度（固定）。</summary>
        public const int VerticalImageHeight = 420;

        /// <summary>横向效果图宽度（固定）。</summary>
        public const int HorizontalImageWidth = 400;

        /// <summary>横向效果图高度（固定）。</summary>
        public const int HorizontalImageHeight = 120;

        /// <summary>任务栏效果图宽度（固定）。</summary>
        public const int TaskbarImageWidth = 400;

        /// <summary>任务栏效果图高度（固定，与生产参考任务栏高度一致）。</summary>
        public const int TaskbarImageHeight = 32;

        /// <summary>投稿资料里的最终主题载荷文件名。</summary>
        public const string ThemeJsonFileName = "theme.json";

        /// <summary>投稿资料里的资料摘要文件名。</summary>
        public const string SubmissionJsonFileName = "submission.json";

        /// <summary>投稿资料里的投稿入口说明文件名。</summary>
        public const string ReadmeFileName = "README.txt";

        /// <summary>投稿资料里的效果图文件名。</summary>
        public const string PreviewImageFileName = "preview.png";

        /// <summary>无 BOM 的 UTF-8（与 <see cref="ThemeFileService"/> 的写入口径一致）。</summary>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // ============================================================
        // JSON 导出
        // ============================================================

        /// <summary>
        /// 导出当前修订的主题 JSON（不要求已保存；不修改会话）。
        /// </summary>
        /// <param name="session">只读会话。载荷取自 <c>internal BuildPayload()</c>。</param>
        /// <param name="path">目标文件路径（目录不存在时自动创建）。</param>
        /// <returns>导出结果：SHA-256（对写入文件字节）、字节数与诊断列表。</returns>
        public static ThemeExportResult ExportJson(ThemeDocumentSession session, string path)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("导出路径不能为空。", nameof(path));

            List<string> diagnostics = new();

            // ★ 同程序集直接调用 internal BuildPayload()：导出结果与会话当前修订逐字节一致。
            (byte[] payload, IReadOnlyList<string> payloadDiagnostics) = session.BuildPayload();
            foreach (string item in payloadDiagnostics) diagnostics.Add(item);

            // 载荷自检：必须是可解析的 JSON 对象（BuildPayload 由已解析的源 JSON 派生，理论上恒成立）
            JsonObject? root = null;
            try
            {
                root = JsonNode.Parse(
                    payload,
                    nodeOptions: null,
                    documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject;
            }
            catch (Exception ex)
            {
                return new ThemeExportResult
                {
                    Success = false,
                    Error = "主题载荷不是合法 JSON：" + ex.Message,
                    Diagnostics = diagnostics
                };
            }

            if (root == null)
            {
                return new ThemeExportResult
                {
                    Success = false,
                    Error = "主题载荷的根节点不是 JSON 对象。",
                    Diagnostics = diagnostics
                };
            }

            // 渲染配置校验：问题写进诊断（导出是数据快照，不代替保存/投稿校验）
            CollectRenderingDiagnostics(session.Draft, diagnostics);

            string fullPath = Path.GetFullPath(path);
            try
            {
                // ★ 共用原子写入核心（不先删目标、失败保留原文件）
                ThemeFileService.WriteThemeJsonToPath(fullPath, Encoding.UTF8.GetString(payload));
            }
            catch (Exception ex)
            {
                return new ThemeExportResult
                {
                    Success = false,
                    Error = "写入失败：" + ex.Message,
                    Path = fullPath,
                    Diagnostics = diagnostics
                };
            }

            // SHA-256 与字节数：**对写入文件字节**计算
            byte[] written = File.ReadAllBytes(fullPath);
            return new ThemeExportResult
            {
                Success = true,
                Path = fullPath,
                Sha256 = Sha256Of(written),
                ByteCount = written.Length,
                Diagnostics = diagnostics
            };
        }

        // ============================================================
        // 效果图导出
        // ============================================================

        /// <summary>
        /// 按形态导出效果图：Taskbar 走独立的任务栏渲染器，不落入竖向分支。
        /// 旧 orientation 重载保留，避免破坏既有调用。
        /// </summary>
        public static ThemeImageExportResult ExportEffectImage(
            ThemeDocumentSession session,
            string path,
            ImageRoutePreference preference,
            ThemeExportImageFormFactor formFactor,
            Settings? taskbarSettings = null,
            bool taskbarLightMode = false)
        {
            if (formFactor == ThemeExportImageFormFactor.Taskbar)
            {
                return ExportTaskbarImage(session, path, preference, taskbarSettings, taskbarLightMode);
            }

            ThemeExportImageOrientation orientation =
                formFactor == ThemeExportImageFormFactor.Horizontal
                    ? ThemeExportImageOrientation.Horizontal
                    : ThemeExportImageOrientation.Vertical;
            return ExportEffectImage(session, path, preference, orientation);
        }

        /// <summary>
        /// 导出任务栏形态效果图（固定输入、固定尺寸；使用任务栏渲染器，不是 GPU 效果图）。
        /// </summary>
        public static ThemeImageExportResult ExportTaskbarImage(
            ThemeDocumentSession session,
            string path,
            ImageRoutePreference preference,
            Settings? taskbarSettings = null,
            bool taskbarLightMode = false)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("导出路径不能为空。", nameof(path));

            string fullPath = Path.GetFullPath(path);
            var diagnostics = new List<string>
            {
                "任务栏形态由 TaskbarRenderer 固定输入产出；该图不是 GPU 渲染结果。"
            };

            if (preference == ImageRoutePreference.Gpu)
            {
                return new ThemeImageExportResult
                {
                    Success = false,
                    Error = "任务栏形态使用 GDI 任务栏渲染器，不能按 GPU 路线导出。",
                    Path = fullPath,
                    Diagnostics = diagnostics
                };
            }

            Size size = new(TaskbarImageWidth, TaskbarImageHeight);
            return RenderTaskbarImage(
                session.Draft,
                fullPath,
                size,
                taskbarSettings?.DeepClone() ?? new Settings(),
                taskbarLightMode,
                diagnostics);
        }

        /// <summary>
        /// 导出效果图（经典 / GPU / 兼容外观三条路线，路线如实标注）。
        /// </summary>
        /// <param name="session">只读会话（使用其草稿主题渲染，不修改草稿）。</param>
        /// <param name="path">目标 PNG 路径（目录不存在时自动创建）。</param>
        /// <param name="preference">路线偏好。</param>
        /// <param name="orientation">画幅：竖向 240×420 / 横向 400×120。</param>
        public static ThemeImageExportResult ExportEffectImage(
            ThemeDocumentSession session,
            string path,
            ImageRoutePreference preference,
            ThemeExportImageOrientation orientation = ThemeExportImageOrientation.Vertical)
            => ExportEffectImageCore(session, path, preference, orientation, gpuRenderAttempt: null);

        internal static ThemeImageExportResult ExportEffectImage(
            ThemeDocumentSession session,
            string path,
            ImageRoutePreference preference,
            ThemeExportImageOrientation orientation,
            GpuRenderAttemptForTests gpuRenderAttempt)
            => ExportEffectImageCore(
                session,
                path,
                preference,
                orientation,
                gpuRenderAttempt ?? throw new ArgumentNullException(nameof(gpuRenderAttempt)));

        private static ThemeImageExportResult ExportEffectImageCore(
            ThemeDocumentSession session,
            string path,
            ImageRoutePreference preference,
            ThemeExportImageOrientation orientation,
            GpuRenderAttemptForTests? gpuRenderAttempt)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("导出路径不能为空。", nameof(path));

            Theme theme = session.Draft;
            Size size = orientation == ThemeExportImageOrientation.Horizontal
                ? new Size(HorizontalImageWidth, HorizontalImageHeight)
                : new Size(VerticalImageWidth, VerticalImageHeight);

            string fullPath = Path.GetFullPath(path);
            List<string> diagnostics = new();

            bool gpuTheme = IsGpuTheme(theme, out IReadOnlyList<string> renderingErrors, out IReadOnlyList<string> renderingDiagnostics);
            foreach (string item in renderingDiagnostics) diagnostics.Add(item);

            if (!gpuTheme)
            {
                if (preference == ImageRoutePreference.Gpu)
                {
                    return new ThemeImageExportResult
                    {
                        Success = false,
                        Error = "主题未声明 GPU 渲染路线（rendering.backend != gpu），无法产出 GPU 效果图。",
                        Path = fullPath,
                        Diagnostics = diagnostics
                    };
                }

                if (renderingErrors.Count > 0)
                {
                    diagnostics.Add("rendering 校验未通过：" + string.Join(" ", renderingErrors));
                }

                return RenderClassicImage(theme, fullPath, size, orientation, ThemeExportImageRoute.Classic, diagnostics);
            }

            // GPU 主题
            if (preference == ImageRoutePreference.Classic)
            {
                diagnostics.Add("按偏好导出经典渲染图：本图不是 GPU 渲染结果。");
                // ★ B-1 整改（计划 §5.2 B-1）：GPU 主题回退到经典渲染时，
                //   实际渲染的是**回退样式派生主题**（LegacyFallbackStyleResolver），
                //   不是主题本体的配色。旧实现传原始主题 ⇒ 导出图用了主效果配色，
                //   与真实回退画面不一致（市场审图会看到「图和主题对不上」）。
                return RenderClassicImage(
                    LegacyFallbackStyleResolver.BuildDerivedTheme(theme),
                    fullPath, size, orientation, ThemeExportImageRoute.Fallback, diagnostics);
            }

            bool gpuOk;
            string? gpuError;
            if (gpuRenderAttempt is null)
            {
                gpuOk = TryRenderGpuImage(theme, fullPath, size, orientation, out gpuError);
            }
            else
            {
                GpuRenderAttempt attempt = gpuRenderAttempt(theme, fullPath, size, orientation);
                gpuOk = attempt.Success;
                gpuError = attempt.Error;
            }
            if (gpuOk)
            {
                ThemeImageExportResult gpuResult = DescribeImage(
                    fullPath,
                    ThemeExportImageRoute.Gpu,
                    size,
                    diagnostics);
                if (gpuResult.Success)
                {
                    diagnostics.Add("效果图由 GPU 渲染器产出（GpuSceneRenderer 固定输入回读）。");
                    return gpuResult;
                }

                gpuOk = false;
                gpuError = gpuResult.Error ?? "GPU 效果图产物校验失败。";
                diagnostics.Add("GPU 效果图产物校验失败：" + gpuError);
                TryDeleteFile(fullPath);
            }

            // GPU 不可用：按偏好决定"失败"还是"明确标注的兼容外观图"
            diagnostics.Add("GPU 路线不可用：" + (gpuError ?? "未知原因"));
            TryDeleteFile(fullPath);

            if (preference == ImageRoutePreference.Gpu)
            {
                return new ThemeImageExportResult
                {
                    Success = false,
                    Error = "GPU 路线不可用，未产出效果图：" + (gpuError ?? "未知原因"),
                    Path = fullPath,
                    Diagnostics = diagnostics
                };
            }

            diagnostics.Add("已回退到兼容外观图（经典渲染器）：该图不是 GPU 渲染结果。");
            // ★ B-1 整改：回退图必须用回退样式派生主题（与上面的经典偏好分支同源）。
            return RenderClassicImage(
                LegacyFallbackStyleResolver.BuildDerivedTheme(theme),
                fullPath, size, orientation, ThemeExportImageRoute.Fallback, diagnostics);
        }

        // ============================================================
        // 投稿资料
        // ============================================================

        /// <summary>
        /// 准备投稿资料目录：theme.json（最终载荷）+ submission.json（素材摘要）+ README.txt（投稿入口说明），
        /// 并附一张按**实际路线**标注的效果图。
        /// </summary>
        /// <param name="session">只读会话（不修改）。</param>
        /// <param name="directory">资料目录（不存在时自动创建）。</param>
        public static ThemeSubmissionResult PrepareSubmissionMaterials(
            ThemeDocumentSession session, string directory, ThemeSubmissionMetadata? metadata = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("资料目录不能为空。", nameof(directory));

            string fullDir = Path.GetFullPath(directory);
            Directory.CreateDirectory(fullDir);

            List<string> diagnostics = new();
            List<string> reviewRequired = new();

            // ① 最终载荷
            string themeJsonPath = Path.Combine(fullDir, ThemeJsonFileName);
            ThemeExportResult export = ExportJson(session, themeJsonPath);
            if (!export.Success)
            {
                return new ThemeSubmissionResult
                {
                    Success = false,
                    Error = export.Error,
                    Directory = fullDir,
                    Diagnostics = export.Diagnostics
                };
            }

            diagnostics.AddRange(export.Diagnostics);

            // ② 本地结构校验：JSON 可解析 + rendering.Validate()
            //    （市场校验 ThemeMarket.ValidateThemeContent 是 private 且下载侧专用，
            //      本地**不**冒充市场校验结论）
            JsonObject root;
            try
            {
                root = JsonNode.Parse(
                    File.ReadAllBytes(themeJsonPath),
                    nodeOptions: null,
                    documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject
                    ?? throw new InvalidDataException("主题载荷的根节点不是 JSON 对象。");
            }
            catch (Exception ex)
            {
                return new ThemeSubmissionResult
                {
                    Success = false,
                    Error = "主题载荷不是合法 JSON：" + ex.Message,
                    Directory = fullDir,
                    ThemeJsonPath = themeJsonPath,
                    Diagnostics = diagnostics
                };
            }

            bool gpuTheme = IsGpuTheme(session.Draft, out IReadOnlyList<string> renderingErrors, out IReadOnlyList<string> renderingDiagnostics);
            foreach (string item in renderingDiagnostics) diagnostics.Add(item);
            if (renderingErrors.Count > 0)
            {
                return new ThemeSubmissionResult
                {
                    Success = false,
                    Error = "rendering 配置校验未通过：" + string.Join(" ", renderingErrors),
                    Directory = fullDir,
                    ThemeJsonPath = themeJsonPath,
                    ThemeRoute = gpuTheme ? ThemeRenderingConfig.GpuBackend : ThemeRenderingConfig.LegacyBackend,
                    Sha256 = export.Sha256,
                    ByteCount = export.ByteCount,
                    Diagnostics = diagnostics
                };
            }

            // ②b [S04] 已知颜色字段统一校验（与运行时/主题市场同一校验器）：
            // 非法颜色阻断投稿资料生成，指向具体字段与原因。
            IReadOnlyList<string> colorFailures = ThemeColorParser.ValidateTheme(session.Draft);
            if (colorFailures.Count > 0)
            {
                return new ThemeSubmissionResult
                {
                    Success = false,
                    Error = "颜色字段校验未通过：" + string.Join("; ", colorFailures),
                    Directory = fullDir,
                    ThemeJsonPath = themeJsonPath,
                    ThemeRoute = gpuTheme ? ThemeRenderingConfig.GpuBackend : ThemeRenderingConfig.LegacyBackend,
                    Sha256 = export.Sha256,
                    ByteCount = export.ByteCount,
                    Diagnostics = diagnostics
                };
            }

            // ③ 远程引用自检：主题包不得引用远程资源（市场合同 F7 的同一口径）
            if (ContainsRemoteReference(root))
            {
                reviewRequired.Add("远程引用需人工确认：主题 JSON 含 http/https 引用，提交前请移除或说明。");
                diagnostics.Add("远程引用需人工确认：主题 JSON 含 http/https 引用。");
            }

            // ④ 效果图（按实际路线标注）
            string previewPath = Path.Combine(fullDir, PreviewImageFileName);
            ThemeImageExportResult preview = ExportEffectImage(session, previewPath, ImageRoutePreference.Auto);
            diagnostics.AddRange(preview.Diagnostics);
            if (!preview.Success)
            {
                reviewRequired.Add("效果图未产出：" + (preview.Error ?? "未知原因"));
            }

            // ★ B-3 整改（计划 §5.2 B-3，审核意见 M-1 采纳）：
            //   submission.json 的 route 字段是**主题声明路线**（合同字段，取值 gpu|legacy，
            //   与主题 rendering.backend 互验，市场侧会校验）—— 一律不改写。
            //   实际出图路线由既有 previewRoute / previewIsFallback 如实承载。
            //   两者不一致时（GPU 主题但实际产出的是回退/经典图）写入既有 reviewRequired，
            //   请人工确认 —— 不新增第三处 route 写入，也不改合同字段结构。
            if (preview.Success && gpuTheme && preview.Route != ThemeExportImageRoute.Gpu)
            {
                reviewRequired.Add(
                    "效果图与主题声明路线不一致：主题声明 GPU 路线，但效果图实际由" +
                    (preview.Route == ThemeExportImageRoute.Fallback ? "兼容外观回退渲染" : "经典渲染") +
                    "产出（previewRoute=" + preview.Route + "），提交前请人工确认是否接受。");
            }

            // ⑤ submission.json：素材摘要 + TM02 市场投稿元数据
            ThemeSubmissionMetadata meta = metadata ?? new ThemeSubmissionMetadata
            {
                Id = BuildDefaultSubmissionId(session.ThemeName),
                Name = ReadString(root, "name") ?? session.ThemeName,
                NameEn = ReadString(root, "name") ?? session.ThemeName,
                Author = ReadString(root, "author") ?? string.Empty
            };
            if (string.IsNullOrWhiteSpace(meta.Id) || !IsValidSubmissionId(meta.Id))
            {
                return new ThemeSubmissionResult
                {
                    Success = false,
                    Error = "投稿 ID 非法：只允许 1–40 个小写字母、数字或连字符，且不能以连字符开头/结尾。",
                    Directory = fullDir,
                    ThemeJsonPath = themeJsonPath,
                    Diagnostics = diagnostics
                };
            }
            if (!IsValidMarketVersion(meta.MarketVersion))
            {
                return new ThemeSubmissionResult
                {
                    Success = false,
                    Error = "发布版本非法：必须是 major.minor.patch 三段非负整数。",
                    Directory = fullDir,
                    ThemeJsonPath = themeJsonPath,
                    Diagnostics = diagnostics
                };
            }
            if (string.IsNullOrWhiteSpace(meta.License))
            {
                return new ThemeSubmissionResult
                {
                    Success = false,
                    Error = "许可证不能为空。",
                    Directory = fullDir,
                    ThemeJsonPath = themeJsonPath,
                    Diagnostics = diagnostics
                };
            }

            string submissionPath = Path.Combine(fullDir, SubmissionJsonFileName);
            var submission = new Dictionary<string, object?>
            {
                ["id"] = meta.Id,
                ["name"] = string.IsNullOrWhiteSpace(meta.Name) ? (ReadString(root, "name") ?? session.ThemeName) : meta.Name,
                ["name_en"] = meta.NameEn,
                ["description"] = meta.Description,
                ["description_en"] = meta.DescriptionEn,
                ["market_version"] = meta.MarketVersion,
                ["license"] = meta.License,
                ["author"] = string.IsNullOrWhiteSpace(meta.Author) ? (ReadString(root, "author") ?? string.Empty) : meta.Author,
                ["version"] = ReadRaw(root, "version"),
                ["material"] = ReadMaterial(session.Draft),
                ["route"] = gpuTheme ? ThemeRenderingConfig.GpuBackend : ThemeRenderingConfig.LegacyBackend,
                ["capabilities"] = BuildCapabilities(session.Draft, gpuTheme),
                ["file"] = ThemeJsonFileName,
                ["byteCount"] = export.ByteCount,
                ["sha256"] = export.Sha256,
                ["exportedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["previewImage"] = preview.Success ? PreviewImageFileName : null,
                ["previewRoute"] = preview.Success ? preview.Route.ToString() : null,
                ["previewIsFallback"] = preview.Success ? preview.IsFallback : (bool?)null,
                ["marketValidation"] = new Dictionary<string, object?>
                {
                    ["status"] = "not_validated_locally",
                    ["statement"] = "本地只做结构与 rendering 校验；市场校验由市场 CI 与维护者审核完成。",
                    ["reviewRequired"] = reviewRequired
                },
                ["diagnostics"] = diagnostics
            };

            try
            {
                // 复用共用原子写入核心（同目录临时文件 + 安全替换）
                // 用宽松编码器：中文等非 ASCII 字符保持可读（文件仍是标准 JSON）
                ThemeFileService.WriteThemeJsonToPath(
                    submissionPath,
                    JsonSerializer.Serialize(submission, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    }));
            }
            catch (Exception ex)
            {
                return new ThemeSubmissionResult
                {
                    Success = false,
                    Error = "写入投稿摘要失败：" + ex.Message,
                    Directory = fullDir,
                    ThemeJsonPath = themeJsonPath,
                    Diagnostics = diagnostics
                };
            }

            // ⑥ README.txt：投稿入口说明
            string readmePath = Path.Combine(fullDir, ReadmeFileName);
            try
            {
                File.WriteAllText(readmePath, BuildReadme(session, gpuTheme, preview), Utf8NoBom);
            }
            catch (Exception ex)
            {
                return new ThemeSubmissionResult
                {
                    Success = false,
                    Error = "写入投稿说明失败：" + ex.Message,
                    Directory = fullDir,
                    ThemeJsonPath = themeJsonPath,
                    SubmissionJsonPath = submissionPath,
                    Diagnostics = diagnostics
                };
            }

            return new ThemeSubmissionResult
            {
                Success = true,
                Directory = fullDir,
                ThemeJsonPath = themeJsonPath,
                SubmissionJsonPath = submissionPath,
                ReadmePath = readmePath,
                PreviewImagePath = preview.Success ? preview.Path : null,
                PreviewRoute = preview.Success ? preview.Route : null,
                ThemeRoute = gpuTheme ? ThemeRenderingConfig.GpuBackend : ThemeRenderingConfig.LegacyBackend,
                Sha256 = export.Sha256,
                ByteCount = export.ByteCount,
                ReviewRequired = reviewRequired,
                Diagnostics = diagnostics
            };
        }

        // ============================================================
        // 内部实现
        // ============================================================

        /// <summary>
        /// 判定主题是否走 GPU 路线（判据与市场快照服务同源：
        /// rendering 存在 + backend == gpu + rendering 校验全绿）。
        /// </summary>
        private static bool IsGpuTheme(
            Theme theme,
            out IReadOnlyList<string> errors,
            out IReadOnlyList<string> diagnostics)
        {
            errors = Array.Empty<string>();
            diagnostics = Array.Empty<string>();

            ThemeRenderingConfig? rendering = theme?.Rendering;
            if (rendering == null)
            {
                return false;
            }

            errors = rendering.Validate(out IReadOnlyList<string> renderDiagnostics);
            diagnostics = renderDiagnostics;

            return rendering.IsGpuRequested && errors.Count == 0;
        }

        /// <summary>把渲染配置校验结果写进诊断列表（错误也记录，不改变导出是否成功）。</summary>
        private static void CollectRenderingDiagnostics(Theme theme, List<string> diagnostics)
        {
            ThemeRenderingConfig? rendering = theme?.Rendering;
            if (rendering == null)
            {
                return;
            }

            IReadOnlyList<string> errors = rendering.Validate(out IReadOnlyList<string> renderDiagnostics);
            foreach (string item in renderDiagnostics) diagnostics.Add(item);
            foreach (string item in errors) diagnostics.Add("rendering 校验错误：" + item);
        }

        /// <summary>
        /// 经典路线渲染：固定 Mock 输入 + 固定样例背景 + 固定画幅 ⇒ 结果可复现。
        /// 绝不抓取真实桌面。
        /// <para>
        /// ★ B-2 整改（计划 §5.2 B-2 修法 2）：按画幅分派到**生产同源**的渲染器 ——
        ///   竖向走 <c>UILayout</c> + <c>ThemePreviewControl.RenderPreview</c>，
        ///   横向走 <c>HorizontalLayout</c> + <c>HorizontalRenderer</c>（横排主窗口同一套）。
        ///   旧实现两种画幅都走竖向引擎 ⇒ 横向导出图实际是「竖向内容塞进横向画布」。
        /// </para>
        /// </summary>
        private static ThemeImageExportResult RenderClassicImage(
            Theme theme,
            string path,
            Size size,
            ThemeExportImageOrientation orientation,
            ThemeExportImageRoute route,
            List<string> diagnostics)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.FromArgb(16, 20, 24));
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                    if (orientation == ThemeExportImageOrientation.Horizontal)
                    {
                        RenderClassicHorizontal(g, theme, size);
                    }
                    else
                    {
                        RenderClassicVertical(g, theme, size);
                    }

                    DrawRouteCaption(g, size, route, diagnostics);
                }

                bitmap.Save(path, ImageFormat.Png);
            }
            catch (Exception ex)
            {
                TryDeleteFile(path);
                return new ThemeImageExportResult
                {
                    Success = false,
                    Error = "经典路线渲染失败：" + ex.Message,
                    Path = path,
                    Route = route,
                    Diagnostics = diagnostics
                };
            }

            return DescribeImage(path, route, size, diagnostics);
        }

        /// <summary>
        /// 任务栏经典渲染：固定 400×32 画布，使用任务栏布局和 TaskbarRenderer。
        /// 不创建控件、不读全局设置、不触碰生产任务栏静态样式。
        /// </summary>
        private static ThemeImageExportResult RenderTaskbarImage(
            Theme theme,
            string path,
            Size size,
            Settings settings,
            bool lightMode,
            List<string> diagnostics)
        {
            ThemeExportImageRoute route = theme.Rendering?.IsGpuRequested == true
                ? ThemeExportImageRoute.Fallback
                : ThemeExportImageRoute.Classic;

            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(lightMode
                        ? Color.FromArgb(243, 243, 243)
                        : Color.FromArgb(32, 32, 32));
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                    Theme previewTheme = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);
                    List<Column> columns = ThemePreviewScene.BuildColumns(
                        ThemePreviewScene.CreateMetrics(),
                        settings.TaskbarSingleLine);

                    // 固定宽度 400，行高由任务栏样式决定；不依赖 DeviceDpi。
                    var layout = new HorizontalLayout(
                        previewTheme,
                        Math.Max(1, size.Width),
                        LayoutMode.Taskbar,
                        settings,
                        null);
                    layout.Build(columns, Math.Max(TaskbarPreviewControl.ReferenceTaskbarHeight, size.Height));

                    using var styleContext = new TaskbarRenderer.TaskbarStyleContext(settings);
                    TaskbarRenderer.Render(g, columns, lightMode, styleContext);
                }

                bitmap.Save(path, ImageFormat.Png);
            }
            catch (Exception ex)
            {
                TryDeleteFile(path);
                return new ThemeImageExportResult
                {
                    Success = false,
                    Error = "任务栏效果图渲染失败：" + ex.Message,
                    Path = path,
                    Route = route,
                    Diagnostics = diagnostics
                };
            }

            return DescribeImage(path, route, size, diagnostics);
        }

        /// <summary>竖向经典渲染（与竖向主窗口同源）。</summary>
        private static void RenderClassicVertical(Graphics g, Theme theme, Size size)
        {
            // 预览副本：只读借用原主题，绝不改写会话草稿
            Theme previewTheme = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);

            // 固定 Mock 分组 + 主题布局（让图里有真实内容，而不是空底板）
            List<GroupLayoutInfo> groups = ThemePreviewControl.CreateMockGroups();
            var layout = new UILayout(previewTheme);
            layout.Build(groups);

            // ★ 正式渲染器四参重载：与主窗口同源的背景层 / 组块 / 外框
            ThemePreviewControl.RenderPreview(g, groups, previewTheme, new Rectangle(0, 0, size.Width, size.Height));
        }

        /// <summary>
        /// 横向经典渲染（与横排主窗口同源）。
        /// <para>
        /// 直接调用 <c>HorizontalLayout</c> + <c>HorizontalRenderer</c>（<c>HorizontalPreviewControl</c>
        /// 的内部同一套），不创建控件实例 ⇒ 无句柄、无 DPI 依赖、无线程亲和要求，结果可复现。
        /// </para>
        /// </summary>
        private static void RenderClassicHorizontal(Graphics g, Theme theme, Size size)
        {
            Theme previewTheme = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);

            // ★ 显式 new Settings()：绝不 Settings.Load()（不读用户磁盘配置）。
            var settings = new Settings { HorizontalSingleLine = false };

            List<Column> columns = ThemePreviewScene.BuildColumns(ThemePreviewScene.CreateMetrics(), singleLine: false);

            // ★ 测量控件传 null ⇒ 无显示上下文 ⇒ 行高按 96 DPI 基线确定性测量。
            var layout = new HorizontalLayout(previewTheme, Math.Max(1, size.Width), LayoutMode.Horizontal, settings, null);
            layout.Build(columns);

            // ★ appearance = null ⇒ 走旧路径（无外观层），与横排生产链逐像素同源。
            HorizontalRenderer.Render(g, previewTheme, columns, layout.PanelWidth, appearance: null);
        }

        /// <summary>
        /// GPU 路线渲染：隐藏宿主 + <see cref="GpuThemePreviewControl"/> 固定输入回读 PNG。
        ///
        /// ★ 启动守卫注入**临时目录**（构造参数）：不写也不读 %LOCALAPPDATA% 的生产状态。
        /// ★ 主题必须带非空 <c>rendering</c>（否则控件启动渲染时空引用）。
        ///
        /// ★★ 为什么**不**直接复用 <c>ThemePreviewService.TryRenderGpuSnapshot</c>（同程序集 internal）：
        ///   1) 它用进程级 <c>GpuThemePreviewControl.PreviewGuard</c>，状态写在 %LOCALAPPDATA%，
        ///      与"导出不得污染 %LOCALAPPDATA%"的硬约束冲突；
        ///   2) 它只调 <c>CreateControl()</c>，隐藏宿主（Opacity=0、未 Show）的句柄常常建不出来，
        ///      于是在有 GPU 的机器上也会静默退回经典帧（实测如此）。
        ///   本线按"显式取 Handle"的方式建链，保证 GPU 路线在有 GPU 的机器上真能出图。
        /// </summary>
        private static bool TryRenderGpuImage(
            Theme theme,
            string path,
            Size size,
            ThemeExportImageOrientation orientation,
            out string? error)
        {
            error = null;

            if (theme.Rendering == null)
            {
                error = "主题缺少 rendering 块，GPU 预览无法启动。";
                return false;
            }

            string guardDir = Path.Combine(
                Path.GetTempPath(),
                "litemonitor-te07-gpu-guard",
                Guid.NewGuid().ToString("N"));

            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // 临时目录守卫：与生产守卫（%LOCALAPPDATA%）完全隔离
                var guard = new GpuStartupGuard(guardDir, AppContext.BaseDirectory);

                using var owner = new Form
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-30000, -30000),
                    Size = new Size(size.Width + 40, size.Height + 60),
                    FormBorderStyle = FormBorderStyle.None,
                    Opacity = 0d
                };

                // ★ B-2 整改（计划 §5.2 B-2 修法 1）：方向必须经**构造函数**传入 ——
                //   导出链走 new GpuThemePreviewControl(...)，从不调用 UpdateTheme，
                //   所以 shape / horizontalCols 只能走构造形参。
                //   横向还要显式传 width=size.Width：默认列宽 320 会让 400 宽的画幅铺不满。
                var host = orientation == ThemeExportImageOrientation.Horizontal
                    ? new GpuThemePreviewControl(
                        theme,
                        ThemePreviewControl.CreateMockGroups(),
                        guard,
                        PreviewShape.Horizontal,
                        HorizontalPreviewMock.BuildColumns(theme, singleLine: false, width: size.Width))
                    {
                        Bounds = new Rectangle(10, 10, size.Width, size.Height)
                    }
                    : new GpuThemePreviewControl(
                        theme,
                        ThemePreviewControl.CreateMockGroups(),
                        guard)
                    {
                        Bounds = new Rectangle(10, 10, size.Width, size.Height)
                    };
                owner.Controls.Add(host);

                owner.CreateControl();
                // ★ 显式取 Handle：Control.CreateControl 只保证"可见子控件"的句柄，
                //   隐藏宿主（Opacity=0、未 Show）常常不会建链。取 Handle 会强制建句柄，
                //   句柄建好后 GpuThemePreviewControl 的 HandleCreated 钩子才会启动渲染器。
                if (!owner.IsHandleCreated) _ = owner.Handle;
                if (!host.IsHandleCreated) _ = host.Handle;
                if (!host.IsHandleCreated)
                {
                    error = "GPU 预览宿主句柄未创建。";
                    return false;
                }

                if (!host.TryCaptureOnce(path))
                {
                    error = host.Error ?? "GPU 帧回读失败。";
                    return false;
                }

                return File.Exists(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                // 临时守卫目录用完即删：不留状态、不污染 %LOCALAPPDATA%
                try
                {
                    if (Directory.Exists(guardDir)) Directory.Delete(guardDir, recursive: true);
                }
                catch
                {
                    // 清理失败不影响导出结果
                }
            }
        }

        /// <summary>读回已写出的图片文件并组装结果。</summary>
        private static ThemeImageExportResult DescribeImage(
            string path,
            ThemeExportImageRoute route,
            Size expectedSize,
            List<string> diagnostics)
        {
            if (!TryReadValidatedImage(path, expectedSize, out byte[]? bytes, out string? error))
            {
                return new ThemeImageExportResult
                {
                    Success = false,
                    Error = error ?? "图片产物校验失败。",
                    Path = path,
                    Route = route,
                    Diagnostics = diagnostics
                };
            }

            byte[] resolvedBytes = bytes!;
            return new ThemeImageExportResult
            {
                Success = true,
                Path = path,
                Sha256 = Sha256Of(resolvedBytes),
                ByteCount = resolvedBytes.Length,
                Route = route,
                Diagnostics = diagnostics
            };
        }

        /// <summary>
        /// 验证效果图产物确为本次可读取的 PNG，并匹配请求像素尺寸。
        /// 仅检查文件存在会让旧文件、损坏文件和错误尺寸产物被误报成功。
        /// </summary>
        internal static bool TryValidateImageArtifact(
            string path,
            Size expectedSize,
            out string? error)
        {
            return TryReadValidatedImage(path, expectedSize, out _, out error);
        }

        private static bool TryReadValidatedImage(
            string path,
            Size expectedSize,
            out byte[]? bytes,
            out string? error)
        {
            bytes = null;
            error = null;

            try
            {
                if (!File.Exists(path))
                {
                    error = "图片文件未生成或不存在。";
                    return false;
                }

                byte[] candidate = File.ReadAllBytes(path);
                if (candidate.Length == 0)
                {
                    error = "图片文件为空。";
                    return false;
                }

                using var stream = new MemoryStream(candidate, writable: false);
                using Image image = Image.FromStream(
                    stream,
                    useEmbeddedColorManagement: false,
                    validateImageData: true);
                if (image.RawFormat.Guid != ImageFormat.Png.Guid)
                {
                    error = "图片产物不是 PNG。";
                    return false;
                }

                if (image.Width != expectedSize.Width || image.Height != expectedSize.Height)
                {
                    error = $"图片尺寸不匹配：实际 {image.Width}x{image.Height}，" +
                        $"期望 {expectedSize.Width}x{expectedSize.Height}。";
                    return false;
                }

                bytes = candidate;
                return true;
            }
            catch (Exception ex)
            {
                error = "图片产物无法读取或已损坏：" + ex.Message;
                return false;
            }
        }

        /// <summary>在图上标注渲染路线（图或伴随资料必须能看出这是哪条路线出的图）。</summary>
        private static void DrawRouteCaption(Graphics g, Size size, ThemeExportImageRoute route, List<string> diagnostics)
        {
            string caption = route switch
            {
                ThemeExportImageRoute.Gpu => "GPU render / GPU 渲染",
                ThemeExportImageRoute.Fallback => "Fallback · compatible look / 兼容外观图（非 GPU 渲染）",
                _ => "Classic render / 经典渲染"
            };

            try
            {
                using var font = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
                using var brush = new SolidBrush(Color.FromArgb(235, 255, 214, 0));
                using var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
                var point = new PointF(4f, Math.Max(2f, size.Height - 16f));
                g.DrawString(caption, font, shadow, new PointF(point.X + 1f, point.Y + 1f));
                g.DrawString(caption, font, brush, point);
            }
            catch
            {
                // 标注失败不影响图片本身
            }
        }

        /// <summary>主题材料信息：材质（无渲染块即 legacy）。</summary>
        private static string ReadMaterial(Theme theme)
        {
            ThemeRenderingConfig? rendering = theme?.Rendering;
            if (rendering == null) return ThemeRenderingConfig.LegacyMaterial;
            string material = rendering.Material?.Trim().ToLowerInvariant() ?? string.Empty;
            return string.IsNullOrEmpty(material) ? ThemeRenderingConfig.LegacyMaterial : material;
        }

        /// <summary>
        /// 主题能力清单（从主题实际内容推导，不臆测）：
        /// 渲染路线、材质、是否请求动态背景、是否有动效、是否带兼容外观六槽。
        /// </summary>
        private static List<string> BuildCapabilities(Theme theme, bool gpuTheme)
        {
            List<string> capabilities = new();
            capabilities.Add(gpuTheme ? "gpu-render" : "legacy-render");

            ThemeRenderingConfig? rendering = theme?.Rendering;
            if (rendering != null)
            {
                string material = rendering.Material?.Trim().ToLowerInvariant() ?? string.Empty;
                if (!string.IsNullOrEmpty(material)) capabilities.Add("material:" + material);

                if (GetBoolean(rendering.Parameters, "liveBackground")) capabilities.Add("live-background");
                if (GetBoolean(rendering.Motion, "motionEnabled")) capabilities.Add("motion");
                if (rendering.FallbackStyle != null) capabilities.Add("custom-fallback-style");
            }

            if (theme?.Backdrop != null) capabilities.Add("backdrop");
            return capabilities;
        }

        private static bool GetBoolean(IReadOnlyDictionary<string, JsonElement> source, string key)
        {
            if (source == null) return false;
            foreach (KeyValuePair<string, JsonElement> pair in source)
            {
                if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                return pair.Value.ValueKind == JsonValueKind.True;
            }

            return false;
        }

        /// <summary>远程引用自检（与市场合同 F7 的口径一致：http/https 字符串）。</summary>
        private static bool ContainsRemoteReference(JsonNode root)
        {
            string text = root.ToJsonString();
            return text.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>读取字符串字段（大小写不敏感）。</summary>
        private static string? ReadString(JsonObject root, string key)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in root)
            {
                if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Value is JsonValue value)
                {
                    return value.TryGetValue<string>(out string? text) ? text : value.ToString();
                }

                return pair.Value?.ToString();
            }

            return null;
        }

        /// <summary>原样读取字段节点（数字/字符串都按原始形态写出）。</summary>
        private static JsonNode? ReadRaw(JsonObject root, string key)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in root)
            {
                if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                return pair.Value?.DeepClone();
            }

            return null;
        }

        /// <summary>
        /// 投稿入口说明（README.txt）。
        ///
        /// ★ 边界声明：只做"生成材料 → 人工提交 PR → 市场 CI / 维护者审核"，
        ///   不写任何"已上架 / 已发布 / 已通过校验"式的结论。
        /// </summary>
        private static string BuildReadme(ThemeDocumentSession session, bool gpuTheme, ThemeImageExportResult preview)
        {
            string routeText = gpuTheme ? "gpu" : "legacy";
            string previewText = preview.Success
                ? preview.Route switch
                {
                    ThemeExportImageRoute.Gpu => "preview.png —— 由 GPU 渲染器产出（真实 GPU 路线）",
                    ThemeExportImageRoute.Fallback => "preview.png —— 兼容外观图（本次未产出 GPU 图，图上已标注）",
                    _ => "preview.png —— 经典渲染图"
                }
                : "preview.png —— 未产出（见 submission.json 的 diagnostics）";

            var sb = new StringBuilder();
            sb.AppendLine("LiteMonitor++ 主题投稿资料");
            sb.AppendLine("=========================");
            sb.AppendLine();
            sb.AppendLine("本目录由主题工作室的“准备投稿”功能生成，只包含**本地材料**：");
            sb.AppendLine("没有任何内容被上传，也没有替你完成提交。");
            sb.AppendLine();
            sb.AppendLine("目录内容");
            sb.AppendLine("--------");
            sb.AppendLine("- theme.json        最终主题载荷（保留未知扩展字段；字节与 submission.json 的 sha256 / byteCount 对应）");
            sb.AppendLine("- submission.json   素材摘要（名称 / 作者 / 版本 / 材质 / 能力 / 字节数 / SHA-256 / 导出时间 / 渲染路线）");
            sb.AppendLine("- " + previewText);
            sb.AppendLine("- README.txt        本文件");
            sb.AppendLine();
            sb.AppendLine("主题： " + session.ThemeName);
            sb.AppendLine("渲染路线（主题声明）： " + routeText);
            sb.AppendLine();
            sb.AppendLine("投稿入口（人工提交）");
            sb.AppendLine("--------------------");
            sb.AppendLine("1. 在 LiteMonitor++ 主题市场仓库的 themes/ 目录下准备主题文件：把 theme.json 放进去。");
            sb.AppendLine("2. 补齐市场索引条目（themes/ 附加段）：name / name_en / author / version / file / download_url / sha256 / route / material。");
            sb.AppendLine("3. 核对 submission.json 里的 sha256 与 byteCount 和 theme.json 的实际文件字节一致。");
            sb.AppendLine("4. 新建分支，提交这些文件，向仓库发起 Pull Request。");
            sb.AppendLine();
            sb.AppendLine("审核流程");
            sb.AppendLine("--------");
            sb.AppendLine("- Pull Request 会由市场 CI 自动校验（主题 JSON 结构、渲染配置规则、远程引用限制等）。");
            sb.AppendLine("- 维护者会对 Pull Request 做人工审核（视觉与兼容性）。");
            sb.AppendLine("- CI 与维护者都通过、并且变更被合并之后，主题才会进入市场列表。");
            sb.AppendLine();
            sb.AppendLine("状态说明（重要）");
            sb.AppendLine("----------------");
            sb.AppendLine("- 本地只做**结构自检**（JSON 可解析、rendering 配置规则、远程引用扫描），");
            sb.AppendLine("  这类自检不能代替市场 CI 的校验结论，也不能代替维护者的人工审核。");
            sb.AppendLine("- 上传与提交是你在上面的 Pull Request 步骤里的显式操作；");
            sb.AppendLine("  本目录本身不代表任何发布或上架状态。");
            sb.AppendLine("- 若 submission.json 的 marketValidation.reviewRequired 非空");
            sb.AppendLine("  （例如出现“远程引用需人工确认”），请先修正 theme.json 再提交。");
            sb.AppendLine();
            sb.AppendLine("效果图说明");
            sb.AppendLine("------------");
            sb.AppendLine("- 效果图使用固定模拟数据与固定背景生成，不抓取真实桌面。");
            sb.AppendLine("- 效果图上的标注与 submission.json 的 previewRoute / previewIsFallback 一致，");
            sb.AppendLine("  标明它是主效果、兼容外观还是 GPU 路线。");
            return sb.ToString();
        }

        internal static bool TryValidateSubmissionMetadata(ThemeSubmissionMetadata meta, out string error)
        {
            if (!IsValidSubmissionId(meta.Id))
            {
                error = "投稿 ID 非法：只允许 1–40 个小写字母、数字或连字符，且不能以连字符开头/结尾。";
                return false;
            }
            if (!IsValidMarketVersion(meta.MarketVersion))
            {
                error = "发布版本非法：必须是 major.minor.patch 三段非负整数。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(meta.License))
            {
                error = "许可证不能为空。";
                return false;
            }
            error = string.Empty;
            return true;
        }

        internal static ThemeSubmissionMetadata CreateDefaultMetadata(string themeName)
        {
            string name = (themeName ?? string.Empty).Trim();
            if (name.Length == 0) name = "Theme";
            return new ThemeSubmissionMetadata
            {
                Id = BuildDefaultSubmissionId(name),
                Name = name,
                NameEn = name,
                MarketVersion = "1.0.0",
                License = "MIT"
            };
        }

        private static string BuildDefaultSubmissionId(string themeName)
        {
            var sb = new StringBuilder();
            foreach (char ch in (themeName ?? string.Empty).Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
                else if (ch == '-' || ch == '_' || ch == ' ') sb.Append('-');
            }
            string id = sb.ToString().Trim('-');
            while (id.Contains("--", StringComparison.Ordinal)) id = id.Replace("--", "-", StringComparison.Ordinal);
            if (id.Length == 0) id = "theme";
            if (id.Length > 40) id = id[..40].TrimEnd('-');
            return id;
        }

        private static bool IsValidSubmissionId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 40) return false;
            if (id[0] == '-' || id[^1] == '-') return false;
            foreach (char ch in id)
                if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-')) return false;
            return true;
        }

        private static bool IsValidMarketVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;
            string[] parts = version.Split('.');
            if (parts.Length != 3) return false;
            foreach (string part in parts)
            {
                if (part.Length == 0) return false;
                if (part.Length > 1 && part[0] == '0') return false;
                if (!int.TryParse(part, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int value) || value < 0)
                    return false;
            }
            return true;
        }

        /// <summary>SHA-256（十六进制小写）。</summary>
        private static string Sha256Of(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        /// <summary>尽力删除文件（不掩盖原始异常）。</summary>
        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 清理失败不影响结果判定
            }
        }
    }
}
