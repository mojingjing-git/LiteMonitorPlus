using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// LiteMonitor++ UI Utilities (Refactored)
    /// Logic delegated to MetricUtils.
    /// This class now focuses on GDI+ Rendering helpers and Resource Management.
    /// </summary>
    public static class UIUtils
    {
        // ============================================================
        // 1. String Interning (Memory Optimization)
        // ============================================================
        private static readonly Dictionary<string, string> _stringPool = new(StringComparer.Ordinal);
        private static readonly object _poolLock = new object();

        public static string Intern(string str)
        {
            if (string.IsNullOrEmpty(str)) return string.Empty;
            lock (_poolLock)
            {
                if (_stringPool.TryGetValue(str, out var pooled)) return pooled;
                _stringPool[str] = str;
                return str;
            }
        }

        public static void ClearStringPool()
        {
            lock (_poolLock) _stringPool.Clear();
        }

        // ============================================================
        // 2. DPI Scaling
        // ============================================================
        public static float ScaleFactor { get; set; } = 1.0f;

        public static int S(int px) => (int)(px * ScaleFactor);
        public static float S(float px) => px * ScaleFactor;
        public static Size S(Size size) => new Size(S(size.Width), S(size.Height));
        public static Padding S(Padding p) => new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));

        // ============================================================
        // 3. GDI+ Resource Cache (Brushes & Fonts)
        // ============================================================
        private static readonly Dictionary<string, SolidBrush> _brushCache = new(16);
        private static readonly Dictionary<string, Pen> _penCache = new(16);
        private static readonly Dictionary<string, Font> _fontCache = new(16);
        private static readonly object _brushLock = new object();
        private const int MAX_BRUSH_CACHE = 32;

        public static Pen GetPen(Color color, float width = 1.0f)
        {
            string key = $"{color.ToArgb()}_{width:F1}";
            lock (_brushLock)
            {
                if (!_penCache.TryGetValue(key, out var pen))
                {
                    if (_penCache.Count >= MAX_BRUSH_CACHE)
                    {
                        foreach (var p in _penCache.Values) p.Dispose();
                        _penCache.Clear();
                    }
                    pen = new Pen(color, width);
                    _penCache[key] = pen;
                }
                return pen;
            }
        }

        public static SolidBrush GetBrush(string color)
        {
            if (string.IsNullOrEmpty(color)) return (SolidBrush)Brushes.Transparent;

            lock (_brushLock)
            {
                if (!_brushCache.TryGetValue(color, out var br))
                {
                    // Cache eviction policy
                    if (_brushCache.Count >= MAX_BRUSH_CACHE)
                    {
                        var keysToRemove = _brushCache.Keys.Take(_brushCache.Count / 2).ToList();
                        foreach (var k in keysToRemove)
                        {
                            if (_brushCache.TryGetValue(k, out var oldBrush))
                            {
                                oldBrush.Dispose();
                                _brushCache.Remove(k);
                            }
                        }
                    }

                    br = new SolidBrush(ThemeManager.ParseColor(color));
                    _brushCache[color] = br;
                }
                return br;
            }
        }

        /// <summary>
        /// 取字体缓存里的**共享**实例。
        ///
        /// ★ 同一个 (familyName, size, bold) 永远返回同一个 <see cref="Font"/> 对象，
        ///   所以调用方**不得 Dispose** 它。释放后缓存里会留下一个已释放对象，
        ///   下次同 key 取值把坏对象原样取回；而 <c>TextRenderer.DrawText</c> 用已释放字体
        ///   并不抛异常、只是静默降级 —— 症状表现成「字体莫名不生效」，没有任何异常可查。
        ///   （<c>TaskbarRenderer</c> 曾因此让「换肤 / 换语言后任务栏字体失效」长期无从排查。）
        ///   需要独占实例时，请自己 <c>new Font(...)</c> 并自行释放，谁建谁释放。
        ///
        /// ★ 生命周期只由 <see cref="ClearBrushCache"/> 统一管理（它会 Dispose 并清空字体缓存）。
        ///   这也意味着返回值的**有效期只到下一次 ClearBrushCache 为止**：
        ///   若要存进长生命周期字段，必须在 ClearBrushCache 之后重新取用，否则该字段会悬空。
        ///   （<c>UIController.ApplyTheme</c> 与 <c>MainFormBizHelper.RebuildMenus</c> 都会触发它。）
        /// </summary>
        public static Font GetFont(string familyName, float size, bool bold)
        {
            string key = $"{familyName}_{size}_{bold}";
            lock (_brushLock)
            {
                if (!_fontCache.TryGetValue(key, out var font))
                {
                    try 
                    {
                        var style = bold ? FontStyle.Bold : FontStyle.Regular;
                        font = new Font(familyName, size, style);
                    }
                    catch
                    {
                        font = new Font(SystemFonts.DefaultFont.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular);
                    }
                    _fontCache[key] = font;
                }
                return font;
            }
        }

        /// <summary>
        /// 释放并清空画刷 / 画笔 / 字体缓存。
        ///
        /// ★ 这里是这三类资源的**唯一**释放点。调用之后，此前经 <see cref="GetFont"/> 取到的
        ///   字体实例全部变成已释放对象 —— 把它们存进字段的调用方必须在此之后重新取用，
        ///   否则该字段就是悬空引用，且绘制时会静默降级而不是报错。
        /// ★ 已知调用点：<c>UIController.ApplyTheme</c>（换肤，后面会重新 ReloadStyle）、
        ///   <c>MainFormBizHelper.RebuildMenus</c>（换语言 / 改监控布局 / 主题编辑器确认）、
        ///   <c>ThemeManager.ClearCaches</c>（当前无调用方）。
        /// </summary>
        public static void ClearBrushCache()
        {
            lock (_brushLock)
            {
                foreach (var b in _brushCache.Values) b.Dispose();
                _brushCache.Clear();

                foreach (var p in _penCache.Values) p.Dispose();
                _penCache.Clear();
                
                foreach (var f in _fontCache.Values) f.Dispose();
                _fontCache.Clear();
            }
        }

        // ============================================================
        // 4. Theme Helpers
        // ============================================================

        public static Color GetStateColor(int state, Theme t, bool isValueText = true)
        {
            if (state == MetricUtils.STATE_CRIT) return ThemeManager.ParseColor(isValueText ? t.Color.ValueCrit : t.Color.BarHigh);
            if (state == MetricUtils.STATE_WARN) return ThemeManager.ParseColor(isValueText ? t.Color.ValueWarn : t.Color.BarMid);
            return ThemeManager.ParseColor(t.Color.ValueSafe);
        }

        // ============================================================
        // 5. Drawing Helpers
        // ============================================================

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return p;

            if (radius <= 0)
            {
                p.AddRectangle(r);
                return p;
            }

            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRoundRect(Graphics g, Rectangle r, int radius, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using var brush = new SolidBrush(c);
            using var path = RoundRect(r, radius);
            g.FillPath(brush, path);
        }

        public static void DrawBar(Graphics g, MetricItem item, Theme t)
        {
            if (item.BarRect.Width <= 0 || item.BarRect.Height <= 0) return;

            // Background
            using (var bgPath = RoundRect(item.BarRect, item.BarRect.Height / 2))
            {
                g.FillPath(GetBrush(t.Color.BarBackground), bgPath);
            }

            // 2. Value Bar
            // [Refactor] CachedPercent already includes visual correction (min 5%, max 100%) via MetricUtils.GetProgressValue
            double percent = item.CachedPercent; 
            
            int w = (int)(item.BarRect.Width * percent);
            if (w < 1 && percent > 0) w = 1; // 绝对防御：只要有百分比，至少画1像素

            // Color
            Color barColor = GetStateColor(item.CachedColorState, t, false);

            if (w > 0)
            {
                Rectangle bar = new Rectangle(item.BarRect.X, item.BarRect.Y, w, item.BarRect.Height);
                if (bar.Width > 0 && bar.Height > 0)
                {
                    using (var fgPath = RoundRect(bar, bar.Height / 2))
                    using (var brush = new SolidBrush(barColor))
                    {
                        g.FillPath(brush, fgPath);
                    }
                }
            }
        }
    }
}
