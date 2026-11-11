// 职责：单个 IO 点位的展示模型（点位号 / 中文名 / ON-OFF / 是否强制），手动页与 IO 页共用
// 层：App/ViewModels
// 依赖：CommunityToolkit.Mvvm

using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// IO 点位展示模型。集合在页面创建时一次性建好，刷新时只改属性值而不重建集合，
/// 让绑定只刷数值（10-编码约定 五：不得 200ms 重建整个集合）。
/// </summary>
public sealed partial class IoPointViewModel : ObservableObject
{
    /// <summary>构造点位展示模型。</summary>
    /// <param name="point">点位号，如 <c>"X00"</c>。</param>
    /// <param name="displayName">中文名称，如 <c>"上料位有料"</c>。</param>
    public IoPointViewModel(string point, string displayName)
    {
        Point = point;
        DisplayName = displayName;
    }

    /// <summary>点位号。</summary>
    public string Point { get; }

    /// <summary>中文名称（来自 appsettings 的 DiMap / DoMap）。</summary>
    public string DisplayName { get; }

    /// <summary>是否 ON（绿 = ON，深灰 = OFF，08-页面清单 页面 7）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isOn;

    /// <summary>是否处于强制输出状态（强制时黄色边框，08-页面清单 页面 7）。</summary>
    [ObservableProperty]
    private bool _isForced;

    /// <summary>状态文字（ON / OFF）。</summary>
    public string StateText => IsOn ? "ON" : "OFF";

    /// <summary>用快照刷新本点位状态（必须在 UI 线程调用）。</summary>
    /// <param name="states">DI 或 DO 状态字典。</param>
    /// <param name="forcedPoints">强制点位集合；输入点位传 <see langword="null"/>。</param>
    public void Apply(IReadOnlyDictionary<string, bool> states, IReadOnlyCollection<string>? forcedPoints)
    {
        IsOn = states.TryGetValue(Point, out var value) && value;
        IsForced = forcedPoints is not null && forcedPoints.Contains(Point);
    }

    /// <summary>供下拉框等场景显示「点位号 + 中文名」（同时让辅助功能正确朗读）。</summary>
    public override string ToString() => $"{Point} {DisplayName}";
}
