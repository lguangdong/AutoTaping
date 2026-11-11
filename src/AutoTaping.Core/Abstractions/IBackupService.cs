// 职责：一键备份抽象——把数据库、配方、模板与配置文件打包成 zip（08-页面清单 页面 8）
// 层：Core/Abstractions
// 依赖：BackupResult（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 一键备份服务。备份内容：数据库文件 + <c>recipes/</c> + <c>templates/</c> + <c>appsettings.json</c>。
/// 实现位于持久层（只有它知道数据库路径与文件布局），界面只依赖本接口。
/// </summary>
public interface IBackupService
{
    /// <summary>备份输出目录（绝对路径），用于界面显示「备份到 ...」。</summary>
    string BackupDirectory { get; }

    /// <summary>执行一次备份。失败不抛异常，通过 <see cref="BackupResult"/> 返回原因（不崩是硬约束）。</summary>
    /// <returns>备份结果（成功时含 zip 绝对路径与大小）。</returns>
    BackupResult CreateBackup();
}
