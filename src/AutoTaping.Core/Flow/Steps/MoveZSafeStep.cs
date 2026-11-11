// 职责：步骤 13 Z 抬到安全高度（05 第 4.2 节第 13 步）
// 层：Core/Flow/Steps
// 依赖：IMotionCard（Core/Abstractions）、MoveHelper

using AutoTaping.Core.Abstractions;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>Z 抬到安全高度步骤（3s 超时）。</summary>
public sealed class MoveZSafeStep : FlowStep
{
    private readonly IMotionCard _motion;

    /// <summary>构造 Z 抬升步骤。</summary>
    public MoveZSafeStep(IMotionCard motion)
    {
        _motion = motion;
    }

    /// <inheritdoc />
    public override string Name => "MoveZSafe";

    /// <inheritdoc />
    public override int TimeoutMs => 3_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var safeHeight = ctx.Recipe.Parameters.Process.SafeHeightMm;
        var speed = ctx.Recipe.Parameters.Process.MoveSpeedMmPerSec;

        await MoveHelper.MoveAsync(_motion, AxisId.Z, safeHeight, speed, TimeoutMs, Name, ct);
    }
}
