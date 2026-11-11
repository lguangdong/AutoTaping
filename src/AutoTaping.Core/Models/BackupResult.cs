// 职责：一键备份的结果值对象（成功标志、zip 路径、大小、面向操作工的中文说明）
// 层：Core/Models
// 依赖：无（纯数据）

namespace AutoTaping.Core.Models;

/// <summary>
/// 备份结果。失败原因必须是操作工能看懂的一句话
/// （10-编码约定 6.3：不得把异常 ToString 直接显示给操作工）。
/// </summary>
/// <param name="Succeeded">是否备份成功。</param>
/// <param name="FilePath">备份文件绝对路径；失败时为空字符串。</param>
/// <param name="SizeBytes">备份文件大小（字节）；失败时为 0。</param>
/// <param name="Message">失败原因；成功时为空字符串。</param>
public sealed record BackupResult(bool Succeeded, string FilePath, long SizeBytes, string Message)
{
    /// <summary>构造成功结果。</summary>
    /// <param name="filePath">备份文件绝对路径。</param>
    /// <param name="sizeBytes">备份文件大小（字节）。</param>
    public static BackupResult Success(string filePath, long sizeBytes) => new(true, filePath, sizeBytes, string.Empty);

    /// <summary>构造失败结果。</summary>
    /// <param name="message">面向操作工的失败原因。</param>
    public static BackupResult Failure(string message) => new(false, string.Empty, 0, message);
}
