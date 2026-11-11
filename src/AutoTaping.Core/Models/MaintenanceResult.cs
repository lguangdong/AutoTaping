// 职责：每日维护任务的结果值对象（归档条数、删除条数、生成的归档文件）
// 层：Core/Models
// 依赖：无（纯数据）

namespace AutoTaping.Core.Models;

/// <summary>
/// 一次维护任务的结果。归档条数指「先写成 CSV 再删除」的记录数，
/// 用于在系统设置页向工程师反馈本次维护做了什么（08-页面清单 页面 8）。
/// </summary>
/// <param name="RunAt">执行时刻。</param>
/// <param name="ArchivedOutputCount">归档的产出记录条数。</param>
/// <param name="ArchivedAlarmCount">归档的报警记录条数。</param>
/// <param name="ArchivedOperationLogCount">归档的操作日志条数。</param>
/// <param name="DeletedCalibrationCount">删除的过期标定记录条数。</param>
/// <param name="DeletedLogFileCount">删除的过期日志文件数。</param>
/// <param name="ArchiveFiles">本次生成的归档文件（绝对路径）。</param>
public sealed record MaintenanceResult(
    DateTime RunAt,
    int ArchivedOutputCount,
    int ArchivedAlarmCount,
    int ArchivedOperationLogCount,
    int DeletedCalibrationCount,
    int DeletedLogFileCount,
    IReadOnlyList<string> ArchiveFiles)
{
    /// <summary>归档记录总条数。</summary>
    public int ArchivedTotalCount => ArchivedOutputCount + ArchivedAlarmCount + ArchivedOperationLogCount;
}
