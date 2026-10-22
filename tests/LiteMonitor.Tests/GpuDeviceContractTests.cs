using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Gpu.Device;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.Rendering.Scene;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class GpuDeviceContractTests
{
    private static readonly RenderGenerations Generations = new(1, 2, 3, 4, 5);

    [Fact]
    public void ResourceLeaseReleasesExactlyOnceAndReturnsLedgerToZero()
    {
        GpuResourceLedger ledger = new(byteBudget: 1024, resourceBudget: 4);
        GpuResourceLease lease = ledger.Reserve(
            GpuResourceKind.Texture,
            512,
            "scene-color",
            Generations);

        Assert.Equal(512, ledger.Snapshot().ActiveBytes);
        lease.Dispose();
        lease.Dispose();

        GpuResourceSnapshot snapshot = ledger.Snapshot();
        Assert.Equal(0, snapshot.ActiveResourceCount);
        Assert.Equal(0, snapshot.ActiveBytes);
        Assert.Equal(1, snapshot.TotalReleases);
    }

    [Fact]
    public void ResourceBudgetRejectsWithoutAddingAnEntry()
    {
        GpuResourceLedger ledger = new(byteBudget: 100, resourceBudget: 2);
        using GpuResourceLease first = ledger.Reserve(
            GpuResourceKind.Texture,
            80,
            "first",
            Generations);

        bool accepted = ledger.TryReserve(
            GpuResourceKind.Buffer,
            30,
            "second",
            Generations,
            out GpuResourceLease? second,
            out string? rejection);

        Assert.False(accepted);
        Assert.Null(second);
        Assert.Equal("gpu_resource_byte_budget_exceeded", rejection);
        Assert.Equal(1, ledger.Snapshot().ActiveResourceCount);
    }

    [Fact]
    public void StaleGenerationResourcesCanBeReleasedTogether()
    {
        GpuResourceLedger ledger = new(byteBudget: 2048, resourceBudget: 4);
        using GpuResourceLease current = ledger.Reserve(
            GpuResourceKind.Texture,
            128,
            "current",
            Generations);
        GpuResourceLease stale = ledger.Reserve(
            GpuResourceKind.GlyphCache,
            256,
            "stale-glyphs",
            new RenderGenerations(0, 2, 3, 4, 5));

        Assert.True(ledger.ReleaseGeneration(Generations));
        Assert.True(stale.IsDisposed);
        Assert.False(current.IsDisposed);
        Assert.Equal(128, ledger.Snapshot().ActiveBytes);
    }

    [Fact]
    public void DeviceFailureAllowsOnlyOneAutomaticRecovery()
    {
        GpuDeviceLifecycle lifecycle = new(maximumAutomaticRecoveryAttempts: 1);
        lifecycle.Probe();
        lifecycle.Initialize();
        lifecycle.MarkRunning("adapter");

        Assert.Equal(RenderLifecycleState.Recovering, lifecycle.Fail("device_removed").Current);
        lifecycle.MarkRunning("adapter");
        GpuDeviceTransition failedAgain = lifecycle.Fail("device_removed_again");
        Assert.Equal(RenderLifecycleState.Fallback, failedAgain.Current);
        Assert.Equal(1, failedAgain.RecoveryAttempt);
    }

    [Fact]
    public void DeviceGenerationChangesOnRecreationAndClearsOnStop()
    {
        GpuDeviceLifecycle lifecycle = new();
        lifecycle.Probe();
        lifecycle.Initialize();
        lifecycle.MarkRunning();
        Assert.Equal(1UL, lifecycle.DeviceGeneration);

        lifecycle.Fail("device_removed");
        lifecycle.MarkRunning();
        Assert.Equal(2UL, lifecycle.DeviceGeneration);

        lifecycle.BeginStop();
        lifecycle.CompleteStop();
        Assert.Equal(RenderLifecycleState.Disabled, lifecycle.State);
        Assert.Equal(0UL, lifecycle.DeviceGeneration);
    }

    [Fact]
    public void HardwareDeviceCreatesAndReleasesOffscreenTexture()
    {
        GpuResourceLedger ledger = new(
            byteBudget: 64L * 1024 * 1024,
            resourceBudget: 16);
        bool created = D3D11DeviceService.TryCreate(
            ledger,
            Generations,
            out D3D11DeviceService? service,
            out string? errorCode,
            out string? errorMessage);

        string failure = $"created={created}; code={errorCode ?? "<null>"}; message={errorMessage ?? "<null>"}; service={(service is null ? "null" : "set")}";
        if (!created)
        {
            throw new InvalidOperationException(failure);
        }
        using (service)
        {
            GpuTextureProbeResult probe = service!.ProbeOffscreenTexture(64, 64, Generations);
            Assert.Equal("64x64:B8G8R8A8_UNorm", probe.Description);
            Assert.True(probe.ActiveBytesDuringLease >= 64 * 64 * 4);
            Assert.Equal(2, probe.ActiveResourceCountDuringLease);
            Assert.Throws<InvalidOperationException>(() =>
                Task.Run(service.ProbeOwnershipFromCurrentThread).GetAwaiter().GetResult());
        }

        Assert.Equal(0, ledger.Snapshot().ActiveResourceCount);
        Assert.Equal(0, ledger.Snapshot().ActiveBytes);
    }

    [Fact]
    public void PartialRendererInitializationReleasesInventoryAtEveryStagedFailure()
    {
        using Form form = new();
        form.ClientSize = new Size(320, 180);
        IntPtr handle = form.Handle;
        foreach (GpuRendererInitializationStage stage in Enum.GetValues<GpuRendererInitializationStage>())
        {
            GpuResourceLedger ledger = new(
                byteBudget: 128L * 1024 * 1024,
                resourceBudget: 64);
            bool created = GpuSceneRenderer.TryCreate(
                handle,
                Generations,
                320,
                180,
                MaterialKind.LiquidGlass,
                ledger,
                MaterialRuntimeSettings.Create(
                    null,
                    MaterialKind.LiquidGlass),
                dpi: 96,
                userScale: 1,
                initializationHook: failedStage =>
                {
                    if (failedStage == stage)
                    {
                        throw new InvalidOperationException(
                            $"Injected failure at {stage}.");
                    }
                },
                out GpuSceneRenderer? renderer,
                out string? code,
                out _);

            Assert.False(created);
            Assert.Null(renderer);
            Assert.Equal("gpu_scene_renderer_initialization_failed", code);
            GpuResourceSnapshot snapshot = ledger.Snapshot();
            Assert.Equal(0, snapshot.ActiveResourceCount);
            Assert.Equal(0, snapshot.ActiveBytes);
            Assert.Equal(snapshot.TotalReservations, snapshot.TotalReleases);
        }
    }

    [Fact]
    public void ProductionHostDisposeReturnsEveryLedgerEntryToZero()
    {
        using Form form = new();
        form.ClientSize = new Size(420, 260);
        _ = form.Handle;
        using GpuPresentationHostController host = new(
            form,
            MaterialKind.LiquidGlass,
            liveBackgroundRequested: false);

        Assert.True(host.Start(
            WindowCapturePolicy.None,
            out string? errorCode,
            out string? errorMessage),
            $"{errorCode}: {errorMessage}");
        SceneSnapshot scene = SceneSnapshotBuilder.Build(
            host.Generations,
            420,
            260,
            96,
            [
                new SceneSourceGroup(
                    "CPU",
                    "CPU",
                    new LogicalRect(12, 12, 396, 120),
                    [
                        new SceneSourceMetric(
                            "cpu.load",
                            "CPU",
                            "50",
                            "%",
                            0.5,
                            MetricStatus.Normal,
                            false,
                            [])
                    ])
            ]);
        host.UpdateScene(scene);
        host.RenderNow();
        Assert.True(host.ResourceSnapshot.ActiveResourceCount > 0);

        host.Dispose();

        GpuResourceSnapshot released = host.ResourceSnapshot;
        Assert.Equal(0, released.ActiveResourceCount);
        Assert.Equal(0, released.ActiveBytes);
        Assert.Equal(released.TotalReservations, released.TotalReleases);
    }
}
