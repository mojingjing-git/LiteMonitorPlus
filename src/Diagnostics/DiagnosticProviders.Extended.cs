using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiteMonitor.Contracts;
using LiteMonitor.Core.Actions.Services;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI.SettingsPage;
using LiteMonitor.src.WebServer;

namespace LiteMonitor.Diagnostics;

/// <summary>渲染路线事实：只读 UIController 已发布属性，不在诊断路径创建 GPU 资源。</summary>
public sealed class RenderingDiagnosticProvider : IDiagnosticProvider
{
    private readonly Func<RenderingRouteStatusSnapshot> _snapshot;
    private readonly Func<DateTimeOffset> _clock;

    public RenderingDiagnosticProvider(Func<RenderingRouteStatusSnapshot> snapshot, Func<DateTimeOffset>? clock = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Module => "rendering";

    public DiagnosticModuleSnapshot Read()
    {
        RenderingRouteStatusSnapshot s = _snapshot();
        IReadOnlyList<RenderingRouteStatusLine> lines = RenderingRouteStatusBuilder.Build(s);
        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["themeUsesGpuMaterial"] = s.ThemeUsesGpuMaterial.ToString(),
            ["gpuEffectsEnabled"] = s.GpuEffectsEnabled.ToString(),
            ["presentationActive"] = s.PresentationActive.ToString(),
            ["liveBackgroundActive"] = s.LiveBackgroundActive.ToString(),
            ["liveBackgroundRequested"] = s.LiveBackgroundRequested.ToString(),
            ["windowIsLayered"] = s.WindowIsLayered.ToString(),
            ["routeLines"] = string.Join(",", lines.Select(l => l.Kind.ToString())),
        };
        if (!string.IsNullOrWhiteSpace(s.FailureOrFallbackCode)) facts["failureOrFallbackCode"] = s.FailureOrFallbackCode!;

        bool warning = lines.Any(l =>
            l.Kind is RenderingRouteStatusKind.NotStarted or RenderingRouteStatusKind.LiveBackgroundUnavailable);
        string summary = lines.Count == 0 ? "no route state" : string.Join("; ", lines.Select(l => l.Kind));
        string? reason = warning ? s.FailureOrFallbackCode ?? "rendering_not_active" : null;
        return new DiagnosticModuleSnapshot(
            Module, warning ? DiagnosticModuleState.Warning : DiagnosticModuleState.Ok,
            reason, summary, _clock(), facts);
    }
}

/// <summary>Web 模块事实：只读服务状态与会话计数，不暴露密码/令牌/请求内容。</summary>
public sealed class WebDiagnosticProvider : IDiagnosticProvider
{
    private readonly Func<bool> _enabled;
    private readonly Func<LiteWebServer?> _server;
    private readonly Func<DateTimeOffset> _clock;

    public WebDiagnosticProvider(Func<bool> enabled, Func<LiteWebServer?> server, Func<DateTimeOffset>? clock = null)
    {
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Module => "web";

    public DiagnosticModuleSnapshot Read()
    {
        bool enabled = _enabled();
        LiteWebServer? server = _server();
        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = enabled.ToString(),
            ["running"] = (server?.IsRunning ?? false).ToString(),
        };
        if (server is not null)
        {
            facts["port"] = server.CurrentRunningPort.ToString(CultureInfo.InvariantCulture);
            facts["authenticatedConnections"] = server.AuthenticatedConnectionCount.ToString(CultureInfo.InvariantCulture);
            facts["hasWebDataDemand"] = server.HasWebDataDemand.ToString();
        }

        if (!enabled)
            return new DiagnosticModuleSnapshot(Module, DiagnosticModuleState.Ok, "disabled", "Web server disabled", _clock(), facts);
        if (server is null || !server.IsRunning)
            return new DiagnosticModuleSnapshot(Module, DiagnosticModuleState.Warning, "not_running", "Web server enabled but not running", _clock(), facts);
        return new DiagnosticModuleSnapshot(Module, DiagnosticModuleState.Ok, null, "Web server running", _clock(), facts);
    }
}

/// <summary>插件模块事实：只暴露实例计数，不读取凭据/输入值；单实例健康尚未仪表化。</summary>
public sealed class PluginDiagnosticProvider : IDiagnosticProvider
{
    private readonly Func<int> _configured;
    private readonly Func<int> _enabled;
    private readonly Func<DateTimeOffset> _clock;

    public PluginDiagnosticProvider(Func<int> configured, Func<int> enabled, Func<DateTimeOffset>? clock = null)
    {
        _configured = configured ?? throw new ArgumentNullException(nameof(configured));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Module => "plugins";

    public DiagnosticModuleSnapshot Read()
    {
        int configured = _configured();
        int enabled = _enabled();
        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["configuredInstances"] = configured.ToString(CultureInfo.InvariantCulture),
            ["enabledInstances"] = enabled.ToString(CultureInfo.InvariantCulture),
        };
        if (configured == 0)
            return new DiagnosticModuleSnapshot(Module, DiagnosticModuleState.Ok, "no_instances", "No plugin instances configured", _clock(), facts);
        if (enabled == 0)
            return new DiagnosticModuleSnapshot(Module, DiagnosticModuleState.Ok, "no_enabled_instances", "No enabled plugin instances", _clock(), facts);
        return new DiagnosticModuleSnapshot(Module, DiagnosticModuleState.Warning, "health_not_instrumented",
            "Plugin instances enabled; per-instance health is not exposed", _clock(), facts);
    }
}

/// <summary>IPC 模块事实：当前只声明传输健康未仪表化，不伪造可用性。</summary>
public sealed class IpcDiagnosticProvider : IDiagnosticProvider
{
    private readonly Func<DateTimeOffset> _clock;

    public IpcDiagnosticProvider(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Module => "ipc";

    public DiagnosticModuleSnapshot Read()
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["healthInstrumented"] = "false",
        };
        return new DiagnosticModuleSnapshot(Module, DiagnosticModuleState.Unknown, "health_not_instrumented",
            "IPC transport health is not instrumented", _clock(), facts);
    }
}
