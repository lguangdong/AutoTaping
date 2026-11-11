// 职责：手动调试页的 code-behind（仅 InitializeComponent，逻辑全部在 ViewModel）
// 层：App/Views/Pages
// 依赖：无

using System.Windows.Controls;

namespace AutoTaping.App.Views.Pages;

/// <summary>手动调试页。轴控制、IO 与气缸动作全部由 <see cref="ViewModels.ManualViewModel"/> 驱动。</summary>
public partial class ManualPage : UserControl
{
    /// <summary>创建页面。</summary>
    public ManualPage()
    {
        InitializeComponent();
    }
}
