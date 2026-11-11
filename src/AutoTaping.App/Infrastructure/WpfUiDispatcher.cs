// 职责：IUiDispatcher 的 WPF 实现（包装 Application.Current.Dispatcher）
// 层：App/Infrastructure
// 依赖：System.Windows.Threading

using System.Windows;
using System.Windows.Threading;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// <see cref="IUiDispatcher"/> 的 WPF 实现。统一走 <see cref="Dispatcher.InvokeAsync(Action)"/>，
/// 避免后台线程直接操作绑定属性导致的跨线程异常。
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    /// <summary>应用当前 Dispatcher；设计器或无 Application 场景退化为当前线程的 Dispatcher。</summary>
    private static Dispatcher Current => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <inheritdoc />
    public bool CheckAccess() => Current.CheckAccess();

    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = Current;
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
