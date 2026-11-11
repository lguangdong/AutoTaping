// 职责：MARK 模板匹配——归一化相关系数匹配 + 亚像素峰值细化 + 阈值判定
// 层：Vision
// 依赖：OpenCvSharp4

using OpenCvSharp;

namespace AutoTaping.Vision;

/// <summary>一次模板匹配的结果（中心点 + 匹配得分）。</summary>
/// <param name="Center">匹配到的模板中心（像素，亚像素）。</param>
/// <param name="Score">匹配得分（0~1，越接近 1 越像）。</param>
public readonly record struct TemplateMatchResult(Point2f Center, double Score);

/// <summary>
/// 模板匹配器。在 ROI 内用归一化相关系数（CCoeffNormed）搜索模板，对峰值做 3x3 抛物线拟合求亚像素中心，
/// 得分低于阈值返回 <see langword="null"/>（见 <c>docs/04-视觉与标定.md</c> 第 3.2 节）。
/// </summary>
public sealed class TemplateMatcher : IDisposable
{
    private Mat? _template;
    private bool _disposed;

    /// <summary>从文件加载灰度模板。</summary>
    /// <param name="path">模板 PNG 路径。</param>
    /// <exception cref="FileNotFoundException">文件不存在或无法读取。</exception>
    public void LoadTemplate(string path)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var template = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (template.Empty())
        {
            template.Dispose();
            throw new FileNotFoundException($"模板不存在或无法读取：{path}");
        }

        LoadTemplate(template);
    }

    /// <summary>从内存 Mat 加载模板（复制一份，调用方仍负责释放传入的 Mat）。</summary>
    /// <param name="template">灰度模板图。</param>
    public void LoadTemplate(Mat template)
    {
        ArgumentNullException.ThrowIfNull(template);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (template.Empty())
        {
            throw new ArgumentException("模板图为空", nameof(template));
        }

        _template?.Dispose();
        _template = template.Channels() == 1
            ? template.Clone()
            : ConvertToGrayClone(template);
    }

    /// <summary>模板是否已加载。</summary>
    public bool HasTemplate => _template is not null && !_template.Empty();

    /// <summary>
    /// 在 ROI 内搜索模板，返回亚像素中心与得分；未找到（低于阈值）返回 <see langword="null"/>。
    /// </summary>
    /// <param name="image">源灰度图。</param>
    /// <param name="roi">搜索区域（像素），必须落在图像内。</param>
    /// <param name="threshold">匹配得分阈值，默认 0.7。</param>
    public TemplateMatchResult? Match(Mat image, Rect roi, double threshold = 0.7)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_template is null || _template.Empty())
        {
            return null;
        }

        if (!IsRoiValid(image, roi))
        {
            return null;
        }

        using var search = new Mat(image, roi);

        var resultRows = search.Rows - _template.Rows + 1;
        var resultCols = search.Cols - _template.Cols + 1;
        if (resultRows <= 0 || resultCols <= 0)
        {
            return null;
        }

        using var result = new Mat(resultRows, resultCols, MatType.CV_32FC1);
        Cv2.MatchTemplate(search, _template, result, TemplateMatchModes.CCoeffNormed);

        Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc);
        if (maxVal < threshold)
        {
            return null;
        }

        var sub = RefineSubPixel(result, maxLoc);
        var center = new Point2f(
            roi.X + sub.X + _template.Width / 2f,
            roi.Y + sub.Y + _template.Height / 2f);

        return new TemplateMatchResult(center, maxVal);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _template?.Dispose();
        _template = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>多通道 Mat 转灰度副本（模板必须单通道）。</summary>
    private static Mat ConvertToGrayClone(Mat template)
    {
        var gray = new Mat();
        Cv2.CvtColor(template, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    /// <summary>对相关响应图的峰值做 3x3 抛物线拟合，求亚像素峰值（04 文档 3.2：约 0.1px 精度）。</summary>
    private static Point2f RefineSubPixel(Mat result, Point p)
    {
        var dx = 0f;
        var dy = 0f;

        if (p.X > 0 && p.X < result.Cols - 1)
        {
            var l = result.At<float>(p.Y, p.X - 1);
            var c = result.At<float>(p.Y, p.X);
            var r = result.At<float>(p.Y, p.X + 1);
            var denom = 2 * (2 * c - l - r);
            if (Math.Abs(denom) > 1e-6)
            {
                dx = (l - r) / denom;
            }
        }

        if (p.Y > 0 && p.Y < result.Rows - 1)
        {
            var t = result.At<float>(p.Y - 1, p.X);
            var c = result.At<float>(p.Y, p.X);
            var b = result.At<float>(p.Y + 1, p.X);
            var denom = 2 * (2 * c - t - b);
            if (Math.Abs(denom) > 1e-6)
            {
                dy = (t - b) / denom;
            }
        }

        return new Point2f(p.X + dx, p.Y + dy);
    }

    /// <summary>ROI 是否完整落在图像内。</summary>
    private static bool IsRoiValid(Mat img, Rect roi)
        => roi.Width > 0 && roi.Height > 0 && roi.Right <= img.Width && roi.Bottom <= img.Height;
}
