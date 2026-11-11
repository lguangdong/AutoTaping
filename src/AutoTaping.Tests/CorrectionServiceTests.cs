// 职责：双 MARK 纠偏服务单元测试（平移/旋转/超阈值拦截），对应 S5 验收标准 4、5
// 层：Tests
// 依赖：AutoTaping.Vision、OpenCvSharp

using AutoTaping.Vision;
using OpenCvSharp;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="CorrectionService"/> 的测试。用「像素 = 平台」的简化标定矩阵
/// （AffineMatrix = [1,0,0;0,1,0]）验证平移与超阈值拦截。
/// </summary>
public sealed class CorrectionServiceTests
{
    /// <summary>简化标定：像素位移直接等于平台位移（1px = 1mm）。</summary>
    private static CalibrationResult CreateUnitCalibration()
        => new()
        {
            AffineMatrix = new double[] { 1, 0, 0, 0, 1, 0 },
            RmsError = 0.01,
            PointCount = 9
        };

    /// <summary>整体平移 (+3,-3)px → ΔX=+3mm、ΔY=-3mm、Δθ=0（验收标准 4）。</summary>
    [Fact]
    public void Compute_PureTranslation_ReturnsCorrectDelta()
    {
        var service = new CorrectionService();

        var result = service.Compute(
            mark1Pixel: new Point2f(403, 497),
            mark2Pixel: new Point2f(883, 497),
            template1Pixel: new Point2f(400, 500),
            template2Pixel: new Point2f(880, 500),
            calib: CreateUnitCalibration());

        Assert.True(result.IsValid);
        Assert.Equal(3.0, result.DeltaX, 3);
        Assert.Equal(-3.0, result.DeltaY, 3);
        Assert.Equal(0.0, result.DeltaTheta, 3);
    }

    /// <summary>纠偏量超阈值必须被拦截并给出可读说明（验收标准 5：识别错误不导致设备乱跑）。</summary>
    [Fact]
    public void Compute_ExceedsThreshold_ReturnsInvalid()
    {
        var service = new CorrectionService();

        var result = service.Compute(
            mark1Pixel: new Point2f(420, 500),
            mark2Pixel: new Point2f(900, 500),
            template1Pixel: new Point2f(400, 500),
            template2Pixel: new Point2f(880, 500),
            calib: CreateUnitCalibration(),
            maxDeltaX: 5, maxDeltaY: 5, maxDeltaTheta: 10);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    /// <summary>两点绕中点对称旋转，中点不动、只有 Δθ 变化（MARK1 上移 20、MARK2 下移 20）。</summary>
    [Fact]
    public void Compute_Rotation_ReturnsDeltaTheta()
    {
        var service = new CorrectionService();

        var result = service.Compute(
            mark1Pixel: new Point2f(400, 480),
            mark2Pixel: new Point2f(880, 520),
            template1Pixel: new Point2f(400, 500),
            template2Pixel: new Point2f(880, 500),
            calib: CreateUnitCalibration(),
            maxDeltaX: 5, maxDeltaY: 5, maxDeltaTheta: 20);

        Assert.True(result.IsValid);
        Assert.Equal(0.0, result.DeltaX, 3);
        Assert.Equal(0.0, result.DeltaY, 3);
        // 连线角度 = atan2(40, 480)，参考为 0，因此 Δθ = atan2(40,480) 转角度
        Assert.Equal(Math.Atan2(40.0, 480.0) * 180.0 / Math.PI, result.DeltaTheta, 2);
    }
}
