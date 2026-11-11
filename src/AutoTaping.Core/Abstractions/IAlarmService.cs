// 职责：报警触发抽象——流程 / 状态机 / 协调器统一报警入口（触发、5 秒去重、恢复、确认、导出）
// 层：Core/Abstractions
// 依赖：AlarmLevel、AlarmRecord、AlarmRaisedEventArgs（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 报警服务抽象。触发与去重规则见 <c>docs/09-报警与参数清单.md</c> 第三节：
/// 同一 <c>Code</c> 在 5 秒内重复触发不新增记录；条件消失时写恢复时间并从「当前」列表移除；
/// 所有等级 3 报警必须人工确认后才能复位。
/// </summary>
public interface IAlarmService
{
    /// <summary>新报警落库后触发（去重窗口内的重复触发不触发本事件）。在调用线程上同步触发。</summary>
    event EventHandler<AlarmRaisedEventArgs>? AlarmRaised;

    /// <summary>触发一条报警并落库（5 秒内同代码重复触发只累加计数，不新增记录）。</summary>
    /// <param name="code">报警码，如 <c>AL100</c>。</param>
    /// <param name="level">报警级别。</param>
    /// <param name="message">面向操作工的中文内容。</param>
    /// <param name="source">报警来源，如 <c>视觉</c> / <c>贴附头</c>。</param>
    void Raise(string code, AlarmLevel level, string message, string source = "");

    /// <summary>是否存在未确认且未恢复的严重报警（用于启动 / 继续 / 复位的互锁）。</summary>
    bool HasUnackedCritical();

    /// <summary>关闭所有仍在生效的报警（写恢复时间），用于复位 / 停止后清理。</summary>
    void CloseAllActive();

    /// <summary>取当前报警列表（未确认且未恢复），按开始时间倒序。</summary>
    IReadOnlyList<AlarmRecord> GetActiveAlarms();

    /// <summary>按时间区间查询历史报警（含已确认 / 已恢复），按开始时间倒序。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    IReadOnlyList<AlarmRecord> QueryHistory(DateTime from, DateTime to);

    /// <summary>确认单条报警：写确认时间与确认人，从「当前」列表移除。</summary>
    /// <param name="id">报警主键。</param>
    /// <param name="user">确认人用户名。</param>
    void Ack(int id, string user);

    /// <summary>确认当前列表中的全部报警（08-页面清单 页面 5：仅工程师可用）。</summary>
    /// <param name="user">确认人用户名。</param>
    /// <returns>实际确认的条数。</returns>
    int AckAll(string user);

    /// <summary>报警条件消失时自动恢复：写恢复时间，从「当前」列表移除（09 文档第三节）。</summary>
    /// <param name="code">报警码；没有生效中的同类报警时安全无操作。</param>
    void Recover(string code);

    /// <summary>导出区间内报警记录为 CSV（UTF-8 with BOM，Excel 打开中文不乱码）。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    /// <param name="path">目标 CSV 文件路径。</param>
    void ExportCsv(DateTime from, DateTime to, string path);
}
