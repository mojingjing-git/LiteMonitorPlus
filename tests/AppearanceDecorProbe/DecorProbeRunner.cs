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
            RunBarTextureTileShape();
            RunTextureResourceOwnership();
            RunTextureBrushFailureContainment();
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
        // 用例 8：进度条纹理瓦片的斜纹形态（贯穿内部 + 平铺连续）
        // ============================================================

        private void RunBarTextureTileShape()
        {
            ProbeCase c = NewCase("bar_texture_tile_diagonal_continuity");

            // 瓦片边长 = 条高 × 2（与 BarRenderer 的规则一致），覆盖多种条高
            int[] tiles = { 12, 16, 20, 24, 32 };

            JsonArray arr = new();
            foreach (int tile in tiles)
            {
                int thickness = DecorTextureFactory.GetBarTextureThickness(tile, tile);
                int period = DecorTextureFactory.GetBarTexturePeriod(tile, tile);

                // 周期必须是边长的公约数，否则平铺后接缝处斜线错位
                c.Require(period > 0 && tile % period == 0,
                    $"瓦片 {tile}：周期 {period} 必须整除边长 {tile}（否则平铺不连续）");

                Bitmap? created = DecorTextureFactory.CreateBarTextureTile(tile, tile, BarStart);
                c.Require(created != null, $"瓦片 {tile}×{tile} 应创建成功");
                if (created == null) continue;
                using Bitmap bmp = created;

                int painted = 0;
                int interiorPainted = 0;
                List<int> residues = new();
                for (int y = 0; y < tile; y++)
                {
                    for (int x = 0; x < tile; x++)
                    {
                        if (bmp.GetPixel(x, y).A == 0) continue;
                        painted++;
                        if (x >= 2 && x < tile - 2 && y >= 2 && y < tile - 2) interiorPainted++;
                        residues.Add((x + y) % period);
                    }
                }

                // 1) 斜纹必须贯穿瓦片内部
                //    （旧实现把两条线正好画在角点上，16×16 瓦片只有 7 个非透明像素，
                //      去掉两像素边缘后内部为 0 个）
                c.Require(painted > 0, $"瓦片 {tile}：应画出斜纹，实际非透明像素 {painted} 个");
                c.Require(interiorPainted > 0,
                    $"瓦片 {tile}：内部（去掉 2 像素边缘）应有斜纹，实际 {interiorPainted} 个");

                // 2) 斜线必须属于**单一相位族**（x + y = phase + k × 周期）：
                //    所有非透明像素的 (x+y) mod 周期 落在同一个窄窗口里
                int window = MinimalCircularWindow(residues, period);
                c.Require(window <= thickness * 2 + 4,
                    $"瓦片 {tile}：斜线应属于同一相位族（(x+y) mod {period} 的最小覆盖窗口 {window} 超过 {thickness * 2 + 4}）");

                // 3) 平铺连续：把瓦片平铺 3×3，斜纹必须跨接缝连成一条不断的斜线
                int broken = CheckTiledDiagonal(bmp, tile, out int tiledPainted);
                c.Require(tiledPainted > 0, $"瓦片 {tile}：平铺后应有斜纹像素");
                c.Require(broken == 0, $"瓦片 {tile}：平铺后有 {broken} 处斜纹在接缝处断开（平铺不连续）");

                arr.Add(new JsonObject
                {
                    ["tile"] = tile,
                    ["thickness"] = thickness,
                    ["period"] = period,
                    ["paintedPixels"] = painted,
                    ["interiorPaintedPixels"] = interiorPainted,
                    ["phaseWindow"] = window,
                    ["tiledPaintedPixels"] = tiledPainted,
                    ["brokenDiagonalLinks"] = broken
                });
            }

            c.Detail("tiles", arr);
            c.Detail("convention", "斜线族 x+y = phase + k×gcd(宽,高)；内部非空 + 单一相位 + 跨接缝不断 = 贯穿内部且平铺连续。");
        }

        /// <summary>
        /// 把瓦片平铺到 3×3 的画布上，检查斜纹是否<b>跨接缝连成一条不断的斜线</b>。
        /// 斜线是 45°，方向为 (x+1, y−1)：只要该方向上的下一个像素也是斜纹像素，
        /// 斜线就没有断。返回断链处数（0 = 连续）。
        /// </summary>
        private static int CheckTiledDiagonal(Bitmap tile, int size, out int paintedCount)
        {
            int side = size * 3;
            using Bitmap canvas = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Transparent);
                using (TextureBrush brush = new TextureBrush(tile))
                {
                    g.FillRectangle(brush, new Rectangle(0, 0, side, side));
                }
            }

            // 阈值 32：避开抗锯齿最淡的一圈（8% 覆盖率的边缘像素）
            bool[,] mask = new bool[side, side];
            paintedCount = 0;
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    if (canvas.GetPixel(x, y).A < 32) continue;
                    mask[x, y] = true;
                    paintedCount++;
                }
            }

            int broken = 0;
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    if (!mask[x, y]) continue;
                    int nx = x + 1;
                    int ny = y - 1;
                    if (nx < 0 || nx >= side || ny < 0 || ny >= side) continue;   // 出画布不算断链
                    if (!mask[nx, ny]) broken++;
                }
            }
            return broken;
        }

        /// <summary>
        /// 求一组余数在<b>环形</b>（模 period）上的最小覆盖窗口（步数）。
        /// 余数集合越集中，窗口越小；用于判定"是否属于同一个相位族"。
        /// </summary>
        private static int MinimalCircularWindow(List<int> values, int period)
        {
            if (values.Count == 0) return period;

            List<int> sorted = new(values);
            sorted.Sort();

            List<int> distinct = new();
            foreach (int v in sorted)
            {
                if (distinct.Count == 0 || distinct[distinct.Count - 1] != v) distinct.Add(v);
            }
            if (distinct.Count == 1) return 0;

            int maxGap = 0;
            for (int i = 0; i < distinct.Count; i++)
            {
                int next = (i + 1 < distinct.Count) ? distinct[i + 1] : distinct[0] + period;
                int gap = next - distinct[i];
                if (gap > maxGap) maxGap = gap;
            }
            return period - maxGap;
        }

        // ============================================================
        // 用例 9：纹理资源的所有权与释放路径
        // ============================================================

        private void RunTextureResourceOwnership()
        {
            ProbeCase c = NewCase("texture_resource_ownership");

            // ---------- 9.1 缓存取出的画刷必须真的能画出内容 ----------
            // TextureBrush **不复制**源位图：源位图必须与画刷同生共死。
            // ---------- 9.1 缓存取出的画刷必须真的能画出内容 ----------
            // TextureBrush **不复制**源位图：源位图必须与画刷同生共死。
            // （把源位图提前释放的实现，画刷拿到的是一张已释放的位图 → 画不出东西。）
            int paintedFromCacheBrush = 0;
            {
                Theme theme0 = BuildTheme(1, 3, 12, 0, 0, 0, 2);
                ThemeAppearanceOptions options0 = ThemeAppearanceOptions.Create(
                    theme0, 1.0f, 1.0f, new Rectangle(0, 0, 240, 120), 1);

                using DecorResourceHost host0 = new DecorResourceHost();
                host0.UpdateContext(options0);

                TextureBrush? brush = host0.GetOrAddTiledBrush(
                    options0, DecorCacheKey.KindBarTexture, BarStart, 16, 16);
                c.Require(brush != null, "应取到纹理画刷");

                if (brush != null)
                {
                    try
                    {
                        using Bitmap canvas = new Bitmap(16, 16, PixelFormat.Format32bppPArgb);
                        using (Graphics g = Graphics.FromImage(canvas))
                        {
                            g.Clear(Color.Transparent);
                            g.FillRectangle(brush, new Rectangle(0, 0, 16, 16));
                        }
                        paintedFromCacheBrush = CountPainted(canvas, 1);
                    }
                    catch (Exception ex)
                    {
                        c.Fail("用缓存里的纹理画刷绘制时抛出异常：" + ex.Message);
                    }
                }
            }
            c.Require(paintedFromCacheBrush > 0,
                "缓存取出的纹理画刷必须能画出内容（源位图必须与画刷同时存活）");

            // ---------- 9.2 缓存：创建若干纹理 → 淘汰 → 计数归零且不再持有图像 ----------
            Theme theme = BuildTheme(1, 3, 12, 0, 0, 0, 2);
            ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(
                theme, 1.0f, 1.0f, new Rectangle(0, 0, 240, 120), 1);

            using DecorResourceHost host = new DecorResourceHost();
            host.UpdateContext(options);

            List<TextureBrush> brushes = new();
            long expectedBytes = 0;
            for (int i = 0; i < 8; i++)
            {
                int tile = 8 + i;
                TextureBrush? brush = host.GetOrAddTiledBrush(
                    options, DecorCacheKey.KindBarTexture, BarStart, tile, tile);
                if (brush != null) brushes.Add(brush);
                expectedBytes += (long)tile * tile * 4;
            }

            c.Require(host.Cache.Count == 8, $"应有 8 条纹理缓存，实际 {host.Cache.Count}");
            c.Require(host.Cache.TotalBytes == expectedBytes,
                $"纹理缓存字节应按显式瓦片尺寸核算：期望 {expectedBytes}，实际 {host.Cache.TotalBytes}");
            c.Require(host.Cache.CreatedResourceCount == 8,
                $"应创建 8 个资源，实际 {host.Cache.CreatedResourceCount}");

            int removed = host.Cache.InvalidateAll();
            c.Require(removed == 8, $"应淘汰 8 条缓存，实际 {removed}");
            c.Require(host.Cache.Count == 0, $"淘汰后缓存条目应为 0，实际 {host.Cache.Count}");
            c.Require(host.Cache.TotalBytes == 0, $"淘汰后位图字节应归零，实际 {host.Cache.TotalBytes}");
            c.Require(host.Cache.ReleasedResourceCount == host.Cache.CreatedResourceCount,
                $"释放的资源数 {host.Cache.ReleasedResourceCount} 应与创建的 {host.Cache.CreatedResourceCount} 相等");

            // 淘汰后的画刷必须已经失效（要么抛异常，要么画不出任何像素）
            int stillAlive = 0;
            foreach (TextureBrush brush in brushes)
            {
                bool threw = false;
                int painted = 0;
                try
                {
                    using Bitmap canvas = new Bitmap(8, 8, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.Transparent);
                        g.FillRectangle(brush, new Rectangle(0, 0, 8, 8));
                    }
                    painted = CountPainted(canvas, 1);
                }
                catch (Exception)
                {
                    threw = true;
                }
                if (!threw && painted > 0) stillAlive++;
            }
            c.Require(stillAlive == 0, $"淘汰后仍有 {stillAlive} 个画刷可以继续绘制（缓存没有释放它）");

            c.Detail("paintedFromCacheBrush", paintedFromCacheBrush);
            c.Detail("cacheEntries", host.Cache.Count);
            c.Detail("cacheBytes", host.Cache.TotalBytes);
            c.Detail("createdResources", host.Cache.CreatedResourceCount);
            c.Detail("releasedResources", host.Cache.ReleasedResourceCount);
            c.Detail("evictions", host.Cache.EvictionCount);
            c.Detail("note", "源位图与画刷都由缓存持有、一起释放；字节按显式尺寸核算，不再取画刷的 Image。");
        }

        /// <summary>统计 alpha ≥ 最小值的像素数。</summary>
        private static int CountPainted(Bitmap bitmap, int minAlpha)
        {
            int count = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A >= minAlpha) count++;
                }
            }
            return count;
        }

        // ============================================================
        // 用例 10：纹理画刷的异常安全（构造失败 / 入表失败）
        // ============================================================

        /// <summary>
        /// 纹理画刷的异常路径必须<b>不漏资源、不改计数、不卡缓存</b>：
        /// - 10.1 画刷<b>构造失败</b>（用可注入的创建入口触发，<b>不靠耗尽内存</b>）：
        ///   源位图必须已被释放，缓存的条目数 / 字节数 / 创建数 / 释放数 / 淘汰数
        ///   必须<b>回到调用前</b>，同一个 key 再次请求必须能正常创建；
        /// - 10.2 画刷已创建、<b>入表失败</b>：已创建的画刷与源位图<b>都必须被释放</b>，
        ///   计数同样回到调用前，重试必须能成功；
        /// - 正常路径的既有断言（计数 / 字节归零、创建数 = 释放数）继续成立。
        /// </summary>
        private void RunTextureBrushFailureContainment()
        {
            ProbeCase c = NewCase("texture_brush_failure_containment");

            // ---------- 10.1 画刷构造失败 ----------
            DecorResourceCache cache = new DecorResourceCache();
            try
            {
                DecorCacheKey key = BuildTextureKey(16, 16);
                Bitmap? handedOver = null;
                bool aliveAtInjection = false;
                int tileFactoryCalls = 0;
                int brushFactoryCalls = 0;

                // 注入"必然失败的画刷创建入口"：稳定复现构造失败，不需要耗尽内存
                string hookError;
                bool hooked = DecorCacheFailureHooks.TryInstall(cache, "TextureBrushFactory",
                    new Func<Bitmap, TextureBrush>(_ =>
                    {
                        brushFactoryCalls++;
                        throw new InvalidOperationException("注入的纹理画刷构造失败。");
                    }),
                    out hookError);
                if (!hooked) c.Fail(hookError);

                int countBefore = cache.Count;
                long bytesBefore = cache.TotalBytes;
                long createdBefore = cache.CreatedResourceCount;
                long createdBytesBefore = cache.CreatedBitmapBytes;
                long releasedBefore = cache.ReleasedResourceCount;
                long evictionsBefore = cache.EvictionCount;
                long missesBefore = cache.MissCount;

                Exception? failure = null;
                try
                {
                    cache.GetOrAddTextureBrush(key, k =>
                    {
                        tileFactoryCalls++;
                        handedOver = CreateSolidTile(k.WidthPx, k.HeightPx);
                        // 对照：注入失败之前，这张位图是活的
                        aliveAtInjection = DecorCacheFailureHooks.IsAlive(handedOver);
                        return handedOver;
                    }, 16, 16);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                c.Require(failure is InvalidOperationException,
                    $"构造失败必须抛出 InvalidOperationException，实际 {(failure == null ? "没有抛异常" : failure.GetType().FullName)}");
                c.Require(tileFactoryCalls == 1, $"源位图工厂应被调用 1 次，实际 {tileFactoryCalls} 次");
                c.Require(brushFactoryCalls == 1, $"注入的画刷创建入口应被调用 1 次，实际 {brushFactoryCalls} 次");
                c.Require(aliveAtInjection, "对照：注入失败之前源位图必须仍然是活的（否则已释放的判定不可信）");

                // 失败的位图必须已经被释放
                bool bitmapReleased = DecorCacheFailureHooks.IsReleased(handedOver, out string bitmapEvidence);
                c.Require(bitmapReleased, "构造失败后源位图必须已被释放（访问它应抛异常）：" + bitmapEvidence);

                // 计数必须回到调用前（不是 0，也不是多算）
                c.Require(cache.Count == countBefore,
                    $"构造失败后条目数应回到调用前 {countBefore}，实际 {cache.Count}");
                c.Require(cache.TotalBytes == bytesBefore,
                    $"构造失败后位图字节应回到调用前 {bytesBefore}，实际 {cache.TotalBytes}");
                c.Require(cache.CreatedResourceCount == createdBefore,
                    $"构造失败后创建资源数应回到调用前 {createdBefore}，实际 {cache.CreatedResourceCount}");
                c.Require(cache.CreatedBitmapBytes == createdBytesBefore,
                    $"构造失败后创建字节数应回到调用前 {createdBytesBefore}，实际 {cache.CreatedBitmapBytes}");
                c.Require(cache.ReleasedResourceCount == releasedBefore,
                    $"构造失败后释放条目数应回到调用前 {releasedBefore}，实际 {cache.ReleasedResourceCount}");
                c.Require(cache.EvictionCount == evictionsBefore,
                    $"构造失败后淘汰数应回到调用前 {evictionsBefore}，实际 {cache.EvictionCount}");
                c.Require(cache.MissCount == missesBefore + 1,
                    $"构造失败后未命中数只应 +1（这次请求确实未命中），实际 +{cache.MissCount - missesBefore}");

                // 失败后重试同一个 key 必须能正常创建（缓存没有被卡死）
                string resetError;
                DecorCacheFailureHooks.TryInstall<Func<Bitmap, TextureBrush>>(cache, "TextureBrushFactory", null, out resetError);

                TextureBrush? retry = null;
                Exception? retryFailure = null;
                try
                {
                    retry = cache.GetOrAddTextureBrush(key, k => CreateSolidTile(k.WidthPx, k.HeightPx), 16, 16);
                }
                catch (Exception ex)
                {
                    retryFailure = ex;
                }

                c.Require(retryFailure == null,
                    $"构造失败后重试同一个 key 必须能创建成功，但抛出 {retryFailure?.GetType().FullName}");
                c.Require(retry != null, "构造失败后重试同一个 key 必须能创建成功");
                c.Require(cache.Count == countBefore + 1,
                    $"重试后条目数应为 {countBefore + 1}，实际 {cache.Count}");
                c.Require(cache.TotalBytes == bytesBefore + 16 * 16 * 4,
                    $"重试后位图字节应为 {bytesBefore + 16 * 16 * 4}，实际 {cache.TotalBytes}");
                c.Require(cache.CreatedResourceCount == createdBefore + 1,
                    $"重试后创建资源数应为 {createdBefore + 1}，实际 {cache.CreatedResourceCount}");

                c.Detail("case10_1_failure", failure?.GetType().FullName ?? "none");
                c.Detail("case10_1_bitmapReleased", bitmapReleased);
                c.Detail("case10_1_bitmapEvidence", bitmapEvidence);
                c.Detail("case10_1_retrySucceeded", retry != null);
            }
            finally
            {
                cache.Dispose();
            }

            // 正常路径的记账仍然配平（释放后归零、创建数 = 释放数）
            c.Require(cache.Count == 0, $"释放后缓存条目应为 0，实际 {cache.Count}");
            c.Require(cache.TotalBytes == 0, $"释放后位图字节应归零，实际 {cache.TotalBytes}");
            c.Require(cache.ReleasedResourceCount == cache.CreatedResourceCount,
                $"释放条目数 {cache.ReleasedResourceCount} 应与创建资源数 {cache.CreatedResourceCount} 相等");

            // ---------- 10.2 入表失败（画刷已创建、条目未入表） ----------
            DecorResourceCache cache2 = new DecorResourceCache();
            try
            {
                DecorCacheKey key = BuildTextureKey(16, 16);
                Bitmap? handedOver = null;
                TextureBrush? createdBrush = null;
                bool brushAliveBeforeFailure = false;
                bool bitmapAliveBeforeFailure = false;

                string hookError;
                bool hooked = DecorCacheFailureHooks.TryInstall(cache2, "TextureBrushFactory",
                    new Func<Bitmap, TextureBrush>(src =>
                    {
                        createdBrush = new TextureBrush(src);
                        return createdBrush;
                    }),
                    out hookError);
                if (!hooked) c.Fail(hookError);

                hooked = DecorCacheFailureHooks.TryInstall(cache2, "TextureBrushPreInsertHook",
                    new Action(() =>
                    {
                        // 对照：注入失败之前，画刷与位图都还活着
                        brushAliveBeforeFailure = DecorCacheFailureHooks.BrushIsAlive(createdBrush);
                        bitmapAliveBeforeFailure = DecorCacheFailureHooks.IsAlive(handedOver);
                        throw new InvalidOperationException("注入的入表失败。");
                    }),
                    out hookError);
                if (!hooked) c.Fail(hookError);

                int countBefore = cache2.Count;
                long bytesBefore = cache2.TotalBytes;
                long createdBefore = cache2.CreatedResourceCount;
                long createdBytesBefore = cache2.CreatedBitmapBytes;
                long releasedBefore = cache2.ReleasedResourceCount;
                long evictionsBefore = cache2.EvictionCount;

                Exception? failure = null;
                try
                {
                    cache2.GetOrAddTextureBrush(key, k =>
                    {
                        handedOver = CreateSolidTile(k.WidthPx, k.HeightPx);
                        return handedOver;
                    }, 16, 16);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                c.Require(failure is InvalidOperationException,
                    $"入表失败必须抛出 InvalidOperationException，实际 {(failure == null ? "没有抛异常" : failure.GetType().FullName)}");
                c.Require(createdBrush != null, "入表失败之前画刷应已创建出来");
                c.Require(brushAliveBeforeFailure,
                    "对照：注入失败之前画刷必须仍然是活的（否则已释放的判定不可信）");
                c.Require(bitmapAliveBeforeFailure,
                    "对照：注入失败之前源位图必须仍然是活的（否则已释放的判定不可信）");

                // 已创建的画刷与源位图都必须已经被释放
                bool brushReleased = DecorCacheFailureHooks.IsReleased(createdBrush, out string brushEvidence);
                c.Require(brushReleased, "入表失败后已创建的画刷必须已被释放：" + brushEvidence);
                bool bitmapReleased = DecorCacheFailureHooks.IsReleased(handedOver, out string bitmapEvidence);
                c.Require(bitmapReleased, "入表失败后源位图必须已被释放（访问它应抛异常）：" + bitmapEvidence);

                // 计数必须回到调用前
                c.Require(cache2.Count == countBefore,
                    $"入表失败后条目数应回到调用前 {countBefore}，实际 {cache2.Count}");
                c.Require(cache2.TotalBytes == bytesBefore,
                    $"入表失败后位图字节应回到调用前 {bytesBefore}，实际 {cache2.TotalBytes}");
                c.Require(cache2.CreatedResourceCount == createdBefore,
                    $"入表失败后创建资源数应回到调用前 {createdBefore}，实际 {cache2.CreatedResourceCount}");
                c.Require(cache2.CreatedBitmapBytes == createdBytesBefore,
                    $"入表失败后创建字节数应回到调用前 {createdBytesBefore}，实际 {cache2.CreatedBitmapBytes}");
                c.Require(cache2.ReleasedResourceCount == releasedBefore,
                    $"入表失败后释放条目数应回到调用前 {releasedBefore}，实际 {cache2.ReleasedResourceCount}");
                c.Require(cache2.EvictionCount == evictionsBefore,
                    $"入表失败后淘汰数应回到调用前 {evictionsBefore}，实际 {cache2.EvictionCount}");

                // 失败后重试必须能成功
                string resetError;
                DecorCacheFailureHooks.TryInstall<Func<Bitmap, TextureBrush>>(cache2, "TextureBrushFactory", null, out resetError);
                DecorCacheFailureHooks.TryInstall<Action>(cache2, "TextureBrushPreInsertHook", null, out resetError);

                TextureBrush? retry = null;
                Exception? retryFailure = null;
                try
                {
                    retry = cache2.GetOrAddTextureBrush(key, k => CreateSolidTile(k.WidthPx, k.HeightPx), 16, 16);
                }
                catch (Exception ex)
                {
                    retryFailure = ex;
                }

                c.Require(retryFailure == null,
                    $"入表失败后重试同一个 key 必须能创建成功，但抛出 {retryFailure?.GetType().FullName}");
                c.Require(retry != null, "入表失败后重试同一个 key 必须能创建成功");
                c.Require(cache2.Count == countBefore + 1,
                    $"重试后条目数应为 {countBefore + 1}，实际 {cache2.Count}");
                c.Require(cache2.CreatedResourceCount == createdBefore + 1,
                    $"重试后创建资源数应为 {createdBefore + 1}，实际 {cache2.CreatedResourceCount}");

                c.Detail("case10_2_failure", failure?.GetType().FullName ?? "none");
                c.Detail("case10_2_brushReleased", brushReleased);
                c.Detail("case10_2_brushEvidence", brushEvidence);
                c.Detail("case10_2_bitmapReleased", bitmapReleased);
                c.Detail("case10_2_bitmapEvidence", bitmapEvidence);
                c.Detail("case10_2_retrySucceeded", retry != null);
            }
            finally
            {
                cache2.Dispose();
            }

            c.Require(cache2.Count == 0, $"释放后缓存条目应为 0，实际 {cache2.Count}");
            c.Require(cache2.TotalBytes == 0, $"释放后位图字节应归零，实际 {cache2.TotalBytes}");
            c.Require(cache2.ReleasedResourceCount == cache2.CreatedResourceCount,
                $"释放条目数 {cache2.ReleasedResourceCount} 应与创建资源数 {cache2.CreatedResourceCount} 相等");

            // ---------- 10.3 纹理路径的容量上限仍然成立 ----------
            RunTextureCapacityLimits(c);

            c.Detail("note", "画刷创建入口与入表前钩子都是 internal 接缝（探针用反射安装）；失败路径不漏位图 / 画刷，计数回到调用前，重试可成功。");
        }

        /// <summary>
        /// 纹理路径的容量上限仍然成立：淘汰挪到入表之后<b>不能放宽</b>条目数 / 字节上限，
        /// "单个条目超过字节上限"的例外约定也必须保持原样。
        /// </summary>
        private void RunTextureCapacityLimits(ProbeCase c)
        {
            DecorResourceCache limited = new DecorResourceCache(maxEntries: 4, maxBitmapBytes: 4096);
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    DecorCacheKey key = BuildTextureKey(16, 16, i);
                    limited.GetOrAddTextureBrush(key, k => CreateSolidTile(k.WidthPx, k.HeightPx), 16, 16);
                }

                c.Require(limited.Count <= limited.MaxEntries,
                    $"纹理路径条目数 {limited.Count} 超过上限 {limited.MaxEntries}");
                c.Require(limited.TotalBytes <= limited.MaxBitmapBytes,
                    $"纹理路径位图字节 {limited.TotalBytes} 超过上限 {limited.MaxBitmapBytes}");
                c.Require(limited.EvictionCount > 0, "往 4 条上限的缓存里塞 8 条纹理应发生淘汰");
                c.Require(limited.ReleasedResourceCount + limited.Count == limited.CreatedResourceCount,
                    $"创建资源数 {limited.CreatedResourceCount} 应等于仍在缓存的 {limited.Count} 条 + 已释放的 {limited.ReleasedResourceCount} 条");

                c.Detail("case10_3_entries", limited.Count);
                c.Detail("case10_3_bytes", limited.TotalBytes);
                c.Detail("case10_3_evictions", limited.EvictionCount);
            }
            finally
            {
                limited.Dispose();
            }

            // 全部释放后：创建数 = 释放数、字节归零
            c.Require(limited.Count == 0, $"释放后纹理缓存条目应为 0，实际 {limited.Count}");
            c.Require(limited.TotalBytes == 0, $"释放后纹理缓存字节应归零，实际 {limited.TotalBytes}");
            c.Require(limited.ReleasedResourceCount == limited.CreatedResourceCount,
                $"释放条目数 {limited.ReleasedResourceCount} 应与创建资源数 {limited.CreatedResourceCount} 相等");

            // 单个条目超过字节上限的例外：它必须留下，且下一次插入时先被淘汰
            using (DecorResourceCache tiny = new DecorResourceCache(maxEntries: 8, maxBitmapBytes: 100))
            {
                DecorCacheKey bigKey = BuildTextureKey(16, 16, 100);
                tiny.GetOrAddTextureBrush(bigKey, k => CreateSolidTile(k.WidthPx, k.HeightPx), 16, 16);
                c.Require(tiny.Count == 1, $"超大条目应单独存在，实际条目数 {tiny.Count}");
                c.Require(tiny.TotalBytes == 16 * 16 * 4, $"超大条目字节应为 {16 * 16 * 4}，实际 {tiny.TotalBytes}");

                DecorCacheKey nextKey = BuildTextureKey(16, 16, 101);
                tiny.GetOrAddTextureBrush(nextKey, k => CreateSolidTile(k.WidthPx, k.HeightPx), 16, 16);
                c.Require(tiny.Count == 1, $"超大条目应在下次插入时被优先淘汰，实际条目数 {tiny.Count}");
                c.Require(tiny.EvictionCount >= 1, "超大条目应被淘汰一次");
            }
        }

        /// <summary>造一张<b>有内容</b>的实心瓦片（用于证明画刷"活着时画得出像素"）。</summary>
        private static Bitmap CreateSolidTile(int widthPx, int heightPx)
        {
            Bitmap tile = new Bitmap(Math.Max(1, widthPx), Math.Max(1, heightPx), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(tile))
            {
                g.Clear(Color.FromArgb(255, 0, 229, 255));
            }
            return tile;
        }

        /// <summary>造一个进度条纹理类的缓存键（探针自用，与运行期上下文无关）。</summary>
        private static DecorCacheKey BuildTextureKey(int widthPx, int heightPx, int seed = 0)
        {
            return new DecorCacheKey(
                DecorCacheKey.KindBarTexture,
                1,
                BarStart.ToArgb(),
                Color.Empty.ToArgb(),
                widthPx,
                heightPx,
                DecorCacheKey.DirectionRadial,
                96,
                1000,
                seed);
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
