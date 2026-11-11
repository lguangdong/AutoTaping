// 职责：步骤 14 移动到下料位（05 第 4.2 节第 14 步）
// 层：Core/Flow/Steps
// 依赖：IMotionCard（Core/Abstractions）、MoveHelper

using AutoTaping.Core.Abstractions;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>移动到下料位步骤（5s 超时）。</summary>
public sealed class MoveToUnloadPosStep : FlowStep
{
    private readonly IMotionCard _motion;

    /// <summary>构造移动下料位步骤。</summary>
    public MoveToUnloadPosStep(IMotionCard motion)
    {
        _motion = motion;
    }

    /// <inheritdoc />
    public override string Name => "MoveToUnloadPos";

    /// <inheritdoc />
    public override int TimeoutMs => 5_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var position = ctx.Recipe.Parameters.Position;
        var speed = ctx.Recipe.Parameters.Process.MoveSpeedMmPerSec;

        await MoveHelper.MoveAsync(_motion, AxisId.X, position.UnloadX, speed, TimeoutMs, Name, ct);
        await MoveHelper.MoveAsync(_motion, AxisId.Y, position.UnloadY, speed, TimeoutMs, Name, ct);
    }
}
