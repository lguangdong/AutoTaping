// 职责：步骤 3 移动到拍照位——X/Y 依次定位到配方拍照位（05 第 4.2 节第 3 步）
// 层：Core/Flow/Steps
// 依赖：IMotionCard（Core/Abstractions）、MoveHelper

using AutoTaping.Core.Abstractions;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>移动到拍照位步骤（5s 超时，不重试）。</summary>
public sealed class MoveToPhotoPosStep : FlowStep
{
    private readonly IMotionCard _motion;

    /// <summary>构造移动拍照位步骤。</summary>
    public MoveToPhotoPosStep(IMotionCard motion)
    {
        _motion = motion;
    }

    /// <inheritdoc />
    public override string Name => "MoveToPhotoPos";

    /// <inheritdoc />
    public override int TimeoutMs => 10_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var position = ctx.Recipe.Parameters.Position;
        var speed = ctx.Recipe.Parameters.Process.MoveSpeedMmPerSec;

        await MoveHelper.MoveAsync(_motion, AxisId.X, position.PhotoPosX, speed, TimeoutMs, Name, ct);
        await MoveHelper.MoveAsync(_motion, AxisId.Y, position.PhotoPosY, speed, TimeoutMs, Name, ct);
    }
}
