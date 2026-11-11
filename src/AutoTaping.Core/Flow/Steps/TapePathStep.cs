// 职责：步骤 10 走贴附路径——从路径第 2 点起依次定位 X/Y（Z 随动到路径点高度）（05 第 4.2 节第 10 步）
// 层：Core/Flow/Steps
// 依赖：IMotionCard（Core/Abstractions）、MoveHelper

using AutoTaping.Core.Abstractions;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>走贴附路径步骤（8s 超时）。默认直线路径两点（首点已在 MoveToTapeStart 到位），本步骤走后续点。</summary>
public sealed class TapePathStep : FlowStep
{
    private readonly IMotionCard _motion;

    /// <summary>构造贴附路径步骤。</summary>
    public TapePathStep(IMotionCard motion)
    {
        _motion = motion;
    }

    /// <inheritdoc />
    public override string Name => "TapePath";

    /// <inheritdoc />
    public override int TimeoutMs => 8_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var tapePath = ctx.Recipe.Parameters.TapePath;
        var speed = ctx.Recipe.Parameters.Process.TapeSpeedMmPerSec;

        for (var i = 1; i < tapePath.Points.Count; i++)
        {
            var point = tapePath.Points[i];

            await MoveHelper.MoveAsync(_motion, AxisId.X, ctx.CorrectedBaseX + point.X, speed, TimeoutMs, Name, ct);
            await MoveHelper.MoveAsync(_motion, AxisId.Y, ctx.CorrectedBaseY + point.Y, speed, TimeoutMs, Name, ct);
            await MoveHelper.MoveAsync(_motion, AxisId.Z, point.Z, speed, TimeoutMs, Name, ct);
        }
    }
}
