// 职责：一键备份的配置项（备份目录、保留份数），来自 appsettings.json 的 Backup 段
// 层：Core/Models
// 依赖：无（纯数据）

namespace AutoTaping.Core.Models;

/// <summary>
/// 一键备份配置。备份内容见 <c>docs/08-页面清单与交互.md</c> 页面 8：
/// 数据库 + 配方 + 模板 + 配置文件。保留份数用于防止备份把磁盘占满（09 文档 AL501）。
/// </summary>
public sealed class BackupOptions
{
    /// <summary>备份输出目录（相对程序目录或绝对路径）。</summary>
    public string BackupDirectory { get; set; } = "data/backup";

    /// <summary>保留的备份份数；超出后删除最旧的备份文件。</summary>
    public int KeepCount { get; set; } = 30;
}
