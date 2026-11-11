// 职责：IUserEditor 的 WPF 实现（弹出 UserEditDialog 收集登录名 / 角色 / 口令）
// 层：App/Infrastructure
// 依赖：IUserEditor、UserEditInput、Views/Dialogs/UserEditDialog、UserRole（Core/Models）、Microsoft.Extensions.Logging

using System.Windows;
using AutoTaping.App.Views.Dialogs;
using AutoTaping.Core.Models;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// <see cref="IUserEditor"/> 的 WPF 实现。必须在 UI 线程调用（由命令触发，天然满足）。
/// </summary>
public sealed class WpfUserEditor : IUserEditor
{
    private readonly ILogger<WpfUserEditor> _log;

    /// <summary>创建实例。</summary>
    /// <param name="log">日志。</param>
    public WpfUserEditor(ILogger<WpfUserEditor> log)
    {
        _log = log;
    }

    /// <inheritdoc />
    public UserEditInput? Show(
        string title,
        string userName,
        bool userNameEditable,
        bool roleEditable,
        bool passwordRequired,
        UserRole role)
    {
        var app = Application.Current;
        if (app is null)
        {
            // 无 WPF 上下文（单元测试 / 极早期启动）：不弹窗，调用方按"取消"处理
            _log.LogWarning("当前无 UI 上下文，无法弹出用户编辑框：{Title}", title);
            return null;
        }

        var dialog = new UserEditDialog { Owner = app.MainWindow };
        dialog.Configure(title, userName, userNameEditable, roleEditable, passwordRequired, role);

        var confirmed = dialog.ShowDialog() == true;
        _log.LogDebug("用户编辑框：{Title}，结果 {Result}", title, confirmed ? dialog.Input?.UserName : "(取消)");
        return confirmed ? dialog.Input : null;
    }
}
