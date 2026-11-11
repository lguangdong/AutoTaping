// 职责：报警仓储抽象（签名与 docs/06-数据模型与数据库 第 5 节完全一致，不得私自变更）
// 层：Core/Abstractions
// 依赖：AlarmRecord（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 报警仓储。去重、恢复、确认等业务规则由 S7 的 <c>AlarmService</c> 负责，本接口只做持久化。
/// </summary>
public interface IAlarmRepository
{
    /// <summary>新增一条报警记录。</summary>
    /// <param name="record">待写入记录。</param>
    void Insert(AlarmRecord record);

    /// <summary>确认报警：写确认时间与确认人，并置 <c>IsAcked = 1</c>。</summary>
    /// <param name="id">报警主键。</param>
    /// <param name="user">确认人用户名。</param>
    void Ack(int id, string user);

    /// <summary>关闭所有仍在生效的报警（写 <c>EndTime</c>），用于停止/复位后清理。</summary>
    void CloseAllActive();

    /// <summary>关闭单条报警（写 <c>EndTime</c>），用于报警条件消失后的自动恢复（09 文档第三节）。</summary>
    /// <param name="id">报警主键。</param>
    /// <param name="endTime">恢复时间。</param>
    void Close(int id, DateTime endTime);

    /// <summary>取当前生效的报警（未确认且未恢复），按开始时间倒序。报警页「当前报警」列表用。</summary>
    IReadOnlyList<AlarmRecord> QueryActive();

    /// <summary>按时间区间查询报警记录（开始时间倒序）。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    /// <param name="onlyUnacked">仅返回未确认记录。</param>
    IReadOnlyList<AlarmRecord> Query(DateTime from, DateTime to, bool onlyUnacked = false);

    /// <summary>统计未确认且未恢复、级别不低于 <paramref name="minLevel"/> 的报警数（用于启动互锁判断）。</summary>
    /// <param name="minLevel">最低级别（含）。</param>
    int CountUnacked(int minLevel);

    /// <summary>导出区间内报警记录为 CSV（UTF-8 with BOM，Excel 打开中文不乱码）。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    /// <param name="path">目标 CSV 文件路径。</param>
    void ExportCsv(DateTime from, DateTime to, string path);
}
