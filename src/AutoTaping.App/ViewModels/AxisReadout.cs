// 职责：单轴实时读数卡片——位置数值 / 到位状态 / 行程占比（自动运行页「四轴实时位置」）
// 层：App/ViewModels
// 依赖：AxisId、DeviceSnapshot（Core）、CommunityToolkit.Mvvm

using System.Globalization;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 单轴实时读数。集合在页面构造时一次性建好，200ms 刷新只改属性值不重建集合
/// （10-编码约定 五：不得周期性重建集合）。
/// </summary>
public sealed partial class AxisReadout : ObservableObject
{
    /// <summary>停止原因「正常」的取值（与 <c>IMotionCard.GetStopReason</c> 约定一致）。</summary>
    private const int StopReasonNormal = 0;

    /// <summary>构造单轴读数卡。</summary>
    /// <param name="axis">轴标识。</param>
    /// <param name="minLimit">行程下限（mm，R 轴为 °）。</param>
    /// <param name="maxLimit">行程上限（mm，R 轴为 °）。</param>
    public AxisReadout(AxisId axis, double minLimit, double maxLimit)
    {
        Axis = axis;
        Unit = axis == AxisId.R ? "°" : "mm";
        MinLimit = minLimit;
        MaxLimit = maxLimit;
        RangeText = $"{minLimit:F0} ~ {maxLimit:F0} {Unit}";
    }

    /// <summary>轴标识。</summary>
    public AxisId Axis { get; }

    /// <summary>轴名（X / Y / Z / R）。</summary>
    public string AxisName => Axis.ToString();

    /// <summary>主单位（mm 或 °）。</summary>
    public string Unit { get; }

    /// <summary>行程下限（来自 appsettings 的轴软限位）。</summary>
    public double MinLimit { get; }

    /// <summary>行程上限（来自 appsettings 的轴软限位）。</summary>
    public double MaxLimit { get; }

    /// <summary>行程范围文字，如 <c>0 ~ 400 mm</c>。</summary>
    public string RangeText { get; }

    /// <summary>当前位置数值（两位小数，不含单位）。</summary>
    [ObservableProperty]
    private string _positionText = "--";

    /// <summary>是否已到位（已停止且无异常）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _inPosition;

    /// <summary>是否已回零（未回零的轴位置不可信，界面用灰点表示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isHomed;

    /// <summary>是否存在轴故障（限位 / 急停 / 软限位 / 驱动报警）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _hasFault;

    /// <summary>停止原因文字（限位 / 急停 / 软限位 / 驱动报警）；正常时为空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private string _stopReasonText = "";

    /// <summary>当前行程占比（0~100，用于限位条）。</summary>
    [ObservableProperty]
    private double _limitPercent;

    /// <summary>是否接近行程端点（≤5% 或 ≥95%），用于把行程条变黄预警。</summary>
    [ObservableProperty]
    private bool _nearLimit;

    /// <summary>状态文字（未回零 / 到位 / 运动 / 具体故障）。</summary>
    public string StateText
    {
        get
        {
            if (!IsHomed)
            {
                return "未回零";
            }

            if (HasFault)
            {
                return string.IsNullOrEmpty(StopReasonText) ? "故障" : StopReasonText;
            }

            return InPosition ? "到位" : "运动";
        }
    }

    /// <summary>用一帧快照刷新本轴读数（必须在 UI 线程调用）。</summary>
    /// <param name="snapshot">设备快照。</param>
    public void Apply(DeviceSnapshot snapshot)
    {
        var position = snapshot.GetPosition(Axis);
        PositionText = position.ToString("F2", CultureInfo.InvariantCulture);
        InPosition = snapshot.IsInPosition(Axis);
        IsHomed = snapshot.IsHomed(Axis);

        // 停止原因取值见 IMotionCard.GetStopReason：0=正常 1=限位 2=急停 3=软限位 4=驱动报警
        var reason = snapshot.GetStopReason(Axis);
        HasFault = reason != StopReasonNormal;
        StopReasonText = reason switch
        {
            1 => "限位",
            2 => "急停",
            3 => "软限位",
            4 => "驱动报警",
            _ => ""
        };

        var span = MaxLimit - MinLimit;
        if (span <= 0)
        {
            LimitPercent = 0;
            NearLimit = false;
            return;
        }

        var ratio = (position - MinLimit) / span;
        LimitPercent = Math.Clamp(ratio * 100, 0, 100);
        NearLimit = ratio <= 0.05 || ratio >= 0.95;
    }
}
