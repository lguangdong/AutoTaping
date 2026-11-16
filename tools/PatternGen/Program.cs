// 职责：生成圆点阵标定板 PNG（按 DPI 把 mm 换算成像素，用于标定/模板匹配的离线练习）
// 层：tools（独立控制台工具，不参与 AutoTaping.sln 构建）
// 依赖：OpenCvSharp4、OpenCvSharp4.runtime.win

using System.Globalization;
using OpenCvSharp;

namespace PatternGen;

/// <summary>
/// 圆点阵生成器。长度单位统一为 mm，输出位图的像素尺寸由 <c>--dpi</c> 决定，
/// 因此「按 100% 实际大小打印」后点间距即为标称值（打印后仍建议用量具复核）。
/// </summary>
internal static class Program
{
    /// <summary>一英寸的毫米数（DPI 与 mm 的换算基准）。</summary>
    private const double MmPerInch = 25.4;

    /// <summary>命令行入口。</summary>
    /// <param name="args">形如 <c>--cols 9 --rows 7 --pitch 20 --out dots.png</c>。</param>
    /// <returns>0 成功；1 参数非法；2 写文件失败。</returns>
    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"参数错误：{ex.Message}");
            Console.Error.WriteLine();
            PrintUsage();
            return 1;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        using var image = Render(options);

        var fullPath = Path.GetFullPath(options.OutputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!Cv2.ImWrite(fullPath, image))
        {
            Console.Error.WriteLine($"写入失败：{fullPath}");
            return 2;
        }

        Report(options, image, fullPath);
        return 0;
    }

    /// <summary>按参数绘制点阵并返回灰度图（白底黑点，可用 <c>--invert</c> 反转）。</summary>
    /// <param name="options">命令行参数。</param>
    private static Mat Render(Options options)
    {
        var pxPerMm = options.Dpi / MmPerInch;

        // --origins 时在左侧与上方额外留一条角标带，避免角标压住第一个点
        var originStripMm = options.ShowOrigins ? options.PitchMm * 0.8 : 0.0;
        var leadMm = options.MarginMm + originStripMm;

        var gridWidthMm = (options.Cols - 1) * options.PitchMm;
        var gridHeightMm = (options.Rows - 1) * options.PitchMm;
        var widthMm = leadMm + gridWidthMm + options.MarginMm + options.DiameterMm;
        var heightMm = leadMm + gridHeightMm + options.MarginMm + options.DiameterMm;

        var width = Math.Max(8, (int)Math.Round(widthMm * pxPerMm, MidpointRounding.AwayFromZero));
        var height = Math.Max(8, (int)Math.Round(heightMm * pxPerMm, MidpointRounding.AwayFromZero));

        var background = options.Invert ? (byte)20 : (byte)245;
        var foreground = options.Invert ? (byte)235 : (byte)25;

        var image = new Mat(height, width, MatType.CV_8UC1, Scalar.All(background));

        var radius = Math.Max(2, (int)Math.Round(options.DiameterMm / 2.0 * pxPerMm, MidpointRounding.AwayFromZero));
        var firstCenterMm = leadMm + (options.DiameterMm / 2.0);

        for (var row = 0; row < options.Rows; row++)
        {
            for (var col = 0; col < options.Cols; col++)
            {
                var cx = (int)Math.Round((firstCenterMm + (col * options.PitchMm)) * pxPerMm, MidpointRounding.AwayFromZero);
                var cy = (int)Math.Round((firstCenterMm + (row * options.PitchMm)) * pxPerMm, MidpointRounding.AwayFromZero);
                Cv2.Circle(image, new Point(cx, cy), radius, Scalar.All(foreground), -1, LineTypes.AntiAlias);
            }
        }

        if (originStripMm > 0)
        {
            DrawOriginMarker(image, originStripMm, pxPerMm, foreground);
        }

        if (options.BlurSigma > 0)
        {
            // 模拟镜头轻微离焦：让模板匹配更接近真实相机图像（纯打印用途可设 --blur 0）
            Cv2.GaussianBlur(image, image, new Size(0, 0), options.BlurSigma);
        }

        return image;
    }

    /// <summary>在左上角预留的角标带里画实心方块 + 文字，用于判断板的正方向（防 180° 翻转误配）。</summary>
    /// <param name="image">目标图像。</param>
    /// <param name="stripMm">角标带宽度（mm）。</param>
    /// <param name="pxPerMm">每毫米像素数。</param>
    /// <param name="foreground">前景灰度。</param>
    private static void DrawOriginMarker(Mat image, double stripMm, double pxPerMm, byte foreground)
    {
        var stripPx = (int)Math.Round(stripMm * pxPerMm, MidpointRounding.AwayFromZero);
        var size = Math.Max(8, (int)Math.Round(stripPx * 0.55, MidpointRounding.AwayFromZero));
        var offset = Math.Max(2, (int)Math.Round(stripPx * 0.18, MidpointRounding.AwayFromZero));

        Cv2.Rectangle(image, new Rect(offset, offset, size, size), Scalar.All(foreground), -1);

        var fontScale = Math.Max(0.4, size / 60.0);
        var thickness = Math.Max(1, size / 24);
        var textOrigin = new Point(offset + size + Math.Max(4, size / 4), offset + size);
        Cv2.PutText(image, "TL", textOrigin, HersheyFonts.HersheySimplex, fontScale, Scalar.All(foreground), thickness, LineTypes.AntiAlias);
    }

    /// <summary>打印生成结果摘要（尺寸、等效分辨率、打印提示）。</summary>
    /// <param name="options">命令行参数。</param>
    /// <param name="image">生成的图像。</param>
    /// <param name="path">写入的绝对路径。</param>
    private static void Report(Options options, Mat image, string path)
    {
        var pxPerMm = options.Dpi / MmPerInch;
        var boardWidthMm = image.Width / pxPerMm;
        var boardHeightMm = image.Height / pxPerMm;

        Console.WriteLine($"已生成：{path}");
        Console.WriteLine($"  点阵    ：{options.Cols} 列 × {options.Rows} 行，间距 {options.PitchMm:F2} mm，直径 {options.DiameterMm:F2} mm");
        Console.WriteLine($"  位图    ：{image.Width} × {image.Height} px（{options.Dpi:F0} dpi → {pxPerMm:F3} px/mm）");
        Console.WriteLine($"  等效分辨率：{1 / pxPerMm:F4} mm/px");
        Console.WriteLine($"  板面尺寸：{boardWidthMm:F2} × {boardHeightMm:F2} mm");
        Console.WriteLine(options.ShowOrigins ? "  角标    ：左上角已标记 TL（用于判定正方向）" : "  角标    ：无");
        Console.WriteLine("  打印提示：必须选「实际大小 / 100%」，打印后用卡尺复核点间距；仅做算法练习时无需打印。");
    }

    /// <summary>打印用法说明。</summary>
    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            用法：PatternGen --out <文件> [选项]

              --cols <n>        列数（≥3，默认 9）
              --rows <n>        行数（≥3，默认 7）
              --pitch <mm>      点间距（默认 20）
              --diameter <mm>   点直径（默认 8，必须小于点间距）
              --margin <mm>     四周留白（默认 15）
              --dpi <n>         输出分辨率（默认 150；72 dpi ≈ 2.83 px/mm）
              --blur <sigma>    高斯模糊 sigma（默认 0.8，模拟镜头离焦；0 = 关闭）
              --invert          黑底白点（默认白底黑点）
              --origins         左上角画 TL 角标，便于判断正方向
              --out <path>      输出 PNG 路径（默认 dot-grid.png）
              -h, --help        显示本帮助

            示例：
              PatternGen --cols 9 --rows 7 --pitch 50 --diameter 12 --dpi 72 --origins --out dot-grid-50mm.png
            """);
    }

    /// <summary>命令行参数集合（全部带默认值，解析时逐项校验范围）。</summary>
    private sealed class Options
    {
        /// <summary>列数。</summary>
        public int Cols { get; private set; } = 9;

        /// <summary>行数。</summary>
        public int Rows { get; private set; } = 7;

        /// <summary>点间距（mm）。</summary>
        public double PitchMm { get; private set; } = 20.0;

        /// <summary>点直径（mm）。</summary>
        public double DiameterMm { get; private set; } = 8.0;

        /// <summary>四周留白（mm）。</summary>
        public double MarginMm { get; private set; } = 15.0;

        /// <summary>输出 DPI。</summary>
        public double Dpi { get; private set; } = 150.0;

        /// <summary>高斯模糊 sigma（0 表示不模糊）。</summary>
        public double BlurSigma { get; private set; } = 0.8;

        /// <summary>是否黑底白点。</summary>
        public bool Invert { get; private set; }

        /// <summary>是否绘制左上角方向标记。</summary>
        public bool ShowOrigins { get; private set; }

        /// <summary>是否只显示帮助。</summary>
        public bool ShowHelp { get; private set; }

        /// <summary>输出文件路径。</summary>
        public string OutputPath { get; private set; } = "dot-grid.png";

        /// <summary>解析命令行参数。</summary>
        /// <param name="args">原始参数数组。</param>
        /// <exception cref="ArgumentException">参数未知、缺值或超出允许范围。</exception>
        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var key = args[i];

                string NextValue()
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"{key} 缺少取值");
                    }

                    return args[++i];
                }

                switch (key)
                {
                    case "-h":
                    case "--help":
                        options.ShowHelp = true;
                        break;
                    case "--cols":
                        options.Cols = ReadInt(NextValue(), key, 3, 200);
                        break;
                    case "--rows":
                        options.Rows = ReadInt(NextValue(), key, 3, 200);
                        break;
                    case "--pitch":
                        options.PitchMm = ReadDouble(NextValue(), key, 0.5, 1000);
                        break;
                    case "--diameter":
                        options.DiameterMm = ReadDouble(NextValue(), key, 0.1, 1000);
                        break;
                    case "--margin":
                        options.MarginMm = ReadDouble(NextValue(), key, 0, 1000);
                        break;
                    case "--dpi":
                        options.Dpi = ReadDouble(NextValue(), key, 30, 2400);
                        break;
                    case "--blur":
                        options.BlurSigma = ReadDouble(NextValue(), key, 0, 20);
                        break;
                    case "--invert":
                        options.Invert = true;
                        break;
                    case "--origins":
                        options.ShowOrigins = true;
                        break;
                    case "--out":
                        options.OutputPath = NextValue();
                        break;
                    default:
                        throw new ArgumentException($"未知参数：{key}");
                }
            }

            if (options.DiameterMm >= options.PitchMm)
            {
                throw new ArgumentException("点直径必须小于点间距，否则圆点会连成一片");
            }

            return options;
        }

        /// <summary>读取并校验整数参数。</summary>
        /// <param name="text">原始文本。</param>
        /// <param name="key">参数名（用于报错）。</param>
        /// <param name="min">最小值（含）。</param>
        /// <param name="max">最大值（含）。</param>
        private static int ReadInt(string text, string key, int min, int max)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            {
                throw new ArgumentException($"{key} 需要 {min} ~ {max} 的整数，实际是「{text}」");
            }

            return value;
        }

        /// <summary>读取并校验浮点参数。</summary>
        /// <param name="text">原始文本。</param>
        /// <param name="key">参数名（用于报错）。</param>
        /// <param name="min">最小值（含）。</param>
        /// <param name="max">最大值（含）。</param>
        private static double ReadDouble(string text, string key, double min, double max)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            {
                throw new ArgumentException($"{key} 需要 {min} ~ {max} 的数值，实际是「{text}」");
            }

            return value;
        }
    }
}
