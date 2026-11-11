// 职责：视觉参数分组（双 MARK 的 ROI / 模板路径 / 模板基准像素坐标 / 阈值 / 纠偏上限）
// 层：Core/Models
// 依赖：RoiRect

namespace AutoTaping.Core.Models;

/// <summary>
/// 视觉参数。模板路径中的 <c>{recipe}</c> 占位符在加载时替换为配方名（见 06 文档第 4 节规则）。
/// </summary>
public sealed class VisionParameters
{
    /// <summary>MARK1 搜索区域（像素）。</summary>
    public RoiRect Mark1Roi { get; set; } = new(300, 400, 200, 200);

    /// <summary>MARK2 搜索区域（像素）。</summary>
    public RoiRect Mark2Roi { get; set; } = new(780, 400, 200, 200);

    /// <summary>MARK1 模板图片相对路径。</summary>
    public string Template1 { get; set; } = "templates/{recipe}/mark1.png";

    /// <summary>MARK2 模板图片相对路径。</summary>
    public string Template2 { get; set; } = "templates/{recipe}/mark2.png";

    /// <summary>模板制作时记录的 MARK1 像素 U 坐标。</summary>
    public double TemplateMark1U { get; set; } = 400.0;

    /// <summary>模板制作时记录的 MARK1 像素 V 坐标。</summary>
    public double TemplateMark1V { get; set; } = 500.0;

    /// <summary>模板制作时记录的 MARK2 像素 U 坐标。</summary>
    public double TemplateMark2U { get; set; } = 880.0;

    /// <summary>模板制作时记录的 MARK2 像素 V 坐标。</summary>
    public double TemplateMark2V { get; set; } = 500.0;

    /// <summary>模板匹配阈值，范围 0.5 ~ 0.95。</summary>
    public double MatchThreshold { get; set; } = 0.7;

    /// <summary>ΔX 纠偏上限（mm），范围 1 ~ 20。</summary>
    public double MaxDeltaXMm { get; set; } = 5.0;

    /// <summary>ΔY 纠偏上限（mm），范围 1 ~ 20。</summary>
    public double MaxDeltaYMm { get; set; } = 5.0;

    /// <summary>Δθ 纠偏上限（°），范围 1 ~ 30。</summary>
    public double MaxDeltaThetaDeg { get; set; } = 10.0;
}
