using System;
using System.Drawing;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 背景模式解析与降级决策（纯策略，不触碰 Graphics）
    // ================================================================

    /// <summary>
    /// 一次渲染尝试中"底图是否可用"的描述。由渲染器采集，交给解析器做决策。
    /// </summary>
    public readonly struct BackdropFrameOffer
    {
        private BackdropFrameOffer(bool hasFrame, bool isPreview, BackdropDegradeReason unavailableCode, string? unavailableReason)
        {
            HasFrame = hasFrame;
            IsPreview = isPreview;
            UnavailableCode = unavailableCode;
            UnavailableReason = unavailableReason;
        }

        /// <summary>是否拿到了可用底图。</summary>
        public bool HasFrame { get; }

        /// <summary>底图是否来自预览固定源（预览路径不启动捕获）。</summary>
        public bool IsPreview { get; }

        /// <summary>拿不到底图的原因码。</summary>
        public BackdropDegradeReason UnavailableCode { get; }

        /// <summary>拿不到底图的原因文本（可为空）。</summary>
        public string? UnavailableReason { get; }

        /// <summary>有可用底图。</summary>
        public static BackdropFrameOffer Available(bool isPreview)
        {
            return new BackdropFrameOffer(true, isPreview, BackdropDegradeReason.None, null);
        }

        /// <summary>没有可用底图，并带上原因。</summary>
        public static BackdropFrameOffer Missing(BackdropDegradeReason code, string? reason)
        {
            return new BackdropFrameOffer(false, false, code, reason);
        }
    }

    /// <summary>
    /// 背景模式解析器：把"主题请求的模式 + 底图是否可用"解析成
    /// "实际生效的模式 + 是否降级 + 降级原因"。
    ///
    /// ★ 合同要点：
    /// 1. 未知模式退回 solid（沿用旧背景路径），并且**不**算降级。
    /// 2. glass 拿不到底图时降级链固定为 gradient → solid，且每次降级都带原因文本。
    /// 3. 渐变两端颜色 alpha 均为 0 时认为渐变不可用，只能退到 solid。
    /// 4. 本类不做任何绘制，也不分配位图。
    /// </summary>
    public sealed class BackdropModeResolver
    {
        // 原因文本缓存：同一后端原因重复出现时不再拼接字符串（降级路径也不产生额外分配）
        private BackdropDegradeReason _memoCode = BackdropDegradeReason.None;
        private string? _memoBackendReason;
        private string? _memoFallback;
        private string? _memoText;

        /// <summary>
        /// 解析本次渲染的有效模式。
        /// </summary>
        /// <param name="options">外观快照（只读）。</param>
        /// <param name="offer">底图是否可用的描述。</param>
        public BackdropStatus Resolve(ThemeAppearanceOptions options, in BackdropFrameOffer offer)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));

            string requested = options.BackdropMode ?? ThemeAppearanceModeNames.Solid;

            switch (requested)
            {
                case ThemeAppearanceModeNames.Gradient:
                    return new BackdropStatus(
                        requested,
                        BackdropRenderMode.Gradient,
                        false,
                        BackdropDegradeReason.None,
                        null,
                        BackdropFrameSource.Gradient,
                        ComputeLayers(options, BackdropRenderMode.Gradient),
                        options.BlurPx);

                case ThemeAppearanceModeNames.Glass:
                    if (offer.HasFrame)
                    {
                        return new BackdropStatus(
                            requested,
                            BackdropRenderMode.Glass,
                            false,
                            BackdropDegradeReason.None,
                            null,
                            offer.IsPreview ? BackdropFrameSource.PreviewFrame : BackdropFrameSource.LiveFrame,
                            ComputeLayers(options, BackdropRenderMode.Glass),
                            options.BlurPx);
                    }

                    // 降级 1：glass → gradient
                    if (IsGradientUsable(options))
                    {
                        return new BackdropStatus(
                            requested,
                            BackdropRenderMode.Gradient,
                            true,
                            offer.UnavailableCode,
                            BuildReason(offer.UnavailableCode, offer.UnavailableReason, ThemeAppearanceModeNames.Gradient),
                            BackdropFrameSource.Gradient,
                            ComputeLayers(options, BackdropRenderMode.Gradient),
                            options.BlurPx);
                    }

                    // 降级 2：glass → solid（沿用旧背景路径）
                    return new BackdropStatus(
                        requested,
                        BackdropRenderMode.Solid,
                        true,
                        BackdropDegradeReason.GradientUnavailable,
                        BuildReason(BackdropDegradeReason.GradientUnavailable, offer.UnavailableReason, ThemeAppearanceModeNames.Solid),
                        BackdropFrameSource.Legacy,
                        BackdropLayer.None,
                        options.BlurPx);

                default:
                    // solid 与未知模式：沿用旧背景路径，本组件零副作用、零分配
                    return new BackdropStatus(
                        requested,
                        BackdropRenderMode.Solid,
                        false,
                        BackdropDegradeReason.None,
                        null,
                        BackdropFrameSource.Legacy,
                        BackdropLayer.None,
                        options.BlurPx);
            }
        }

        /// <summary>
        /// 计算某个有效模式实际会绘制的层。渲染器与状态使用同一份判定，避免"状态说有、实际没画"。
        /// </summary>
        public static BackdropLayer ComputeLayers(ThemeAppearanceOptions options, BackdropRenderMode effectiveMode)
        {
            if (effectiveMode == BackdropRenderMode.Solid) return BackdropLayer.None;

            BackdropLayer layers = BackdropLayer.Base;
            if (ComputeTintAlpha(options) > 0) layers |= BackdropLayer.Tint;
            if (options.BackdropNoise > 0) layers |= BackdropLayer.Noise;
            if (options.BackdropEdgeGlow > 0) layers |= BackdropLayer.EdgeGlow;
            return layers;
        }

        /// <summary>
        /// 背景染色层的 alpha：<c>Tint 自身 alpha × Opacity</c>，结果夹紧到 0–255。
        /// Opacity 为 0 时不产生染色层（连画刷都不创建）。
        /// </summary>
        public static int ComputeTintAlpha(ThemeAppearanceOptions options)
        {
            double opacity = options.BackdropOpacity;
            if (double.IsNaN(opacity) || opacity <= 0) return 0;
            if (opacity > 1) opacity = 1;

            int alpha = (int)Math.Round(options.TintColor.A * opacity, MidpointRounding.AwayFromZero);
            if (alpha < 0) return 0;
            if (alpha > 255) return 255;
            return alpha;
        }

        /// <summary>
        /// 渐变是否可用：两端颜色至少有一端不是全透明。
        /// 两端都透明时渐变画出来等于没有，只能退到 solid。
        /// </summary>
        public static bool IsGradientUsable(ThemeAppearanceOptions options)
        {
            return options.GradientStartColor.A > 0 || options.GradientEndColor.A > 0;
        }

        // 原因文本：同一 (原因码, 后端原因, 回落模式) 组合只拼接一次
        private string BuildReason(BackdropDegradeReason code, string? backendReason, string fallbackMode)
        {
            if (_memoText != null
                && _memoCode == code
                && string.Equals(_memoFallback, fallbackMode, StringComparison.Ordinal)
                && string.Equals(_memoBackendReason, backendReason, StringComparison.Ordinal))
            {
                return _memoText;
            }

            string text;
            switch (code)
            {
                case BackdropDegradeReason.BackendMissing:
                    text = "未注册实时背景后端（IBackdropFrameSource 为空）；glass 已降级为 " + fallbackMode + "。";
                    break;
                case BackdropDegradeReason.BackendNotReady:
                    text = "实时背景后端未就绪：" + (backendReason ?? "未提供原因") + "；glass 已降级为 " + fallbackMode + "。";
                    break;
                case BackdropDegradeReason.BackendFrameInvalid:
                    text = "实时背景后端未提供有效帧：" + (backendReason ?? "未提供原因") + "；glass 已降级为 " + fallbackMode + "。";
                    break;
                case BackdropDegradeReason.GradientUnavailable:
                    text = "实时背景后端不可用（" + (backendReason ?? "未提供原因")
                         + "）且渐变两端颜色均不可用；glass 已降级为 solid（沿用旧背景路径）。";
                    break;
                default:
                    text = "背景模式已回落为 " + fallbackMode + "。";
                    break;
            }

            _memoCode = code;
            _memoBackendReason = backendReason;
            _memoFallback = fallbackMode;
            _memoText = text;
            return text;
        }
    }
}
