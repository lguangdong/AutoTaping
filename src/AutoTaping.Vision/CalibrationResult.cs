// 职责：九点标定的计算结果（仿射矩阵 + 残差 + 像素→平台换算）
// 层：Vision
// 依赖：无

namespace AutoTaping.Vision;

/// <summary>
/// 标定结果。矩阵含义见 <c>docs/04-视觉与标定.md</c> 第 2.1 节：
/// <c>[X;Y] = [a b c; d e f] · [u;v;1]</c>，即把像素坐标 (u,v) 映射为平台坐标 (X,Y)。
/// </summary>
public sealed class CalibrationResult
{
    /// <summary>仿射矩阵元素个数（a b c d e f）。</summary>
    public const int AffineElementCount = 6;

    /// <summary>残差合格阈值（mm），见 04 文档 2.4 节：RMS &gt; 0.1 必须重标。</summary>
    public const double RmsPassThresholdMm = 0.1;

    /// <summary>仿射矩阵元素，顺序固定为 a b c d e f。</summary>
    public double[] AffineMatrix { get; init; } = new double[AffineElementCount];

    /// <summary>标定残差 RMS（mm）。</summary>
    public double RmsError { get; init; }

    /// <summary>标定完成时刻。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>参与拟合的点数，至少 3。</summary>
    public int PointCount { get; init; }

    /// <summary>结果是否合格（残差 &lt; 0.1mm 且点数 ≥ 3），见 04 文档 2.4 节。</summary>
    public bool IsValid => RmsError >= 0 && RmsError < RmsPassThresholdMm && PointCount >= 3;

    /// <summary>像素坐标 → 平台坐标。</summary>
    /// <param name="u">像素横坐标。</param>
    /// <param name="v">像素纵坐标。</param>
    public (double X, double Y) PixelToPlatform(double u, double v)
    {
        var m = AffineMatrix;
        return (m[0] * u + m[1] * v + m[2],
                m[3] * u + m[4] * v + m[5]);
    }
}
