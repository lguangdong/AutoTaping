// 职责：贴附路径上的一个点（相对贴附基准的偏移量，mm）
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 贴附路径点。坐标相对贴附基准（<see cref="PositionParameters.TapeBaseX"/> 等），单位 mm。
/// </summary>
/// <param name="X">横向偏移（mm）。</param>
/// <param name="Y">纵向偏移（mm）。</param>
/// <param name="Z">该点的贴附高度（mm）。</param>
public readonly record struct TapePathPoint(double X, double Y, double Z);
