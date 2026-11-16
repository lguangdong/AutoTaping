// 职责：虚拟相机的样本图来源——从目录随机读取一张灰度图并转成 Mono8 字节
// 层：App/Infrastructure
// 依赖：OpenCvSharp4、Microsoft.Extensions.Logging（Hardware 层不引 OpenCV，故解码放在这一侧）

using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 虚拟相机的模拟采样图提供者。目录由 appsettings 的 <c>Hardware:Camera:SampleImageDirectory</c> 指定，
/// 每次调用随机返回其中一张（灰度化后转 Mono8）。目录不存在或无图片时返回 <see langword="null"/>，
/// 由 <c>VirtualCamera</c> 退化为合成图。
/// </summary>
public static class SampleImageProvider
{
    /// <summary>支持的图片扩展名。</summary>
    private static readonly string[] Extensions = ["*.png", "*.bmp", "*.jpg"];

    /// <summary>
    /// 创建样本图提供者。
    /// </summary>
    /// <param name="directory">样本图目录；相对路径先按程序目录解析，再按当前工作目录解析。</param>
    /// <param name="log">日志。</param>
    /// <returns>可取帧的委托；目录缺失或没有图片时返回 <see langword="null"/>。</returns>
    public static Func<(byte[] Mono8, int Width, int Height)?>? Create(string? directory, ILogger log)
    {
        var resolved = ResolveDirectory(directory);
        if (resolved is null)
        {
            log.LogWarning("未找到虚拟相机样本图目录（{Directory}），取图将使用合成图", directory);
            return null;
        }

        var files = Extensions
            .SelectMany(extension => Directory.EnumerateFiles(resolved, extension))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            log.LogWarning("样本图目录内没有图片（{Directory}），取图将使用合成图", resolved);
            return null;
        }

        log.LogInformation("虚拟相机样本图已加载：{Count} 张，目录 {Directory}", files.Length, resolved);

        var random = new Random();
        return () => ReadFrame(files[random.Next(files.Length)], log);
    }

    /// <summary>读取一张图片并转换为 Mono8 字节数组（失败返回 <see langword="null"/>）。</summary>
    private static (byte[] Mono8, int Width, int Height)? ReadFrame(string path, ILogger log)
    {
        try
        {
            using var loaded = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (loaded.Empty())
            {
                log.LogWarning("样本图读取失败（空图）：{Path}", path);
                return null;
            }

            var source = loaded.IsContinuous() ? loaded : loaded.Clone();
            try
            {
                var length = source.Width * source.Height;
                var buffer = new byte[length];
                Marshal.Copy(source.Data, buffer, 0, length);
                return (buffer, source.Width, source.Height);
            }
            finally
            {
                if (!ReferenceEquals(source, loaded))
                {
                    source.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "样本图读取异常：{Path}", path);
            return null;
        }
    }

    /// <summary>解析样本图目录：绝对路径直接用；相对路径先按程序目录、再按当前工作目录查找。</summary>
    private static string? ResolveDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        if (Path.IsPathRooted(directory))
        {
            return Directory.Exists(directory) ? Path.GetFullPath(directory) : null;
        }

        var nearExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, directory));
        if (Directory.Exists(nearExe))
        {
            return nearExe;
        }

        var nearWorkingDirectory = Path.GetFullPath(directory);
        return Directory.Exists(nearWorkingDirectory) ? nearWorkingDirectory : null;
    }
}
