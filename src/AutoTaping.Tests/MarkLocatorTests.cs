// 职责：MarkLocator 单元测试——框选偏心时仍能定位实心圆真实中心（亮/暗两种极性）
// 层：Tests
// 依赖：AutoTaping.Vision、OpenCvSharp

using AutoTaping.Vision;
using OpenCvSharp;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="MarkLocator"/> 的测试：验证「框选区域不必居中」也能找到圆心，
/// 以及区域内没有圆形目标时明确失败（不返回一个看似合理的坐标）。
/// </summary>
public sealed class MarkLocatorTests
{
    [Fact]
    public void TryLocate_FindsCenter_WhenCircleIsOffCenterInRoi()
    {
        using var image = new Mat(400, 600, MatType.CV_8UC1, Scalar.All(40));

        // 圆心 (250,120)，框选 (200,60,120,120)：圆明显偏上偏右，不在框中心
        DrawFilledCircle(image, 250, 120, 15, 255);
        var roi = new Rect(200, 60, 120, 120);

        var located = MarkLocator.TryLocate(image, roi, 15, out var center, out var reason);

        Assert.True(located, reason);
        Assert.InRange(center.X, 249.5, 250.5);
        Assert.InRange(center.Y, 119.5, 120.5);
    }

    [Fact]
    public void TryLocate_WorksForDarkCircleOnBrightBackground()
    {
        // 样本图是浅色底板（205）上的深色圆（25），与合成图极性相反
        using var image = new Mat(400, 600, MatType.CV_8UC1, Scalar.All(205));
        DrawFilledCircle(image, 300, 200, 12, 25);
        var roi = new Rect(240, 140, 120, 120);

        var located = MarkLocator.TryLocate(image, roi, 12, out var center, out var reason);

        Assert.True(located, reason);
        Assert.InRange(center.X, 299.5, 300.5);
        Assert.InRange(center.Y, 199.5, 200.5);
    }

    [Fact]
    public void TryLocate_Fails_WhenRoiHasNoCircle()
    {
        using var image = new Mat(400, 600, MatType.CV_8UC1, Scalar.All(40));

        var located = MarkLocator.TryLocate(image, new Rect(100, 100, 120, 120), 15, out _, out var reason);

        Assert.False(located);
        Assert.NotEmpty(reason);
    }

    /// <summary>
    /// 端到端：按定位到的圆心重裁模板后，模板匹配返回的中心应落在圆心上（用户报的"识别到框中心"场景）。
    /// </summary>
    [Fact]
    public void TemplateCroppedOnLocatedCenter_MatchReturnsCircleCenter()
    {
        const int circleX = 250;
        const int circleY = 120;
        const int radius = 15;

        using var image = new Mat(400, 600, MatType.CV_8UC1, Scalar.All(40));
        DrawFilledCircle(image, circleX, circleY, radius, 255);

        // 偏心框选：圆在框的右上部
        var selection = new Rect(200, 60, 120, 120);
        Assert.True(MarkLocator.TryLocate(image, selection, radius, out var markCenter, out var reason), reason);

        // 与 VisionViewModel.CenterWindowOn 相同的居中公式：左上角 = 圆心 − (边长 − 1)/2
        var left = (int)Math.Round(markCenter.X - ((selection.Width - 1) / 2.0));
        var top = (int)Math.Round(markCenter.Y - ((selection.Height - 1) / 2.0));

        using var template = new Mat(image, new Rect(left, top, selection.Width, selection.Height)).Clone();
        using var matcher = new TemplateMatcher();
        matcher.LoadTemplate(template);

        var match = matcher.Match(image, new Rect(0, 0, image.Width, image.Height), 0.5);

        Assert.NotNull(match);
        Assert.InRange(match.Value.Center.X, circleX - 0.6, circleX + 0.6);
        Assert.InRange(match.Value.Center.Y, circleY - 0.6, circleY + 0.6);
    }

    /// <summary>画实心圆（与虚拟相机同一像素判定：到圆心距离 ≤ 半径）。</summary>
    private static void DrawFilledCircle(Mat image, int cx, int cy, int radius, byte gray)
    {
        var radiusSquared = radius * radius;

        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    image.Set(cy + dy, cx + dx, gray);
                }
            }
        }
    }
}
