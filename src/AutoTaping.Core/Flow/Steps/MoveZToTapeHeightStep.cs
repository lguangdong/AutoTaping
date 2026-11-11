// 职责：步骤 7 Z 下降到贴附高度（05 第 4.2 节第 7 步）
// 层：Core/Flow/Steps
// 依赖：IMotionCard（Core/Abstractions）、MoveHelper

using AutoTaping.Core.Abstractions;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>Z 下降到贴附高度步骤（3s 超时）。</summary>
public sealed class MoveZToTapeHeightStep : FlowStep
{
    private readonly IMotionCard _motion;

    /// <summary>构造 Z 下降步骤。</summary>
    public MoveZToTapeHeightStep(IMotionCard motion)
    {
        _motion = motion;
    }

    /// <inheritdoc />
    public override string Name => "MoveZToTapeHeight";

    /// <inheritdoc />
    public override int TimeoutMs => 3_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var tapeHeight = ctx.Recipe.Parameters.Process.TapeHeightMm;
        var speed = ctx.Recipe.Parameters.Process.MoveSpeedMmPerSec;

        await MoveHelper.MoveAsync(_motion, AxisId.Z, tapeHeight, speed, TimeoutMs, Name, ct);
    }
}
