// 职责：CSV 导出（UTF-8 with BOM + RFC4180 转义），保证 Excel 打开中文不乱码
// 层：Data
// 依赖：System.IO、System.Text

using System.IO;
using System.Text;

namespace AutoTaping.Data;

/// <summary>
/// CSV 写出工具。编码固定 UTF-8 with BOM（<c>docs/06-数据模型与数据库.md</c> 第 9 节强制要求）。
/// </summary>
internal static class CsvWriter
{
    /// <summary>字段分隔符。</summary>
    private const char Delimiter = ',';

    /// <summary>需要触发引号包裹的字符。</summary>
    private static readonly char[] MustQuoteChars = [Delimiter, '"', '\r', '\n'];

    /// <summary>写出一个 CSV 文件（覆盖同名文件，自动创建父目录）。</summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="header">表头字段。</param>
    /// <param name="rows">数据行。</param>
    public static void Write(string path, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // encoderShouldEmitUTF8Identifier = true → 写 BOM
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        writer.WriteLine(FormatRow(header));
        foreach (var row in rows)
        {
            writer.WriteLine(FormatRow(row));
        }
    }

    private static string FormatRow(IReadOnlyList<string> fields)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(Delimiter);
            }

            builder.Append(Escape(fields[i]));
        }

        return builder.ToString();
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var needsQuote = value.IndexOfAny(MustQuoteChars) >= 0;
        return needsQuote ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
