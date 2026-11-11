// 职责：产量统计页的 code-behind（仅 InitializeComponent，逻辑全部在 ViewModel）
// 层：App/Views/Pages
// 依赖：无

using System.Windows.Controls;

namespace AutoTaping.App.Views.Pages;

/// <summary>产量统计页。S3 阶段为空壳，内容在 S7 实现。</summary>
public partial class OutputPage : UserControl
{
    /// <summary>创建页面。</summary>
    public OutputPage()
    {
        InitializeComponent();
    }
}
