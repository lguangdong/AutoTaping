// 职责：标定服务单元测试（已知仿射矩阵反算 + 残差判定 + 非法输入），对应 S5 验收标准 6 与 3
// 层：Tests
// 依赖：AutoTaping.Vision、OpenCvSharp

using AutoTaping.Vision;
using OpenCvSharp;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="CalibrationService"/> 的测试：用已知「像素 → 平台」映射反推像素点，
/// 验证拟合能恢复原矩阵且残差 &lt; 0.05mm（04 文档 2.4 节判据）。
/// </summary>
public sealed class CalibrationServiceTests
{
    /// <summary>
    /// 测试用的真实映射（与 VirtualCamera 的模拟标定映射一致）：
    /// u = 400 + (X-150)*4，v = 500 - (Y-200)*4，
    /// 反解得 X = 0.25u + 50，Y = 325 - 0.25v。
    /// </summary>
    [Fact]
    public void Calibrate_RecoversKnownAffineMatrix_WithLowRms()
    {
        var service = new CalibrationService();

        var pixelPoints = new List<Point2f>();
        var platformPoints = new List<Point2f>();

        // 九宫格：中心 (150,200)，步长 50
        for (var row = 1; row >= -1; row--)
        {
            for (var col = -1; col <= 1; col++)
            {
                var x = 150.0 + col * 50.0;
                var y = 200.0 + row * 50.0;
                var u = 400.0 + (x - 150.0) * 4.0;
                var v = 500.0 - (y - 200.0) * 4.0;

                platformPoints.Add(new Point2f((float)x, (float)y));
                pixelPoints.Add(new Point2f((float)u, (float)v));
            }
        }

        var result = service.Calibrate(pixelPoints.ToArray(), platformPoints.ToArray());

        Assert.True(result.IsValid, "合成图场景标定结果应合格");
        Assert.Equal(9, result.PointCount);
        Assert.True(result.RmsError < 0.05, $"残差应 < 0.05mm，实际 {result.RmsError:F6}");

        Assert.Equal(0.25, result.AffineMatrix[0], 3);
        Assert.Equal(0.0, result.AffineMatrix[1], 3);
        Assert.Equal(50.0, result.AffineMatrix[2], 3);
        Assert.Equal(0.0, result.AffineMatrix[3], 3);
        Assert.Equal(-0.25, result.AffineMatrix[4], 3);
        Assert.Equal(325.0, result.AffineMatrix[5], 3);
    }

    /// <summary>像素 → 平台换算应与拟合矩阵一致（验收标准 4：注入偏移能算成对应 mm）。</summary>
    [Fact]
    public void PixelToPlatform_UsesAffineMatrix()
    {
        var result = new CalibrationResult
        {
            AffineMatrix = new double[] { 0.25, 0, 50, 0, -0.25, 325 },
            RmsError = 0.01,
            PointCount = 9
        };

        var (x, y) = result.PixelToPlatform(400, 500);

        Assert.Equal(150.0, x, 6);
        Assert.Equal(200.0, y, 6);
    }

    /// <summary>点数不足 3 必须抛参数异常。</summary>
    [Fact]
    public void Calibrate_RejectsTooFewPoints()
    {
        var service = new CalibrationService();

        Assert.Throws<ArgumentException>(() =>
            service.Calibrate(
                new[] { new Point2f(0, 0), new Point2f(1, 1) },
                new[] { new Point2f(0, 0), new Point2f(1, 1) }));
    }

    /// <summary>两组点数不一致必须抛参数异常。</summary>
    [Fact]
    public void Calibrate_RejectsMismatchedCounts()
    {
        var service = new CalibrationService();

        Assert.Throws<ArgumentException>(() =>
            service.Calibrate(
                new[] { new Point2f(0, 0), new Point2f(1, 1), new Point2f(2, 2), new Point2f(3, 3) },
                new[] { new Point2f(0, 0), new Point2f(1, 1), new Point2f(2, 2) }));
    }
}
