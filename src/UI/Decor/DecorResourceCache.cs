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
    /// - <b>纹理画刷</b>：缓存<b>同时拥有源位图与画刷</b>（画刷不复制源位图，
    ///   两者必须同生共死），淘汰时一起释放；
    ///   字节按调用方<b>显式给出</b>的瓦片尺寸核算（不取画刷的 <c>Image</c>，
    ///   每取一次都会得到一个新的副本，取了不释放就是多留一份 GDI 图像）。
    ///
    /// ★ 异常安全：纹理画刷的"源位图 → 画刷 → 入表"是一条
    ///   <b>要么全成、要么全废</b>的链：入表成功之前，待接管的位图与画刷
    ///   由当次调用兜底释放；只有入表成功才提交所有权与字节计数，
    ///   失败时缓存计数保持调用前的状态。
    ///
    /// ★ 容量与淘汰策略（可复核）：
    /// - 条目上限 <see cref="DefaultMaxEntries"/> = 128；
    /// - 位图 / 静态装饰位图总字节上限 <see cref="DefaultMaxBitmapBytes"/> = 16 MiB；
    /// - 淘汰规则 = <b>LRU + 总量核算</b>：按"最久未使用"顺序淘汰，
    ///   直到条目数不超过上限且总字节不超过字节上限；
    ///   位图 / 路径条目在<b>插入前</b>淘汰，纹理画刷条目在<b>入表成功后</b>淘汰
    ///   （资源没准备好就不淘汰旧条目），两者的最终口径一致；
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
            /// <summary>
            /// 条目<b>额外拥有</b>的资源（纹理画刷的源位图）。
            /// TextureBrush <b>不会复制</b>源位图，源位图必须比画刷活得久，
            /// 所以它由缓存一起持有、一起释放。
            /// </summary>
            public IDisposable? Owned;
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
        private long _releasedResourceCount;

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

        /// <summary>
        /// 累计释放过的<b>缓存条目</b>数。
        /// 用来证明"创建过的每一个条目都有确定的释放点"——
        /// 淘汰或释放缓存后它应当与 <see cref="CreatedResourceCount"/> 相等
        /// （纹理条目的画刷与源位图是一起释放的）。
        /// </summary>
        public long ReleasedResourceCount { get { lock (_gate) return _releasedResourceCount; } }

        // ============================================================
        // 内部测试接缝（internal：生产路径上没有可被外部随意改写的公开开关）
        // ============================================================

        /// <summary>
        /// 内部测试接缝：<b>从源位图创建纹理画刷</b>的入口。
        /// 默认走 <c>new TextureBrush(source)</c>。
        ///
        /// ★ 用途：GDI+ 的画刷构造失败几乎无法自然复现，
        ///   这里给出<b>可注入</b>的创建入口，让测试能在
        ///   <b>不靠耗尽内存</b>的前提下稳定触发"画刷构造失败"这条异常路径。
        /// ★ 可见性：保持 <c>internal</c>；测试工程通过
        ///   <c>InternalsVisibleTo</c>（或反射）使用它，
        ///   生产路径上<b>没有</b>可被外部随意改写的公开开关。
        /// </summary>
        internal Func<Bitmap, TextureBrush>? TextureBrushFactory { get; set; }

        /// <summary>
        /// 内部测试接缝：纹理画刷<b>入表前</b>的钩子（默认不触发）。
        ///
        /// ★ 用途：在"画刷已创建、条目尚未入表"这一步注入异常，
        ///   用来验证失败路径会把<b>已创建的画刷与源位图一起释放</b>。
        /// ★ 可见性：保持 <c>internal</c>（同 <see cref="TextureBrushFactory"/>）。
        /// </summary>
        internal Action? TextureBrushPreInsertHook { get; set; }

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
        /// 取或建一个纹理画刷。缓存<b>同时拥有源位图与画刷</b>，淘汰时两者一起释放。
        /// <b>返回的是借用引用：不要 Dispose，也不要修改其 Transform。</b>
        ///
        /// ★ 所有权合同（v4 §3.3 的补充）：
        /// - 工厂只负责<b>产出源位图</b>（所有权随即转移给缓存，工厂不要再释放它）；
        ///   画刷由缓存创建，<b>TextureBrush 不会复制源位图</b>，
        ///   源位图必须与画刷同生共死，所以两者都由缓存持有；
        /// - 字节按<b>显式给出的瓦片尺寸</b>核算，<b>不取画刷的 Image</b>——
        ///   每取一次都会得到一个<b>新的副本</b>，取了不释放就是白留一份 GDI 图像；
        /// - 淘汰时释放的就是缓存自己创建的那一个画刷和它自己的那一张源位图，
        ///   <b>记账与释放指向同一份图像</b>。
        ///
        /// ★ 异常安全（所有权交接）：
        /// - 源位图从工厂接手后，到<b>入表成功</b>之前由本次调用兜底：
        ///   画刷构造失败、入表失败等任何异常，都会在 finally 里把
        ///   <b>已创建的位图 / 画刷释放掉</b>，不让它们脱离缓存管理；
        /// - <b>只有入表成功才提交所有权与字节计数</b>；失败时缓存的条目数、
        ///   字节数、创建数与淘汰数都保持<b>调用前</b>的状态；
        /// - 淘汰发生在<b>入表之后</b>：资源没准备好就不会白白淘汰掉旧条目。
        /// </summary>
        /// <param name="key">缓存键。</param>
        /// <param name="tileFactory">瓦片位图工厂（位图的所有权转移给缓存）。</param>
        /// <param name="tileWidthPx">瓦片宽度（物理像素，用于字节核算）。</param>
        /// <param name="tileHeightPx">瓦片高度（物理像素，用于字节核算）。</param>
        public TextureBrush GetOrAddTextureBrush(DecorCacheKey key, Func<DecorCacheKey, Bitmap> tileFactory, int tileWidthPx, int tileHeightPx)
        {
            if (tileFactory == null) throw new ArgumentNullException(nameof(tileFactory));

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_map.TryGetValue(key, out Entry? found))
                {
                    _hitCount++;
                    Touch(found);
                    return (TextureBrush)found.Value;
                }

                _missCount++;

                // 源位图的所有权在此转移给缓存
                Bitmap source = tileFactory(key);
                if (source == null) throw new InvalidOperationException("装饰缓存工厂返回了 null。");

                // ★ 异常安全（所有权交接的"要么全成、要么全废"）：
                //   1) 待接管的源位图与已创建的画刷，在**入表成功之前**由本次调用兜底；
                //   2) 只有**入表成功**才提交所有权与字节计数；
                //   3) 失败时 finally 释放已创建的位图 / 画刷，缓存计数回到调用前的状态。
                TextureBrush? brush = null;
                bool committed = false;
                try
                {
                    // 画刷与源位图同属缓存：淘汰时一起释放
                    brush = CreateTextureBrush(source);
                    if (brush == null) throw new InvalidOperationException("装饰缓存的纹理画刷工厂返回了 null。");

                    // 入表前的故障注入钩子（仅测试接缝，默认不触发）
                    TextureBrushPreInsertHook?.Invoke();

                    long bytes = (long)Math.Max(1, tileWidthPx) * Math.Max(1, tileHeightPx) * 4;

                    Entry entry = new Entry { Key = key, Value = brush, Owned = source, Bytes = bytes };

                    // 入表：先挂 LRU 再挂字典；任一步失败都要把半成品摘掉
                    LinkedListNode<Entry> node = _lru.AddFirst(entry);
                    entry.Node = node;
                    try
                    {
                        _map[key] = entry;
                    }
                    catch
                    {
                        _lru.Remove(node);
                        entry.Node = null;
                        throw;
                    }

                    // ★ 只有入表成功才提交记账（失败时计数保持调用前的状态）
                    _totalBytes += bytes;
                    _createdResourceCount++;
                    _createdBitmapBytes += bytes;
                    committed = true;

                    // ★ 淘汰放在入表之后：资源没准备好就不会白白淘汰掉旧条目
                    EvictAfterInsert(entry);

                    return brush;
                }
                finally
                {
                    if (!committed)
                    {
                        // 未入表：本次创建的画刷与源位图由本次调用释放，不让它们脱离管理
                        SafeDispose(brush);
                        SafeDispose(source);
                    }
                }
            }
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

        /// <summary>
        /// <b>入表之后</b>的淘汰：新条目已经在表内，按"最久未使用"顺序淘汰旧条目，
        /// 直到条目数与位图字节总量回到上限内。
        ///
        /// ★ 与 <see cref="EvictFor"/> 的差别：核算口径已<b>包含新条目</b>
        ///   （条目数按表内实际数量、字节按已含新条目的总量），
        ///   因此<b>永不淘汰新条目本身</b>；
        /// ★ 好处：资源还没准备好就失败时，旧条目不会被白白淘汰掉。
        /// </summary>
        /// <param name="keep">刚入表的新条目（不淘汰它）。</param>
        private void EvictAfterInsert(Entry keep)
        {
            // 1) 条目数上限
            while (_map.Count > _maxEntries && _lru.Last != null && !ReferenceEquals(_lru.Last.Value, keep))
            {
                RemoveEntry(_lru.Last.Value, countEviction: true);
            }

            // 2) 字节总量核算
            while (_totalBytes > _maxBitmapBytes && _lru.Last != null && !ReferenceEquals(_lru.Last.Value, keep))
            {
                RemoveEntry(_lru.Last.Value, countEviction: true);
            }

            // 若单个条目本身超过总预算，上面已经淘汰到只剩它自己；
            // 此时允许它单独存在（总量暂时超限），
            // 它是最久未使用项，下一次插入必然先被淘汰。
        }

        /// <summary>
        /// 按当前接缝创建纹理画刷（默认 <c>new TextureBrush(source)</c>）。
        /// </summary>
        /// <param name="source">源位图（所有权已转移给缓存）。</param>
        private TextureBrush CreateTextureBrush(Bitmap source)
        {
            Func<Bitmap, TextureBrush>? factory = TextureBrushFactory;
            return factory != null ? factory(source) : new TextureBrush(source);
        }

        /// <summary>
        /// 安全释放：释放失败不能盖掉正在处理的原始异常。
        /// </summary>
        /// <param name="resource">待释放对象（可为 null）。</param>
        private static void SafeDispose(object? resource)
        {
            if (resource is not IDisposable disposable) return;
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // 释放失败不再抛出：此刻正在清理失败路径，不能盖掉原始异常
            }
        }

        private void RemoveEntry(Entry entry, bool countEviction)
        {
            _map.Remove(entry.Key);
            if (entry.Node != null) _lru.Remove(entry.Node);
            _totalBytes -= entry.Bytes;
            if (_totalBytes < 0) _totalBytes = 0;
            if (countEviction) _evictionCount++;

            DisposeValue(entry.Value, entry.Owned);
            _releasedResourceCount++;
        }

        private static void DisposeValue(object? value, IDisposable? owned)
        {
            try
            {
                switch (value)
                {
                    case TextureBrush tb:
                        // ★ 画刷不复制源位图：源位图由缓存一起持有（entry.Owned），
                        //   两者都在这里释放。
                        //   这里不再取 tb.Image：每取一次都会得到一个**新的副本**，
                        //   取了不释放就等于多留一份 GDI 图像（旧实现就是这么漏的）。
                        tb.Dispose();
                        break;
                    case GraphicsPath gp:
                        gp.Dispose();
                        break;
                    case IDisposable d:
                        d.Dispose();
                        break;
                }
            }
            finally
            {
                owned?.Dispose();
            }
        }

        private static long ResolveBytes(object value, long estimated)
        {
            switch (value)
            {
                case Bitmap bmp:
                    return (long)bmp.Width * bmp.Height * 4;
                case TextureBrush:
                    // 纹理画刷走 <see cref="GetOrAddTextureBrush"/>（按显式瓦片尺寸核算）。
                    // 兜底分支也**不取 tb.Image**（每取一次都是一个新的副本，
                    // 取了不释放就是泄漏），直接用核算值。
                    return Math.Max(0, estimated);
                case Image img:
                    return (long)img.Width * img.Height * 4;
                default:
                    return Math.Max(0, estimated);
            }
        }
    }
}
