// 职责：像素坐标系下的矩形区域（搜索 ROI / 裁剪区域）
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 像素坐标矩形：左上角 <paramref name="X"/>, <paramref name="Y"/> 加宽高。
/// 字段名与 <c>docs/06-数据模型与数据库.md</c> 第 4 节的 <c>{"x":..,"y":..,"w":..,"h":..}</c> 一致。
/// </summary>
/// <param name="X">左上角横坐标（像素）。</param>
/// <param name="Y">左上角纵坐标（像素）。</param>
/// <param name="W">宽度（像素），必须大于 0。</param>
/// <param name="H">高度（像素），必须大于 0。</param>
public readonly record struct RoiRect(int X, int Y, int W, int H)
{
    /// <summary>是否为有效区域（宽高都大于 0）。</summary>
    public bool IsValid => W > 0 && H > 0;
}
