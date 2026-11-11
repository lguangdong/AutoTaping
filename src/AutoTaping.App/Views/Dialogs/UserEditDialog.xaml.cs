// 职责：用户编辑对话框的 code-behind（按模式显隐字段、校验两次口令一致、返回用户输入）
// 层：App/Views/Dialogs
// 依赖：AutoTaping.App.Infrastructure.UserEditInput、UserRole（Core/Models）

using System.Windows;
using AutoTaping.App.Infrastructure;
using AutoTaping.Core.Models;

namespace AutoTaping.App.Views.Dialogs;

/// <summary>
/// 用户编辑对话框。三种用途共用一套控件：新增用户（登录名可改、角色可改、口令必填）、
/// 改角色（登录名只读、口令可选）、改密（登录名与角色只读、口令必填）。
/// 业务校验（用户名唯一、最后一个工程师保护、口令长度）在 Core 里做，本类只做界面级检查。
/// </summary>
public partial class UserEditDialog : Window
{
    /// <summary>角色下拉的候选项（显示名 + 枚举值）。</summary>
    private static readonly IReadOnlyList<RoleOption> RoleOptions = new[]
    {
        new RoleOption("操作员", UserRole.Operator),
        new RoleOption("技术员", UserRole.Technician),
        new RoleOption("工程师", UserRole.Engineer)
    };

    private bool _passwordRequired;

    /// <summary>创建对话框。</summary>
    public UserEditDialog()
    {
        InitializeComponent();

        RoleCombo.ItemsSource = RoleOptions;
        RoleCombo.DisplayMemberPath = nameof(RoleOption.Name);
        RoleCombo.SelectedValuePath = nameof(RoleOption.Value);
        RoleCombo.SelectedIndex = 0;
    }

    /// <summary>用户输入；取消时为 <see langword="null"/>。</summary>
    public UserEditInput? Input { get; private set; }

    /// <summary>按用途初始化对话框。</summary>
    /// <param name="title">窗口标题。</param>
    /// <param name="userName">登录名初值。</param>
    /// <param name="userNameEditable">登录名是否可编辑。</param>
    /// <param name="roleEditable">角色是否可编辑。</param>
    /// <param name="passwordRequired">口令是否必填。</param>
    /// <param name="role">角色初值。</param>
    public void Configure(
        string title,
        string userName,
        bool userNameEditable,
        bool roleEditable,
        bool passwordRequired,
        UserRole role)
    {
        Title = title;
        _passwordRequired = passwordRequired;

        UserNameInput.Text = userName;
        UserNameInput.IsReadOnly = !userNameEditable;
        UserNameInput.IsTabStop = userNameEditable;

        RoleCombo.IsEnabled = roleEditable;
        RoleCombo.SelectedValue = role;

        PasswordLabel.Text = passwordRequired ? "口令" : "新口令（留空表示不修改）";
        PasswordInput.IsEnabled = true;
    }

    /// <summary>确定：做界面级校验后把输入写入 <see cref="Input"/> 并关闭对话框。</summary>
    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var userName = UserNameInput.Text?.Trim() ?? string.Empty;
        if (userName.Length == 0)
        {
            ShowError("请输入用户名。", UserNameInput);
            return;
        }

        var password = PasswordInput.Password;
        var confirm = ConfirmPasswordInput.Password;

        if (_passwordRequired && password.Length == 0)
        {
            ShowError("请输入口令。", PasswordInput);
            return;
        }

        if (password.Length > 0 && !string.Equals(password, confirm, StringComparison.Ordinal))
        {
            ShowError("两次输入的口令不一致，请重新输入。", ConfirmPasswordInput);
            return;
        }

        var role = RoleCombo.SelectedValue is UserRole selected ? selected : UserRole.Operator;

        Input = new UserEditInput(userName, role, password);
        DialogResult = true;
    }

    private void ShowError(string message, FrameworkElement focusTarget)
    {
        ErrorText.Text = message;
        focusTarget.Focus();
    }

    /// <summary>角色下拉项。</summary>
    /// <param name="Name">显示名。</param>
    /// <param name="Value">角色枚举值。</param>
    private sealed record RoleOption(string Name, UserRole Value);
}
