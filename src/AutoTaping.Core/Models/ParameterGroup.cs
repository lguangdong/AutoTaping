// 职责：配方参数分组枚举（工艺 / 位置 / 视觉 / 贴附路径 / 报警阈值），与 08 文档参数页左侧分组树一致
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 配方参数分组。<c>docs/08-页面清单与交互.md</c> 页面 3 的左侧分组树按本枚举顺序显示，
/// 每个分组的键与 <c>docs/06-数据模型与数据库.md</c> 第 4 节 JSON 的顶层键一一对应。
/// </summary>
public enum ParameterGroup
{
    /// <summary>工艺参数（JSON 键 <c>process</c>）。</summary>
    Process = 0,

    /// <summary>位置参数（JSON 键 <c>position</c>）。</summary>
    Position = 1,

    /// <summary>视觉参数（JSON 键 <c>vision</c>）。</summary>
    Vision = 2,

    /// <summary>贴附路径（JSON 键 <c>tapePath</c>）：模式 + 路径点 + 圆弧参数。</summary>
    TapePath = 3,

    /// <summary>报警阈值（JSON 键 <c>alarm</c>）。</summary>
    Alarm = 4
}
