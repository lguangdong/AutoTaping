// 职责：R 轴旋转中心补偿——吸嘴绕旋转中心 C 转 θ 后产生的额外偏移
// 层：Vision
// 依赖：无（纯几何计算）

namespace AutoTaping.Vision;

/// <summary>
/// R 轴旋转中心补偿。吸嘴不绕自身中心而是绕旋转中心 C 旋转，旋转 θ 后吸嘴偏离目标位置，
/// 需要把额外偏移加回目标坐标（见 <c>docs/04-视觉与标定.md</c> 第 4 节）。
/// </summary>
public static class RotationCompensation
{
    /// <summary>
    /// 计算旋转 θ 后吸嘴的额外偏移（需要加到目标位置上的补偿量）。
    /// </summary>
    /// <param name="nozzleX">吸嘴当前 X（mm）。</param>
    /// <param name="nozzleY">吸嘴当前 Y（mm）。</param>
    /// <param name="centerX">旋转中心 C 的 X（mm）。</param>
    /// <param name="centerY">旋转中心 C 的 Y（mm）。</param>
    /// <param name="thetaDeg">旋转角度（°）。</param>
    /// <returns>补偿量 (CompX, CompY)，即 目标 - 实际 的差值。</returns>
    public static (double CompX, double CompY) Compute(
        double nozzleX, double nozzleY,
        double centerX, double centerY,
        double thetaDeg)
    {
        var t = thetaDeg * Math.PI / 180.0;
        var cos = Math.Cos(t);
        var sin = Math.Sin(t);

        var dx = nozzleX - centerX;
        var dy = nozzleY - centerY;

        // 绕 C 旋转 θ 后的吸嘴位置
        var newX = centerX + dx * cos - dy * sin;
        var newY = centerY + dx * sin + dy * cos;

        // 补偿量 = 目标 - 实际
        return (nozzleX - newX, nozzleY - newY);
    }
}
