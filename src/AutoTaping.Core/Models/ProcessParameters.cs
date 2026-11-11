// 职责：工艺参数分组（速度、高度、压力、延时），默认值取自 docs/09-报警与参数清单 4.1
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 工艺参数。键名与 <c>docs/09-报警与参数清单.md</c> 4.1 节一一对应，范围校验（<c>ParameterValidator</c>）在 S7 实现。
/// </summary>
public sealed class ProcessParameters
{
    /// <summary>安全高度（mm），范围 10 ~ 80。</summary>
    public double SafeHeightMm { get; set; } = 50.0;

    /// <summary>贴附高度（mm），范围 0 ~ 10。</summary>
    public double TapeHeightMm { get; set; } = 2.0;

    /// <summary>移动速度（mm/s），范围 10 ~ 300。</summary>
    public double MoveSpeedMmPerSec { get; set; } = 100.0;

    /// <summary>贴附速度（mm/s），范围 10 ~ 200。</summary>
    public double TapeSpeedMmPerSec { get; set; } = 80.0;

    /// <summary>旋转速度（°/s），范围 10 ~ 180。</summary>
    public double RotateSpeedDegPerSec { get; set; } = 90.0;

    /// <summary>保压时间（ms），范围 100 ~ 3000。</summary>
    public int HoldPressureMs { get; set; } = 500;

    /// <summary>贴附压力（MPa），范围 0.10 ~ 0.40。</summary>
    public double HoldPressureMpa { get; set; } = 0.25;

    /// <summary>真空等待时间（ms），范围 500 ~ 5000。</summary>
    public int VacuumWaitMs { get; set; } = 1500;

    /// <summary>吹气（破真空）时间（ms），范围 100 ~ 1000。</summary>
    public int BlowOffMs { get; set; } = 300;
}
