// 职责：每日维护任务的配置项（归档目录、各表保留天数、标定保留条数、执行时刻），来自 appsettings.json 的 Maintenance 段
// 层：Core/Models
// 依赖：无（纯数据）

namespace AutoTaping.Core.Models;

/// <summary>
/// 每日维护配置。保留策略与 <c>docs/06-数据模型与数据库.md</c> 第 7 节一致：
/// <c>OutputRecord</c> 90 天、<c>AlarmRecord</c> 180 天、<c>OperationLog</c> 365 天、
/// <c>Calibration</c> 只保留最近 20 条；过期数据先归档成 CSV 再删除。
/// </summary>
public sealed class MaintenanceOptions
{
    /// <summary>每天执行维护的时刻（0 ~ 23 点，整点）。文档要求 02:00。</summary>
    public int RunHourOfDay { get; set; } = 2;

    /// <summary>归档 CSV 的输出目录（相对程序目录或绝对路径）。</summary>
    public string ArchiveDirectory { get; set; } = "data/archive";

    /// <summary>日志目录（相对程序目录或绝对路径），用于清理过期日志文件。</summary>
    public string LogDirectory { get; set; } = "logs";

    /// <summary>产出记录保留天数。</summary>
    public int OutputRetainDays { get; set; } = 90;

    /// <summary>报警记录保留天数。</summary>
    public int AlarmRetainDays { get; set; } = 180;

    /// <summary>操作日志保留天数。</summary>
    public int OperationLogRetainDays { get; set; } = 365;

    /// <summary>标定记录保留条数（超出删除最旧，含标定原图目录）。</summary>
    public int CalibrationKeepCount { get; set; } = 20;

    /// <summary>日志文件保留天数（Serilog 另有按文件数保留策略，这里是按天数的兜底清理）。</summary>
    public int LogRetainDays { get; set; } = 90;
}
