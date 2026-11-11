// 职责：双 MARK 纠偏的计算结果（ΔX/ΔY/Δθ 与合理性判定）
// 层：Vision
// 依赖：无

namespace AutoTaping.Vision;

/// <summary>
/// 纠偏结果。ΔX/ΔY 为平台 mm，Δθ 为角度（°）。
/// <see cref="IsValid"/> 为 <see langword="false"/> 表示纠偏量超出安全阈值（识别错误会被拦截，见 04 文档 3.3 节）。
/// </summary>
public sealed class CorrectionResult
{
    /// <summary>ΔX 纠偏量（mm）。</summary>
    public double DeltaX { get; init; }

    /// <summary>ΔY 纠偏量（mm）。</summary>
    public double DeltaY { get; init; }

    /// <summary>Δθ 纠偏量（°）。</summary>
    public double DeltaTheta { get; init; }

    /// <summary>纠偏量是否在安全阈值内（<see langword="false"/> 表示被拦截，不得用于运动）。</summary>
    public bool IsValid { get; init; }

    /// <summary>不合理时的可读说明；合理时为 <see langword="null"/>。</summary>
    public string? Message { get; init; }
}
