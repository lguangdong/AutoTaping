// 职责：九点标定——用最小二乘拟合二维仿射变换并计算残差 RMS
// 层：Vision
// 依赖：OpenCvSharp4（EstimateAffine2D）

using OpenCvSharp;

namespace AutoTaping.Vision;

/// <summary>
/// 标定服务。用 N 组（像素, 平台）点对拟合 2x3 仿射矩阵 <c>[a b c; d e f]</c>，
/// 使 <c>[X;Y] = M · [u;v;1]</c>，并计算残差 RMS（见 <c>docs/04-视觉与标定.md</c> 第 2 节）。
/// </summary>
public sealed class CalibrationService
{
    /// <summary>
    /// 拟合仿射变换并计算残差。
    /// </summary>
    /// <param name="pixelPoints">N 个 MARK 像素坐标。</param>
    /// <param name="platformPoints">N 个对应的平台 mm 坐标，与像素坐标一一对应。</param>
    /// <exception cref="ArgumentException">点数 &lt; 3 或两组数量不一致。</exception>
    /// <exception cref="InvalidOperationException">仿射求解失败。</exception>
    public CalibrationResult Calibrate(Point2f[] pixelPoints, Point2f[] platformPoints)
    {
        ArgumentNullException.ThrowIfNull(pixelPoints);
        ArgumentNullException.ThrowIfNull(platformPoints);

        if (pixelPoints.Length < 3 || pixelPoints.Length != platformPoints.Length)
        {
            throw new ArgumentException(
                $"标定点数量不合法：至少 3 点且两组数量一致（像素 {pixelPoints.Length}，平台 {platformPoints.Length}）");
        }

        using var src = PointsToMat(pixelPoints);
        using var dst = PointsToMat(platformPoints);

        using var affine = Cv2.EstimateAffine2D(src, dst);
        if (affine is null || affine.Empty())
        {
            throw new InvalidOperationException("仿射变换求解失败，请检查标定点是否共线或重合");
        }

        var m = new double[CalibrationResult.AffineElementCount];
        m[0] = affine.At<double>(0, 0);
        m[1] = affine.At<double>(0, 1);
        m[2] = affine.At<double>(0, 2);
        m[3] = affine.At<double>(1, 0);
        m[4] = affine.At<double>(1, 1);
        m[5] = affine.At<double>(1, 2);

        var rms = ComputeRms(m, pixelPoints, platformPoints);

        return new CalibrationResult
        {
            AffineMatrix = m,
            RmsError = rms,
            CreatedAt = DateTime.Now,
            PointCount = pixelPoints.Length
        };
    }

    /// <summary>把点数组转成 Nx2 的 CV_32F 单通道矩阵（EstimateAffine2D 要求的输入形状）。</summary>
    private static Mat PointsToMat(Point2f[] points)
    {
        var mat = new Mat(points.Length, 2, MatType.CV_32FC1);
        var indexer = mat.GetGenericIndexer<float>();

        for (var i = 0; i < points.Length; i++)
        {
            indexer[i, 0] = points[i].X;
            indexer[i, 1] = points[i].Y;
        }

        return mat;
    }

    /// <summary>用拟合出的矩阵反算每个点，累计到平台点的均方根误差。</summary>
    private static double ComputeRms(double[] m, Point2f[] pixelPoints, Point2f[] platformPoints)
    {
        double sumSq = 0;
        for (var i = 0; i < pixelPoints.Length; i++)
        {
            var px = m[0] * pixelPoints[i].X + m[1] * pixelPoints[i].Y + m[2];
            var py = m[3] * pixelPoints[i].X + m[4] * pixelPoints[i].Y + m[5];
            var dx = px - platformPoints[i].X;
            var dy = py - platformPoints[i].Y;
            sumSq += dx * dx + dy * dy;
        }

        return Math.Sqrt(sumSq / pixelPoints.Length);
    }
}
