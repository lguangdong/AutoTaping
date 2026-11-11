// 职责：配方参数根对象，对应 Recipe.JsonData 列 JSON 的顶层结构
// 层：Core/Models
// 依赖：各参数分组模型

namespace AutoTaping.Core.Models;

/// <summary>
/// 配方参数集合。JSON 顶层键为 <c>process</c> / <c>position</c> / <c>vision</c> / <c>alarm</c>
/// （见 <c>docs/06-数据模型与数据库.md</c> 第 4 节）。
/// 所有分组都带默认值，JSON 缺字段时不会抛异常；旧配方里已废弃的贴附路径节点会被静默忽略（点贴无路径）。
/// </summary>
public sealed class RecipeParameters
{
    /// <summary>工艺参数。</summary>
    public ProcessParameters Process { get; set; } = new();

    /// <summary>位置参数。</summary>
    public PositionParameters Position { get; set; } = new();

    /// <summary>视觉参数。</summary>
    public VisionParameters Vision { get; set; } = new();

    /// <summary>报警阈值参数。</summary>
    public AlarmParameters Alarm { get; set; } = new();
}
