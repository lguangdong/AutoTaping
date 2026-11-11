// 职责：把"切回 UI 线程执行"抽象成接口，避免 ViewModel 直接依赖 Application.Current / Dispatcher
// 层：App/Infrastructure
// 依赖：无

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// UI 线程调度抽象。后台线程（状态栏刷新、后续的采集线程）更新界面属性前必须通过本接口切回 UI 线程
/// （01-架构设计 第 3 节：流程线程 / 采集线程不得直接改 UI）。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>当前是否已在 UI 线程上。</summary>
    bool CheckAccess();

    /// <summary>把动作排到 UI 线程执行，并返回可等待的任务。</summary>
    /// <param name="action">要执行的动作。</param>
    Task InvokeAsync(Action action);
}
