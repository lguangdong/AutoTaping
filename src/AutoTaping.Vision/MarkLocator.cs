// 职责：在给定区域内定位实心圆 MARK 的亚像素质心（Otsu 分割 + 图像矩）
// 层：Vision
// 依赖：OpenCvSharp4

using OpenCvSharp;

namespace AutoTaping.Vision;

/// <summary>
/// 实心圆 MARK 定位器。用于模板制作时把裁剪窗口对准 MARK 的真实中心——
/// 模板匹配返回的是"模板框中心"，若框选时 MARK 不居中，识别点就会偏离圆心。
/// 做法：ROI 内 Otsu 二值化，取面积最接近标称圆面积的那一类像素，用一阶图像矩求质心（亚像素）。
/// </summary>
public static class MarkLocator
{
    /// <summary>面积容差：实际前景面积需在标称圆面积的该倍数范围内。</summary>
    private const double MinAreaRatio = 0.30;

    /// <summary>面积容差上限。</summary>
    private const double MaxAreaRatio = 3.00;

    /// <summary>在搜索区域内定位实心圆中心。</summary>
    /// <param name="image">源灰度图。</param>
    /// <param name="roi">搜索区域（像素），必须完整落在图像内。</param>
    /// <param name="nominalRadiusPx">标称半径（像素），用于从两类像素中挑出 MARK。</param>
    /// <param name="center">定位成功时为全图坐标的质心（亚像素）。</param>
    /// <param name="reason">失败原因（面向操作工的可读说明）。</param>
    /// <returns>定位成功返回 <see langword="true"/>。</returns>
    public static bool TryLocate(Mat image, Rect roi, double nominalRadiusPx, out Point2f center, out string reason)
    {
        ArgumentNullException.ThrowIfNull(image);

        center = default;
        reason = string.Empty;

        if (image.Empty())
        {
            reason = "图像为空";
            return false;
        }

        if (roi.Width <= 1 || roi.Height <= 1 ||
            roi.X < 0 || roi.Y < 0 || roi.Right > image.Width || roi.Bottom > image.Height)
        {
            reason = "搜索区域越界";
            return false;
        }

        if (nominalRadiusPx < 2)
        {
            reason = "标称半径过小";
            return false;
        }

        using var patch = new Mat(image, roi);
        using var binary = new Mat();
        Cv2.Threshold(patch, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        var total = (long)roi.Width * roi.Height;
        var brightCount = Cv2.CountNonZero(binary);
        var darkCount = total - brightCount;

        // MARK 可能是亮目标也可能是暗目标（取决于打光与工件底色），
        // 用"面积更接近标称圆面积"的那一类来判定极性
        var nominalArea = Math.PI * nominalRadiusPx * nominalRadiusPx;
        var useBright = Math.Abs(brightCount - nominalArea) <= Math.Abs(darkCount - nominalArea);
        var area = useBright ? brightCount : darkCount;

        if (area < nominalArea * MinAreaRatio || area > nominalArea * MaxAreaRatio)
        {
            reason = $"区域内没有面积接近标称圆的连通块（{area}px²，标称 {nominalArea:F0}px²）";
            return false;
        }

        // 取出目标那一类像素做掩码：亮目标 → 直接二值化；暗目标 → 反相二值化
        using var mask = new Mat();
        Cv2.Threshold(binary, mask, 128, 255,
            useBright ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv);

        var moments = Cv2.Moments(mask, binaryImage: true);
        if (moments.M00 <= 1e-6)
        {
            reason = "无法计算质心（区域内像素过于均匀）";
            return false;
        }

        var u = roi.X + (moments.M10 / moments.M00);
        var v = roi.Y + (moments.M01 / moments.M00);

        if (u < roi.X || u > roi.Right || v < roi.Y || v > roi.Bottom)
        {
            reason = "质心落在搜索区域之外，区域内可能存在多个目标";
            return false;
        }

        center = new Point2f((float)u, (float)v);
        return true;
    }
}
