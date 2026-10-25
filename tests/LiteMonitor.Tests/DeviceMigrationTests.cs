using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [F07] 旧配置 → 新稳定身份的**迁移夹具**测试。
    ///
    /// <para><b>要守住的</b>（母计划 F07 第 5 条）：</para>
    /// <list type="bullet">
    ///   <item>只有**唯一匹配**才迁移；</item>
    ///   <item>同名歧义 → 保留旧值 + 提示，<b>绝不随机选第一块</b>；</item>
    ///   <item>找不到 → 保留旧值 + 提示；</item>
    ///   <item>幂等：已经是新格式时原样返回。</item>
    /// </list>
    /// </summary>
    public class DeviceMigrationTests
    {
        private static DeviceOption Option(string label, string value, string name, string runtimeId = "")
        {
            return new DeviceOption
            {
                Label = label,
                Value = value,
                Name = name,
                Kind = DeviceKind.Storage,
                RuntimeId = runtimeId,
                StableId = value.StartsWith("pnp:", StringComparison.Ordinal) ? value : "",
            };
        }

        [Fact]
        public void 空值_无需迁移()
        {
            DeviceMigrationResult result = DeviceMigration.Migrate("", Array.Empty<DeviceOption>(), DeviceKind.Storage);

            Assert.Equal(DeviceMigrationOutcome.NotNeeded, result.Outcome);
            Assert.False(result.RequiresUserChoice);
            Assert.Equal("", result.ValueToPersist);
        }

        [Fact]
        public void 旧名称唯一匹配_迁移到稳定ID()
        {
            var options = new List<DeviceOption>
            {
                Option("Samsung SSD 980 PRO 1TB", "pnp:SCSI\\DISK\\A", "Samsung SSD 980 PRO 1TB"),
                Option("WD SN850X 2TB", "pnp:SCSI\\DISK\\B", "WD SN850X 2TB"),
            };

            DeviceMigrationResult result = DeviceMigration.Migrate("WD SN850X 2TB", options, DeviceKind.Storage);

            Assert.Equal(DeviceMigrationOutcome.MigratedUnique, result.Outcome);
            Assert.Equal("pnp:SCSI\\DISK\\B", result.MigratedValue);
            Assert.Equal("pnp:SCSI\\DISK\\B", result.ValueToPersist);
            Assert.False(result.RequiresUserChoice);
        }

        [Fact]
        public void 同名歧义_保留旧值并提示_不选第一块()
        {
            var options = new List<DeviceOption>
            {
                Option("Samsung SSD 980 PRO 1TB #1", "pnp:SCSI\\DISK\\A", "Samsung SSD 980 PRO 1TB"),
                Option("Samsung SSD 980 PRO 1TB #2", "pnp:SCSI\\DISK\\B", "Samsung SSD 980 PRO 1TB"),
            };

            DeviceMigrationResult result = DeviceMigration.Migrate("Samsung SSD 980 PRO 1TB", options, DeviceKind.Storage);

            // ★ 歧义 ⇒ 保留旧值 + 提示，**不**迁移到第一块。
            Assert.Equal(DeviceMigrationOutcome.AmbiguousPreserved, result.Outcome);
            Assert.Equal("Samsung SSD 980 PRO 1TB", result.ValueToPersist);
            Assert.True(result.RequiresUserChoice);
            Assert.False(string.IsNullOrWhiteSpace(result.Prompt));
            Assert.Equal(2, result.Candidates.Count);
            // 提示里必须能看到两个候选，用户才知道要选哪个。
            Assert.Contains("#1", result.Prompt, StringComparison.Ordinal);
            Assert.Contains("#2", result.Prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void 找不到_保留旧值并提示()
        {
            var options = new List<DeviceOption>
            {
                Option("WD SN850X 2TB", "pnp:SCSI\\DISK\\B", "WD SN850X 2TB"),
            };

            DeviceMigrationResult result = DeviceMigration.Migrate("E: 老盘", options, DeviceKind.Storage);

            Assert.Equal(DeviceMigrationOutcome.UnresolvedPreserved, result.Outcome);
            Assert.Equal("E: 老盘", result.ValueToPersist);
            Assert.True(result.RequiresUserChoice);
            Assert.False(string.IsNullOrWhiteSpace(result.Prompt));
        }

        [Fact]
        public void 已经是稳定ID_幂等不重复迁移()
        {
            var options = new List<DeviceOption>
            {
                Option("Samsung SSD 980 PRO 1TB #1", "pnp:SCSI\\DISK\\A", "Samsung SSD 980 PRO 1TB"),
            };

            DeviceMigrationResult first = DeviceMigration.Migrate("pnp:SCSI\\DISK\\A", options, DeviceKind.Storage);
            DeviceMigrationResult second = DeviceMigration.Migrate(first.ValueToPersist, options, DeviceKind.Storage);

            Assert.Equal(DeviceMigrationOutcome.AlreadyCurrent, first.Outcome);
            Assert.Equal("pnp:SCSI\\DISK\\A", first.ValueToPersist);
            Assert.Equal(DeviceMigrationOutcome.AlreadyCurrent, second.Outcome);
            Assert.Equal("pnp:SCSI\\DISK\\A", second.ValueToPersist);
        }

        [Fact]
        public void 旧LHM标识唯一命中_迁移()
        {
            // 旧配置存的是 LHM Identifier（显卡是 "/gpu-nvidia/{序号}"）。
            var options = new List<DeviceOption>
            {
                Option("NVIDIA GeForce RTX 3080", "pnp:PCI\\VEN_10DE\\A", "NVIDIA GeForce RTX 3080", "/gpu-nvidia/0"),
                Option("NVIDIA GeForce RTX 3080 #2", "pnp:PCI\\VEN_10DE\\B", "NVIDIA GeForce RTX 3080", "/gpu-nvidia/1"),
            };

            DeviceMigrationResult result = DeviceMigration.Migrate("/gpu-nvidia/1", options, DeviceKind.Gpu);

            Assert.Equal(DeviceMigrationOutcome.MigratedUnique, result.Outcome);
            Assert.Equal("pnp:PCI\\VEN_10DE\\B", result.MigratedValue);
        }

        [Fact]
        public void 旧LHM标识命中多个_歧义保留()
        {
            // LHM 0.9.6 的存储 Identifier 只是媒体类型（"/nvme"）⇒ 所有 NVMe 盘共用同一个值。
            var options = new List<DeviceOption>
            {
                Option("Samsung SSD 980 PRO 1TB #1", "pnp:SCSI\\DISK\\A", "Samsung SSD 980 PRO 1TB", "/nvme|\\\\.\\PhysicalDrive0"),
                Option("Samsung SSD 980 PRO 1TB #2", "pnp:SCSI\\DISK\\B", "Samsung SSD 980 PRO 1TB", "/nvme|\\\\.\\PhysicalDrive1"),
            };

            // 旧值是 "/nvme"（媒体类型）⇒ 命中两块盘 ⇒ 歧义，必须保留旧值。
            var legacyOptions = options
                .Select(o => new DeviceOption
                {
                    Label = o.Label,
                    Value = o.Value,
                    Name = o.Name,
                    Kind = o.Kind,
                    RuntimeId = "/nvme",
                    StableId = o.StableId,
                })
                .ToList();

            DeviceMigrationResult result = DeviceMigration.Migrate("/nvme", legacyOptions, DeviceKind.Storage);

            Assert.Equal(DeviceMigrationOutcome.AmbiguousPreserved, result.Outcome);
            Assert.Equal("/nvme", result.ValueToPersist);
            Assert.Equal(2, result.Candidates.Count);
        }

        [Fact]
        public void 迁移后原值不再被当成候选_可重复执行()
        {
            var options = new List<DeviceOption>
            {
                Option("Samsung SSD 980 PRO 1TB #1", "pnp:SCSI\\DISK\\A", "Samsung SSD 980 PRO 1TB"),
                Option("Samsung SSD 980 PRO 1TB #2", "pnp:SCSI\\DISK\\B", "Samsung SSD 980 PRO 1TB"),
            };

            // 第一次：歧义 ⇒ 保留旧值。
            DeviceMigrationResult first = DeviceMigration.Migrate("Samsung SSD 980 PRO 1TB", options, DeviceKind.Storage);
            Assert.Equal(DeviceMigrationOutcome.AmbiguousPreserved, first.Outcome);

            // 第二次（重复加载）：结论一致 ⇒ 幂等，不会"漂移"成别的结果。
            DeviceMigrationResult second = DeviceMigration.Migrate(first.ValueToPersist, options, DeviceKind.Storage);
            Assert.Equal(first.Outcome, second.Outcome);
            Assert.Equal(first.ValueToPersist, second.ValueToPersist);
        }
    }
}
