// 职责：登录框的 code-behind（口令框同步、登录成功即关闭对话框；不做任何校验逻辑）
// 层：App/Views/Dialogs
// 依赖：AutoTaping.App.ViewModels.LoginViewModel

using System.Windows;
using AutoTaping.App.ViewModels;

namespace AutoTaping.App.Views.Dialogs;

/// <summary>
/// 登录框。校验与权限判定全部在 <see cref="LoginViewModel"/> / Core 的认证服务里，
/// 本类只把 <see cref="System.Windows.Controls.PasswordBox"/> 的内容同步给 ViewModel，
/// 并在登录成功时关闭对话框。
/// </summary>
public partial class LoginDialog : Window
{
    private readonly LoginViewModel _viewModel;

    /// <summary>创建登录框。</summary>
    /// <param name="viewModel">登录 ViewModel（由 DI 注入）。</param>
    public LoginDialog(LoginViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.LoginSucceeded += OnLoginSucceeded;
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.LoginSucceeded -= OnLoginSucceeded;
        base.OnClosed(e);
    }

    /// <summary>口令框内容变化时同步到 ViewModel（PasswordBox.Password 不是依赖属性）。</summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.Password = PasswordInput.Password;
    }

    /// <summary>登录成功后关闭对话框并返回 <see langword="true"/>。</summary>
    private void OnLoginSucceeded(object? sender, EventArgs e)
    {
        DialogResult = true;
    }
}
