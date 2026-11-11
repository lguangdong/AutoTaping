// 职责：IAppController 的 WPF 实现（调用 Application.Shutdown）
// 层：App/Infrastructure
// 依赖：IAppController、System.Windows

using System.Windows;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// <see cref="IAppController"/> 的 WPF 实现。调用 <see cref="Application.Shutdown()"/> 后，
/// App.OnExit 会写正常退出标记并释放 DI 容器（06 文档第 8 节）。
/// </summary>
public sealed class WpfAppController : IAppController
{
    private readonly ILogger<WpfAppController> _log;

    /// <summary>创建实例。</summary>
    /// <param name="log">日志。</param>
    public WpfAppController(ILogger<WpfAppController> log)
    {
        _log = log;
    }

    /// <inheritdoc />
    public void RequestShutdown()
    {
        var app = Application.Current;
        if (app is null)
        {
            // 无 WPF 上下文（单元测试）：只记录，不抛异常
            _log.LogWarning("当前无 UI 上下文，忽略退出请求");
            return;
        }

        _log.LogInformation("已请求退出程序");
        app.Shutdown();
    }
}
