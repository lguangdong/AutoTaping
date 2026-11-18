// 职责：视觉帧快照——最近一次拍照的 Mono8 像素、尺寸与版本号，供主画面显示缩略图
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 视觉帧快照。像素为紧凑排列的 Mono8（一像素一字节），行宽 = <see cref="Width"/>。
/// 由协调器在拍照步骤完成后整体替换，持有独立副本，界面可在任意线程安全转换。
/// </summary>
public sealed class VisionFrameSnapshot
{
    /// <summary>Mono8 像素数据（长度 = Width × Height）。</summary>
    public byte[] Mono8 { get; init; } = Array.Empty<byte>();

    /// <summary>图像宽度（像素）。</summary>
    public int Width { get; init; }

    /// <summary>图像高度（像素）。</summary>
    public int Height { get; init; }

    /// <summary>帧版本号（每拍自增）；界面据此判断是否需要重新转换图像。</summary>
    public int Version { get; init; }

    /// <summary>该帧的识别是否成功（失败时界面标红提示）。</summary>
    public bool Success { get; init; }
}
