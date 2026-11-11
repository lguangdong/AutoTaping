// 职责：导航栏单项——把页面 ViewModel 的标识/标题/图标与"是否可点、是否选中"的界面状态放在一起
// 层：App/ViewModels
// 依赖：PageViewModel、CommunityToolkit.Mvvm

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 导航栏一项。权限不足时 <see cref="IsAllowed"/> 为 <see langword="false"/>：
/// 按钮仍然显示但置灰（07-UI设计规范 5.2：不要隐藏，否则用户不知道为什么找不到），
/// 点击时由 ShellViewModel 再次校验并提示所需角色（08-页面清单 全局交互约定）。
/// <para>
/// 切页可能是异步的（手动页进入前要等确认框），因此命令用 <see cref="AsyncRelayCommand"/>。
/// </para>
/// </summary>
public sealed partial class NavigationItem : ObservableObject
{
    /// <summary>是否具备进入本页的权限；为 <see langword="false"/> 时按钮置灰。</summary>
    [ObservableProperty]
    private bool _isAllowed = true;

    /// <summary>是否为当前选中页（选中项用 StatusInfo 色高亮）。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>构造导航项。</summary>
    /// <param name="page">对应的页面 ViewModel。</param>
    /// <param name="onSelected">点击时回调（由 ShellViewModel 提供，内部做权限二次校验与进入确认）。</param>
    public NavigationItem(PageViewModel page, Func<PageViewModel, Task> onSelected)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(onSelected);

        Page = page;
        SelectCommand = new AsyncRelayCommand(() => onSelected(page));
    }

    /// <summary>对应的页面 ViewModel。</summary>
    public PageViewModel Page { get; }

    /// <summary>导航文字。</summary>
    public string Title => Page.Title;

    /// <summary>导航图标字符。</summary>
    public string Icon => Page.Icon;

    /// <summary>点击导航的命令（始终可执行，权限在 ShellViewModel 里判定并给出提示）。</summary>
    public IAsyncRelayCommand SelectCommand { get; }
}
