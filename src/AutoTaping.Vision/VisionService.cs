// 职责：IVisionService 的视觉实现——双 MARK 模板匹配 + 纠偏，封装模板加载与标定矩阵
// 层：Vision
// 依赖：IVisionService/VisionParameters/Calibration/VisionCorrection（Core）、TemplateMatcher/CorrectionService/CalibrationResult/ImageConverter（Vision）、OpenCvSharp4

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using OpenCvSharp;

namespace AutoTaping.Vision;

/// <summary>
/// <see cref="IVisionService"/> 的视觉实现。把「识别两个 MARK + 计算纠偏」封装给 Core 的流程引擎。
/// 识别方式：两个不同直径的实心圆共用**一块**搜索区域，身份由直径区分（两个模板半径不同），
/// 命中后再用两点间距做一次合理性校验。模板文件缺失时按配方半径生成合成模板（便于无硬件演示）；
/// 真实硬件（S8）使用 VisionPage 制作的真实模板文件。
/// </summary>
public sealed class VisionService : IVisionService
{
    /// <summary>合成模板在圆半径之外每侧留的背景像素。</summary>
    private const int SyntheticTemplatePadding = 5;

    private readonly TemplateMatcher _matcher1 = new();
    private readonly TemplateMatcher _matcher2 = new();
    private readonly CorrectionService _correction = new();

    private VisionParameters _vision = new();
    private CalibrationResult? _calibration;
    private bool _disposed;

    /// <inheritdoc />
    public void Configure(VisionParameters vision, Calibration? calibration, string recipeName)
    {
        ArgumentNullException.ThrowIfNull(vision);

        _vision = vision;
        _calibration = calibration is null
            ? null
            : new CalibrationResult
            {
                AffineMatrix = calibration.AffineMatrix,
                RmsError = calibration.RmsError,
                CreatedAt = calibration.CreatedAt,
                PointCount = calibration.PointCount
            };

        LoadTemplate(vision.Template1, _matcher1, recipeName, vision.Mark1RadiusPx);
        LoadTemplate(vision.Template2, _matcher2, recipeName, vision.Mark2RadiusPx);
    }

    /// <inheritdoc />
    public VisionCorrection Correct(byte[] mono8, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 搜索区域必须显著大于模板：等于模板尺寸时只有一个候选位置，匹配必然"高分"且坐标恒为区域中心
        var templateSize = Math.Max(_matcher1.TemplateWidth, _matcher2.TemplateWidth);
        if (!_vision.IsSearchRoiUsable(templateSize))
        {
            return new VisionCorrection
            {
                Success = false,
                Error = VisionCorrectionError.MarkNotFound,
                ErrorDetail = $"搜索区域过小（{_vision.SearchRoi.W}×{_vision.SearchRoi.H}，模板 {templateSize}px），请在配方参数中重置搜索区域"
            };
        }

        using var mat = ImageConverter.ToMat(mono8, width, height);
        var searchRoi = ToRect(_vision.SearchRoi);

        // 两个模板在同一块区域各搜一次：直径不同 → 大圆模板只命中大圆、小圆模板只命中小圆
        var mark1 = _matcher1.Match(mat, searchRoi, _vision.MatchThreshold);
        var mark2 = _matcher2.Match(mat, searchRoi, _vision.MatchThreshold);

        if (mark1 is null || mark2 is null)
        {
            return new VisionCorrection
            {
                Success = false,
                Error = VisionCorrectionError.MarkNotFound,
                ErrorDetail = "MARK 识别失败，请检查光照与模板"
            };
        }

        var template1 = new Point2f((float)_vision.TemplateMark1U, (float)_vision.TemplateMark1V);
        var template2 = new Point2f((float)_vision.TemplateMark2U, (float)_vision.TemplateMark2V);

        // 间距校验：拦住「某个模板命中了画面里的干扰圆」这类错误，避免算出一个看似合理却错误的位姿
        var expectedDistance = Distance(template1, template2);
        var actualDistance = Distance(mark1.Value.Center, mark2.Value.Center);
        if (Math.Abs(actualDistance - expectedDistance) > _vision.MarkDistanceTolerancePx)
        {
            return new VisionCorrection
            {
                Success = false,
                Error = VisionCorrectionError.MarkNotFound,
                ErrorDetail = $"两个 MARK 间距异常（实测 {actualDistance:F1}px，标称 {expectedDistance:F1}px），请检查标记与搜索区域"
            };
        }

        if (_calibration is null)
        {
            return new VisionCorrection
            {
                Success = false,
                Error = VisionCorrectionError.NoCalibration,
                ErrorDetail = "无生效标定，请先执行九点标定"
            };
        }

        var correction = _correction.Compute(
            mark1.Value.Center, mark2.Value.Center, template1, template2, _calibration,
            _vision.MaxDeltaXMm, _vision.MaxDeltaYMm, _vision.MaxDeltaThetaDeg);

        if (!correction.IsValid)
        {
            return new VisionCorrection
            {
                Success = false,
                Error = VisionCorrectionError.CorrectionExceeded,
                ErrorDetail = correction.Message
            };
        }

        return new VisionCorrection
        {
            Success = true,
            DeltaX = correction.DeltaX,
            DeltaY = correction.DeltaY,
            DeltaTheta = correction.DeltaTheta,
            Score1 = mark1.Value.Score,
            Score2 = mark2.Value.Score,
            Error = VisionCorrectionError.None
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _matcher1.Dispose();
        _matcher2.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>加载模板；文件缺失时按配方半径生成合成实心圆模板（虚拟模式演示用）。</summary>
    private void LoadTemplate(string relativePath, TemplateMatcher matcher, string recipeName, double radiusPx)
    {
        var resolved = relativePath.Replace("{recipe}", recipeName, StringComparison.Ordinal);
        var fullPath = Path.Combine(AppContext.BaseDirectory, resolved);

        if (File.Exists(fullPath))
        {
            matcher.LoadTemplate(fullPath);
            return;
        }

        using var synthetic = GenerateFilledCircleTemplate(radiusPx);
        matcher.LoadTemplate(synthetic);
    }

    /// <summary>
    /// 按半径生成合成实心圆模板（白圆、深色底），与虚拟相机逐像素判定保持一致（同样不做抗锯齿），
    /// 圆心落在整数像素上，保证模板中心与真实 MARK 中心同口径。模板边长 = 直径 + 两侧各 5px 背景。
    /// </summary>
    /// <param name="radiusPx">圆半径（像素）。</param>
    private static Mat GenerateFilledCircleTemplate(double radiusPx)
    {
        var radius = Math.Max(3, (int)Math.Round(radiusPx));
        var size = (radius * 2) + 1 + (SyntheticTemplatePadding * 2);
        var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.All(40));
        var center = size / 2;
        var radiusSquared = radius * radius;

        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    SetPixel(mat, center + dx, center + dy, 255);
                }
            }
        }

        return mat;
    }

    /// <summary>两点欧氏距离（像素）。</summary>
    private static double Distance(Point2f a, Point2f b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static void SetPixel(Mat mat, int x, int y, byte value)
    {
        if (x < 0 || x >= mat.Width || y < 0 || y >= mat.Height)
        {
            return;
        }

        mat.At<byte>(y, x) = value;
    }

    private static Rect ToRect(RoiRect roi) => new(roi.X, roi.Y, roi.W, roi.H);
}
