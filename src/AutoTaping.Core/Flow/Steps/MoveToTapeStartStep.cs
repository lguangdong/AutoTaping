// 职责：步骤 9 移动到贴附起点——有效贴附基准 + 路径首点偏移（05 第 4.2 节第 9 步）
// 层：Core/Flow/Steps
// 依赖：IMotionCard（Core/Abstractions）、MoveHelper

using AutoTaping.Core.Abstractions;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>移动到贴附起点步骤（3s 超时）。</summary>
public sealed class MoveToTapeStartStep : FlowStep
{
    private readonly IMotionCard _motion;

    /// <summary>构造移动贴附起点步骤。</summary>
    public MoveToTapeStartStep(IMotionCard motion)
    {
        _motion = motion;
    }

    /// <inheritdoc />
    public override string Name => "MoveToTapeStart";

    /// <inheritdoc />
    public override int TimeoutMs => 3_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var tapePath = ctx.Recipe.Parameters.TapePath;
        var speed = ctx.Recipe.Parameters.Process.TapeSpeedMmPerSec;
        var start = tapePath.Points[0];

        await MoveHelper.MoveAsync(_motion, AxisId.X, ctx.CorrectedBaseX + start.X, speed, TimeoutMs, Name, ct);
        await MoveHelper.MoveAsync(_motion, AxisId.Y, ctx.CorrectedBaseY + start.Y, speed, TimeoutMs, Name, ct);
    }
}
