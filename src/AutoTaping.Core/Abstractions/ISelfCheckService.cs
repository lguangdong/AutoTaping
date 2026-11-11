// 职责：启动自检抽象——磁盘空间（AL501）、数据库可写（AL502）与硬件状态巡检
// 层：Core/Abstractions
// 依赖：SelfCheckReport（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 系统自检服务（11-开发任务分解 S9：启动自检覆盖磁盘、数据库、硬件）。
/// 启动时执行一次，随后按 <c>SelfCheckOptions.IntervalSeconds</c> 周期巡检；
/// 条件不满足时报警（AL501 / AL502），条件恢复时自动关闭报警（09 文档第三节）。
/// 自检失败永不阻断启动（01-架构设计 第 5 节）。
/// </summary>
public interface ISelfCheckService : IDisposable
{
    /// <summary>立即执行一次自检（不抛异常：单项检查失败只记为不通过）。</summary>
    /// <returns>自检结果。</returns>
    SelfCheckReport Run();

    /// <summary>启动周期巡检（重复调用安全）。</summary>
    void Start();

    /// <summary>停止周期巡检（重复调用安全）。</summary>
    void Stop();
}
