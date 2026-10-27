using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteMonitor.Core.Actions.Services
{
    /// <summary>一次设置/主题/场景应用操作的外层状态。模块细节由 <see cref="ModuleApplyResult"/> 承载。</summary>
    public enum ApplyOperationStatus
    {
        Applied,
        Partial,
        Pending,
        Failed,
        Cancelled,
        Unknown,
    }

    /// <summary>
    /// C1 统一应用结果：R3、S04、D01 只读这一层，不各自定义同义模型。
    /// <para>不声称原子回滚；部分成功必须显式表达为 <see cref="ApplyOperationStatus.Partial"/>。</para>
    /// </summary>
    public sealed record ApplyOperationResult(
        string OperationId,
        long RequestRevision,
        long? EffectiveRevision,
        ApplyOperationStatus OverallStatus,
        IReadOnlyList<ModuleApplyResult> Modules,
        string? FailureCode = null)
    {
        /// <summary>由模块结果推导外层状态；模块为空时明确为 Unknown，不伪造成功。</summary>
        public static ApplyOperationResult FromModules(
            string operationId,
            long requestRevision,
            IEnumerable<ModuleApplyResult> modules,
            long? effectiveRevision = null,
            string? failureCode = null)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("operationId 不能为空", nameof(operationId));
            if (modules is null)
                throw new ArgumentNullException(nameof(modules));

            var list = modules.ToList();
            ApplyOperationStatus status;
            if (list.Count == 0)
                status = ApplyOperationStatus.Unknown;
            else if (list.All(m => m.Applied))
                status = ApplyOperationStatus.Applied;
            else if (list.Any(m => m.Applied))
                status = ApplyOperationStatus.Partial;
            else
                status = ApplyOperationStatus.Failed;

            long? effective = effectiveRevision;
            if (!effective.HasValue)
            {
                var revisions = list
                    .Where(m => m.Applied && m.ModuleRevision > 0)
                    .Select(m => m.ModuleRevision)
                    .ToList();
                if (revisions.Count > 0)
                    effective = revisions.Max();
            }

            return new ApplyOperationResult(operationId, requestRevision, effective, status, list, failureCode);
        }
    }
}
