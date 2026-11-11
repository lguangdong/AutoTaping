// 职责：步骤 11 保压——按配方保压时间延时（05 第 4.2 节第 11 步）
// 层：Core/Flow/Steps
// 依赖：无

namespace AutoTaping.Core.Flow.Steps;

/// <summary>保压步骤（超时按配方保压时间，无失败）。</summary>
public sealed class HoldPressureStep : FlowStep
{
    /// <inheritdoc />
    public override string Name => "HoldPressure";

    /// <inheritdoc />
    public override int TimeoutMs => 5_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var holdMs = ctx.Recipe.Parameters.Process.HoldPressureMs;
        await Task.Delay(holdMs, ct);
    }
}
