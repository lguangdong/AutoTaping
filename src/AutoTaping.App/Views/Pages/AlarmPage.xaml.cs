// 职责：报警页的 code-behind（仅 InitializeComponent，逻辑全部在 ViewModel）
// 层：App/Views/Pages
// 依赖：无

using System.Windows.Controls;

namespace AutoTaping.App.Views.Pages;

/// <summary>报警页。S3 阶段为空壳，内容在 S7 实现。</summary>
public partial class AlarmPage : UserControl
{
    /// <summary>创建页面。</summary>
    public AlarmPage()
    {
        InitializeComponent();
    }
}
