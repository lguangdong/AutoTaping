// 职责：贴附路径模式枚举
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 贴附路径模式。JSON 中序列化为字符串（<c>"Line"</c> / <c>"Polyline"</c> / <c>"Arc"</c>）。
/// </summary>
public enum TapePathMode
{
    /// <summary>直线：从起点走到终点。</summary>
    Line = 0,

    /// <summary>折线：依次经过各路径点。</summary>
    Polyline = 1,

    /// <summary>圆弧：按圆心、半径、起止角走弧。</summary>
    Arc = 2
}
