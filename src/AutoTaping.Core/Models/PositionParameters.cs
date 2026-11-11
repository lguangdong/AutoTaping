// 职责：位置参数分组（拍照站位、贴附基准、料盘取料位、相机偏置、旋转中心），默认值取自 docs/09 第 4.2 节
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 位置参数（**头坐标**，单位 mm；R 为角度，单位 °）。
/// 产线形态下 X/Y 是龙门坐标，不再随工件移动。
/// </summary>
public sealed class PositionParameters
{
    /// <summary>
    /// 拍照位 X（mm）——**头站位**：站到这里，相机光轴正好落在工位中心。
    /// 若相机与吸嘴有偏置，则 <c>PhotoPos = TapeBase − CameraOffset</c>。
    /// </summary>
    public double PhotoPosX { get; set; } = 156.0;

    /// <summary>拍照位 Y（mm）——同上。</summary>
    public double PhotoPosY { get; set; } = 200.0;

    /// <summary>贴附基准 X（mm）——吸嘴落点基准（工位中心）。</summary>
    public double TapeBaseX { get; set; } = 200.0;

    /// <summary>贴附基准 Y（mm）。</summary>
    public double TapeBaseY { get; set; } = 200.0;

    /// <summary>贴附基准角度（°），范围 -180 ~ 180。</summary>
    public double TapeBaseR { get; set; }

    /// <summary>料盘取料位 X（mm）——头过去吸一片胶带的位置。</summary>
    public double PickPosX { get; set; } = 200.0;

    /// <summary>料盘取料位 Y（mm）。</summary>
    public double PickPosY { get; set; } = 80.0;

    /// <summary>相机与吸嘴的 X 偏置（mm，**手眼标定值**）：吸嘴中心 − 相机光轴。</summary>
    public double CameraOffsetX { get; set; }

    /// <summary>相机与吸嘴的 Y 偏置（mm，**手眼标定值**）：吸嘴中心 − 相机光轴。</summary>
    public double CameraOffsetY { get; set; } = 44.0;

    /// <summary>旋转中心 X（mm）——R 轴旋转中心，通常与吸嘴中心重合。</summary>
    public double RotationCenterX { get; set; } = 200.0;

    /// <summary>旋转中心 Y（mm）。</summary>
    public double RotationCenterY { get; set; } = 200.0;
}
