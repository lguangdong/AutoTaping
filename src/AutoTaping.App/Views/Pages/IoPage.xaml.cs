// 职责：IO 监控页的 code-behind（仅 InitializeComponent，逻辑全部在 ViewModel）
// 层：App/Views/Pages
// 依赖：无

using System.Windows.Controls;

namespace AutoTaping.App.Views.Pages;

/// <summary>IO 监控页。点位网格与强制输出全部由 <see cref="ViewModels.IoViewModel"/> 驱动。</summary>
public partial class IoPage : UserControl
{
    /// <summary>创建页面。</summary>
    public IoPage()
    {
        InitializeComponent();
    }
}
