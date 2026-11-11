// 职责：流程步骤抽象——每个步骤有名称、超时、重试次数与执行体（05-核心状态机 第 4.1 节）
// 层：Core/Flow
// 依赖：FlowContext、FlowException（Core/Exceptions）

using AutoTaping.Core.Exceptions;

namespace AutoTaping.Core.Flow;

/// <summary>
/// 自动流程步骤抽象。标准 15 个步骤见 <c>docs/05-核心状态机与流程.md</c> 第 4.2 节；
/// 每个步骤必须带超时，不得无限等待（05 第 8 节约束 2）。
/// </summary>
public abstract class FlowStep
{
    /// <summary>步骤名（用于日志与主画面工位高亮）。</summary>
    public abstract string Name { get; }

    /// <summary>步骤超时（ms），引擎用 <see cref="CancellationTokenSource.CancelAfter"/> 强制中断。</summary>
    public virtual int TimeoutMs => 10_000;

    /// <summary>失败重试次数（引擎在此次数内自动重试，见 05 第 4.3 节）。</summary>
    public int RetryCount { get; init; }

    /// <summary>执行步骤体。失败抛 <see cref="FlowException"/>，被取消抛 <see cref="OperationCanceledException"/>。</summary>
    /// <param name="ctx">流程上下文。</param>
    /// <param name="ct">取消令牌（超时 / 暂停 / 停止 / 急停）。</param>
    public abstract Task ExecuteAsync(FlowContext ctx, CancellationToken ct);
}
