// 职责：IUserPrompt 的 WPF 实现（弹出 TextInputDialog 收集单行文本）
// 层：App/Infrastructure
// 依赖：IUserPrompt、Views/Dialogs/TextInputDialog、Microsoft.Extensions.Logging、System.Windows

using System.Windows;
using AutoTaping.App.Views.Dialogs;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// <see cref="IUserPrompt"/> 的 WPF 实现。必须在 UI 线程调用（命令触发，天然满足）。
/// </summary>
public sealed class WpfUserPrompt : IUserPrompt
{
    private readonly ILogger<WpfUserPrompt> _log;

    /// <summary>创建实例。</summary>
    /// <param name="log">日志。</param>
    public WpfUserPrompt(ILogger<WpfUserPrompt> log)
    {
        _log = log;
    }

    /// <inheritdoc />
    public string? PromptText(string title, string label, string defaultValue)
    {
        var app = Application.Current;
        if (app is null)
        {
            // 无 WPF 上下文（单元测试 / 极早期启动）：不弹窗，仅记录，调用方按"取消"处理
            _log.LogWarning("当前无 UI 上下文，无法弹出输入框：{Title}/{Label}", title, label);
            return null;
        }

        var dialog = new TextInputDialog { Owner = app.MainWindow };
        dialog.Configure(title, label, defaultValue);

        var confirmed = dialog.ShowDialog() == true;
        _log.LogDebug("文本输入框：{Title}，结果 {Result}", title, confirmed ? dialog.InputText : "(取消)");
        return confirmed ? dialog.InputText : null;
    }
}
