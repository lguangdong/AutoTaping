// 职责：IUserNotifier 的 WPF 实现（以 MessageBox 呈现，所有提示统一走这里）
// 层：App/Infrastructure
// 依赖：AutoTaping.Core.Abstractions、Microsoft.Extensions.Logging、System.Windows

using System.Windows;
using AutoTaping.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// <see cref="IUserNotifier"/> 的 WPF 实现。可被后台线程调用，内部自动切回 UI 线程。
/// </summary>
public sealed class WpfUserNotifier : IUserNotifier
{
    /// <summary>弹窗标题（固定，避免各处写法不一致）。</summary>
    private const string DialogCaption = "自动贴胶机上位机";

    private readonly ILogger<WpfUserNotifier> _log;

    /// <summary>创建实例。</summary>
    /// <param name="log">日志记录器。</param>
    public WpfUserNotifier(ILogger<WpfUserNotifier> log)
    {
        _log = log;
    }

    /// <inheritdoc />
    public void Info(string message)
    {
        _log.LogInformation("提示用户：{Message}", message);
        Show(message, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <inheritdoc />
    public void Warn(string message)
    {
        _log.LogWarning("警告用户：{Message}", message);
        Show(message, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <inheritdoc />
    public void Error(string message)
    {
        _log.LogError("错误提示用户：{Message}", message);
        Show(message, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <inheritdoc />
    public bool Confirm(string title, string message)
    {
        _log.LogInformation("请求用户确认：{Title} / {Message}", title, message);
        // 默认按钮为「取消」，防止误触回车（07-UI设计规范 第 8 节）
        var result = ShowCore(title, message, MessageBoxButton.OKCancel,
                              MessageBoxImage.Warning, MessageBoxResult.Cancel);
        return result == MessageBoxResult.OK;
    }

    private void Show(string message, MessageBoxButton button, MessageBoxImage image)
    {
        ShowCore(DialogCaption, message, button, image, MessageBoxResult.OK);
    }

    private MessageBoxResult ShowCore(string title, string message, MessageBoxButton button,
                                      MessageBoxImage image, MessageBoxResult defaultResult)
    {
        var app = Application.Current;
        if (app is null)
        {
            // 无 WPF 上下文（单元测试 / 极早期启动失败）：不弹窗，仅保留日志
            _log.LogWarning("当前无 UI 上下文，弹窗内容仅记录日志：{Message}", message);
            return defaultResult;
        }

        try
        {
            if (app.Dispatcher.CheckAccess())
            {
                return MessageBox.Show(message, title, button, image, defaultResult);
            }

            // 后台线程调用时切回 UI 线程（采集线程 / 流程线程的通知都走这里）
            return app.Dispatcher.Invoke(
                () => MessageBox.Show(message, title, button, image, defaultResult));
        }
        catch (InvalidOperationException ex)
        {
            // 应用正在关闭时 Dispatcher 已不可用，属于可恢复场景，只记日志
            _log.LogError(ex, "弹窗失败（应用可能正在退出）：{Message}", message);
            return defaultResult;
        }
    }
}
