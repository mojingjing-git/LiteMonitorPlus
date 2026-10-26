using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiteMonitor.Contracts;

namespace LiteMonitor.SystemServices.Pipe;

/// <summary>
/// E02 命令面线格式（客户端侧）。与 <c>src/HardwareHost/HostCommandProtocol.cs</c>
/// （Host 侧同名契约）**必须保持一致**：两处各自持有同构的 JSON 选项与 DTO，
/// 以便在不触碰 Contracts（集成人独占）的前提下演进协议次版本。
/// <para>约定：参数 JSON 用 camelCase（对齐 IpcMessages 注释的
/// <c>{ "consumerId": string, "demand": SamplingDemand }</c>）；
/// 读取一律大小写不敏感（容忍 PascalCase 写法）；结果 DTO 的字段名以 IpcMessages
/// 注释为准（HardwareOperationResult 的 Accepted/OperationId/State/FailureReason）。</para>
/// </summary>
internal static class PipeCommandJson
{
    /// <summary>
    /// 读写共用选项：大小写不敏感（camelCase/PascalCase 都能解析）+ 写出用 camelCase
    /// （与 IpcMessages 注释的 {"triggered": bool} 形状一致）。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>按消费者登记需求的命令参数（IpcMessages: submit-demand-v2）。</summary>
internal sealed record SubmitDemandArgs(string ConsumerId, SamplingDemand Demand);

/// <summary>释放需求的命令参数（IpcMessages: release-demand）。</summary>
internal sealed record ReleaseDemandArgs(string ConsumerId);

/// <summary>查询操作状态的命令参数（IpcMessages: operation-status）。</summary>
internal sealed record OperationStatusArgs(string OperationId);

/// <summary>到期采样的结果（IpcMessages: sampling-due → { "triggered": bool }）。</summary>
internal sealed record SamplingDueResult(bool Triggered);

/// <summary>需求登记结果（submit-demand-v2 → { "registered": bool }）。</summary>
internal sealed record DemandRegisterResult(bool Registered);

/// <summary>需求释放结果（release-demand → { "released": bool }）。</summary>
internal sealed record DemandReleaseResult(bool Released);
