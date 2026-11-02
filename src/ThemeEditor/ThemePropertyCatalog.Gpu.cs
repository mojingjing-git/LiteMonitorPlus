using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>编辑目标（重做计划 §4.1）：只切换文档视图，不修改 rendering.backend。</summary>
    public enum ThemeEditTarget
    {
        /// <summary>经典外观（经典主题唯一目标）。</summary>
        Classic = 0,

        /// <summary>GPU 主效果。</summary>
        GpuMain = 1,

        /// <summary>GPU 兼容外观（回退样式六槽）。</summary>
        GpuFallback = 2
    }

    /// <summary>
    /// GPU 属性描述符（TE03 GPU 视图，输入以 TE00 能力矩阵 §2.1/§2.2/§2.4 为准）。
    ///
    /// 可见性规则（合同硬要求）：
    /// - liquid_glass 只显示其运行时消费的参数；cyber_punk 只显示它消费的参数；
    /// - 校验允许但运行时不消费的交叉项不出现（避免"能调但无效果"）；
    /// - 共享字段（字体/布局/阈值）在 GPU 目标下带 Shared 作用域显示；
    /// - fallbackStyle 六槽独立编辑，accent 显示为"标题强调色"。
    /// </summary>
    public static partial class ThemePropertyCatalog
    {
        /// <summary>按草稿与编辑目标取属性集（UI/预览共用，不各自判断能力）。</summary>
        public static IReadOnlyList<ThemePropertyDescriptor> GetProperties(Theme theme, ThemeEditTarget target)
        {
            ArgumentNullException.ThrowIfNull(theme);

            bool isGpu = theme.Rendering?.IsGpuRequested == true;
            if (!isGpu)
            {
                return _classic;
            }

            var list = new List<ThemePropertyDescriptor>();
            string material = NormalizeMaterial(theme.Rendering?.Material);
            bool namedPreset = IsNamedVisualPreset(theme);
            bool toolbar = !namedPreset && IsFloatingToolbar(theme);

            switch (target)
            {
                case ThemeEditTarget.GpuMain:
                    list.AddRange(material == ThemeRenderingConfig.CyberPunkMaterial
                        ? BuildCyberPunkMain()
                        : BuildLiquidGlassMain(toolbar, namedPreset));
                    list.Add(VisualPreset(material));
                    list.AddRange(BuildMotion(material));
                    break;
                case ThemeEditTarget.GpuFallback:
                    list.AddRange(BuildFallbackStyle());
                    break;
                default:
                    return _classic;
            }

            // 共享设置：布局/阈值始终与主效果、兼容外观共享；字体仅在
            // floatingToolbar 样式下被 GPU 前景消费（TE00 §2.3），standard/cyber_punk
            // 使用固定前景，不得显示主题字体控件。
            IEnumerable<ThemePropertyDescriptor> shared =
                _classic.Where(p => p.Scope.HasFlag(ThemePropertyScope.Shared));
            if (target == ThemeEditTarget.GpuMain && !toolbar && !namedPreset)
            {
                shared = shared.Where(p => !p.JsonPath.StartsWith("font.", StringComparison.Ordinal));
            }

            list.AddRange(shared);
            if (target == ThemeEditTarget.GpuMain && namedPreset)
            {
                list.AddRange(_classic.Where(
                    p => p.JsonPath.StartsWith("color.", StringComparison.Ordinal)));
            }
            return list;
        }

        /// <summary>默认编辑目标：经典主题 → Classic；GPU 主题 → GpuMain。</summary>
        public static ThemeEditTarget DefaultTarget(Theme theme)
        {
            bool isGpu = theme.Rendering?.IsGpuRequested == true;
            return isGpu ? ThemeEditTarget.GpuMain : ThemeEditTarget.Classic;
        }

        private static string NormalizeMaterial(string? material) =>
            string.Equals(material?.Trim(), ThemeRenderingConfig.CyberPunkMaterial, StringComparison.OrdinalIgnoreCase)
                ? ThemeRenderingConfig.CyberPunkMaterial
                : ThemeRenderingConfig.LiquidGlassMaterial;

        private static bool IsFloatingToolbar(Theme theme)
        {
            if (theme.Rendering?.Parameters is not { } parameters ||
                !parameters.TryGetValue("glassStyle", out JsonElement value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return string.Equals(
                value.GetString()?.Trim(),
                ThemeRenderingConfig.FloatingToolbarGlassStyle,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNamedVisualPreset(Theme theme)
        {
            string? preset = theme.Rendering is null
                ? null
                : ReadParameterString(theme, "visualPreset")?.Trim();
            return preset is not null &&
                (string.Equals(
                    preset,
                    ThemeRenderingConfig.NebulaAuroraVisualPreset,
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                    preset,
                    ThemeRenderingConfig.NixieBrassVisualPreset,
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                    preset,
                    ThemeRenderingConfig.CrtPhosphorVisualPreset,
                    StringComparison.OrdinalIgnoreCase));
        }

        // ============================================================
        // liquid_glass（TE00 §2.1 消费清单）
        // ============================================================

        private static IEnumerable<ThemePropertyDescriptor> BuildLiquidGlassMain(
            bool toolbar,
            bool namedPreset)
        {
            yield return Param("圆角半径", "cornerRadius", 0, 64, toolbar ? 30 : 26);
            yield return Param("玻璃厚度", "thickness", 0, 8, toolbar ? 8 : 1);
            yield return Param("折射强度", "refraction", 0, 8, toolbar ? 8 : 1.6);
            yield return Param("模糊半径", "blurRadius", 0, 32, 8);
            yield return Param("中心透明比", "clearCenterRatio", 0, 1, 0);
            yield return Param("表面不透明度", "surfaceOpacity", 0, 1, 0.055);
            yield return Param("高光强度", "highlight", 0, 1, toolbar ? 0.72 : 1);
            yield return Param("逆光高光", "counterHighlight", 0, 1, 1);
            yield return Param("阴影强度", "shadow", 0, 1, toolbar ? 0.115 : 0.34);
            yield return Param("雾化强度", "fogStrength", 0, 1, toolbar ? 0.16 : 0.42);
            yield return Param("染色强度", "tintStrength", 0, 1, toolbar ? 0.038 : 0.055);
            yield return Param("阴影半径", "shadowRadius", 0, 64, toolbar ? 30 : 0);
            if (!namedPreset)
            {
                yield return GlassStyle();
            }
        }

        // ============================================================
        // cyber_punk（TE00 §2.1 消费清单）
        // ============================================================

        private static IEnumerable<ThemePropertyDescriptor> BuildCyberPunkMain()
        {
            yield return Param("主切角", "primaryCut", 0, 64, 14);
            yield return Param("次切角", "secondaryCut", 0, 32, 9);
            yield return Param("描边宽度", "outlineWidth", 0, 8, 1);
            yield return Param("辉光强度", "glow", 0, 100, 12);
            yield return Param("环境光比", "ambientRatio", 0, 1, 0.12);
            yield return Param("面板不透明度", "panelOpacity", 0, 1, 0.965);
            yield return Param("阴影强度", "shadow", 0, 1, 0.34);
        }

        // ============================================================
        // 动效（两种材质共享参数名，缺省值按材质不同）
        // ============================================================

        private static IEnumerable<ThemePropertyDescriptor> BuildMotion(string material)
        {
            bool cyber = material == ThemeRenderingConfig.CyberPunkMaterial;
            yield return MotionBool("启用动效", "motionEnabled", true);
            yield return MotionInt("目标帧率", "targetFps", 1, 240, 30);
            yield return MotionInt("状态过渡(ms)", "stateTransitionMs", 0, 5000, cyber ? 140 : 180);
            yield return MotionInt("扫描周期(ms)", "scannerPeriodMs", 0, 60000, cyber ? 2800 : 0);
        }

        // ============================================================
        // 兼容外观六槽（fallbackStyle）
        // ============================================================

        private static IEnumerable<ThemePropertyDescriptor> BuildFallbackStyle()
        {
            yield return Fallback("页面底色", "background", s => s.Background,
                (s, v) => s.Background = v, LegacyFallbackStyleResolver.DefaultBackground);
            yield return Fallback("分组底色", "panelBackground", s => s.PanelBackground,
                (s, v) => s.PanelBackground = v, LegacyFallbackStyleResolver.DefaultPanelBackground);
            yield return Fallback("边框色", "border", s => s.Border,
                (s, v) => s.Border = v, LegacyFallbackStyleResolver.DefaultBorder);
            yield return Fallback("主前景色", "text", s => s.Text,
                (s, v) => s.Text = v, LegacyFallbackStyleResolver.DefaultText);
            yield return Fallback("次要前景色", "mutedText", s => s.MutedText,
                (s, v) => s.MutedText = v, LegacyFallbackStyleResolver.DefaultMutedText);
            // accent 在生产里派生为 Color.TextTitle（裁决 D-H）：按"标题强调色"呈现，
            // 不冒充进度条色、不占用告警色（TE00 §2.5）。
            yield return Fallback("标题强调色", "accent", s => s.Accent,
                (s, v) => s.Accent = v, LegacyFallbackStyleResolver.DefaultAccent);
        }

        // ============================================================
        // 描述符构造
        // ============================================================

        private static ThemePropertyDescriptor Param(
            string display, string key, double min, double max, double materialDefault)
        {
            return new ThemePropertyDescriptor
            {
                DisplayName = display,
                JsonPath = "rendering.parameters." + key,
                Group = "GPU 材质",
                Scope = ThemePropertyScope.GpuMain,
                EditorKind = ThemePropertyEditorKind.Number,
                Invalidation = ThemePropertyInvalidation.Rendering,
                Minimum = min,
                Maximum = max,
                Getter = theme => ReadParameter(theme, key) ?? materialDefault,
                Setter = (theme, value) => WriteParameter(theme, key, JsonSerializer.SerializeToElement(ToDouble(value))),
                DefaultGetter = () => materialDefault,
                ToJson = v => JsonValue.Create(ToDouble(v))
            };
        }

        private static ThemePropertyDescriptor GlassStyle()
        {
            string[] choices =
                [ThemeRenderingConfig.StandardGlassStyle, ThemeRenderingConfig.FloatingToolbarGlassStyle];
            return new ThemePropertyDescriptor
            {
                DisplayName = "玻璃样式",
                JsonPath = "rendering.parameters.glassStyle",
                Group = "GPU 材质",
                Scope = ThemePropertyScope.GpuMain,
                EditorKind = ThemePropertyEditorKind.Choice,
                Invalidation = ThemePropertyInvalidation.Rendering,
                Choices = choices,
                Getter = theme => ReadParameterString(theme, "glassStyle") ?? ThemeRenderingConfig.StandardGlassStyle,
                Setter = (theme, value) => WriteParameter(
                    theme,
                    "glassStyle",
                    JsonSerializer.SerializeToElement(ChoiceText(value, choices))),
                DefaultGetter = () => ThemeRenderingConfig.StandardGlassStyle,
                ToJson = v => JsonValue.Create(ChoiceText(v, choices))
            };
        }

        private static ThemePropertyDescriptor VisualPreset(string material)
        {
            string[] choices = material == ThemeRenderingConfig.LiquidGlassMaterial
                ? [ThemeRenderingConfig.DefaultVisualPreset,
                   ThemeRenderingConfig.NebulaAuroraVisualPreset]
                : [ThemeRenderingConfig.DefaultVisualPreset,
                   ThemeRenderingConfig.NixieBrassVisualPreset,
                   ThemeRenderingConfig.CrtPhosphorVisualPreset];
            return new ThemePropertyDescriptor
            {
                DisplayName = "视觉效果预设",
                JsonPath = "rendering.parameters.visualPreset",
                Group = "GPU 效果",
                Scope = ThemePropertyScope.GpuMain,
                EditorKind = ThemePropertyEditorKind.Choice,
                Invalidation = ThemePropertyInvalidation.Rendering,
                Choices = choices,
                Getter = theme => ReadParameterString(theme, "visualPreset")?.Trim() ??
                    ThemeRenderingConfig.DefaultVisualPreset,
                Setter = (theme, value) =>
                {
                    theme.Rendering ??= new ThemeRenderingConfig();
                    string preset = ChoiceText(value, choices);
                    if (string.Equals(
                        preset,
                        ThemeRenderingConfig.DefaultVisualPreset,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        theme.Rendering.Parameters.Remove("visualPreset");
                        return;
                    }

                    theme.Rendering.Parameters["visualPreset"] =
                        JsonSerializer.SerializeToElement(preset);
                },
                SideEffects = (session, value) =>
                {
                    string preset = ChoiceText(value, choices);
                    var edits = new List<ThemeFieldEdit>();
                    if (string.Equals(
                        preset,
                        ThemeRenderingConfig.DefaultVisualPreset,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        // 切回 default：两路恢复为源文件的值（不是覆盖层当前值——否则
                        // 来回切换后永远回不到源状态）；源无键 → 覆盖层删除标记，
                        // 载荷对该键是无操作（缺省不物化）。
                        JsonNode? liveBefore = session.GetSourceValue("rendering.parameters.liveBackground");
                        edits.Add(new ThemeFieldEdit(
                            "rendering.parameters.liveBackground", liveBefore,
                            theme => RestoreParameter(theme, "liveBackground", liveBefore)));
                        JsonNode? glassBefore = session.GetSourceValue("rendering.parameters.glassStyle");
                        edits.Add(new ThemeFieldEdit(
                            "rendering.parameters.glassStyle", glassBefore,
                            theme => RestoreParameter(theme, "glassStyle", glassBefore)));
                        return edits;
                    }

                    // 命名预设关闭实时背景并必须物化 false：nebula 校验强制该项，
                    // 且无键时运行时 liquid_glass 缺省开启实时背景——缺省不落盘语义在此不适用。
                    edits.Add(new ThemeFieldEdit(
                        "rendering.parameters.liveBackground", JsonValue.Create(false),
                        theme =>
                        {
                            theme.Rendering ??= new ThemeRenderingConfig();
                            theme.Rendering.Parameters["liveBackground"] =
                                JsonSerializer.SerializeToElement(false);
                        }));
                    if (string.Equals(
                        preset,
                        ThemeRenderingConfig.NebulaAuroraVisualPreset,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        edits.Add(new ThemeFieldEdit(
                            "rendering.parameters.glassStyle", null,
                            theme => theme.Rendering?.Parameters.Remove("glassStyle")));
                    }

                    return edits;
                },
                DefaultGetter = () => ThemeRenderingConfig.DefaultVisualPreset,
                ToJson = value =>
                {
                    string preset = ChoiceText(value, choices);
                    return string.Equals(
                        preset,
                        ThemeRenderingConfig.DefaultVisualPreset,
                        StringComparison.OrdinalIgnoreCase)
                        ? null
                        : JsonValue.Create(preset);
                }
            };
        }

        private static string ChoiceText(object? value, IReadOnlyList<string> choices)
        {
            if (value is int index && index >= 0 && index < choices.Count)
            {
                return choices[index];
            }

            if (value is string text)
            {
                // 命中后返回规范候选项，避免源 JSON 的大小写/空白漂移被原样写回。
                for (int i = 0; i < choices.Count; i++)
                {
                    if (string.Equals(choices[i], text.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return choices[i];
                    }
                }
            }

            return choices[0];
        }

        /// <summary>按快照恢复 rendering.parameters 下的一个参数（null = 恢复为无键）。</summary>
        private static void RestoreParameter(Theme theme, string key, JsonNode? snapshot)
        {
            if (theme.Rendering is null) return;

            if (snapshot is null)
            {
                theme.Rendering.Parameters.Remove(key);
                return;
            }

            theme.Rendering.Parameters[key] =
                JsonSerializer.Deserialize<JsonElement>(snapshot.ToJsonString());
        }

        private static ThemePropertyDescriptor MotionBool(string display, string key, bool materialDefault)
        {
            return new ThemePropertyDescriptor
            {
                DisplayName = display,
                JsonPath = "rendering.motion." + key,
                Group = "动效",
                Scope = ThemePropertyScope.GpuMain,
                EditorKind = ThemePropertyEditorKind.Boolean,
                Invalidation = ThemePropertyInvalidation.Rendering,
                Getter = theme => ReadMotionBool(theme, key) ?? materialDefault,
                Setter = (theme, value) => WriteMotion(theme, key, JsonSerializer.SerializeToElement(value is true)),
                DefaultGetter = () => materialDefault,
                ToJson = v => JsonValue.Create(v is true)
            };
        }

        private static ThemePropertyDescriptor MotionInt(
            string display, string key, int min, int max, int materialDefault)
        {
            return new ThemePropertyDescriptor
            {
                DisplayName = display,
                JsonPath = "rendering.motion." + key,
                Group = "动效",
                Scope = ThemePropertyScope.GpuMain,
                EditorKind = ThemePropertyEditorKind.Number,
                Invalidation = ThemePropertyInvalidation.Rendering,
                Minimum = min,
                Maximum = max,
                Getter = theme => ReadMotionInt(theme, key) ?? materialDefault,
                Setter = (theme, value) => WriteMotion(
                    theme, key, JsonSerializer.SerializeToElement((int)Math.Round(ToDouble(value)))),
                DefaultGetter = () => materialDefault,
                ToJson = v => JsonValue.Create((int)Math.Round(ToDouble(v)))
            };
        }

        private static ThemePropertyDescriptor Fallback(
            string display,
            string key,
            Func<ThemeFallbackStyle, string?> read,
            Action<ThemeFallbackStyle, string?> write,
            string modelDefault)
        {
            return new ThemePropertyDescriptor
            {
                DisplayName = display,
                JsonPath = "rendering.fallbackStyle." + key,
                Group = "兼容外观配色",
                Scope = ThemePropertyScope.GpuFallback,
                EditorKind = ThemePropertyEditorKind.Color,
                Invalidation = ThemePropertyInvalidation.Rendering,
                Getter = theme => read(EnsureFallbackStyle(theme)) ?? modelDefault,
                Setter = (theme, value) => write(EnsureFallbackStyle(theme), value as string),
                DefaultGetter = () => modelDefault,
                // null = 删除显式覆盖，回退到中性默认（LegacyFallbackStyleResolver）
                ToJson = v => v is string s ? JsonValue.Create(s) : null
            };
        }

        // ============================================================
        // 读写实现（直接落 ThemeRenderingConfig，未触碰其他字段）
        // ============================================================

        private static double? ReadParameter(Theme theme, string key)
        {
            if (theme.Rendering?.Parameters is not { } parameters ||
                !parameters.TryGetValue(key, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out double number) ||
                !double.IsFinite(number))
            {
                return null;
            }

            return number;
        }

        private static string? ReadParameterString(Theme theme, string key)
        {
            if (theme.Rendering?.Parameters is not { } parameters ||
                !parameters.TryGetValue(key, out JsonElement value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }

        private static bool? ReadMotionBool(Theme theme, string key)
        {
            if (theme.Rendering?.Motion is not { } motion ||
                !motion.TryGetValue(key, out JsonElement value) ||
                value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                return null;
            }

            return value.GetBoolean();
        }

        private static double? ReadMotionInt(Theme theme, string key)
        {
            if (theme.Rendering?.Motion is not { } motion ||
                !motion.TryGetValue(key, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out int number))
            {
                return null;
            }

            return number;
        }

        private static void WriteParameter(Theme theme, string key, JsonElement value)
        {
            theme.Rendering ??= new ThemeRenderingConfig();
            theme.Rendering.Parameters[key] = value;
        }

        private static void WriteMotion(Theme theme, string key, JsonElement value)
        {
            theme.Rendering ??= new ThemeRenderingConfig();
            theme.Rendering.Motion[key] = value;
        }

        private static ThemeFallbackStyle EnsureFallbackStyle(Theme theme)
        {
            theme.Rendering ??= new ThemeRenderingConfig();
            return theme.Rendering.FallbackStyle ??= new ThemeFallbackStyle();
        }

        private static double ToDouble(object? value) => value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string s when double.TryParse(
                s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed) => parsed,
            JsonNode node when node is JsonValue json && json.TryGetValue(out double d2) => d2,
            _ => 0
        };
    }
}
