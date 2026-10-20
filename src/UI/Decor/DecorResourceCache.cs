using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LiteMonitor.src.UI.Decor
{
    /// <summary>
    /// 装饰绘图资源缓存（纹理 / 渐变 / 图案）。
    ///
    /// ★ 所有权合同（v4 §3.3）：
    /// - <b>缓存是资源的所有者</b>：命中返回的位图 / 画刷由缓存负责释放；
    ///   <b>借用者不得 Dispose，也不得修改共享对象的状态</b>
    ///   （例如不得改动 <see cref="TextureBrush.Transform"/>）。
    ///   需要变换时用 <see cref="DecorPainter.CloneTextureBrush"/> 克隆出独占对象。
    /// - 需要自己控制生命周期的位图请直接用
    ///   <see cref="DecorTextureFactory"/>，不要经过本缓存。
    ///
    /// ★ 容量与淘汰策略（可复核）：
    /// - 条目上限 <see cref="DefaultMaxEntries"/> = 128；
    /// - 位图 / 静态装饰位图总字节上限 <see cref="DefaultMaxBitmapBytes"/> = 16 MiB；
    /// - 淘汰规则 = <b>LRU + 总量核算</b>：插入前先按"最久未使用"顺序淘汰，
    ///   直到条目数低于上限且总字节 + 新条目字节不超过字节上限；
    /// - 例外（已文档化）：<b>单个条目本身超过整个字节上限</b>时无法既缓存又守恒，
    ///   此时允许它单独存在并使总量暂时超限；由于它是最久未使用，
    ///   <b>下一次插入时一定会被优先淘汰</b>。
    ///
    /// ★ 线程安全：所有公开方法都在同一把锁内完成，UI 线程与后台线程可共用。
    /// </summary>
    public sealed class DecorResourceCache : IDisposable
    {
        /// <summary>绘图缓存条目数上限（v4 §3.3 初始值）。</summary>
        public const int DefaultMaxEntries = 128;

        /// <summary>纹理 / 静态装饰位图总字节上限（v4 §3.3 初始值：16 MiB）。</summary>
        public const long DefaultMaxBitmapBytes = 16L * 1024 * 1024;

        private sealed class Entry
        {
            public DecorCacheKey Key;
            public object Value = null!;
            public long Bytes;
            public LinkedListNode<Entry>? Node;
        }

        private readonly object _gate = new();
        private readonly Dictionary<DecorCacheKey, Entry> _map = new();
        private readonly LinkedList<Entry> _lru = new();   // 表头 = 最近使用
        private readonly int _maxEntries;
        private readonly long _maxBitmapBytes;

        private long _totalBytes;
        private bool _disposed;

        // ---- 诊断计数（供探针与验收取证） ----
        private long _hitCount;
        private long _missCount;
        private long _evictionCount;
        private long _createdResourceCount;
        private long _createdBitmapBytes;

        /// <summary>
        /// 创建缓存。
        /// </summary>
        /// <param name="maxEntries">条目数上限（≤0 时用 <see cref="DefaultMaxEntries"/>）。</param>
        /// <param name="maxBitmapBytes">位图总字节上限（≤0 时用 <see cref="DefaultMaxBitmapBytes"/>）。</param>
        public DecorResourceCache(int maxEntries = DefaultMaxEntries, long maxBitmapBytes = DefaultMaxBitmapBytes)
        {
            _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
            _maxBitmapBytes = maxBitmapBytes > 0 ? maxBitmapBytes : DefaultMaxBitmapBytes;
        }

        /// <summary>当前缓存条目数。</summary>
        public int Count { get { lock (_gate) return _map.Count; } }

        /// <summary>当前位图 / 纹理占用的字节总量（估算值）。</summary>
        public long TotalBytes { get { lock (_gate) return _totalBytes; } }

        /// <summary>条目数上限。</summary>
        public int MaxEntries => _maxEntries;

        /// <summary>位图字节上限。</summary>
        public long MaxBitmapBytes => _maxBitmapBytes;

        /// <summary>累计命中次数。</summary>
        public long HitCount { get { lock (_gate) return _hitCount; } }

        /// <summary>累计未命中次数。</summary>
        public long MissCount { get { lock (_gate) return _missCount; } }

        /// <summary>累计淘汰次数。</summary>
        public long EvictionCount { get { lock (_gate) return _evictionCount; } }

        /// <summary>累计创建过的资源个数（位图 / 画刷）。用来证明"不随进度持续增长"。</summary>
        public long CreatedResourceCount { get { lock (_gate) return _createdResourceCount; } }

        /// <summary>累计创建过的位图字节数。</summary>
        public long CreatedBitmapBytes { get { lock (_gate) return _createdBitmapBytes; } }

        // ============================================================
        // 读取
        // ============================================================

        /// <summary>
        /// 尝试取缓存项。<b>返回的是借用引用：不要 Dispose、不要改 Transform。</b>
        /// </summary>
        /// <typeparam name="T">资源类型（位图 / 画刷等）。</typeparam>
        /// <param name="key">缓存键。</param>
        /// <param name="value">命中时为借用引用，未命中时为 null。</param>
        /// <returns>是否命中。</returns>
        public bool TryGet<T>(DecorCacheKey key, out T? value) where T : class
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    value = null;
                    return false;
                }
                if (_map.TryGetValue(key, out Entry? entry))
                {
                    _hitCount++;
                    Touch(entry);
                    value = entry.Value as T;
                    return value != null;
                }
                value = null;
                return false;
            }
        }

        // ============================================================
        // 取或建
        // ============================================================

        /// <summary>
        /// 取缓存项；未命中时用工厂创建并缓存。
        /// <b>返回的是借用引用：不要 Dispose、不要改 Transform。</b>
        /// </summary>
        /// <typeparam name="T">资源类型（必须是可释放的绘图对象）。</typeparam>
        /// <param name="key">缓存键。</param>
        /// <param name="factory">创建工厂（只会在未命中时调用一次）。</param>
        /// <param name="estimatedBytes">字节估算（位图 / 纹理会按实际尺寸重新核算，此值只用于非位图资源）。</param>
        /// <returns>借用引用（缓存拥有它）。</returns>
        public T GetOrAdd<T>(DecorCacheKey key, Func<DecorCacheKey, T> factory, long estimatedBytes = 0) where T : class, IDisposable
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_map.TryGetValue(key, out Entry? found))
                {
                    _hitCount++;
                    Touch(found);
                    return (T)found.Value;
                }

                _missCount++;
                T created = factory(key);
                if (created == null) throw new InvalidOperationException("装饰缓存工厂返回了 null。");

                long bytes = ResolveBytes(created, estimatedBytes);
                _createdResourceCount++;
                _createdBitmapBytes += bytes;

                EvictFor(bytes);

                Entry entry = new Entry { Key = key, Value = created, Bytes = bytes };
                entry.Node = _lru.AddFirst(entry);
                _map[key] = entry;
                _totalBytes += bytes;
                return created;
            }
        }

        /// <summary>
        /// 取或建一个位图（条槽渐变 / 纹理瓦片）。字节按实际尺寸核算。
        /// <b>返回的是借用引用。</b>
        /// </summary>
        /// <param name="key">缓存键。</param>
        /// <param name="factory">创建工厂。</param>
        public Bitmap GetOrAddBitmap(DecorCacheKey key, Func<DecorCacheKey, Bitmap> factory)
        {
            return GetOrAdd(key, factory);
        }

        /// <summary>
        /// 取或建一个纹理画刷。缓存拥有画刷，并负责连同画刷引用的位图一起释放。
        /// <b>返回的是借用引用：不要 Dispose，也不要修改其 Transform。</b>
        /// </summary>
        /// <param name="key">缓存键。</param>
        /// <param name="factory">创建工厂（画刷必须自己创建并独占其位图）。</param>
        public TextureBrush GetOrAddTextureBrush(DecorCacheKey key, Func<DecorCacheKey, TextureBrush> factory)
        {
            return GetOrAdd(key, factory);
        }

        /// <summary>
        /// 取（或建并缓存）一条<b>局部坐标</b>的外框路径。
        /// 缓存拥有路径，<b>借用者不要 Dispose，也不要修改它</b>；
        /// 绘制时用 <c>Graphics.TranslateTransform</c> 把它移到目标位置。
        /// </summary>
        /// <param name="key">缓存键。</param>
        /// <param name="factory">创建工厂。</param>
        /// <returns>借用引用（不要 Dispose）。</returns>
        public GraphicsPath GetOrAddPath(DecorCacheKey key, Func<DecorCacheKey, GraphicsPath> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_map.TryGetValue(key, out Entry? found))
                {
                    _hitCount++;
                    Touch(found);
                    return (GraphicsPath)found.Value;
                }

                _missCount++;
                GraphicsPath created = factory(key);
                if (created == null) throw new InvalidOperationException("装饰缓存工厂返回了 null。");

                _createdResourceCount++;
                EvictFor(0);

                Entry entry = new Entry { Key = key, Value = created, Bytes = 0 };
                entry.Node = _lru.AddFirst(entry);
                _map[key] = entry;
                return created;
            }
        }

        // ============================================================
        // 失效入口（显式 API，不靠猜测）
        // ============================================================

        /// <summary>
        /// <b>主题代次失效</b>：移除主题代次不等于 <paramref name="themeGeneration"/> 的条目。
        /// 换肤后调用一次即可。
        /// </summary>
        /// <param name="themeGeneration">新的主题代次。</param>
        /// <returns>被移除的条目数。</returns>
        public int InvalidateTheme(int themeGeneration)
        {
            lock (_gate)
            {
                if (_disposed) return 0;
                List<Entry> stale = new List<Entry>();
                foreach (Entry e in _map.Values)
                {
                    if (e.Key.ThemeGeneration != themeGeneration) stale.Add(e);
                }
                foreach (Entry e in stale) RemoveEntry(e, countEviction: false);
                return stale.Count;
            }
        }

        /// <summary>
        /// <b>上下文失效（DPI / 用户缩放 / 主题代次）</b>：
        /// 移除主题代次、DPI 或用户缩放与当前不一致的条目。
        /// </summary>
        /// <param name="themeGeneration">当前主题代次。</param>
        /// <param name="deviceDpi">当前窗口实际 DPI。</param>
        /// <param name="userScaleKey">当前用户缩放（千分比）。</param>
        /// <returns>被移除的条目数。</returns>
        public int InvalidateStale(int themeGeneration, int deviceDpi, int userScaleKey)
        {
            lock (_gate)
            {
                if (_disposed) return 0;
                List<Entry> stale = new List<Entry>();
                foreach (Entry e in _map.Values)
                {
                    if (e.Key.ThemeGeneration != themeGeneration
                        || e.Key.DeviceDpi != deviceDpi
                        || e.Key.UserScaleKey != userScaleKey)
                    {
                        stale.Add(e);
                    }
                }
                foreach (Entry e in stale) RemoveEntry(e, countEviction: false);
                return stale.Count;
            }
        }

        /// <summary>
        /// <b>尺寸失效 / 全量失效</b>：清空全部条目。
        /// 窗口尺寸变化后槽位尺寸不再复用，直接全量失效最简单也最可复核。
        /// </summary>
        /// <returns>被移除的条目数。</returns>
        public int InvalidateAll()
        {
            lock (_gate)
            {
                if (_disposed) return 0;
                int n = _map.Count;
                foreach (Entry e in new List<Entry>(_map.Values)) RemoveEntry(e, countEviction: false);
                return n;
            }
        }

        /// <summary>
        /// 立即按上限淘汰（不改变已有条目的新旧顺序语义）。
        /// </summary>
        public void Trim()
        {
            lock (_gate)
            {
                if (_disposed) return;
                while (_map.Count > _maxEntries && _lru.Last != null) RemoveEntry(_lru.Last.Value, countEviction: true);
                while (_totalBytes > _maxBitmapBytes && _lru.Last != null) RemoveEntry(_lru.Last.Value, countEviction: true);
            }
        }

        /// <summary>
        /// 释放全部缓存资源并清空缓存。释放后本对象不可再用。
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (Entry e in new List<Entry>(_map.Values)) RemoveEntry(e, countEviction: false);
                _map.Clear();
                _lru.Clear();
                _totalBytes = 0;
            }
        }

        // ============================================================
        // 内部实现
        // ============================================================

        private void Touch(Entry entry)
        {
            if (entry.Node == null) return;
            _lru.Remove(entry.Node);
            entry.Node = _lru.AddFirst(entry);
        }

        private void EvictFor(long incomingBytes)
        {
            // 1) 条目数上限
            while (_map.Count >= _maxEntries && _lru.Last != null)
            {
                RemoveEntry(_lru.Last.Value, countEviction: true);
            }

            // 2) 字节总量核算
            while (_totalBytes + incomingBytes > _maxBitmapBytes && _lru.Last != null)
            {
                RemoveEntry(_lru.Last.Value, countEviction: true);
            }

            // 若单个条目本身超过总预算，上面已经淘汰到空；
            // 此时允许该条目单独存在（总量暂时超限），
            // 它是最久未使用项，下一次插入必然先被淘汰。
        }

        private void RemoveEntry(Entry entry, bool countEviction)
        {
            _map.Remove(entry.Key);
            if (entry.Node != null) _lru.Remove(entry.Node);
            _totalBytes -= entry.Bytes;
            if (_totalBytes < 0) _totalBytes = 0;
            if (countEviction) _evictionCount++;

            DisposeValue(entry.Value);
        }

        private static void DisposeValue(object? value)
        {
            switch (value)
            {
                case TextureBrush tb:
                    // TextureBrush 释放时不会释放它引用的位图，这里一并处理
                    Image? img = tb.Image;
                    tb.Dispose();
                    img?.Dispose();
                    break;
                case GraphicsPath gp:
                    gp.Dispose();
                    break;
                case IDisposable d:
                    d.Dispose();
                    break;
            }
        }

        private static long ResolveBytes(object value, long estimated)
        {
            switch (value)
            {
                case Bitmap bmp:
                    return (long)bmp.Width * bmp.Height * 4;
                case TextureBrush tb when tb.Image is Image timg:
                    return (long)timg.Width * timg.Height * 4;
                case Image img:
                    return (long)img.Width * img.Height * 4;
                default:
                    return Math.Max(0, estimated);
            }
        }
    }
}
