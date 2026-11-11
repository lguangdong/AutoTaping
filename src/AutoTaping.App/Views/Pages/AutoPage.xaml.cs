// 职责：自动运行页的 code-behind（仅 InitializeComponent，逻辑全部在 ViewModel）
// 层：App/Views/Pages
// 依赖：无

using System.Windows.Controls;

namespace AutoTaping.App.Views.Pages;

/// <summary>自动运行页（主画面）。S3 阶段为空壳，内容在 S6 实现。</summary>
public partial class AutoPage : UserControl
{
    /// <summary>创建页面。</summary>
    public AutoPage()
    {
        InitializeComponent();
    }
}
