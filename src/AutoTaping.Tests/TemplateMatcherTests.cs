// 职责：模板匹配器单元测试（合成图找到 MARK 中心 + 未加载模板返回 null），对应 S5 交付物 TemplateMatcher
// 层：Tests
// 依赖：AutoTaping.Vision、OpenCvSharp

using AutoTaping.Vision;
using OpenCvSharp;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="TemplateMatcher"/> 的测试：用合成十字图验证能找到 MARK 中心（04 文档 3.2 节方案）。
/// </summary>
public sealed class TemplateMatcherTests
{
    /// <summary>合成图中能精确找到 MARK 中心。</summary>
    [Fact]
    public void Match_FindsMarkCenter_InSyntheticImage()
    {
        using var image = new Mat(1024, 1280, MatType.CV_8UC1, Scalar.All(40));
        Cv2.DrawMarker(image, new Point(400, 500), Scalar.White, MarkerTypes.Cross, 40, 2);

        // 以 MARK 中心裁剪 80x80 作为模板
        using var template = new Mat(image, new Rect(360, 460, 80, 80)).Clone();

        using var matcher = new TemplateMatcher();
        matcher.LoadTemplate(template);

        var result = matcher.Match(image, new Rect(0, 0, 1280, 1024), 0.5);

        Assert.NotNull(result);

        // 模板边长 80 → 中心约定为 (80-1)/2 = 39.5；十字线宽 2 居中画在 (400,500)，
        // 墨迹在"像素索引"空间的中心即 (399.5, 499.5)，容差 1px 内即可
        Assert.InRange(result.Value.Center.X, 399.0, 400.0);
        Assert.InRange(result.Value.Center.Y, 499.0, 500.0);
        Assert.True(result.Value.Score > 0.9, $"匹配分应接近 1，实际 {result.Value.Score:F3}");
    }

    /// <summary>未加载模板时返回 null（而不是抛异常）。</summary>
    [Fact]
    public void Match_ReturnsNull_WhenTemplateNotLoaded()
    {
        using var image = new Mat(1024, 1280, MatType.CV_8UC1, Scalar.All(40));
        using var matcher = new TemplateMatcher();

        var result = matcher.Match(image, new Rect(0, 0, 1280, 1024), 0.5);

        Assert.Null(result);
    }

    /// <summary>搜索区域与模板同尺寸（result 1×1）时返回 null —— 只有唯一候选位置，结果没有意义。</summary>
    [Fact]
    public void Match_ReturnsNull_WhenSearchAreaEqualsTemplate()
    {
        using var image = new Mat(1024, 1280, MatType.CV_8UC1, Scalar.All(40));
        Cv2.DrawMarker(image, new Point(400, 500), Scalar.White, MarkerTypes.Cross, 40, 2);

        using var template = new Mat(image, new Rect(360, 460, 80, 80)).Clone();
        using var matcher = new TemplateMatcher();
        matcher.LoadTemplate(template);

        // 搜索区 = 刚裁模板的那块区域：不做防御时会返回"满分 + 区域中心"，属于静默错误
        var result = matcher.Match(image, new Rect(360, 460, 80, 80), 0.5);

        Assert.Null(result);
    }

    /// <summary>模板与图像不匹配（分数低于阈值）返回 null。</summary>
    [Fact]
    public void Match_ReturnsNull_WhenScoreBelowThreshold()
    {
        // 模板是一个十字，但图像里是纯背景（无十字）
        using var templateImage = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(40));
        Cv2.DrawMarker(templateImage, new Point(40, 40), Scalar.White, MarkerTypes.Cross, 40, 2);

        using var image = new Mat(1024, 1280, MatType.CV_8UC1, Scalar.All(40));

        using var matcher = new TemplateMatcher();
        matcher.LoadTemplate(templateImage);

        var result = matcher.Match(image, new Rect(0, 0, 1280, 1024), 0.7);

        Assert.Null(result);
    }
}
