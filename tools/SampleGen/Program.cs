// 职责：生成模拟相机拍摄的工件图片序列（随机 ΔX/ΔY/Δθ），并输出真值 CSV 供纠偏算法回归验证
// 层：tools（独立控制台工具，不参与 AutoTaping.sln 构建）
// 依赖：OpenCvSharp4、OpenCvSharp4.runtime.win

using System.Globalization;
using System.Text;
using OpenCvSharp;

namespace SampleGen;

/// <summary>
/// 工件采样图生成器。先画一张"标准姿态"的工件（板 + 两个实心圆 MARK），
/// 再对整张图做随机旋转 + 平移并叠加模糊/噪声，得到带真值的模拟相机画面。
/// 真值口径与 <c>CorrectionService.Compute</c> 一致，可直接用来验证纠偏链路。
/// </summary>
internal static class Program
{
    /// <summary>MARK1 标准姿态像素坐标（与配方默认 templateMark1U/V 一致）。</summary>
    private static readonly Point2f NominalMark1 = new(400, 500);

    /// <summary>MARK2 标准姿态像素坐标（与配方默认 templateMark2U/V 一致）。</summary>
    private static readonly Point2f NominalMark2 = new(880, 500);

    /// <summary>MARK1 实心圆半径（像素）：大圆。</summary>
    private const int Mark1Radius = 12;

    /// <summary>MARK2 实心圆半径（像素）：小圆，与 MARK1 差 5px（≈2.5mm @4px/mm），靠直径区分身份。</summary>
    private const int Mark2Radius = 7;

    /// <summary>命令行入口。</summary>
    /// <param name="args">形如 <c>--count 20 --out samples --verify</c>。</param>
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

        Directory.CreateDirectory(options.OutputDirectory);

        using var clean = RenderWorkpiece(options);
        using var template = CropTemplate(clean);

        var random = new Random(options.Seed);
        var rows = new List<SampleRow>(options.Count);

        for (var i = 0; i < options.Count; i++)
        {
            var row = GenerateOne(options, clean, template, random, i + 1);
            rows.Add(row);
        }

        var csvPath = WriteCsv(options, rows);
        Report(options, rows, csvPath);

        if (options.Verify)
        {
            Verify(options, template, rows);
        }

        return 0;
    }

    /// <summary>绘制标准姿态的工件底图（背景 + 板 + 两个 MARK + 一行标识文字）。</summary>
    /// <param name="options">命令行参数。</param>
    private static Mat RenderWorkpiece(Options options)
    {
        var image = new Mat(options.Height, options.Width, MatType.CV_8UC1, Scalar.All(options.Background));

        // 工件本体：以画面中心为旋转中心，覆盖两个 MARK
        var plate = new Rect(options.Width / 2 - 300, options.Height / 2 - 90, 600, 180);
        Cv2.Rectangle(image, plate, Scalar.All(205), -1);
        Cv2.Rectangle(image, plate, Scalar.All(70), 2, LineTypes.AntiAlias);

        DrawFilledCircle(image, NominalMark1, radius: Mark1Radius, gray: 25);
        DrawFilledCircle(image, NominalMark2, radius: Mark2Radius, gray: 25);

        Cv2.PutText(image, "DEMO", new Point(plate.X + 24, plate.Bottom - 18),
            HersheyFonts.HersheySimplex, 0.8, Scalar.All(90), 2, LineTypes.AntiAlias);

        return image;
    }

    /// <summary>
    /// 画一个实心圆 MARK。半径取整数、直径 <c>2r+1</c> 为奇数，
    /// 保证圆的几何中心正好落在整数像素上（否则会引入 0.5px 系统性偏差，自检时表现为固定误差）。
    /// 实心圆对旋转不敏感，因此样本里的 Δθ 不会再拉低匹配得分。
    /// </summary>
    /// <param name="image">目标图像。</param>
    /// <param name="center">圆心。</param>
    /// <param name="radius">半径（像素）。</param>
    /// <param name="gray">灰度值。</param>
    private static void DrawFilledCircle(Mat image, Point2f center, int radius, byte gray)
    {
        var cx = (int)Math.Round(center.X);
        var cy = (int)Math.Round(center.Y);

        Cv2.Circle(image, new Point(cx, cy), radius, Scalar.All(gray), -1, LineTypes.AntiAlias);
    }

    /// <summary>从底图上裁出 MARK1 模板（用于 <c>--verify</c> 自检）。</summary>
    /// <param name="clean">标准姿态底图。</param>
    private static Mat CropTemplate(Mat clean)
    {
        const int half = 20;
        var rect = new Rect(
            (int)NominalMark1.X - half,
            (int)NominalMark1.Y - half,
            (half * 2) + 1,
            (half * 2) + 1);

        return new Mat(clean, rect).Clone();
    }

    /// <summary>生成一张样本：随机旋转 + 平移 + 模糊 + 噪声，落盘并返回真值行。</summary>
    /// <param name="options">命令行参数。</param>
    /// <param name="clean">标准姿态底图。</param>
    /// <param name="template">MARK1 模板（未使用，保留给扩展）。</param>
    /// <param name="random">随机数源。</param>
    /// <param name="index">样本序号（从 1 开始）。</param>
    private static SampleRow GenerateOne(Options options, Mat clean, Mat template, Random random, int index)
    {
        _ = template;

        var shiftXPx = ((random.NextDouble() * 2) - 1) * options.DeltaMm * options.PxPerMm;
        var shiftYPx = ((random.NextDouble() * 2) - 1) * options.DeltaMm * options.PxPerMm;
        var rotateDeg = ((random.NextDouble() * 2) - 1) * options.DeltaDeg;

        var center = new Point2f(options.Width / 2f, options.Height / 2f);
        using var matrix = Cv2.GetRotationMatrix2D(center, rotateDeg, 1.0);
        matrix.Set<double>(0, 2, matrix.At<double>(0, 2) + shiftXPx);
        matrix.Set<double>(1, 2, matrix.At<double>(1, 2) + shiftYPx);

        using var warped = new Mat();
        Cv2.WarpAffine(clean, warped, matrix, new Size(options.Width, options.Height),
            InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(options.Background));

        if (options.BlurSigma > 0)
        {
            Cv2.GaussianBlur(warped, warped, new Size(0, 0), options.BlurSigma);
        }

        if (options.NoiseSigma > 0)
        {
            using var noise = new Mat(warped.Size(), MatType.CV_8UC1);
            Cv2.Randn(noise, Scalar.All(0), Scalar.All(options.NoiseSigma));
            Cv2.Add(warped, noise, warped);
        }

        var fileName = $"sample-{index:D4}.png";
        var filePath = Path.Combine(options.OutputDirectory, fileName);
        if (!Cv2.ImWrite(filePath, warped))
        {
            throw new IOException($"写入失败：{filePath}");
        }

        // 真值：把标准姿态的两个 MARK 用同一变换矩阵映射，再按 CorrectionService 的口径换算
        var mark1 = Transform(matrix, NominalMark1);
        var mark2 = Transform(matrix, NominalMark2);

        var midCurrentX = (mark1.X + mark2.X) / 2.0;
        var midCurrentY = (mark1.Y + mark2.Y) / 2.0;
        var midNominalX = (NominalMark1.X + NominalMark2.X) / 2.0;
        var midNominalY = (NominalMark1.Y + NominalMark2.Y) / 2.0;

        var angleCurrent = Math.Atan2(mark2.Y - mark1.Y, mark2.X - mark1.X) * 180.0 / Math.PI;
        var angleNominal = Math.Atan2(NominalMark2.Y - NominalMark1.Y, NominalMark2.X - NominalMark1.X) * 180.0 / Math.PI;

        return new SampleRow(
            Index: index,
            FileName: fileName,
            DeltaXMm: (midCurrentX - midNominalX) / options.PxPerMm,
            DeltaYMm: -(midCurrentY - midNominalY) / options.PxPerMm,   // 图像 v 向下，机械 Y 向上
            DeltaThetaDeg: angleCurrent - angleNominal,                  // 与 CorrectionService 同口径（图像角度差）
            ShiftXPx: shiftXPx,
            ShiftYPx: shiftYPx,
            RotateDeg: rotateDeg,
            Mark1X: mark1.X,
            Mark1Y: mark1.Y);
    }

    /// <summary>用仿射矩阵变换一个点。</summary>
    /// <param name="matrix">2×3 仿射矩阵。</param>
    /// <param name="point">输入点。</param>
    private static Point2f Transform(Mat matrix, Point2f point)
    {
        var x = (matrix.At<double>(0, 0) * point.X) + (matrix.At<double>(0, 1) * point.Y) + matrix.At<double>(0, 2);
        var y = (matrix.At<double>(1, 0) * point.X) + (matrix.At<double>(1, 1) * point.Y) + matrix.At<double>(1, 2);
        return new Point2f((float)x, (float)y);
    }

    /// <summary>输出真值 CSV（UTF-8 with BOM，Excel 打开中文不乱码）。</summary>
    /// <param name="options">命令行参数。</param>
    /// <param name="rows">真值行。</param>
    /// <returns>CSV 绝对路径。</returns>
    private static string WriteCsv(Options options, IReadOnlyList<SampleRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("index,file,deltaXmm,deltaYmm,deltaThetaDeg,shiftXpx,shiftYpx,rotateDeg,mark1X,mark1Y");

        foreach (var row in rows)
        {
            builder
                .Append(row.Index.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.FileName).Append(',')
                .Append(row.DeltaXMm.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.DeltaYMm.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.DeltaThetaDeg.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.ShiftXPx.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.ShiftYPx.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.RotateDeg.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Mark1X.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Mark1Y.ToString("F2", CultureInfo.InvariantCulture))
                .AppendLine();
        }

        var path = Path.GetFullPath(Path.Combine(options.OutputDirectory, "ground-truth.csv"));
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>自检：用模板匹配重新定位 MARK1，与真值比较（确认生成的图像可被算法稳定识别）。</summary>
    /// <param name="options">命令行参数。</param>
    /// <param name="template">MARK1 模板。</param>
    /// <param name="rows">真值行。</param>
    private static void Verify(Options options, Mat template, IReadOnlyList<SampleRow> rows)
    {
        double maxErrorPx = 0;
        double sumErrorPx = 0;
        var failed = 0;

        foreach (var row in rows)
        {
            var path = Path.Combine(options.OutputDirectory, row.FileName);
            using var image = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (image.Empty())
            {
                failed++;
                continue;
            }

            // 只在左半图搜索：MARK1 与 MARK2 形状相同，不限定范围会匹配到错误的一侧
            using var searchArea = new Mat(image, new Rect(0, 0, image.Width / 2, image.Height));
            using var result = new Mat();
            Cv2.MatchTemplate(searchArea, template, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out _, out _, out var maxLoc);

            var (peakX, peakY) = RefinePeak(result, maxLoc);

            // 峰值位置是模板"左上角"，奇数尺寸模板的中心偏移是 (size-1)/2（写成 size/2 会引入 0.5px 系统偏差）
            var foundX = peakX + ((template.Width - 1) / 2.0);
            var foundY = peakY + ((template.Height - 1) / 2.0);

            var error = Math.Sqrt(
                ((foundX - row.Mark1X) * (foundX - row.Mark1X)) +
                ((foundY - row.Mark1Y) * (foundY - row.Mark1Y)));

            maxErrorPx = Math.Max(maxErrorPx, error);
            sumErrorPx += error;
        }

        var averageErrorPx = rows.Count == 0 ? 0 : sumErrorPx / rows.Count;

        Console.WriteLine();
        Console.WriteLine("自检（模板匹配重定位 MARK1 vs 真值）：");
        Console.WriteLine($"  平均偏差：{averageErrorPx:F3} px（{averageErrorPx / options.PxPerMm:F4} mm）");
        Console.WriteLine($"  最大偏差：{maxErrorPx:F3} px（{maxErrorPx / options.PxPerMm:F4} mm）");
        Console.WriteLine(failed == 0 ? "  结论    ：全部样本可被稳定识别 ✅" : $"  结论    ：{failed} 张读取失败 ❌");
    }

    /// <summary>对匹配峰值做 3×3 抛物线拟合，得到亚像素坐标（与项目 TemplateMatcher 同一做法）。</summary>
    /// <param name="result">归一化相关系数结果图（CV_32FC1）。</param>
    /// <param name="peak">整数峰值位置。</param>
    private static (double X, double Y) RefinePeak(Mat result, Point peak)
    {
        var dx = 0.0;
        var dy = 0.0;

        var hasHorizontal = peak.X > 0 && peak.X < result.Cols - 1;
        var hasVertical = peak.Y > 0 && peak.Y < result.Rows - 1;

        var center = result.At<float>(peak.Y, peak.X);

        if (hasHorizontal)
        {
            var left = result.At<float>(peak.Y, peak.X - 1);
            var right = result.At<float>(peak.Y, peak.X + 1);
            var denominator = left - (2 * center) + right;
            if (Math.Abs(denominator) > 1e-6)
            {
                dx = Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
            }
        }

        if (hasVertical)
        {
            var up = result.At<float>(peak.Y - 1, peak.X);
            var down = result.At<float>(peak.Y + 1, peak.X);
            var denominator = up - (2 * center) + down;
            if (Math.Abs(denominator) > 1e-6)
            {
                dy = Math.Clamp(0.5 * (up - down) / denominator, -0.5, 0.5);
            }
        }

        return (peak.X + dx, peak.Y + dy);
    }

    /// <summary>打印生成摘要。</summary>
    /// <param name="options">命令行参数。</param>
    /// <param name="rows">真值行。</param>
    /// <param name="csvPath">真值 CSV 路径。</param>
    private static void Report(Options options, IReadOnlyList<SampleRow> rows, string csvPath)
    {
        Console.WriteLine($"已生成 {rows.Count} 张样本：{Path.GetFullPath(options.OutputDirectory)}");
        Console.WriteLine($"  图像    ：{options.Width} × {options.Height} px（{options.PxPerMm:F3} px/mm → {1 / options.PxPerMm:F4} mm/px）");
        Console.WriteLine($"  随机范围：ΔX/ΔY ±{options.DeltaMm:F2} mm，Δθ ±{options.DeltaDeg:F2}°");
        Console.WriteLine($"  传感器  ：模糊 sigma {options.BlurSigma:F2}，噪声 sigma {options.NoiseSigma:F1}");
        Console.WriteLine($"  随机种子：{options.Seed}（固定种子可复现同一批样本）");
        Console.WriteLine($"  真值文件：{csvPath}");
        Console.WriteLine("  真值口径：deltaXmm/deltaYmm/deltaThetaDeg 即 CorrectionService.Compute 应输出的 ΔX/ΔY/Δθ");
    }

    /// <summary>打印用法。</summary>
    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            用法：SampleGen [选项]

              --count <n>        生成张数（默认 20）
              --out <dir>        输出目录（默认 samples）
              --seed <n>         随机种子（默认 20261116）
              --width <px>       图像宽（默认 1280）
              --height <px>      图像高（默认 1024）
              --px-per-mm <n>    比例（默认 4，与 VirtualCamera 的 4 px/mm 一致）
              --delta-mm <mm>    ΔX/ΔY 随机范围 ±值（默认 4）
              --delta-deg <deg>  Δθ 随机范围 ±值（默认 5）
              --blur <sigma>     高斯模糊 sigma（默认 0.8，模拟离焦）
              --noise <sigma>    高斯噪声 sigma（默认 2.0，模拟传感器噪声）
              --verify           生成后自检（模板匹配重定位 MARK1 并与真值比较）
              -h, --help         显示本帮助

            示例：
              SampleGen --count 50 --delta-mm 4 --delta-deg 5 --verify --out samples
            """);
    }

    /// <summary>一条样本的真值。</summary>
    /// <param name="Index">序号（从 1 开始）。</param>
    /// <param name="FileName">图片文件名。</param>
    /// <param name="DeltaXMm">纠偏量 ΔX（mm，机械坐标）。</param>
    /// <param name="DeltaYMm">纠偏量 ΔY（mm，机械坐标）。</param>
    /// <param name="DeltaThetaDeg">纠偏量 Δθ（°，图像角度差口径）。</param>
    /// <param name="ShiftXPx">实际施加的像素平移 X。</param>
    /// <param name="ShiftYPx">实际施加的像素平移 Y。</param>
    /// <param name="RotateDeg">实际施加的旋转角（°）。</param>
    /// <param name="Mark1X">变换后 MARK1 的像素 X（真值坐标）。</param>
    /// <param name="Mark1Y">变换后 MARK1 的像素 Y（真值坐标）。</param>
    private sealed record SampleRow(
        int Index,
        string FileName,
        double DeltaXMm,
        double DeltaYMm,
        double DeltaThetaDeg,
        double ShiftXPx,
        double ShiftYPx,
        double RotateDeg,
        double Mark1X,
        double Mark1Y);

    /// <summary>命令行参数集合。</summary>
    private sealed class Options
    {
        /// <summary>生成张数。</summary>
        public int Count { get; private set; } = 20;

        /// <summary>输出目录。</summary>
        public string OutputDirectory { get; private set; } = "samples";

        /// <summary>随机种子。</summary>
        public int Seed { get; private set; } = 20261116;

        /// <summary>图像宽（像素）。</summary>
        public int Width { get; private set; } = 1280;

        /// <summary>图像高（像素）。</summary>
        public int Height { get; private set; } = 1024;

        /// <summary>每毫米像素数。</summary>
        public double PxPerMm { get; private set; } = 4.0;

        /// <summary>ΔX/ΔY 随机范围（mm，正负对称）。</summary>
        public double DeltaMm { get; private set; } = 4.0;

        /// <summary>Δθ 随机范围（°，正负对称）。</summary>
        public double DeltaDeg { get; private set; } = 5.0;

        /// <summary>高斯模糊 sigma。</summary>
        public double BlurSigma { get; private set; } = 0.8;

        /// <summary>高斯噪声 sigma。</summary>
        public double NoiseSigma { get; private set; } = 2.0;

        /// <summary>背景灰度。</summary>
        public byte Background { get; private set; } = 245;

        /// <summary>是否生成后自检。</summary>
        public bool Verify { get; private set; }

        /// <summary>是否只显示帮助。</summary>
        public bool ShowHelp { get; private set; }

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
                    case "--count":
                        options.Count = ReadInt(NextValue(), key, 1, 10000);
                        break;
                    case "--out":
                        options.OutputDirectory = NextValue();
                        break;
                    case "--seed":
                        options.Seed = ReadInt(NextValue(), key, int.MinValue, int.MaxValue);
                        break;
                    case "--width":
                        options.Width = ReadInt(NextValue(), key, 320, 4096);
                        break;
                    case "--height":
                        options.Height = ReadInt(NextValue(), key, 240, 4096);
                        break;
                    case "--px-per-mm":
                        options.PxPerMm = ReadDouble(NextValue(), key, 0.5, 200);
                        break;
                    case "--delta-mm":
                        options.DeltaMm = ReadDouble(NextValue(), key, 0, 100);
                        break;
                    case "--delta-deg":
                        options.DeltaDeg = ReadDouble(NextValue(), key, 0, 180);
                        break;
                    case "--blur":
                        options.BlurSigma = ReadDouble(NextValue(), key, 0, 20);
                        break;
                    case "--noise":
                        options.NoiseSigma = ReadDouble(NextValue(), key, 0, 50);
                        break;
                    case "--verify":
                        options.Verify = true;
                        break;
                    default:
                        throw new ArgumentException($"未知参数：{key}");
                }
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
