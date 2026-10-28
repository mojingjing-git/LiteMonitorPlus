using System;
using System.Reflection;
using System.Reflection.Emit;
using LiteMonitor.Rendering.Materials;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 工具栏固定背景的**尺寸档位 + 缩放**护栏。
    /// <para>背景：逐像素程序化生成原本是「每次尺寸变化都重算」，
    /// 在高 DPI 大面板上会把 UI 线程阻塞数百毫秒 ⇒ 改缩放时窗口来不及改高度，
    /// 面板只显示顶部一条（用户报障）。现在改成「等比面积上限的档位 + 双线性缩放」。</para>
    /// </summary>
    public sealed class ToolbarPreviewSurfaceScalingTests
    {
        private static MethodInfo BuildMethod =>
            typeof(ToolbarPreviewSurface).GetMethod(
                "Build",
                BindingFlags.NonPublic | BindingFlags.Static)!;

        private static MethodInfo ScaleMethod =>
            typeof(ToolbarPreviewSurface).GetMethod(
                "Scale",
                BindingFlags.NonPublic | BindingFlags.Static)!;

        private static MethodInfo BucketSizeMethod =>
            typeof(ToolbarPreviewSurface).GetMethod(
                "BucketSize",
                BindingFlags.NonPublic | BindingFlags.Static)!;

        private static MethodInfo ExpNegMethod =>
            typeof(ToolbarPreviewSurface).GetMethod(
                "ExpNeg",
                BindingFlags.NonPublic | BindingFlags.Static)!;

        private static ToolbarPreviewSurface InvokeSurface(MethodInfo method, params object[] args)
        {
            object? result = method.Invoke(null, args);
            return (ToolbarPreviewSurface)result!;
        }

        [Fact]
        public void 查表指数_与真实指数的相对误差不超过千分之五()
        {
            for (int step = 0; step <= 118; step++)
            {
                float z = step * 0.1f;
                float actual = (float)ExpNegMethod.Invoke(null, new object[] { z })!;
                float expected = MathF.Exp(-z);
                if (expected < 1e-6f)
                {
                    continue;
                }

                float relativeError = MathF.Abs(actual - expected) / expected;
                Assert.True(
                    relativeError < 0.005f,
                    $"exp(-{z}) 查表误差 {relativeError:P3} 超限（实际 {actual}，真实 {expected}）。");
            }
        }

        [Fact]
        public void 查表指数_非正自变量返回一_超上界返回零()
        {
            Assert.Equal(1f, (float)ExpNegMethod.Invoke(null, new object[] { 0f })!, 5);
            Assert.Equal(1f, (float)ExpNegMethod.Invoke(null, new object[] { -3f })!, 5);
            Assert.Equal(0f, (float)ExpNegMethod.Invoke(null, new object[] { 64f })!, 5);
        }

        [Theory]
        [InlineData(432, 1289)]
        [InlineData(800, 3000)]
        [InlineData(1280, 900)]
        [InlineData(360, 1083)]
        public void 尺寸档位_等比且面积受限(int width, int height)
        {
            object? result = BucketSizeMethod.Invoke(null, new object[] { width, height })!;
            int bucketWidth = (int)result.GetType().GetField("Item1")!.GetValue(result)!;
            int bucketHeight = (int)result.GetType().GetField("Item2")!.GetValue(result)!;

            Assert.True(bucketWidth >= 1 && bucketHeight >= 1);
            Assert.True(
                (long)bucketWidth * bucketHeight <= 256L * 256L,
                $"档位面积 {(long)bucketWidth * bucketHeight} 超出上限。");

            double sourceRatio = (double)width / height;
            double bucketRatio = (double)bucketWidth / bucketHeight;
            Assert.True(
                Math.Abs(sourceRatio - bucketRatio) / sourceRatio < 0.02,
                $"档位长宽比 {bucketRatio:F4} 与请求 {sourceRatio:F4} 偏差过大（等比才能保证图案不被拉伸）。");
        }

        [Fact]
        public void 缩放_输出尺寸精确且像素非空()
        {
            ToolbarPreviewSurface source = InvokeSurface(BuildMethod, 64, 64);
            ToolbarPreviewSurface scaled = InvokeSurface(ScaleMethod, source, 128, 96);

            Assert.Equal(128, scaled.Width);
            Assert.Equal(96, scaled.Height);
            Assert.Equal(128 * 96 * 4, scaled.Pixels.Length);

            bool anyNonZero = false;
            for (int i = 0; i < scaled.Pixels.Length; i++)
            {
                if (scaled.Pixels[i] != 0)
                {
                    anyNonZero = true;
                    break;
                }
            }

            Assert.True(anyNonZero, "缩放结果不能是全零（否则固定背景会变成纯黑）。");
        }

        [Fact]
        public void 生成_不得再直接调用超越函数Exp()
        {
            IlInspector.Disassembly body = IlInspector.Disassemble(BuildMethod);

            foreach (IlInspector.IlInstruction instruction in body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                {
                    continue;
                }

                if (instruction.Target is not MethodInfo called)
                {
                    continue;
                }

                Assert.False(
                    called.Name == "Exp" && called.DeclaringType?.Name.Contains("MathF") == true,
                    "逐像素生成里不能再直接调用 MathF.Exp（会重新引入 UI 线程卡顿），必须走查表。");
            }
        }

        [Fact]
        public void 生成_仍能产出与直接实现一致的亮度量级()
        {
            // 守卫：查表替换不能把画面改成纯色或纯黑（视觉回归的粗筛）。
            ToolbarPreviewSurface surface = InvokeSurface(BuildMethod, 96, 96);
            int min = 255;
            int max = 0;
            for (int i = 0; i < surface.Pixels.Length; i += 4)
            {
                int value = surface.Pixels[i];
                if (value < min) min = value;
                if (value > max) max = value;
            }

            Assert.True(max - min > 12, $"生成结果几乎是纯色（min={min}, max={max}），视觉可能已损坏。");
        }
    }
}
