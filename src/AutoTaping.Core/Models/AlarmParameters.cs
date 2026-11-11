// 职责：报警阈值参数分组（真空等待超时、位置容差）
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 报警阈值参数，对应 JSON 的 <c>alarm</c> 节点。
/// </summary>
public sealed class AlarmParameters
{
    /// <summary>开真空后等待 DI 吸住确认的超时时间（ms），超时报警 AL301。</summary>
    public int VacuumTimeoutMs { get; set; } = 1500;

    /// <summary>位置到位容差（mm），超过视为贴附位置偏差（AL305）。</summary>
    public double PositionToleranceMm { get; set; } = 0.05;
}
