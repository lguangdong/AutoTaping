// 职责：小时柱状图的一根柱子（小时标签 + 柱高 + 产量文字），固定 24 个实例只改属性
// 层：App/ViewModels
// 依赖：HourlyOutput（Core/Models）、CommunityToolkit.Mvvm

using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 小时柱状图数据点。柱高由产量占比换算成像素，图表用矩形绘制，不引入第三方图表库
/// （08-页面清单 页面 6 交互规则）。
/// </summary>
public sealed partial class HourlyBarViewModel : ObservableObject
{
    /// <summary>柱子最大高度（像素），与页面上的柱状图区高度匹配。</summary>
    private const double MaxBarHeightPx = 140;

    /// <summary>构造柱子（初始产量为 0）。</summary>
    /// <param name="hour">小时（0 ~ 23）。</param>
    public HourlyBarViewModel(int hour)
    {
        HourText = hour.ToString("D2");
    }

    /// <summary>横轴标签（两位小时数）。</summary>
    public string HourText { get; }

    /// <summary>柱高（像素）；产量为 0 时保留 2px 基线，便于看出该小时无数据。</summary>
    [ObservableProperty]
    private double _barHeight = 2;

    /// <summary>产量文字（鼠标/触摸无需悬停即可读，07 文档禁止用 Tooltip 承载关键信息）。</summary>
    [ObservableProperty]
    private string _countText = "";

    /// <summary>该小时是否有产出（用于柱体颜色：有产出用运行绿，无产出用空闲灰）。</summary>
    [ObservableProperty]
    private bool _hasData;

    /// <summary>按本小时产量与当日最大值刷新柱高。</summary>
    /// <param name="count">本小时产量。</param>
    /// <param name="maxCount">当日单小时最大产量（用于归一化）。</param>
    public void Apply(int count, int maxCount)
    {
        HasData = count > 0;
        CountText = count > 0 ? count.ToString() : "";
        BarHeight = count <= 0 || maxCount <= 0
            ? 2
            : Math.Max(2, MaxBarHeightPx * count / maxCount);
    }
}
