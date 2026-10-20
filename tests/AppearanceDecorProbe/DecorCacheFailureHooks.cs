using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using LiteMonitor.src.UI.Decor;

namespace LiteMonitor.AppearanceDecorProbe
{
    /// <summary>
    /// 装饰缓存的<b>故障注入</b>与<b>释放取证</b>辅助件。
    ///
    /// ★ 为什么用反射安装接缝：主工程把接缝收为 <c>internal</c>
    ///   （生产路径上没有可被外部随意改写的公开开关），
    ///   而本探针工程当前没有 <c>InternalsVisibleTo</c> 授权，
    ///   因此这里用反射安装；主工程补上
    ///   <c>&lt;InternalsVisibleTo Include="AppearanceDecorProbe" /&gt;</c>
    ///   之后可以直接调用同一批成员，接缝名字保持不变。
    /// ★ 释放取证不依赖"句柄计数"这类脆弱证据：
    ///   位图用"释放后访问抛异常"判定，画刷用"释放后画不出内容 / 抛异常"判定，
    ///   并且都带一条<b>注入前仍然存活</b>的对照，证明判定本身有效。
    /// </summary>
    internal static class DecorCacheFailureHooks
    {
        /// <summary>
        /// 给缓存安装一个 <c>internal</c> 测试接缝（属性名见主工程 <c>DecorResourceCache</c>）。
        /// </summary>
        /// <typeparam name="T">接缝的委托类型。</typeparam>
        /// <param name="cache">目标缓存。</param>
        /// <param name="propertyName">接缝属性名。</param>
        /// <param name="value">要写入的值（null 表示恢复默认行为）。</param>
        /// <param name="error">安装失败的原因（成功时为空）。</param>
        /// <returns>是否安装成功。</returns>
        public static bool TryInstall<T>(DecorResourceCache cache, string propertyName, T? value, out string error)
        {
            error = "";

            PropertyInfo? property = typeof(DecorResourceCache).GetProperty(
                propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null)
            {
                error = $"DecorResourceCache 上找不到测试接缝 {propertyName}"
                      + "（主工程 csproj 需要为本探针程序集开放 InternalsVisibleTo）。";
                return false;
            }

            try
            {
                property.SetValue(cache, value);
                return true;
            }
            catch (Exception ex)
            {
                error = $"安装测试接缝 {propertyName} 失败：{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 位图是否<b>仍然存活</b>（未被释放）：访问尺寸不抛异常即为存活。
        /// </summary>
        /// <param name="bitmap">待判定的位图（可为 null）。</param>
        /// <returns>true = 仍然存活。</returns>
        public static bool IsAlive(Bitmap? bitmap)
        {
            if (bitmap == null) return false;
            try
            {
                _ = bitmap.Width;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 位图是否<b>已经被释放</b>：释放后再访问会抛异常
        /// （.NET 8 的 System.Drawing 抛 <c>ArgumentException</c>，
        /// 其他版本可能是 <c>ObjectDisposedException</c>，两者都算"已释放"）。
        /// </summary>
        /// <param name="bitmap">待判定的位图（可为 null）。</param>
        /// <param name="evidence">取证说明（写进证据 JSON）。</param>
        /// <returns>true = 已释放。</returns>
        public static bool IsReleased(Bitmap? bitmap, out string evidence)
        {
            if (bitmap == null)
            {
                evidence = "位图为 null（根本没有创建出来）";
                return false;
            }

            try
            {
                int width = bitmap.Width;
                evidence = $"访问 Width 得到 {width}，没有抛异常（位图仍然存活）";
                return false;
            }
            catch (ArgumentException)
            {
                evidence = "访问 Width 抛 ArgumentException（位图已释放）";
                return true;
            }
            catch (ObjectDisposedException)
            {
                evidence = "访问 Width 抛 ObjectDisposedException（位图已释放）";
                return true;
            }
            catch (Exception ex)
            {
                evidence = $"访问 Width 抛 {ex.GetType().Name}（位图已失效）";
                return true;
            }
        }

        /// <summary>
        /// 画刷是否<b>仍然存活</b>：能画出像素即为存活。
        /// </summary>
        /// <param name="brush">待判定的画刷（可为 null）。</param>
        /// <returns>true = 仍然存活。</returns>
        public static bool BrushIsAlive(TextureBrush? brush)
        {
            return PaintedPixels(brush) > 0;
        }

        /// <summary>
        /// 画刷是否<b>已经被释放</b>：释放后再用它填充会抛异常，
        /// 或者画不出任何像素。
        /// </summary>
        /// <param name="brush">待判定的画刷（可为 null）。</param>
        /// <param name="evidence">取证说明（写进证据 JSON）。</param>
        /// <returns>true = 已释放。</returns>
        public static bool IsReleased(TextureBrush? brush, out string evidence)
        {
            if (brush == null)
            {
                evidence = "画刷为 null（根本没有创建出来）";
                return false;
            }

            int painted = PaintedPixels(brush);
            if (painted < 0)
            {
                evidence = "用画刷填充时抛异常（画刷已释放）";
                return true;
            }
            if (painted == 0)
            {
                evidence = "用画刷填充画不出任何像素（画刷已释放）";
                return true;
            }
            evidence = $"用画刷填充画出 {painted} 个像素（画刷仍然存活）";
            return false;
        }

        /// <summary>
        /// 用画刷填充一块画布，统计画出的像素数。
        /// </summary>
        /// <param name="brush">画刷（可为 null）。</param>
        /// <returns>画出的像素数；抛异常时返回 -1（表示画刷已失效）。</returns>
        private static int PaintedPixels(TextureBrush? brush)
        {
            if (brush == null) return 0;

            try
            {
                using Bitmap canvas = new Bitmap(8, 8, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);
                    g.FillRectangle(brush, new Rectangle(0, 0, 8, 8));
                }

                int painted = 0;
                for (int y = 0; y < canvas.Height; y++)
                {
                    for (int x = 0; x < canvas.Width; x++)
                    {
                        if (canvas.GetPixel(x, y).A >= 1) painted++;
                    }
                }
                return painted;
            }
            catch
            {
                return -1;
            }
        }
    }
}
