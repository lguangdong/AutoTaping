// 职责：每日维护任务抽象——过期数据归档成 CSV 后删除、标定裁剪、日志清理（06 文档第 7 节）
// 层：Core/Abstractions
// 依赖：MaintenanceResult（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 每日维护服务。默认每天固定时刻在后台执行一次（<c>PeriodicTimer</c>，不得阻塞 UI）；
/// 系统设置页也提供「清理旧数据」按钮手动触发一次（08-页面清单 页面 8）。
/// </summary>
public interface IMaintenanceService : IDisposable
{
    /// <summary>最近一次维护的时刻；从未执行返回 <see langword="null"/>。</summary>
    DateTime? LastRunAt { get; }

    /// <summary>启动后台定时执行（重复调用安全）。</summary>
    void Start();

    /// <summary>停止后台定时执行（重复调用安全）。</summary>
    void Stop();

    /// <summary>立即执行一次维护（归档 + 删除 + 清理）。失败抛异常，由调用方兜底并提示。</summary>
    /// <returns>本次维护的结果统计。</returns>
    MaintenanceResult RunNow();
}
