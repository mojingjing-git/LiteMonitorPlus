using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class GpuGroupTests
    {
        // ---------- 分组键推导 ----------

        [Theory]
        [InlineData("GPU.0.Load", "GPU.0")]
        [InlineData("GPU.1.Temp", "GPU.1")]
        [InlineData("GPU.7.VRAM", "GPU.7")]
        public void TryGetPerCardGroupKey_带序号的显卡Key_推出按卡分组键(string key, string expected)
        {
            Assert.True(GpuGroupPolicy.TryGetPerCardGroupKey(key, out string group));
            Assert.Equal(expected, group);
        }

        [Theory]
        [InlineData("GPU.Load")]
        [InlineData("CPU.Load")]
        [InlineData("MEM.Load")]
        [InlineData("GPU.")]
        [InlineData("GPU.x.Temp")]
        [InlineData("")]
        [InlineData(null)]
        public void TryGetPerCardGroupKey_非带序号或非法Key_返回false(string key)
        {
            Assert.False(GpuGroupPolicy.TryGetPerCardGroupKey(key, out _));
        }

        [Theory]
        [InlineData("GPU.0", 0)]
        [InlineData("GPU.3", 3)]
        public void TryGetCardIndex_按卡分组键_取回序号(string groupKey, int expected)
        {
            Assert.True(GpuGroupPolicy.TryGetCardIndex(groupKey, out int index));
            Assert.Equal(expected, index);
        }

        [Theory]
        [InlineData("GPU")]
        [InlineData("GPU.0.Load")]
        [InlineData("CPU")]
        [InlineData("HOST")]
        [InlineData("")]
        [InlineData(null)]
        public void TryGetCardIndex_非按卡分组键_返回false(string groupKey)
        {
            Assert.False(GpuGroupPolicy.TryGetCardIndex(groupKey, out _));
        }

        // ---------- UIGroup 按卡拆组 ----------

        [Theory]
        [InlineData("GPU.0.Load", "GPU.0")]
        [InlineData("GPU.1.Temp", "GPU.1")]
        public void UIGroup_带序号的显卡Key_归到按卡分组(string key, string expected)
        {
            Assert.Equal(expected, new MonitorItemConfig { Key = key }.UIGroup);
        }

        [Theory]
        [InlineData("CPU.Load", "CPU")]
        [InlineData("GPU.Load", "GPU")]
        [InlineData("MEM.Load", "HOST")]
        [InlineData("MOBO.Temp", "HOST")]
        [InlineData("DISK.Temp", "HOST")]
        [InlineData("CASE.Fan", "HOST")]
        [InlineData("FPS", "HOST")]
        [InlineData("DASH.HOST", "DASH")]
        public void UIGroup_非显卡Key_保持原分组不变(string key, string expected)
        {
            Assert.Equal(expected, new MonitorItemConfig { Key = key }.UIGroup);
        }

        // ---------- 网页端分组 id 消毒 ----------

        [Theory]
        [InlineData("GPU.0", "GPU_0")]
        [InlineData("GPU.1", "GPU_1")]
        [InlineData("GPU.0.Load", "GPU_0_LOAD")]
        [InlineData("CPU", "CPU")]
        [InlineData("HOST", "HOST")]
        [InlineData("NET", "NET")]
        [InlineData("DASH", "DASH")]
        public void ToSafeGroupId_非法选择器字符被替换成下划线(string groupKey, string expected)
        {
            Assert.Equal(expected, GroupTitleResolver.ToSafeGroupId(groupKey));
        }

        [Fact]
        public void ToSafeGroupId_空输入_返回OTHER()
        {
            Assert.Equal("OTHER", GroupTitleResolver.ToSafeGroupId(""));
            Assert.Equal("OTHER", GroupTitleResolver.ToSafeGroupId(null));
        }

        [Fact]
        public void ToSafeGroupId_是幂等的()
        {
            Assert.Equal("GPU_0", GroupTitleResolver.ToSafeGroupId(GroupTitleResolver.ToSafeGroupId("GPU.0")));
        }

        [Fact]
        public void ToSafeGroupId_结果可安全用作CSS选择器()
        {
            // 网页前端会把它拼进 querySelector('#rw-' + gid)
            string gid = GroupTitleResolver.ToSafeGroupId("GPU.0");
            Assert.DoesNotContain(".", gid);
            Assert.Matches("^[A-Za-z0-9_-]+$", gid);
        }

        // ---------- 标题解析 ----------

        [Fact]
        public void Resolve_有用户别名_返回别名()
        {
            var cfg = new Settings();
            cfg.GroupAliases["GPU.0"] = "我的游戏卡";
            Assert.Equal("我的游戏卡", GroupTitleResolver.Resolve(cfg, "GPU.0"));
        }

        [Fact]
        public void Resolve_无别名_回退到默认标题()
        {
            var cfg = new Settings();
            Assert.Equal(GroupTitleResolver.GetDefaultTitle("GPU.0"),
                         GroupTitleResolver.Resolve(cfg, "GPU.0"));
        }

        [Fact]
        public void Resolve_空别名_回退到默认标题()
        {
            var cfg = new Settings();
            cfg.GroupAliases["GPU.0"] = "";
            Assert.Equal(GroupTitleResolver.GetDefaultTitle("GPU.0"),
                         GroupTitleResolver.Resolve(cfg, "GPU.0"));
        }

        [Fact]
        public void GetDefaultTitle_普通分组_走语言包()
        {
            string title = GroupTitleResolver.GetDefaultTitle("CPU");
            Assert.False(string.IsNullOrEmpty(title));
            Assert.NotEqual("Groups.CPU", title);
        }

        [Fact]
        public void GetDefaultTitle_不受LanguageManager覆盖影响_不会返回用户别名()
        {
            // GroupAliases 会被注入成 "Groups.{key}" 的 LanguageManager override,
            // GetDefaultTitle 必须只取语言包原值, 否则「输入值 == 默认标题」的判定会失效。
            var cfg = new Settings();
            cfg.GroupAliases["CPU"] = "我的CPU";
            LanguageManager.ClearOverrides();
            LanguageManager.SetOverride("Groups.CPU", "我的CPU");
            try
            {
                Assert.NotEqual("我的CPU", GroupTitleResolver.GetDefaultTitle("CPU"));
            }
            finally
            {
                LanguageManager.ClearOverrides();
            }
        }

        // ---------- 真实语言包下的项标签 (拆组后不能再重复显卡型号) ----------

        [Fact]
        public void 加载语言包后_项标签是基础标签_不含显卡型号()
        {
            // 主程序启动时一定会 Load 语言包 (MainForm_Transparent.cs:129),
            // 这里显式加载一次, 验证真实文案。
            LanguageManager.Load("zh");

            Assert.Equal("使用率", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.Load" }));
            Assert.Equal("温度", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.Temp" }));
            Assert.Equal("频率", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.Clock" }));
            Assert.Equal("功耗", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.Power" }));
            Assert.Equal("风扇", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.Fan" }));
            Assert.Equal("显存占用", MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = "GPU.0.VRAM" }));
        }

        [Fact]
        public void 加载语言包后_项标签不含显卡型号前缀()
        {
            LanguageManager.Load("zh");
            foreach (var key in new[] { "GPU.0.Load", "GPU.0.Temp", "GPU.0.Clock", "GPU.0.Power", "GPU.0.Fan", "GPU.0.VRAM" })
            {
                string label = MetricLabelResolver.ResolveLabel(new MonitorItemConfig { Key = key });
                Assert.DoesNotContain("GPU", label);
                Assert.False(label.StartsWith("."));
            }
        }

        // ---------- 标题随硬件初始化就绪而刷新 (面板标题是启动快照) ----------

        [Fact]
        public void 显卡简称就绪后_按卡分组默认标题会从序号变成简称()
        {
            // 面板的分组标题在 UIController 构造时 (BuildMetrics) 就缓存好了,
            // 那时硬件还没初始化完, 拿不到简称 -> 只能退回 "GPU.0"。
            // 硬件异步初始化完成后, UIController.RefreshPerCardGroupTitles() 会就地刷新,
            // 所以解析函数必须能随着 GpuRegistry 就绪而给出不同的标题。
            GpuRegistry.Clear();
            try
            {
                Assert.Equal("GPU.0", GroupTitleResolver.GetDefaultTitle("GPU.0"));

                GpuRegistry.ReplaceAll(new[]
                {
                    new GpuInfo { ShortName = "3080 Ti", FullName = "NVIDIA GeForce RTX 3080 Ti" }
                });

                Assert.Equal("3080 Ti", GroupTitleResolver.GetDefaultTitle("GPU.0"));
            }
            finally
            {
                GpuRegistry.Clear();
            }
        }

        [Fact]
        public void 用户别名优先于显卡简称()
        {
            GpuRegistry.Clear();
            try
            {
                GpuRegistry.ReplaceAll(new[]
                {
                    new GpuInfo { ShortName = "3080 Ti", FullName = "NVIDIA GeForce RTX 3080 Ti" }
                });
                var cfg = new Settings();
                cfg.GroupAliases["GPU.0"] = "游戏卡";
                Assert.Equal("游戏卡", GroupTitleResolver.Resolve(cfg, "GPU.0"));
            }
            finally
            {
                GpuRegistry.Clear();
            }
        }

        // ---------- 旧版整组别名的迁移 ----------

        [Fact]
        public void 旧版整组别名_迁移到0号卡并删除旧键()
        {
            var s = new Settings { MonitoredGpuCount = 2 };
            s.GroupAliases["GPU"] = "我的游戏卡";

            GpuItemSync.Ensure(s, availableGpuCount: 2);

            Assert.Equal("我的游戏卡", s.GroupAliases["GPU.0"]);
            Assert.False(s.GroupAliases.ContainsKey("GPU"));
        }

        [Fact]
        public void 旧版整组别名_不覆盖已有的按卡别名()
        {
            var s = new Settings { MonitoredGpuCount = 2 };
            s.GroupAliases["GPU"] = "旧名";
            s.GroupAliases["GPU.0"] = "新名";

            GpuItemSync.Ensure(s, availableGpuCount: 2);

            Assert.Equal("新名", s.GroupAliases["GPU.0"]);
            Assert.False(s.GroupAliases.ContainsKey("GPU"));
        }

        [Fact]
        public void 旧版整组别名迁移是幂等的()
        {
            var s = new Settings { MonitoredGpuCount = 2 };
            s.GroupAliases["GPU"] = "我的游戏卡";

            GpuItemSync.Ensure(s, availableGpuCount: 2);
            GpuItemSync.Ensure(s, availableGpuCount: 2);

            Assert.Equal("我的游戏卡", s.GroupAliases["GPU.0"]);
            Assert.False(s.GroupAliases.ContainsKey("GPU"));
        }
        // ---------- 旧式自动名的识别与迁移 ----------

        [Fact]
        public void BuildLegacyAutoLabel_显卡项_返回旧式的自动标签()
        {
            GpuRegistry.Clear();
            try
            {
                GpuRegistry.ReplaceAll(new[]
                {
                    new GpuInfo { ShortName = "3080 Ti", FullName = "NVIDIA GeForce RTX 3080 Ti" }
                });
                LanguageManager.Load("zh");

                // 旧行为下显卡项的自动标签就是 "<显卡简称> <指标名>"
                Assert.Equal("3080 Ti 使用率", MetricLabelResolver.BuildLegacyAutoLabel(new MonitorItemConfig { Key = "GPU.0.Load" }));
                Assert.Equal("3080 Ti 温度", MetricLabelResolver.BuildLegacyAutoLabel(new MonitorItemConfig { Key = "GPU.0.Temp" }));
                Assert.Equal("3080 Ti 显存占用", MetricLabelResolver.BuildLegacyAutoLabel(new MonitorItemConfig { Key = "GPU.0.VRAM" }));
            }
            finally
            {
                GpuRegistry.Clear();
            }
        }

        [Fact]
        public void BuildLegacyAutoLabel_非显卡项或简称未就绪_返回空串()
        {
            GpuRegistry.Clear();
            try
            {
                Assert.Equal("", MetricLabelResolver.BuildLegacyAutoLabel(new MonitorItemConfig { Key = "CPU.Load" }));
                // 简称还没就绪 -> 构造不出旧式自动名
                Assert.Equal("", MetricLabelResolver.BuildLegacyAutoLabel(new MonitorItemConfig { Key = "GPU.0.Load" }));
            }
            finally
            {
                GpuRegistry.Clear();
            }
        }

        [Fact]
        public void 旧式自动名_不会盖过新的分组小标题机制()
        {
            GpuRegistry.Clear();
            try
            {
                GpuRegistry.ReplaceAll(new[]
                {
                    new GpuInfo { ShortName = "3080 Ti", FullName = "NVIDIA GeForce RTX 3080 Ti" }
                });
                LanguageManager.Load("zh");

                // 没有 UserLabel 时, 拆组后行标签就是纯指标名 (显卡型号由组标题承担)
                var item = new MonitorItemConfig { Key = "GPU.0.Temp" };
                Assert.Equal("温度", MetricLabelResolver.ResolveLabel(item));

                // 一旦被固化成旧式自动名, 迁移必须能认出来
                item.UserLabel = MetricLabelResolver.BuildLegacyAutoLabel(item);
                Assert.Equal("3080 Ti 温度", item.UserLabel);
                Assert.Equal("3080 Ti 温度", MetricLabelResolver.BuildLegacyAutoLabel(item));
            }
            finally
            {
                GpuRegistry.Clear();
            }
        }
    }
}
