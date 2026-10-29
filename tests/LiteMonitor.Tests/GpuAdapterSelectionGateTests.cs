using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// GPU 渲染适配器选择护栏。
    /// <para>背景：混合显卡机器上，窗口所在显示器可能只接在核显上，而 D3D11CreateDevice
    /// 传 null 适配器时用的是 DXGI 默认适配器（通常是独显）⇒ 交换链无法呈现到该屏、
    /// Desktop Duplication 也抓不到该屏。用户观感是"切液态玻璃没反应，
    /// 把窗口拖到另一块显示器才正常"。</para>
    /// </summary>
    public sealed class GpuAdapterSelectionGateTests
    {
        [Fact]
        public void 护栏_适配器选择器必须提供按显示器选适配器的能力()
        {
            Type? selector = typeof(LiteMonitor.Rendering.Presentation.GpuSceneRenderer).Assembly
                .GetType("LiteMonitor.Rendering.Gpu.Device.GpuAdapterSelector");
            Assert.NotNull(selector);

            MethodInfo? select = selector!.GetMethod(
                "SelectAdapterForMonitor",
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo? covers = selector.GetMethod(
                "AdapterCoversMonitor",
                BindingFlags.Public | BindingFlags.Static);

            Assert.NotNull(select);
            Assert.NotNull(covers);
            Assert.Equal(typeof(IntPtr), select!.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void 护栏_渲染器创建时必须按窗口所在显示器选适配器()
        {
            MethodInfo create = typeof(LiteMonitor.Rendering.Presentation.GpuSceneRenderer).GetMethod(
                "TryCreate",
                BindingFlags.Public | BindingFlags.Static)!;

            var calls = IlInspector.Disassemble(create).Instructions
                .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                .Select(i => (i.Target as MethodInfo)?.Name)
                .Where(n => n is not null)
                .ToList();

            Assert.Contains("SelectAdapterForMonitor", calls!);
        }

        [Fact]
        public void 护栏_设备服务必须支持显式指定适配器()
        {
            MethodInfo tryCreate = typeof(LiteMonitor.Rendering.Gpu.Device.D3D11DeviceService).GetMethod(
                "TryCreate",
                BindingFlags.Public | BindingFlags.Static)!;

            ParameterInfo[] parameters = tryCreate.GetParameters();
            ParameterInfo? adapterOverride = parameters.FirstOrDefault(
                p => p.ParameterType.Name.Contains("Adapter"));

            Assert.NotNull(adapterOverride);
            Assert.True(adapterOverride!.HasDefaultValue, "显式适配器必须是可选参数（未命中时回退默认适配器）。");
        }

        [Fact]
        public void 护栏_渲染路线页必须仍在设置导航中()
        {
            string formCode = File.ReadAllText(RepoFile("src", "UI", "SettingsForm.cs"));
            Assert.Contains("new RenderingRoutePage()", formCode);
            Assert.Contains("Menu.RenderingRoute", formCode);
        }

        private static string RepoFile(params string[] parts)
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new FileNotFoundException(string.Join("/", parts));
        }
    }
}
