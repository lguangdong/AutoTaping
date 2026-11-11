// 职责：Mono8 原始数据 → OpenCV Mat 的转换，以及 ROI 裁剪保存模板 PNG
// 层：Vision
// 依赖：OpenCvSharp4（Mat → BitmapSource 属于表现层，放在 App，因为 Vision 只依赖 OpenCvSharp，见 04 文档约束 1）

using System.IO;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace AutoTaping.Vision;

/// <summary>
/// 图像转换工具。像素格式约定：工业黑白相机输出 Mono8（一像素一字节），
/// 对应 OpenCV 的 <see cref="MatType.CV_8UC1"/> 单通道灰度图。
/// </summary>
public static class ImageConverter
{
    /// <summary>Mono8 原始数据 → 灰度 Mat（拷贝数据，不共享缓冲区，调用方无需保证 data 生命周期）。</summary>
    /// <param name="mono8">灰度原始数据，长度至少 width*height。</param>
    /// <param name="width">图像宽度（像素）。</param>
    /// <param name="height">图像高度（像素）。</param>
    /// <exception cref="ArgumentException">尺寸非法或数据长度不足。</exception>
    public static Mat ToMat(byte[] mono8, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(mono8);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"图像尺寸非法：{width}x{height}", nameof(width));
        }

        var expected = width * height;
        if (mono8.Length < expected)
        {
            throw new ArgumentException($"Mono8 数据长度不足：期望至少 {expected}，实际 {mono8.Length}");
        }

        var mat = new Mat(height, width, MatType.CV_8UC1);
        Marshal.Copy(mono8, 0, mat.Data, expected);
        return mat;
    }

    /// <summary>裁剪 ROI 并保存为 PNG 模板图（自动创建目录）。</summary>
    /// <param name="image">源灰度图。</param>
    /// <param name="roi">裁剪区域（像素），必须落在图像内。</param>
    /// <param name="path">目标 PNG 路径，相对路径按当前工作目录转绝对路径。</param>
    /// <exception cref="ArgumentException">图像为空或 ROI 越界。</exception>
    public static void SaveRoiPng(Mat image, Rect roi, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(path);

        if (image.Empty())
        {
            throw new ArgumentException("图像为空，无法保存模板", nameof(image));
        }

        if (roi.Width <= 0 || roi.Height <= 0)
        {
            throw new ArgumentException($"ROI 尺寸非法：{roi.Width}x{roi.Height}", nameof(roi));
        }

        if (roi.X < 0 || roi.Y < 0 || roi.Right > image.Width || roi.Bottom > image.Height)
        {
            throw new ArgumentException(
                $"ROI {roi} 越界：图像尺寸 {image.Width}x{image.Height}", nameof(roi));
        }

        using var roiMat = new Mat(image, roi);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Cv2.ImWrite(fullPath, roiMat);
    }
}
