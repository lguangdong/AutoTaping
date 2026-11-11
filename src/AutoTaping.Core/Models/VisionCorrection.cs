// 职责：视觉识别纠偏结果值对象（流程引擎消费，Core 不依赖 Vision 具体类型）
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>视觉识别失败的类别（用于映射到报警码）。</summary>
public enum VisionCorrectionError
{
    /// <summary>成功。</summary>
    None,

    /// <summary>MARK 识别失败（匹配分低于阈值或未找到），报警 AL202。</summary>
    MarkNotFound,

    /// <summary>纠偏量超出安全阈值，报警 AL203。</summary>
    CorrectionExceeded,

    /// <summary>无生效标定，无法换算平台坐标。</summary>
    NoCalibration
}

/// <summary>
/// 视觉识别与纠偏结果。由 <see cref="AutoTaping.Core.Abstractions.IVisionService"/> 产出，
/// 流程引擎据此计算贴附目标位置（docs/04 第 5 节完整纠偏调用链）。
/// </summary>
public sealed class VisionCorrection
{
    /// <summary>是否识别成功。</summary>
    public bool Success { get; init; }

    /// <summary>ΔX 纠偏量（mm）。</summary>
    public double DeltaX { get; init; }

    /// <summary>ΔY 纠偏量（mm）。</summary>
    public double DeltaY { get; init; }

    /// <summary>Δθ 纠偏量（°）。</summary>
    public double DeltaTheta { get; init; }

    /// <summary>MARK1 匹配得分（0~1）。</summary>
    public double Score1 { get; init; }

    /// <summary>MARK2 匹配得分（0~1）。</summary>
    public double Score2 { get; init; }

    /// <summary>失败类别；成功时为 <see cref="VisionCorrectionError.None"/>。</summary>
    public VisionCorrectionError Error { get; init; }

    /// <summary>失败时的可读说明；成功时为 <see langword="null"/>。</summary>
    public string? ErrorDetail { get; init; }
}
