using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json.Nodes;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Decor;

namespace LiteMonitor.AppearanceDecorProbe
{
    /// <summary>
    /// A05 装饰几何 / 资源缓存自证跑批。
    ///
    /// 全部场景都在离屏位图上完成：
    /// 不创建 HardwareMonitor、不 Open 硬件、不做屏幕捕获、不读写用户配置目录。
    /// </summary>
    internal sealed class DecorProbeRunner
    {
        private readonly List<ProbeCase> _cases = new();
        private readonly List<JsonObject> _timeline = new();

        private static readonly Color BarStart = Color.FromArgb(255, 0, 229, 255);
        private static readonly Color BarEnd = Color.FromArgb(255, 255, 45, 149);
        private static readonly Color Cyan = Color.FromArgb(255, 0, 229, 255);
        private static readonly Color Magenta = Color.FromArgb(255, 255, 45, 149);

        public List<ProbeCase> Cases => _cases;
        public List<JsonObject> Timeline => _timeline;

        public void RunAll()
        {
            RunDisabledFeatures();
            RunBoundsEdgeCases();
            RunNoSpillOutsideClient();
            RunProgress10000();
            RunCacheLimits();
            RunInvalidation();
            RunGradientOrientation();
        }

        // ============================================================
        // 用例 1：关闭新能力时无副作用
        // ============================================================

        private void RunDisabledFeatures()
        {
            ProbeCase c = NewCase("disabled_features_no_side_effect");

            Theme theme = BuildTheme(frameStyle: 0, frameWidth: 0, frameLength: 0, scanline: 0, glow: 0, grid: 0, barStyle: 0);
            Rectangle bounds = new Rectangle(0, 0, 240, 120);
            ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, bounds, 1);

            c.Require(!options.FrameEnabled, "FrameStyle=0 时 FrameEnabled 应为 false");
            c.Require(!options.GlowEnabled, "Glow=0 时 GlowEnabled 应为 false");
            c.Require(!options.ScanlineEnabled, "Scanline=0 时 ScanlineEnabled 应为 false");
            c.Require(!options.GridEnabled, "Grid=0 时 GridEnabled 应为 false");
            c.Require(!options.BarStyleEnabled, "BarStyle=0 时 BarStyleEnabled 应为 false");

            using DecorResourceHost host = new DecorResourceHost();
            using Bitmap canvas = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using Graphics g = Graphics.FromImage(canvas);
            g.Clear(Color.FromArgb(0, 0, 0, 0));

            // 模拟 A07 的接线方式：先判开关，再消费颜色
            bool drew = false;
            if (options.FrameEnabled)
            {
                drew = DecorPainter.FillInnerFrame(g, bounds, options.FrameStyle, options.FrameWidthPx,
                    options.FrameLengthPx, 0, options.BorderColor);
            }
            c.Require(!drew, "关闭外框时不应发生任何绘制");

            c.Require(host.Cache.Count == 0, $"关闭时缓存条目应为 0，实际 {host.Cache.Count}");
            c.Require(host.Cache.CreatedResourceCount == 0, $"关闭时不应创建资源，实际 {host.Cache.CreatedResourceCount}");
            c.Require(host.Cache.TotalBytes == 0, $"关闭时不应占用位图字节，实际 {host.Cache.TotalBytes}");

            bool anyOpaque = false;
            for (int y = 0; y < canvas.Height && !anyOpaque; y++)
            {
                for (int x = 0; x < canvas.Width; x++)
                {
                    if (canvas.GetPixel(x, y).A != 0) { anyOpaque = true; break; }
                }
            }
            c.Require(!anyOpaque, "关闭新能力时画布应保持完全透明（无绘制副作用）");

            c.Detail("frameEnabled", options.FrameEnabled);
            c.Detail("glowEnabled", options.GlowEnabled);
            c.Detail("barStyleEnabled", options.BarStyleEnabled);
            c.Detail("cacheEntries", host.Cache.Count);
            c.Detail("createdResources", host.Cache.CreatedResourceCount);
            c.Detail("totalBytes", host.Cache.TotalBytes);
        }

        // ============================================================
        // 用例 2：边界（1×1 / 零宽高 / 窄窗 / 极端半径 / 切角超半宽）
        // ============================================================

        private void RunBoundsEdgeCases()
        {
            ProbeCase c = NewCase("bounds_edge_cases");

            (string Label, int W, int H, int Style, int Width, int Length)[] cases =
            {
                ("1x1",              1,   1, 1, 8, 64),
                ("zero-size",        0,   0, 1, 8, 64),
                ("zero-width",       0, 120, 1, 8, 64),
                ("zero-height",    240,   0, 1, 8, 64),
                ("narrow-160",     160,  40, 1, 4, 16),
                ("extreme-radius", 240, 400, 2, 8, 64),
                ("chamfer-over-half", 60, 40, 1, 8, 64),
                ("tiny-2x2",         2,   2, 1, 8, 64),
                ("holographic-1x1",  1,   1, 3, 8, 64)
            };

            JsonArray arr = new();
            foreach ((string label, int w, int h, int style, int width, int length) in cases)
            {
                Rectangle bounds = new Rectangle(0, 0, w, h);
                Theme theme = BuildTheme(style, width, length, 0, 0, 0, 0);
                ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, bounds, 1);

                bool ok = true;
                string message = "";

                try
                {
                    using (GraphicsPath path = DecorGeometry.BuildFramePath(bounds, options.FrameStyle, options.FrameLengthPx, 0, 0))
                    {
                        if (path.PointCount > 0)
                        {
                            RectangleF pb = path.GetBounds();
                            if (!Contains(bounds, pb))
                            {
                                ok = false;
                                message = $"路径越界：{pb}";
                            }
                        }                    }

                    using (GraphicsPath local = DecorGeometry.BuildLocalFramePath(Math.Max(1, w), Math.Max(1, h), options.FrameStyle, options.FrameLengthPx, 0, 0))
                    {
                        if (local.PointCount > 0)
                        {
                            RectangleF pb = local.GetBounds();
                            RectangleF limit = new RectangleF(0, 0, Math.Max(1, w), Math.Max(1, h));
                            if (!Contains(limit, pb))
                            {
                                ok = false;
                                message = $"局部路径越界：{pb}";
                            }
                        }
                    }

                    using Bitmap canvas = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppPArgb);
                    using Graphics g = Graphics.FromImage(canvas);
                    DecorPainter.FillInnerFrame(g, bounds, options.FrameStyle, options.FrameWidthPx, options.FrameLengthPx, 0, options.BorderColor);
                }
                catch (Exception ex)
                {
                    ok = false;
                    message = $"抛出异常：{ex.GetType().Name}: {ex.Message}";
                }

                c.Require(ok, $"[{label}] {message}");

                arr.Add(new JsonObject
                {
                    ["label"] = label,
                    ["width"] = w,
                    ["height"] = h,
                    ["frameStyle"] = style,
                    ["frameWidthPx"] = options.FrameWidthPx,
                    ["frameLengthPx"] = options.FrameLengthPx,
                    ["hasValidBounds"] = options.HasValidBounds,
                    ["clampedCornerPx"] = DecorGeometry.ClampCornerLengthPx(options.FrameLengthPx, bounds),
                    ["clampedFrameWidthPx"] = DecorGeometry.ClampFrameWidthPx(options.FrameWidthPx, bounds),
                    ["ok"] = ok
                });
            }

            // 切角边长必须被夹紧到 min(宽,高)/2 以内（正好等于一半时再收 1 像素，保证留有直线段）
            int clamped = DecorGeometry.ClampCornerLengthPx(64, new Rectangle(0, 0, 60, 40));
            c.Require(clamped == 19, $"切角边长应被夹紧到 19，实际 {clamped}");

            c.Detail("cases", arr);
            c.Detail("clampCornerLengthPx(64, 60x40)", clamped);
        }

        // ============================================================
        // 用例 3：不得画到客户区外
        // ============================================================

        private void RunNoSpillOutsideClient()
        {
            ProbeCase c = NewCase("no_paint_outside_client");

            const int pad = 24;
            Rectangle client = new Rectangle(pad, pad, 240, 120);
            Color sentinel = Color.FromArgb(255, 7, 11, 13);

            JsonArray arr = new();
            foreach (int style in new[] { 1, 2, 3 })
            {
                int outside = 0;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                using (Bitmap canvas = new Bitmap(client.Width + pad * 2, client.Height + pad * 2, PixelFormat.Format32bppPArgb))
                {
                    using Graphics g = Graphics.FromImage(canvas);
                    g.Clear(sentinel);

                    Theme theme = BuildTheme(style, 8, 64, 0, 0, 0, 0);
                    ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, client, 1);

                    DecorPainter.FillInnerFrame(g, client, options.FrameStyle, options.FrameWidthPx,
                        options.FrameLengthPx, 12, options.BorderColor);
                    DecorPainter.FillInnerLine(g, client, options.FrameStyle, options.FrameLengthPx,
                        12, 1, 2, Magenta);

                    for (int y = 0; y < canvas.Height; y++)
                    {
                        for (int x = 0; x < canvas.Width; x++)
                        {
                            bool inside = x >= client.Left && x < client.Right && y >= client.Top && y < client.Bottom;
                            if (inside) continue;
                            if (canvas.GetPixel(x, y) != sentinel) outside++;
                            if (canvas.GetPixel(x, y) != sentinel)
                            {
                                if (x < minX) minX = x;
                                if (x > maxX) maxX = x;
                                if (y < minY) minY = y;
                                if (y > maxY) maxY = y;
                            }
                        }
                    }
                }

                c.Require(outside == 0,
                    $"样式 {style}：客户区外有 {outside} 个像素被改动，范围 x[{minX}..{maxX}] y[{minY}..{maxY}]");
                arr.Add(new JsonObject
                {
                    ["frameStyle"] = style,
                    ["outsidePixels"] = outside,
                    ["outsideRange"] = outside > 0 ? $"x[{minX}..{maxX}] y[{minY}..{maxY}]" : ""
                });
            }

            c.Detail("clientBounds", $"({client.X},{client.Y},{client.Width},{client.Height})");
            c.Detail("results", arr);
        }

        // ============================================================
        // 用例 4：10,000 次持续变化的进度值
        // ============================================================

        private void RunProgress10000()
        {
            ProbeCase c = NewCase("progress_10000_cache_and_memory_stable");

            // 硬要求是 10,000 次；这里再跑 2 个 10,000 次的区块，
            // 用来证明"原生内存进入有界平台"而不是"还在爬坡"。
            const int blockSize = 10000;
            const int blockCount = 3;
            const int iterations = blockSize * blockCount;

            Rectangle client = new Rectangle(0, 0, 240, 120);
            Rectangle slot = new Rectangle(12, 60, 216, 10);

            Theme theme = BuildTheme(frameStyle: 1, frameWidth: 3, frameLength: 12, scanline: 40, glow: 30, grid: 20, barStyle: 1);
            ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, client, 7);

            using DecorResourceHost host = new DecorResourceHost();
            using Bitmap canvas = new Bitmap(client.Width, client.Height, PixelFormat.Format32bppPArgb);
            using Graphics g = Graphics.FromImage(canvas);

            // 预热：第一次 Paint 把资源建出来
            PaintOnce(g, host, options, client, slot, 0.5);

            long managedBefore = GC.GetTotalMemory(true);
            Process proc = Process.GetCurrentProcess();
            proc.Refresh();
            long workingSetBefore = proc.WorkingSet64;

            double[] paintMs = new double[iterations];
            int[] entries = new int[iterations];
            long[] bytes = new long[iterations];
            long[] workingSets = new long[iterations];
            JsonArray samples = new();
            JsonArray blocks = new();

            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                // 持续变化的进度值：伪随机覆盖 0–1
                double progress = (i * 7919 % 10007) / 10007.0;

                if ((i + 1) % 100 == 0) proc.Refresh();

                long t0 = sw.ElapsedTicks;
                PaintOnce(g, host, options, client, slot, progress);
                paintMs[i] = (sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;

                entries[i] = host.Cache.Count;
                bytes[i] = host.Cache.TotalBytes;
                workingSets[i] = proc.WorkingSet64;

                bool checkpoint = i == 0 || i == 9 || i == 99 || i == 999
                    || (i + 1) % blockSize == 0;
                if (checkpoint)
                {
                    proc.Refresh();
                    workingSets[i] = proc.WorkingSet64;
                    samples.Add(new JsonObject
                    {
                        ["iteration"] = i + 1,
                        ["progress"] = Math.Round(progress, 6),
                        ["cacheEntries"] = entries[i],
                        ["cacheBytes"] = bytes[i],
                        ["createdResources"] = host.Cache.CreatedResourceCount,
                        ["hits"] = host.Cache.HitCount,
                        ["misses"] = host.Cache.MissCount,
                        ["managedBytes"] = GC.GetTotalMemory(false),
                        ["workingSet"] = workingSets[i]
                    });
                }
            }
            sw.Stop();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long managedAfter = GC.GetTotalMemory(true);
            proc.Refresh();
            long workingSetAfter = proc.WorkingSet64;

            int finalEntries = entries[iterations - 1];
            int stableEntries = entries[99];
            int maxEntries = 0;
            foreach (int e in entries) if (e > maxEntries) maxEntries = e;

            c.Require(finalEntries == stableEntries,
                $"缓存条目数在 100 次之后仍在增长：100 次时 {stableEntries}，{iterations} 次时 {finalEntries}");
            c.Require(finalEntries == entries[0],
                $"缓存条目数在第一次绘制后仍在增长：1 次时 {entries[0]}，{iterations} 次时 {finalEntries}");
            c.Require(maxEntries <= DecorResourceCache.DefaultMaxEntries,
                $"缓存条目数 {maxEntries} 超过上限 {DecorResourceCache.DefaultMaxEntries}");
            c.Require(host.Cache.CreatedResourceCount <= 8,
                $"{iterations} 次绘制创建了 {host.Cache.CreatedResourceCount} 个资源（应保持为常数）");
            c.Require(host.Cache.TotalBytes <= DecorResourceCache.DefaultMaxBitmapBytes,
                $"缓存位图字节 {host.Cache.TotalBytes} 超过上限 {DecorResourceCache.DefaultMaxBitmapBytes}");

            long managedDelta = managedAfter - managedBefore;
            c.Require(managedDelta < 4L * 1024 * 1024,
                $"托管内存增长 {managedDelta} 字节（超过 4 MiB 判据）");

            // 逐区块对比：工作集增量必须收敛（后一块不得高于前一块），
            // 否则说明原生内存在持续爬升而不是进入平台期。
            long previous = workingSets[999];   // 1000 次：JIT / GDI+ 一次性开销已完成
            long previousManaged = managedBefore;
            long previousBlockDelta = long.MaxValue;
            for (int b = 1; b <= blockCount; b++)
            {
                int end = b * blockSize - 1;
                long wsDelta = workingSets[end] - previous;
                long managedAtBlock = GC.GetTotalMemory(true);
                long managedBlockDelta = managedAtBlock - previousManaged;

                blocks.Add(new JsonObject
                {
                    ["block"] = b,
                    ["upToIteration"] = end + 1,
                    ["cacheEntries"] = entries[end],
                    ["cacheBytes"] = bytes[end],
                    ["createdResources"] = host.Cache.CreatedResourceCount,
                    ["workingSet"] = workingSets[end],
                    ["workingSetDeltaInBlock"] = wsDelta,
                    ["managedDeltaInBlock"] = managedBlockDelta
                });

                if (b >= 2)
                {
                    c.Require(wsDelta <= previousBlockDelta,
                        $"第 {b} 个区块工作集增长 {wsDelta} 字节，高于上一区块的 {previousBlockDelta} 字节（未收敛）");
                }
                previousBlockDelta = wsDelta;
                previous = workingSets[end];
                previousManaged = managedAtBlock;
            }

            c.Require(previousBlockDelta < 4L * 1024 * 1024,
                $"最后一个区块工作集增长 {previousBlockDelta} 字节（超过 4 MiB 判据）");

            double avg = 0;
            foreach (double v in paintMs) avg += v;
            avg /= iterations;
            double max = 0;
            foreach (double v in paintMs) if (v > max) max = v;
            Array.Sort(paintMs);
            double p95 = paintMs[(int)(iterations * 0.95)];
            double avgFirstBlock = 0;
            for (int i = 0; i < blockSize; i++) avgFirstBlock += paintMs[i];
            avgFirstBlock /= blockSize;

            c.Detail("iterations", iterations);
            c.Detail("blockSize", blockSize);
            c.Detail("blockCount", blockCount);
            c.Detail("samples", samples);
            c.Detail("blocks", blocks);
            c.Detail("finalCacheEntries", finalEntries);
            c.Detail("maxCacheEntriesObserved", maxEntries);
            c.Detail("createdResources", host.Cache.CreatedResourceCount);
            c.Detail("cacheHits", host.Cache.HitCount);
            c.Detail("cacheMisses", host.Cache.MissCount);
            c.Detail("cacheEvictions", host.Cache.EvictionCount);
            c.Detail("cacheBytes", host.Cache.TotalBytes);
            c.Detail("managedBytesBefore", managedBefore);
            c.Detail("managedBytesAfter", managedAfter);
            c.Detail("managedBytesDelta", managedDelta);
            c.Detail("workingSetBefore", workingSetBefore);
            c.Detail("workingSetAfter", workingSetAfter);
            c.Detail("workingSetDelta", workingSetAfter - workingSetBefore);
            c.Detail("workingSetDeltaFirst1000", workingSets[999] - workingSetBefore);
            c.Detail("avgPaintMs", Math.Round(avg, 4));
            c.Detail("avgPaintMsFirstBlock", Math.Round(avgFirstBlock, 4));
            c.Detail("p95PaintMs", Math.Round(p95, 4));
            c.Detail("maxPaintMs", Math.Round(max, 4));

            _timeline.Add(new JsonObject
            {
                ["iteration"] = iterations,
                ["cacheEntries"] = finalEntries,
                ["cacheBytes"] = host.Cache.TotalBytes,
                ["createdResources"] = host.Cache.CreatedResourceCount,
                ["managedDelta"] = managedDelta,
                ["workingSetDelta"] = workingSetAfter - workingSetBefore
            });
        }

        /// <summary>一次完整绘制：窗口底纹 → 组块 → 进度条 → 向内的窗口边框。</summary>
        private static void PaintOnce(Graphics g, DecorResourceHost host, ThemeAppearanceOptions options,
            Rectangle client, Rectangle slot, double progress)
        {
            g.Clear(Color.FromArgb(16, 16, 20));

            // 窗口底纹（扫描线 / 网格）—— 属于底纹层，必须早于文字
            if (options.ScanlineEnabled)
            {
                TextureBrush? scan = host.GetOrAddTiledBrush(options, DecorCacheKey.KindScanlineTile,
                    Color.FromArgb(24, Cyan), 8, 4);
                if (scan != null) DecorPainter.FillTiled(g, scan, client);
            }
            if (options.GridEnabled)
            {
                TextureBrush? grid = host.GetOrAddTiledBrush(options, DecorCacheKey.KindGridTile,
                    Color.FromArgb(16, Magenta), 16, 16);
                if (grid != null) DecorPainter.FillTiled(g, grid, client);
            }

            // 进度条：渐变按"完整条槽"缓存，再按当前填充值裁剪
            if (options.BarStyleEnabled)
            {
                Bitmap? grad = host.GetOrAddGradientSlot(options, BarStart, BarEnd, 0.0, slot.Width, slot.Height);
                if (grad != null) DecorPainter.FillGradientSlot(g, grad, slot, progress);
            }

            // 向内的窗口边框（路径缓存；缩进 1 像素与 FillInnerFrame 的默认一致）
            if (options.FrameEnabled)
            {
                GraphicsPath band = host.GetOrAddBandPath(options, client.Width, client.Height,
                    options.FrameStyle, options.FrameLengthPx, 8, 1, options.FrameWidthPx);
                DecorPainter.FillInnerFrame(g, client, options.FrameStyle, options.FrameWidthPx,
                    options.FrameLengthPx, 8, options.BorderColor, band);
            }
        }

        // ============================================================
        // 用例 5：缓存上限（条目数 / 位图字节）
        // ============================================================

        private void RunCacheLimits()
        {
            ProbeCase c = NewCase("cache_limits");

            Rectangle bounds = new Rectangle(0, 0, 240, 120);
            Theme theme = BuildTheme(1, 3, 12, 0, 0, 0, 1);
            ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, bounds, 1);

            // 5.1 条目数上限：塞 300 个不同尺寸的槽位
            using (DecorResourceCache cache = new DecorResourceCache())
            {
                for (int i = 0; i < 300; i++)
                {
                    DecorCacheKey key = DecorCacheKey.Create(DecorCacheKey.KindGradientSlot, options,
                        BarStart, BarEnd, 100 + i, 10, DecorCacheKey.DirectionHorizontal, i);
                    cache.GetOrAddBitmap(key, k => DecorTextureFactory.CreateGradientSlot(
                        k.WidthPx, k.HeightPx, BarStart, BarEnd, 0.0)!);
                }

                c.Require(cache.Count <= DecorResourceCache.DefaultMaxEntries,
                    $"条目数 {cache.Count} 超过上限 {DecorResourceCache.DefaultMaxEntries}");
                c.Require(cache.TotalBytes <= DecorResourceCache.DefaultMaxBitmapBytes,
                    $"位图字节 {cache.TotalBytes} 超过上限 {DecorResourceCache.DefaultMaxBitmapBytes}");
                c.Detail("entriesAfter300Inserts", cache.Count);
                c.Detail("bytesAfter300Inserts", cache.TotalBytes);
                c.Detail("evictionsAfter300Inserts", cache.EvictionCount);
            }

            // 5.2 字节上限：塞 64 个 1024×1024（每个 4 MiB）的槽位
            using (DecorResourceCache cache = new DecorResourceCache())
            {
                for (int i = 0; i < 64; i++)
                {
                    DecorCacheKey key = DecorCacheKey.Create(DecorCacheKey.KindGradientSlot, options,
                        BarStart, BarEnd, 1024, 1024, DecorCacheKey.DirectionHorizontal, i);
                    cache.GetOrAddBitmap(key, k => DecorTextureFactory.CreateGradientSlot(
                        k.WidthPx, k.HeightPx, BarStart, BarEnd, 0.0)!);
                }

                c.Require(cache.TotalBytes <= DecorResourceCache.DefaultMaxBitmapBytes,
                    $"大位图场景字节 {cache.TotalBytes} 超过上限 {DecorResourceCache.DefaultMaxBitmapBytes}");
                c.Detail("entriesAfter64LargeInserts", cache.Count);
                c.Detail("bytesAfter64LargeInserts", cache.TotalBytes);
                c.Detail("evictionsAfter64LargeInserts", cache.EvictionCount);
            }

            c.Detail("maxEntries", DecorResourceCache.DefaultMaxEntries);
            c.Detail("maxBitmapBytes", DecorResourceCache.DefaultMaxBitmapBytes);
        }

        // ============================================================
        // 用例 6：失效入口
        // ============================================================

        private void RunInvalidation()
        {
            ProbeCase c = NewCase("invalidation_entry_points");

            Rectangle bounds = new Rectangle(0, 0, 240, 120);
            Theme theme = BuildTheme(1, 3, 12, 0, 0, 0, 1);

            using DecorResourceHost host = new DecorResourceHost();

            ThemeAppearanceOptions o1 = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, bounds, 1);
            host.UpdateContext(o1);
            host.GetOrAddGradientSlot(o1, BarStart, BarEnd, 0.0, 216, 10);
            host.GetOrAddFramePath(o1, 240, 120, 1, 12, 8);
            c.Require(host.Cache.Count == 2, $"填充后应有 2 条缓存，实际 {host.Cache.Count}");

            // 主题代次变化
            ThemeAppearanceOptions o2 = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, bounds, 2);
            host.UpdateContext(o2);
            c.Require(host.Cache.Count == 0, $"换肤后旧条目应被清空，实际 {host.Cache.Count}");

            // DPI 变化
            host.GetOrAddGradientSlot(o2, BarStart, BarEnd, 0.0, 216, 10);
            ThemeAppearanceOptions o3 = ThemeAppearanceOptions.Create(theme, 1.5f, 1.0f, bounds, 2);
            host.UpdateContext(o3);
            c.Require(host.Cache.Count == 0, $"DPI 变化后旧条目应被清空，实际 {host.Cache.Count}");

            // 用户缩放变化
            host.GetOrAddGradientSlot(o3, BarStart, BarEnd, 0.0, 216, 10);
            ThemeAppearanceOptions o4 = ThemeAppearanceOptions.Create(theme, 1.5f, 1.25f, bounds, 2);
            host.UpdateContext(o4);
            c.Require(host.Cache.Count == 0, $"用户缩放变化后旧条目应被清空，实际 {host.Cache.Count}");

            // 尺寸变化
            host.GetOrAddGradientSlot(o4, BarStart, BarEnd, 0.0, 216, 10);
            host.OnSizeChanged();
            c.Require(host.Cache.Count == 0, $"尺寸变化后旧条目应被清空，实际 {host.Cache.Count}");

            // 释放后回到基线
            host.Dispose();
            c.Require(host.Cache.Count == 0, $"释放后缓存应为空，实际 {host.Cache.Count}");
            c.Require(host.Cache.TotalBytes == 0, $"释放后字节应归零，实际 {host.Cache.TotalBytes}");

            c.Detail("themeGenerationInvalidated", true);
            c.Detail("dpiInvalidated", true);
            c.Detail("userScaleInvalidated", true);
            c.Detail("sizeInvalidated", true);
        }

        // ============================================================
        // 用例 7：渐变方向约定（取证）
        // ============================================================

        private void RunGradientOrientation()
        {
            ProbeCase c = NewCase("gradient_orientation_evidence");

            using (Bitmap horizontal = DecorTextureFactory.CreateGradientSlot(64, 8, Color.Red, Color.Blue, 0.0)!)
            {
                Color left = horizontal.GetPixel(0, 4);
                Color right = horizontal.GetPixel(63, 4);
                c.Require(left.R > left.B, $"0° 左端应接近起始色（红），实际 R={left.R} B={left.B}");
                c.Require(right.B > right.R, $"0° 右端应接近结束色（蓝），实际 R={right.R} B={right.B}");
                c.Detail("angle0_left", Describe(left));
                c.Detail("angle0_right", Describe(right));
            }

            using (Bitmap vertical = DecorTextureFactory.CreateGradientSlot(64, 8, Color.Red, Color.Blue, 90.0)!)
            {
                Color top = vertical.GetPixel(32, 0);
                Color bottom = vertical.GetPixel(32, 7);
                c.Require(top.R > top.B, $"90° 上端应接近起始色（红），实际 R={top.R} B={top.B}");
                c.Require(bottom.B > bottom.R, $"90° 下端应接近结束色（蓝），实际 R={bottom.R} B={bottom.B}");
                c.Detail("angle90_top", Describe(top));
                c.Detail("angle90_bottom", Describe(bottom));
            }

            c.Detail("convention", "角度按屏幕坐标顺时针为正：0°=左→右，90°=上→下，180°=右→左，270°=下→上");
        }

        // ============================================================
        // 工具
        // ============================================================

        private ProbeCase NewCase(string name)
        {
            ProbeCase c = new ProbeCase { Name = name };
            _cases.Add(c);
            return c;
        }

        private static Theme BuildTheme(int frameStyle, int frameWidth, int frameLength, int scanline, int glow, int grid, int barStyle)
        {
            Theme theme = new Theme { Name = "A05-Probe", Version = 4 };
            theme.Layout.FrameStyle = frameStyle;
            theme.Layout.FrameWidth = frameWidth;
            theme.Layout.FrameLength = frameLength;
            theme.Layout.Scanline = scanline;
            theme.Layout.Glow = glow;
            theme.Layout.Grid = grid;
            theme.Layout.BarStyle = barStyle;
            theme.Color.Background = "#101014";
            theme.Color.GroupBackground = "#1A1A22";
            theme.Color.Border = "#00E5FF";
            theme.Color.Shadow = "#FF2D95";
            return theme;
        }

        private static bool Contains(RectangleF limit, RectangleF candidate)
        {
            const float eps = 0.01f;
            return candidate.Left >= limit.Left - eps
                && candidate.Top >= limit.Top - eps
                && candidate.Right <= limit.Right + eps
                && candidate.Bottom <= limit.Bottom + eps;
        }

        private static string Describe(Color c)
        {
            return string.Format(CultureInfo.InvariantCulture, "A{0} R{1} G{2} B{3}", c.A, c.R, c.G, c.B);
        }
    }
}
