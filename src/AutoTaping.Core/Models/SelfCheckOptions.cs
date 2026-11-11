// 职责：启动自检的配置项（数据目录、磁盘剩余下限、巡检周期），来自 appsettings.json 的 SelfCheck 段
// 层：Core/Models
// 依赖：无（纯数据）

namespace AutoTaping.Core.Models;

/// <summary>
/// 启动自检配置。磁盘剩余下限对应 <c>docs/09-报警与参数清单.md</c> 的 AL501（默认 1 GB），
/// 巡检周期用于运行中持续检测磁盘与数据库可写性，并在条件恢复时自动关闭报警。
/// </summary>
public sealed class SelfCheckOptions
{
    /// <summary>数据目录（相对程序目录或绝对路径）；磁盘剩余空间按它所在的分区计算。</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>磁盘剩余空间下限（GB），低于该值报警 AL501。</summary>
    public double MinFreeDiskGb { get; set; } = 1.0;

    /// <summary>运行中巡检周期（秒）。</summary>
    public int IntervalSeconds { get; set; } = 60;
}
