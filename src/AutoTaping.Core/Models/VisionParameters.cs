// 职责：视觉参数分组（单搜索区域 / 两个大小圆 MARK 的半径 / 模板路径 / 基准像素坐标 / 阈值 / 纠偏上限）
// 层：Core/Models
// 依赖：RoiRect
//
// 识别方式：两个不同直径的实心圆 MARK 共用**一块**搜索区域，身份由直径区分（不再依赖左右两个 ROI）。
// 搜索区域的职责只有「省算力 + 防误检」，与模板制作解耦——模板只决定“长什么样”，不决定“到哪找”。

namespace AutoTaping.Core.Models;

/// <summary>
/// 视觉参数。模板路径中的 <c>{recipe}</c> 占位符在加载时替换为配方名（见 06 文档第 4 节规则）。
/// </summary>
public sealed class VisionParameters
{
    /// <summary>搜索区域的默认余量（像素）：约 25mm @4px/mm，远大于 ±5mm 的纠偏上限。</summary>
    public const int DefaultSearchMarginPx = 100;

    /// <summary>
    /// 两个 MARK 共用的搜索区域（像素）。默认值 = 标称 MARK1(400,500)、MARK2(880,500) ± 100px。
    /// 换型/换工装时才需要调整；模板制作不得改写本字段（改写会导致搜索区退化为模板尺寸）。
    /// </summary>
    public RoiRect SearchRoi { get; set; } = new(300, 400, 680, 200);

    /// <summary>MARK1 标称半径（像素）：大圆，合成模板边长为 2r + 11。</summary>
    public double Mark1RadiusPx { get; set; } = 15.0;

    /// <summary>MARK2 标称半径（像素）：小圆（与 MARK1 至少差 4px 才能可靠区分身份）。</summary>
    public double Mark2RadiusPx { get; set; } = 9.0;

    /// <summary>两个 MARK 中心距的容差（像素）：实测距离偏离标称值超过它即判识别失败。</summary>
    public double MarkDistanceTolerancePx { get; set; } = 20.0;

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

    /// <summary>按两个 MARK 的标称像素位置重算搜索区域。</summary>
    /// <param name="marginPx">四周余量（像素），默认 <see cref="DefaultSearchMarginPx"/>。</param>
    public void ResetSearchRoiToNominal(int marginPx = DefaultSearchMarginPx)
        => SearchRoi = CreateNominalSearchRoi(TemplateMark1U, TemplateMark1V, TemplateMark2U, TemplateMark2V, marginPx);

    /// <summary>按两个 MARK 的标称像素位置计算搜索区域（自动裁掉负坐标）。</summary>
    /// <param name="u1">MARK1 像素 U。</param>
    /// <param name="v1">MARK1 像素 V。</param>
    /// <param name="u2">MARK2 像素 U。</param>
    /// <param name="v2">MARK2 像素 V。</param>
    /// <param name="marginPx">四周余量（像素）。</param>
    public static RoiRect CreateNominalSearchRoi(double u1, double v1, double u2, double v2, int marginPx)
    {
        var left = Math.Max(0, (int)Math.Floor(Math.Min(u1, u2)) - marginPx);
        var top = Math.Max(0, (int)Math.Floor(Math.Min(v1, v2)) - marginPx);
        var right = (int)Math.Ceiling(Math.Max(u1, u2)) + marginPx;
        var bottom = (int)Math.Ceiling(Math.Max(v1, v2)) + marginPx;

        return new RoiRect(left, top, right - left, bottom - top);
    }

    /// <summary>搜索区域是否足够大：边长至少为「模板边长 + 余量」，否则匹配无法定位。</summary>
    /// <param name="templateSize">模板边长（像素）。</param>
    /// <param name="minMarginPx">搜索区相对模板的最小额外余量（像素）。</param>
    public bool IsSearchRoiUsable(int templateSize, int minMarginPx = 20)
        => SearchRoi.IsValid && SearchRoi.W >= templateSize + minMarginPx && SearchRoi.H >= templateSize + minMarginPx;
}
