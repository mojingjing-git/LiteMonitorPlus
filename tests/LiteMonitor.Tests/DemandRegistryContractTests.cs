using LiteMonitor.Contracts;
using LiteMonitor.SystemServices;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class DemandRegistryContractTests
{
    [Fact]
    public void C3_旧五布尔入口保留通道语义()
    {
        var registry = new DemandRegistry();

        Assert.True(registry.Submit("ui", new SamplingDemand(true, false, false, false, false)));
        Assert.True(registry.Submit("web", new SamplingDemand(false, false, true, false, false)));

        var aggregate = registry.Aggregate;
        Assert.True(aggregate.Ui);
        Assert.True(aggregate.Web);
        Assert.False(aggregate.Taskbar);
        Assert.Equal(2, registry.ConsumerCount);
    }

    [Fact]
    public void C3_聚合最大间隔取活跃消费者最小正值并下限1000()
    {
        var registry = new DemandRegistry();
        Assert.True(registry.Submit(new ConsumerSamplingDemand("ui", true, 500, true, "ui", SamplingChannels.Ui)));
        Assert.True(registry.Submit(new ConsumerSamplingDemand("history", true, 30000, true, "history", SamplingChannels.History)));
        Assert.True(registry.Submit(new ConsumerSamplingDemand("inactive", false, 1000, false, "closed", SamplingChannels.Web)));

        Assert.Equal(1000, registry.AggregateMaxIntervalMs);
        Assert.True(registry.Aggregate.Ui);
        Assert.True(registry.Aggregate.History);
        Assert.False(registry.Aggregate.Web);
    }

    [Fact]
    public void C3_不可降级与释放语义()
    {
        var registry = new DemandRegistry();
        Assert.True(registry.Submit(new ConsumerSamplingDemand("alert", true, 5000, false, "alert", SamplingChannels.SensorWindow)));

        Assert.True(registry.HasNonDegradable);
        Assert.Equal(5000, registry.AggregateMaxIntervalMs);
        Assert.True(registry.Release("alert"));
        Assert.False(registry.HasNonDegradable);
        Assert.Equal(0, registry.AggregateMaxIntervalMs);
        Assert.False(registry.Release("alert"));
    }

    [Fact]
    public void C3_拒绝空消费者与负间隔()
    {
        var registry = new DemandRegistry();

        Assert.False(registry.Submit(new ConsumerSamplingDemand("", true, 1000, true, "bad")));
        Assert.False(registry.Submit(new ConsumerSamplingDemand("ui", true, -1, true, "bad")));
        Assert.Equal(0, registry.ConsumerCount);
    }
}
