// 职责：IFilePicker 的 WPF 实现（Microsoft.Win32 的打开 / 保存文件对话框）
// 层：App/Infrastructure
// 依赖：IFilePicker、Microsoft.Win32、Microsoft.Extensions.Logging

using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// <see cref="IFilePicker"/> 的 WPF 实现。必须在 UI 线程调用（由命令触发，天然满足）。
/// </summary>
public sealed class WpfFilePicker : IFilePicker
{
    private readonly ILogger<WpfFilePicker> _log;

    /// <summary>创建实例。</summary>
    /// <param name="log">日志。</param>
    public WpfFilePicker(ILogger<WpfFilePicker> log)
    {
        _log = log;
    }

    /// <inheritdoc />
    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };

        var picked = dialog.ShowDialog() == true ? dialog.FileName : null;
        _log.LogDebug("打开文件对话框：{Title}，结果 {Path}", title, picked ?? "(取消)");
        return picked;
    }

    /// <inheritdoc />
    public string? PickSaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            OverwritePrompt = true,
            AddExtension = true
        };

        var picked = dialog.ShowDialog() == true ? dialog.FileName : null;
        _log.LogDebug("保存文件对话框：{Title}，结果 {Path}", title, picked ?? "(取消)");
        return picked;
    }
}
