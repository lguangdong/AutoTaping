// 职责：主窗口壳的 code-behind（注入 ShellViewModel 作为 DataContext，关闭时释放 ViewModel 的计时器/订阅）
// 层：App/Views
// 依赖：AutoTaping.App.ViewModels.ShellViewModel

using System.Windows;
using AutoTaping.App.ViewModels;

namespace AutoTaping.App.Views;

/// <summary>
/// 主窗口。只负责装配 DataContext 与释放资源，业务逻辑全部在 <see cref="ShellViewModel"/>
/// （10-编码约定 三、分层红线 第 7 条）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;

    /// <summary>创建主窗口。</summary>
    /// <param name="viewModel">主窗口壳 ViewModel（由 DI 注入）。</param>
    public MainWindow(ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
