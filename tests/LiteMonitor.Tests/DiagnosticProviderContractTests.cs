using System;
using System.Collections.Generic;
using LiteMonitor.Contracts;
using LiteMonitor.Core.Actions.Services;
using LiteMonitor.Diagnostics;
using LiteMonitor.src.UI.SettingsPage;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class DiagnosticProviderContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HardwareProvider_MapsReadyAndCounts()
    {
        var snapshot = new HardwareSnapshot(
            42, 7, 3,
            new[]
            {
                new MetricSample("cpu.load", "cpu", 12.5f, "%", SampleQuality.Good, null, 120),
                new MetricSample("gpu.0.temp", "gpu", null, "C", SampleQuality.Missing, "sensor-missing", 900),
            },
            TimeSpan.FromMilliseconds(18.5), 1000, 0);
        var provider = new HardwareDiagnosticProvider(
            () => new HardwareHealth(HardwareState.Ready, "collect", 9, null),
            () => snapshot,
            clock: () => Now);

        DiagnosticModuleSnapshot result = provider.Read();

        Assert.Equal("sampling", result.Module);
        Assert.Equal(DiagnosticModuleState.Ok, result.State);
        Assert.Null(result.ReasonCode);
        Assert.Equal(Now, result.ObservedAtUtc);
        Assert.Equal("42", result.Facts["batchId"]);
        Assert.Equal("1", result.Facts["good"]);
        Assert.Equal("1", result.Facts["missing"]);
        Assert.Equal("900", result.Facts["maxSampleAgeMs"]);
    }

    [Fact]
    public void HardwareProvider_EmptySnapshotIsWarning()
    {
        var provider = new HardwareDiagnosticProvider(
            () => new HardwareHealth(HardwareState.Ready, null, 9, null),
            () => new HardwareSnapshot(1, 1, 1, Array.Empty<MetricSample>(), TimeSpan.Zero, 0, 0),
            clock: () => Now);

        DiagnosticModuleSnapshot result = provider.Read();

        Assert.Equal(DiagnosticModuleState.Warning, result.State);
        Assert.Equal("snapshot-empty", result.ReasonCode);
    }

    [Fact]
    public void ApplyProvider_NoOperationIsUnknown()
    {
        var provider = new ApplyResultDiagnosticProvider(() => null, () => Now);

        DiagnosticModuleSnapshot result = provider.Read();

        Assert.Equal(DiagnosticModuleState.Unknown, result.State);
        Assert.Equal("no-apply-operation", result.ReasonCode);
    }

    [Fact]
    public void ApplyProvider_PartialIsWarningAndKeepsModuleFacts()
    {
        var operation = ApplyOperationResult.FromModules(
            "op-1", 5,
            new[]
            {
                ModuleApplyResult.Ok("Appearance", effective: "theme-a"),
                ModuleApplyResult.Fail("Taskbar", "saved", "old", "taskbar-rebuild-failed", retryable: true),
            });
        var provider = new ApplyResultDiagnosticProvider(() => operation, () => Now);

        DiagnosticModuleSnapshot result = provider.Read();

        Assert.Equal(DiagnosticModuleState.Warning, result.State);
        Assert.Equal("apply-partial", result.ReasonCode);
        Assert.Equal("Partial", result.Facts["overallStatus"]);
        Assert.Equal("applied:theme-a", result.Facts["module.Appearance"]);
        Assert.Equal("failed:taskbar-rebuild-failed", result.Facts["module.Taskbar"]);
    }

    [Fact]
    public void Builder_IsolatesFailingProvider()
    {
        var builder = new DiagnosticSnapshotBuilder(
            new IDiagnosticProvider[]
            {
                new ThrowingProvider(),
                new DelegateDiagnosticProvider("web", () => new DiagnosticModuleSnapshot(
                    "web", DiagnosticModuleState.Ok, null, "running", Now,
                    new Dictionary<string, string>())),
            },
            () => Now);

        DiagnosticSnapshot snapshot = builder.Build();

        Assert.Equal(2, snapshot.Modules.Count);
        Assert.Equal(DiagnosticModuleState.Unknown, snapshot.Find("broken")!.State);
        Assert.Equal("provider-failed", snapshot.Find("broken")!.ReasonCode);
        Assert.Equal(DiagnosticModuleState.Ok, snapshot.Find("web")!.State);
    }

    [Fact]
    public void RenderingProvider_GpuRunningIsOk()
    {
        var provider = new RenderingDiagnosticProvider(
            () => new RenderingRouteStatusSnapshot(
                ThemeUsesGpuMaterial: true,
                GpuEffectsEnabled: true,
                PresentationActive: true,
                LiveBackgroundActive: false,
                LiveBackgroundRequested: false,
                RuntimeFailure: null,
                FallbackCode: null,
                WindowIsLayered: false),
            () => Now);

        DiagnosticModuleSnapshot result = provider.Read();

        Assert.Equal("rendering", result.Module);
        Assert.Equal(DiagnosticModuleState.Ok, result.State);
        Assert.Null(result.ReasonCode);
        Assert.Contains("GpuRunning", result.Facts["routeLines"]);
    }

    [Fact]
    public void RenderingProvider_NotStartedIsWarningWithReason()
    {
        var provider = new RenderingDiagnosticProvider(
            () => new RenderingRouteStatusSnapshot(
                ThemeUsesGpuMaterial: true,
                GpuEffectsEnabled: true,
                PresentationActive: false,
                LiveBackgroundActive: false,
                LiveBackgroundRequested: false,
                RuntimeFailure: "gpu-device-lost",
                FallbackCode: null,
                WindowIsLayered: false),
            () => Now);

        DiagnosticModuleSnapshot result = provider.Read();

        Assert.Equal(DiagnosticModuleState.Warning, result.State);
        Assert.Equal("gpu-device-lost", result.ReasonCode);
        Assert.Contains("NotStarted", result.Facts["routeLines"]);
    }

    [Fact]
    public void WebProvider_DisabledIsOkAndDoesNotRequireServer()
    {
        var provider = new WebDiagnosticProvider(() => false, () => null, () => Now);

        DiagnosticModuleSnapshot result = provider.Read();

        Assert.Equal("web", result.Module);
        Assert.Equal(DiagnosticModuleState.Ok, result.State);
        Assert.Equal("disabled", result.ReasonCode);
        Assert.Equal("False", result.Facts["running"]);
    }

    [Fact]
    public void WebProvider_EnabledButMissingServerIsWarning()
    {
        var provider = new WebDiagnosticProvider(() => true, () => null, () => Now);

        DiagnosticModuleSnapshot result = provider.Read();

        Assert.Equal(DiagnosticModuleState.Warning, result.State);
        Assert.Equal("not_running", result.ReasonCode);
    }

    [Fact]
    public void Hub_RecordsEventWithSession()
    {
        var hub = new DiagnosticHub(sessionId: "session-1");

        bool added = hub.Record("sampling", DiagnosticSeverity.Warning, "stale", "op-1",
            new Dictionary<string, string> { ["revision"] = "3" });

        Assert.True(added);
        DiagnosticEvent evt = Assert.Single(hub.Store.Snapshot());
        Assert.Equal("session-1", evt.SessionId);
        Assert.Equal("sampling", evt.Module);
        Assert.Equal("stale", evt.ReasonCode);
        Assert.Equal("3", evt.Context!["revision"]);
    }

    [Fact]
    public void PluginProvider_EnabledInstanceIsHonestWarning()
    {
        var provider = new PluginDiagnosticProvider(() => 2, () => 1, () => Now);

        DiagnosticModuleSnapshot result = provider.Read();

        Assert.Equal("plugins", result.Module);
        Assert.Equal(DiagnosticModuleState.Warning, result.State);
        Assert.Equal("health_not_instrumented", result.ReasonCode);
        Assert.Equal("2", result.Facts["configuredInstances"]);
        Assert.Equal("1", result.Facts["enabledInstances"]);
    }

    [Fact]
    public void IpcProvider_IsUnknownUntilInstrumented()
    {
        var provider = new IpcDiagnosticProvider(() => Now);

        DiagnosticModuleSnapshot result = provider.Read();

        Assert.Equal("ipc", result.Module);
        Assert.Equal(DiagnosticModuleState.Unknown, result.State);
        Assert.Equal("health_not_instrumented", result.ReasonCode);
        Assert.Equal("false", result.Facts["healthInstrumented"]);
    }

    private sealed class ThrowingProvider : IDiagnosticProvider
    {
        public string Module => "broken";
        public DiagnosticModuleSnapshot Read() => throw new InvalidOperationException("boom");
    }
}
