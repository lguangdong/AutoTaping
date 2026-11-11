// 职责：R 轴旋转中心补偿单元测试（90°/180°/0° 特殊角），对应 S5 验收标准 6
// 层：Tests
// 依赖：AutoTaping.Vision

using AutoTaping.Vision;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="RotationCompensation"/> 的测试：吸嘴绕旋转中心旋转后产生的额外偏移。
/// 取吸嘴 (220,200)、旋转中心 (200,200)，即吸嘴在中心右侧 20mm。
/// </summary>
public sealed class RotationCompensationTests
{
    /// <summary>旋转 90°：吸嘴绕中心转到正上方，补偿 = (20, -20)。</summary>
    [Fact]
    public void Compute_90Degrees_ReturnsExpectedCompensation()
    {
        var (compX, compY) = RotationCompensation.Compute(220, 200, 200, 200, 90);

        Assert.Equal(20.0, compX, 6);
        Assert.Equal(-20.0, compY, 6);
    }

    /// <summary>旋转 180°：吸嘴转到中心左侧 20mm，补偿 = (40, 0)。</summary>
    [Fact]
    public void Compute_180Degrees_ReturnsExpectedCompensation()
    {
        var (compX, compY) = RotationCompensation.Compute(220, 200, 200, 200, 180);

        Assert.Equal(40.0, compX, 6);
        Assert.Equal(0.0, compY, 6);
    }

    /// <summary>旋转 0°：无偏移。</summary>
    [Fact]
    public void Compute_ZeroDegrees_ReturnsZeroCompensation()
    {
        var (compX, compY) = RotationCompensation.Compute(220, 200, 200, 200, 0);

        Assert.Equal(0.0, compX, 6);
        Assert.Equal(0.0, compY, 6);
    }

    /// <summary>吸嘴正好在旋转中心上：任意角度旋转都无偏移。</summary>
    [Fact]
    public void Compute_NozzleAtCenter_ReturnsZeroCompensation()
    {
        var (compX, compY) = RotationCompensation.Compute(200, 200, 200, 200, 90);

        Assert.Equal(0.0, compX, 6);
        Assert.Equal(0.0, compY, 6);
    }
}
