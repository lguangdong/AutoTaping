// 职责：步骤 6 移动到贴附位——贴附基准 + 纠偏 + R 轴旋转中心补偿后定位 X/Y/R（05 第 4.2 节第 6 步）
// 层：Core/Flow/Steps
// 依赖：IMotionCard（Core/Abstractions）、MoveHelper

using AutoTaping.Core.Abstractions;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>移动到贴附位步骤（5s 超时）。计算有效贴附基准并写入 <see cref="FlowContext"/> 供后续步骤使用。</summary>
public sealed class MoveToTapePosStep : FlowStep
{
    private readonly IMotionCard _motion;

    /// <summary>构造移动贴附位步骤。</summary>
    public MoveToTapePosStep(IMotionCard motion)
    {
        _motion = motion;
    }

    /// <inheritdoc />
    public override string Name => "MoveToTapePos";

    /// <inheritdoc />
    public override int TimeoutMs => 5_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var position = ctx.Recipe.Parameters.Position;
        var process = ctx.Recipe.Parameters.Process;
        var correction = ctx.LastCorrection;

        var targetX = position.TapeBaseX + (correction?.DeltaX ?? 0.0);
        var targetY = position.TapeBaseY + (correction?.DeltaY ?? 0.0);
        var targetR = position.TapeBaseR + (correction?.DeltaTheta ?? 0.0);

        // R 轴旋转中心补偿（04 文档 4.2 节，与 Vision.RotationCompensation 等价，Core 不得引用 Vision 故内联；
        // 补偿量必须基于旋转前的原坐标计算，故先算两个分量再加回）
        var (compX, compY) = Compensate(targetX, targetY,
                                        position.RotationCenterX, position.RotationCenterY, targetR);
        targetX += compX;
        targetY += compY;

        await MoveHelper.MoveAsync(_motion, AxisId.X, targetX, process.MoveSpeedMmPerSec, TimeoutMs, Name, ct);
        await MoveHelper.MoveAsync(_motion, AxisId.Y, targetY, process.MoveSpeedMmPerSec, TimeoutMs, Name, ct);
        await MoveHelper.MoveAsync(_motion, AxisId.R, targetR, process.RotateSpeedDegPerSec, TimeoutMs, Name, ct);

        ctx.CorrectedBaseX = targetX;
        ctx.CorrectedBaseY = targetY;
    }

    /// <summary>计算旋转 θ 后吸嘴绕旋转中心产生的额外偏移（需加到目标位置上的补偿量）。</summary>
    private static (double CompX, double CompY) Compensate(
        double x, double y, double centerX, double centerY, double thetaDeg)
    {
        var t = thetaDeg * Math.PI / 180.0;
        var dx = x - centerX;
        var dy = y - centerY;
        var newX = centerX + dx * Math.Cos(t) - dy * Math.Sin(t);
        var newY = centerY + dx * Math.Sin(t) + dy * Math.Cos(t);
        return (x - newX, y - newY);
    }
}
