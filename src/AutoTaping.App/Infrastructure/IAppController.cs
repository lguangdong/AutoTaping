// 职责：应用级控制（退出程序）的抽象，让 ViewModel 不直接依赖 WPF 的 Application
// 层：App/Infrastructure
// 依赖：无

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 应用控制抽象。系统设置页的「退出程序」需要在完成安全检查（非运行状态、关真空）后关闭进程，
/// 窗口与进程生命周期归 App（10-编码约定 五：ViewModel 抛事件，不自己开窗口 / 关进程）。
/// </summary>
public interface IAppController
{
    /// <summary>请求退出程序（由 App 的 OnExit 完成正常退出标记与资源释放）。</summary>
    void RequestShutdown();
}
