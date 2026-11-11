// 职责：系统设置页的 code-behind（仅 InitializeComponent，逻辑全部在 ViewModel）
// 层：App/Views/Pages
// 依赖：无

using System.Windows.Controls;

namespace AutoTaping.App.Views.Pages;

/// <summary>系统设置页（08-页面清单 页面 8，仅工程师可进）。全部逻辑在 <c>SystemViewModel</c> 里。</summary>
public partial class SystemPage : UserControl
{
    /// <summary>创建页面。</summary>
    public SystemPage()
    {
        InitializeComponent();
    }
}
