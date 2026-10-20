using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 固定噪点纹理（可复现、有界缓存）
    // ================================================================

    /// <summary>
    /// 固定噪点纹理缓存。
    ///
    /// ★ 合同要点：
    /// 1. **强度 0 时完全不工作**：不生成位图、不创建画刷、不绘制。
    /// 2. **可复现**：噪点由固定种子 + 固定伪随机序列生成，
    ///    同一强度下每次生成的纹理像素完全一致（不依赖 <see cref="Random"/> 的实现细节）。
    /// 3. **有界**：纹理块固定 128×128 32bpp = 64KiB，最多保留 4 块，
    ///    本组件位图占用上限 256KiB（远低于与 A05 共用的 16MiB 预算），
    ///    超额按最近最少使用淘汰。
    /// 4. 纹理块是**平铺**使用的，不缓存整屏位图，因此与窗口尺寸无关。
    /// 5. **新项永不自我淘汰**：新纹理块入表前先盖上时间戳，
    ///    且淘汰时把刚创建的这一项排除在候选之外，
    ///    杜绝"刚创建的画刷被自己淘汰并释放、却仍被返回"的缺陷。
    /// 6. **画刷生命周期**：返回的画刷在下一次 <see cref="GetBrush"/> 调用之前有效，
    ///    调用方必须**立即使用**（<c>BackdropRenderer.DrawNoise</c> 就是这么用的）；
    ///    被淘汰的纹理块会连同画刷一起释放，不要跨调用长期持有。
    /// </summary>
    public sealed class BackdropNoiseCache : IDisposable
    {
        /// <summary>纹理块边长（像素）。</summary>
        public const int TileSize = 128;

        /// <summary>固定随机种子（噪点可复现的关键）。</summary>
        public const int Seed = 0x51ED;

        /// <summary>最多保留的纹理块数量（每块 64KiB）。</summary>
        public const int MaxEntries = 4;

        /// <summary>强度 100 时噪点像素的最大 alpha 比例（0–1）。</summary>
        private const double MaxAlphaRatio = 0.35;

        private sealed class Entry
        {
            public int Intensity;
            public Bitmap Bitmap = null!;
            public TextureBrush Brush = null!;
            public long LastUsed;
        }

        private readonly Dictionary<int, Entry> _entries = new();
        private long _clock;
        private long _totalBytes;
        private bool _disposed;

        /// <summary>当前保留的纹理块数量。</summary>
        public int EntryCount => _entries.Count;

        /// <summary>当前占用的位图字节数（不含画刷的托管开销）。</summary>
        public long TotalBytes => _totalBytes;

        /// <summary>
        /// 取指定强度的噪点画刷（同一强度复用同一块纹理）。
        /// <para>
        /// ★ 调用方合同：返回的画刷在下一次 <see cref="GetBrush"/> 调用之前有效，
        ///   拿到后必须**立即使用**；被淘汰的纹理块会连同画刷一起释放。
        /// </para>
        /// </summary>
        /// <param name="intensity">噪点强度（0–100，已归一化）。0 时返回 null。</param>
        public TextureBrush? GetBrush(int intensity)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BackdropNoiseCache));
            if (intensity <= 0) return null;   // 强度 0：零开销

            if (_entries.TryGetValue(intensity, out Entry? hit))
            {
                hit.LastUsed = ++_clock;
                return hit.Brush;
            }

            Entry entry = CreateEntry(intensity);

            // ★ 关键：新项必须**先盖时间戳再入表**。
            //   否则新项的 LastUsed=0 会被淘汰逻辑当成"最久未使用"，
            //   紧接着的淘汰会把刚创建的画刷自己释放掉，
            //   而本方法仍把它返回给调用方 → 调用方 FillRectangle 抛
            //   ArgumentException: Parameter is not valid.
            entry.LastUsed = ++_clock;

            _entries[intensity] = entry;
            _totalBytes += (long)TileSize * TileSize * 4;
            EvictIfNeeded(intensity);
            return entry.Brush;
        }

        /// <summary>
        /// 释放全部缓存（尺寸/主题/DPI 变化或退出时调用）。
        /// 只能在两次绘制之间调用，不可在绘制过程中调用。
        /// </summary>
        public void Invalidate()
        {
            foreach (Entry entry in _entries.Values)
            {
                entry.Brush.Dispose();
                entry.Bitmap.Dispose();
            }
            _entries.Clear();
            _totalBytes = 0;
        }

        /// <summary>释放缓存并标记不可用。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            Invalidate();
            _disposed = true;
        }

        // 生成一块固定噪点纹理：黑白噪点，alpha 由强度决定
        private static Entry CreateEntry(int intensity)
        {
            int alpha = (int)Math.Round(255.0 * (intensity / 100.0) * MaxAlphaRatio, MidpointRounding.AwayFromZero);
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;

            var bitmap = new Bitmap(TileSize, TileSize, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, TileSize, TileSize), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] buffer = new byte[data.Stride * data.Height];
                var random = new Xorshift32(Seed);
                int offset = 0;
                for (int y = 0; y < TileSize; y++)
                {
                    for (int x = 0; x < TileSize; x++)
                    {
                        // 一半黑一半白，均值不偏，避免噪点整体压暗或提亮
                        bool white = (random.Next() & 1u) == 1u;
                        byte v = white ? (byte)255 : (byte)0;
                        buffer[offset + 0] = v;   // B
                        buffer[offset + 1] = v;   // G
                        buffer[offset + 2] = v;   // R
                        buffer[offset + 3] = (byte)alpha;
                        offset += 4;
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            // 整块纹理作为一个平铺单元；画刷锚定在世界原点，因此同一 bounds 下结果稳定
            var brush = new TextureBrush(bitmap)
            {
                WrapMode = WrapMode.Tile
            };

            return new Entry { Intensity = intensity, Bitmap = bitmap, Brush = brush };
        }

        // 超出上限时淘汰最久未使用的一块
        // protectedKey 是刚创建、正在返回给调用方的那一项：它绝不能成为淘汰对象，
        // 否则调用方会拿到一块已经被释放的画刷
        private void EvictIfNeeded(int protectedKey)
        {
            while (_entries.Count > MaxEntries)
            {
                long oldest = long.MaxValue;
                int oldestKey = 0;
                bool found = false;
                foreach (KeyValuePair<int, Entry> pair in _entries)
                {
                    if (pair.Key == protectedKey) continue;   // 刚创建的一项不参与淘汰
                    if (pair.Value.LastUsed < oldest)
                    {
                        oldest = pair.Value.LastUsed;
                        oldestKey = pair.Key;
                        found = true;
                    }
                }
                if (!found) return;

                Entry victim = _entries[oldestKey];
                _entries.Remove(oldestKey);
                victim.Brush.Dispose();
                victim.Bitmap.Dispose();
                _totalBytes -= (long)TileSize * TileSize * 4;
                if (_totalBytes < 0) _totalBytes = 0;
            }
        }

        /// <summary>
        /// 固定种子的 xorshift32 伪随机数发生器。
        /// 自带实现而不是用 <see cref="Random"/>：保证跨 .NET 版本、跨进程完全可复现。
        /// </summary>
        private struct Xorshift32
        {
            private uint _state;

            public Xorshift32(int seed)
            {
                uint s = (uint)seed;
                if (s == 0) s = 0x9E3779B9u;   // xorshift 不能以 0 为状态
                _state = s;
            }

            public uint Next()
            {
                uint x = _state;
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                _state = x;
                return x;
            }
        }
    }
}
