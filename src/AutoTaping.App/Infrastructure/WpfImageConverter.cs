// 职责：OpenCV Mat → WPF BitmapSource 的显示转换（跨线程 Freeze）
// 层：App/Infrastructure
// 依赖：OpenCvSharp4、System.Windows.Media（WPF）；Mat → BitmapSource 属于表现层，
//       故放在 App 而非 Vision，保持 Vision 只依赖 OpenCvSharp（04 文档约束 1）

using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// Mat 到 WPF 显示对象的转换。转换后必须 <see cref="Freezable.Freeze"/>，
/// 否则跨线程（取图线程 → UI 线程）使用会抛异常（10-编码约定 五、第 1 条）。
/// </summary>
public static class WpfImageConverter
{
    /// <summary>Mat（灰度或彩色）→ BitmapSource（统一转 Gray8）。</summary>
    /// <param name="mat">源图。空图返回 1x1 占位。</param>
    public static BitmapSource ToBitmapSource(Mat mat)
    {
        ArgumentNullException.ThrowIfNull(mat);

        if (mat.Empty())
        {
            return CreatePlaceholder();
        }

        Mat gray = mat;
        if (mat.Channels() != 1)
        {
            gray = new Mat();
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
        }

        try
        {
            var stride = (int)gray.Step();
            var bitmap = BitmapSource.Create(
                gray.Width, gray.Height, 96, 96,
                PixelFormats.Gray8, null, gray.Data, stride * gray.Height, stride);

            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (!ReferenceEquals(gray, mat))
            {
                gray.Dispose();
            }
        }
    }

    /// <summary>1x1 占位图（空图 / 未连接时显示）。</summary>
    private static BitmapSource CreatePlaceholder()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Gray8, null, new byte[1], 1);
        bitmap.Freeze();
        return bitmap;
    }
}
