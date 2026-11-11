// 职责：一键备份实现——把数据库、配方、模板与配置文件打包成 zip，并按保留份数清理旧备份
// 层：Data
// 依赖：SqliteConnectionFactory、IClock（Core/Abstractions）、BackupOptions/BackupResult（Core/Models）、System.IO.Compression

using System.IO;
using System.IO.Compression;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Data;

/// <summary>
/// <see cref="IBackupService"/> 的默认实现。备份内容与 <c>docs/08-页面清单与交互.md</c> 页面 8 一致：
/// <c>data/autotaping.db</c> + <c>recipes/</c> + <c>templates/</c> + <c>appsettings.json</c>。
/// <para>
/// 数据库用 SQLite 的在线备份 API 导出到临时文件再打包，而不是直接复制 <c>.db</c>：
/// 软件运行时连接池一直持有数据库文件，直接复制会因文件占用失败，且 WAL 模式下新提交的数据可能
/// 还在 <c>-wal</c> 文件里（备份必须包含最近的产出记录）。
/// </para>
/// </summary>
public sealed class BackupService : IBackupService
{
    /// <summary>备份文件前缀（保留份数清理按该前缀匹配）。</summary>
    private const string BackupFilePrefix = "autotaping_backup_";

    /// <summary>备份文件扩展名。</summary>
    private const string BackupFileExtension = ".zip";

    /// <summary>数据库在 zip 内的路径（便于恢复时按 data/ 目录整体解压）。</summary>
    private const string DatabaseEntryName = "data/autotaping.db";

    /// <summary>需要整目录打包的项（相对程序目录，zip 内保持同名目录）。</summary>
    private static readonly string[] DirectoryItems = { "recipes", "templates" };

    /// <summary>需要单独打包的配置文件。</summary>
    private const string AppSettingsFileName = "appsettings.json";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly BackupOptions _options;
    private readonly ILogger<BackupService> _log;

    /// <summary>构造备份服务。</summary>
    /// <param name="connectionFactory">连接工厂（提供数据库路径，并用于 WAL 检查点）。</param>
    /// <param name="clock">时钟（备份文件名时间戳）。</param>
    /// <param name="options">备份配置。</param>
    /// <param name="log">日志。</param>
    public BackupService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        BackupOptions options,
        ILogger<BackupService> log)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _options = options;
        _log = log;

        BackupDirectory = Path.IsPathRooted(options.BackupDirectory)
            ? options.BackupDirectory
            : Path.Combine(AppContext.BaseDirectory, options.BackupDirectory);
    }

    /// <inheritdoc />
    public string BackupDirectory { get; }

    /// <inheritdoc />
    public BackupResult CreateBackup()
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);

            var path = Path.Combine(BackupDirectory,
                                    $"{BackupFilePrefix}{_clock.Now:yyyyMMdd_HHmmss}{BackupFileExtension}");

            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                AddDatabaseEntry(archive);

                foreach (var directory in DirectoryItems)
                {
                    AddDirectory(archive, Path.Combine(AppContext.BaseDirectory, directory), directory);
                }

                AddFile(archive, Path.Combine(AppContext.BaseDirectory, AppSettingsFileName), AppSettingsFileName);
            }

            var size = new FileInfo(path).Length;
            PruneOldBackups();

            _log.LogInformation("一键备份完成：{Path}（{Size:F1} KB）", path, size / 1024.0);
            return BackupResult.Success(path, size);
        }
        catch (Exception ex)
        {
            // 备份失败不得影响生产（01-架构设计 第 5 节），把原因转成操作工能看懂的话
            _log.LogError(ex, "一键备份失败，备份目录 {Directory}", BackupDirectory);
            return BackupResult.Failure($"备份失败：{ex.GetBaseException().Message}。请检查 data 目录权限与磁盘剩余空间。");
        }
    }

    /// <summary>
    /// 用 SQLite 在线备份 API 导出数据库快照并写入 zip。导出到临时文件后立刻删除，
    /// 因此备份期间软件可以继续生产（不需要停流程、不需要独占数据库）。
    /// </summary>
    private void AddDatabaseEntry(ZipArchive archive)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"autotaping-backup-{Guid.NewGuid():N}.db");

        try
        {
            using (var source = _connectionFactory.Create())
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = tempPath,
                       Mode = SqliteOpenMode.ReadWriteCreate,
                       // 临时库不进连接池：池化会一直持有文件句柄，导致随后的打包读不到文件
                       Pooling = false,
                       DefaultTimeout = 5
                   }.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);
            }

            archive.CreateEntryFromFile(tempPath, DatabaseEntryName, CompressionLevel.Optimal);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException ex)
            {
                // 临时文件删不掉只会占用一点磁盘，不影响备份结果
                _log.LogWarning(ex, "删除备份临时文件失败：{Path}", tempPath);
            }
        }
    }

    /// <summary>把一个文件加入 zip；文件不存在时跳过（如尚未做过模板制作）。</summary>
    private void AddFile(ZipArchive archive, string sourcePath, string entryName)
    {
        if (!File.Exists(sourcePath))
        {
            _log.LogDebug("备份跳过不存在的文件：{Path}", sourcePath);
            return;
        }

        archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
    }

    /// <summary>把一个目录（含子目录）加入 zip；目录不存在时跳过。</summary>
    private void AddDirectory(ZipArchive archive, string sourceDirectory, string entryPrefix)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            _log.LogDebug("备份跳过不存在的目录：{Path}", sourceDirectory);
            return;
        }

        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, $"{entryPrefix}/{relative}", CompressionLevel.Optimal);
        }

        _log.LogDebug("备份目录 {Directory}：{Count} 个文件", sourceDirectory, files.Length);
    }

    /// <summary>删除超出保留份数的最旧备份（防止备份把磁盘占满，对应 09 文档 AL501）。</summary>
    private void PruneOldBackups()
    {
        if (_options.KeepCount <= 0)
        {
            return;
        }

        var files = new DirectoryInfo(BackupDirectory)
            .GetFiles($"{BackupFilePrefix}*{BackupFileExtension}")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(_options.KeepCount)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                file.Delete();
                _log.LogInformation("已删除超出保留份数的旧备份：{Name}", file.Name);
            }
            catch (Exception ex)
            {
                // 备份文件被占用（用户正在拷贝）时删不掉很正常，下次再删
                _log.LogWarning(ex, "删除旧备份失败：{Name}", file.Name);
            }
        }
    }
}
