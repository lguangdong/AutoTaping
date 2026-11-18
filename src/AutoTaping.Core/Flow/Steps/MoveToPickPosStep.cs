// 职责：步骤 5 移动到料盘取料位——X/Y 依次定位到配方的取料位（05 第 4.2 节第 5 步）
// 层：Core/Flow/Steps
// 依赖：IMotionCard（Core/Abstractions）、MoveHelper

using AutoTaping.Core.Abstractions;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>移动到料盘取料位步骤（5s 超时，不重试）。点贴的胶带是预裁片，头先过去吸一片。</summary>
public sealed class MoveToPickPosStep : FlowStep
{
    private readonly IMotionCard _motion;

    /// <summary>构造移动到取料位步骤。</summary>
    /// <param name="motion">运控卡。</param>
    public MoveToPickPosStep(IMotionCard motion)
    {
        _motion = motion;
    }

    /// <inheritdoc />
    public override string Name => "MoveToPickPos";

    /// <inheritdoc />
    public override int TimeoutMs => 5_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var position = ctx.Recipe.Parameters.Position;
        var speed = ctx.Recipe.Parameters.Process.MoveSpeedMmPerSec;

        //轮询判断是否到位来确定是否超时
        await MoveHelper.MoveAsync(_motion, AxisId.X, position.PickPosX, speed, TimeoutMs, Name, ct);
        await MoveHelper.MoveAsync(_motion, AxisId.Y, position.PickPosY, speed, TimeoutMs, Name, ct);
    }
}
