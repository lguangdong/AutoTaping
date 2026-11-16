// 职责：VisionService 集成测试——单搜索区域 + 大小圆双模板 + 间距校验
// 层：Tests
// 依赖：AutoTaping.Vision、AutoTaping.Core.Models
//
// 说明：模板文件不存在时 VisionService 按配方半径生成合成模板，因此本测试不需要磁盘素材；
//       覆盖「单 ROI 走两个不同直径模板」「间距异常」「搜索区域过小」三条路径。

using AutoTaping.Core.Models;
using AutoTaping.Vision;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="VisionService"/> 的识别链路测试：一块搜索区域 + 大小两个实心圆 MARK。
/// </summary>
public sealed class VisionServiceTests
{
    private const int Width = 1280;
    private const int Height = 1024;
    private const int Mark1Radius = 15;
    private const int Mark2Radius = 9;

    [Fact]
    public void Correct_Succeeds_WithTwoDifferentRadii()
    {
        using var vision = CreateVision();
        var frame = RenderFrame(400, 500, 880, 500);

        var result = vision.Correct(frame, Width, Height);

        Assert.True(result.Success, result.ErrorDetail);
        Assert.InRange(Math.Abs(result.DeltaX), 0.0, 1.0);
        Assert.InRange(Math.Abs(result.DeltaY), 0.0, 1.0);
        Assert.InRange(Math.Abs(result.DeltaTheta), 0.0, 0.5);
        Assert.True(result.Score1 > 0.9, $"MARK1 得分过低：{result.Score1:F3}");
        Assert.True(result.Score2 > 0.9, $"MARK2 得分过低：{result.Score2:F3}");
    }

    [Fact]
    public void Correct_Fails_WhenDistanceAbnormal()
    {
        using var vision = CreateVision();

        // MARK2 向内移了 100px：间距 380，标称 480，超出容差 20px
        var frame = RenderFrame(400, 500, 780, 500);

        var result = vision.Correct(frame, Width, Height);

        Assert.False(result.Success);
        Assert.Equal(VisionCorrectionError.MarkNotFound, result.Error);
        Assert.Contains("间距", result.ErrorDetail);
    }

    [Fact]
    public void Correct_Fails_WhenSearchRoiTooSmall()
    {
        // 搜索区域与模板同尺寸：只有一个候选位置，坐标恒为区域中心 → 必须判失败
        var parameters = new VisionParameters
        {
            SearchRoi = new RoiRect(380, 480, 41, 41)
        };

        using var vision = CreateVision(parameters);
        var frame = RenderFrame(400, 500, 880, 500);

        var result = vision.Correct(frame, Width, Height);

        Assert.False(result.Success);
        Assert.Equal(VisionCorrectionError.MarkNotFound, result.Error);
        Assert.Contains("搜索区域过小", result.ErrorDetail);
    }

    private static VisionService CreateVision(VisionParameters? parameters = null)
    {
        var vision = new VisionService();
        vision.Configure(parameters ?? new VisionParameters(), CreateCalibration(), "test");
        return vision;
    }

    /// <summary>仿射矩阵按 0.25mm/px（即 4px/mm，与 VirtualCamera 的模拟比例一致），平移项对 Δ 计算无影响。</summary>
    private static Calibration CreateCalibration() => new()
    {
        AffineMatrix = [0.25, 0.0, -75.0, 0.0, -0.25, 300.0],
        RmsError = 0.01,
        PointCount = 9,
        CreatedAt = DateTime.Now
    };

    /// <summary>渲染一帧：背景 40 + 两个大小不同的实心圆（与虚拟相机同一像素判定，不做抗锯齿）。</summary>
    private static byte[] RenderFrame(int x1, int y1, int x2, int y2)
    {
        var buffer = new byte[Width * Height];
        Array.Fill(buffer, (byte)40);

        DrawFilledCircle(buffer, x1, y1, Mark1Radius);
        DrawFilledCircle(buffer, x2, y2, Mark2Radius);
        return buffer;
    }

    private static void DrawFilledCircle(byte[] buffer, int cx, int cy, int radius)
    {
        var radiusSquared = radius * radius;

        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dy * dy) > radiusSquared)
                {
                    continue;
                }

                var x = cx + dx;
                var y = cy + dy;
                if (x >= 0 && x < Width && y >= 0 && y < Height)
                {
                    buffer[(y * Width) + x] = 255;
                }
            }
        }
    }
}
