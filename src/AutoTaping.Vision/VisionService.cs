// 职责：IVisionService 的视觉实现——双 MARK 模板匹配 + 纠偏，封装模板加载与标定矩阵
// 层：Vision
// 依赖：IVisionService/VisionParameters/Calibration/VisionCorrection（Core）、TemplateMatcher/CorrectionService/CalibrationResult/ImageConverter（Vision）、OpenCvSharp4

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using OpenCvSharp;

namespace AutoTaping.Vision;

/// <summary>
/// <see cref="IVisionService"/> 的视觉实现。把「识别两个 MARK + 计算纠偏」封装给 Core 的流程引擎。
/// 模板文件缺失时生成合成十字模板（匹配虚拟相机默认十字 MARK，便于无硬件演示）；
/// 真实硬件（S8）使用 VisionPage 制作的真实模板文件。
/// </summary>
public sealed class VisionService : IVisionService
{
    /// <summary>合成模板尺寸（像素，与虚拟相机十字 MARK 的臂长一致）。</summary>
    private const int SyntheticTemplateSize = 45;

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

        LoadTemplate(vision.Template1, _matcher1, recipeName);
        LoadTemplate(vision.Template2, _matcher2, recipeName);
    }

    /// <inheritdoc />
    public VisionCorrection Correct(byte[] mono8, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var mat = ImageConverter.ToMat(mono8, width, height);

        var mark1 = _matcher1.Match(mat, ToRect(_vision.Mark1Roi), _vision.MatchThreshold);
        var mark2 = _matcher2.Match(mat, ToRect(_vision.Mark2Roi), _vision.MatchThreshold);

        if (mark1 is null || mark2 is null)
        {
            return new VisionCorrection
            {
                Success = false,
                Error = VisionCorrectionError.MarkNotFound,
                ErrorDetail = "MARK 识别失败，请检查光照与模板"
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

        var template1 = new Point2f((float)_vision.TemplateMark1U, (float)_vision.TemplateMark1V);
        var template2 = new Point2f((float)_vision.TemplateMark2U, (float)_vision.TemplateMark2V);

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

    /// <summary>加载模板；文件缺失时生成合成十字模板（虚拟模式演示用）。</summary>
    private void LoadTemplate(string relativePath, TemplateMatcher matcher, string recipeName)
    {
        var resolved = relativePath.Replace("{recipe}", recipeName, StringComparison.Ordinal);
        var fullPath = Path.Combine(AppContext.BaseDirectory, resolved);

        if (File.Exists(fullPath))
        {
            matcher.LoadTemplate(fullPath);
            return;
        }

        using var synthetic = GenerateCrossTemplate();
        matcher.LoadTemplate(synthetic);
    }

    /// <summary>生成合成十字模板（白十字、深色底，匹配虚拟相机默认 MARK）。</summary>
    private static Mat GenerateCrossTemplate()
    {
        var mat = new Mat(SyntheticTemplateSize, SyntheticTemplateSize, MatType.CV_8UC1, Scalar.All(40));
        var center = SyntheticTemplateSize / 2;
        const int armLength = 20;
        const int thickness = 2;

        for (var d = -armLength; d <= armLength; d++)
        {
            for (var t = -thickness; t <= thickness; t++)
            {
                SetPixel(mat, center + t, center + d, 255);   // 竖线
                SetPixel(mat, center + d, center + t, 255);   // 横线
            }
        }

        return mat;
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
