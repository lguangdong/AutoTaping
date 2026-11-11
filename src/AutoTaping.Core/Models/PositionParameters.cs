// 职责：位置参数分组（拍照位、贴附基准、下料位、旋转中心），默认值取自 docs/06-数据模型与数据库 第 4 节
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 位置参数（平台坐标，单位 mm；R 为角度，单位 °）。
/// </summary>
public sealed class PositionParameters
{
    /// <summary>拍照位 X（mm）。</summary>
    public double PhotoPosX { get; set; } = 150.0;

    /// <summary>拍照位 Y（mm）。</summary>
    public double PhotoPosY { get; set; } = 200.0;

    /// <summary>贴附基准 X（mm）。</summary>
    public double TapeBaseX { get; set; } = 220.0;

    /// <summary>贴附基准 Y（mm）。</summary>
    public double TapeBaseY { get; set; } = 200.0;

    /// <summary>贴附基准角度（°），范围 -180 ~ 180。</summary>
    public double TapeBaseR { get; set; }

    /// <summary>下料位 X（mm）。</summary>
    public double UnloadX { get; set; } = 350.0;

    /// <summary>下料位 Y（mm）。</summary>
    public double UnloadY { get; set; } = 200.0;

    /// <summary>旋转中心 X（mm）。</summary>
    public double RotationCenterX { get; set; } = 220.0;

    /// <summary>旋转中心 Y（mm）。</summary>
    public double RotationCenterY { get; set; } = 200.0;
}
