// 职责：双 MARK 纠偏——用当前拍照的两个 MARK 与标定基准比对，算出 ΔX/ΔY/Δθ 并做合理性检查
// 层：Vision
// 依赖：OpenCvSharp4（Point2f）、CalibrationResult

using OpenCvSharp;

namespace AutoTaping.Vision;

/// <summary>
/// 双 MARK 纠偏服务。原理见 <c>docs/04-视觉与标定.md</c> 第 3.3 节：
/// 用两点连线角度算 Δθ，用两点中点的像素位移经标定矩阵换算成平台 ΔX/ΔY。
/// 纠偏量超阈值一律判定为无效（识别错误可能把工件贴飞甚至撞机，必须硬性拦截）。
/// </summary>
public sealed class CorrectionService
{
    /// <summary>默认 ΔX 上限（mm），与 06/09 文档视觉参数默认值一致。</summary>
    public const double DefaultMaxDeltaX = 5.0;

    /// <summary>默认 ΔY 上限（mm）。</summary>
    public const double DefaultMaxDeltaY = 5.0;

    /// <summary>默认 Δθ 上限（°）。</summary>
    public const double DefaultMaxDeltaTheta = 10.0;

    /// <summary>
    /// 计算双 MARK 纠偏。
    /// </summary>
    /// <param name="mark1Pixel">当前帧 MARK1 像素坐标。</param>
    /// <param name="mark2Pixel">当前帧 MARK2 像素坐标。</param>
    /// <param name="template1Pixel">标定时记录的 MARK1 基准像素坐标。</param>
    /// <param name="template2Pixel">标定时记录的 MARK2 基准像素坐标。</param>
    /// <param name="calib">当前生效的标定结果（像素 → 平台）。</param>
    /// <param name="maxDeltaX">ΔX 上限（mm），来自配方视觉参数。</param>
    /// <param name="maxDeltaY">ΔY 上限（mm）。</param>
    /// <param name="maxDeltaTheta">Δθ 上限（°）。</param>
    public CorrectionResult Compute(
        Point2f mark1Pixel, Point2f mark2Pixel,
        Point2f template1Pixel, Point2f template2Pixel,
        CalibrationResult calib,
        double maxDeltaX = DefaultMaxDeltaX,
        double maxDeltaY = DefaultMaxDeltaY,
        double maxDeltaTheta = DefaultMaxDeltaTheta)
    {
        ArgumentNullException.ThrowIfNull(calib);

        // 1. 角度偏差：两点连线与水平轴的夹角差
        var curAngle = Math.Atan2(mark2Pixel.Y - mark1Pixel.Y, mark2Pixel.X - mark1Pixel.X);
        var refAngle = Math.Atan2(template2Pixel.Y - template1Pixel.Y, template2Pixel.X - template1Pixel.X);
        var deltaTheta = (curAngle - refAngle) * 180.0 / Math.PI;

        // 2. 以两点中点为参考，计算像素位移
        var curMidX = (mark1Pixel.X + mark2Pixel.X) / 2.0;
        var curMidY = (mark1Pixel.Y + mark2Pixel.Y) / 2.0;
        var refMidX = (template1Pixel.X + template2Pixel.X) / 2.0;
        var refMidY = (template1Pixel.Y + template2Pixel.Y) / 2.0;

        // 3. 像素位移 → 平台位移（用标定矩阵，含平移项，直接得到平台坐标差）
        var (curX, curY) = calib.PixelToPlatform(curMidX, curMidY);
        var (refX, refY) = calib.PixelToPlatform(refMidX, refMidY);

        var deltaX = curX - refX;
        var deltaY = curY - refY;

        // 4. 合理性检查（04 文档 3.3 节：超阈值一律拒绝，防止识别错误导致设备乱跑）
        if (Math.Abs(deltaX) > maxDeltaX || Math.Abs(deltaY) > maxDeltaY || Math.Abs(deltaTheta) > maxDeltaTheta)
        {
            return new CorrectionResult
            {
                IsValid = false,
                Message = $"纠偏量异常：ΔX={deltaX:F2}mm ΔY={deltaY:F2}mm Δθ={deltaTheta:F2}°，请检查识别结果与模板"
            };
        }

        return new CorrectionResult
        {
            DeltaX = deltaX,
            DeltaY = deltaY,
            DeltaTheta = deltaTheta,
            IsValid = true
        };
    }
}
