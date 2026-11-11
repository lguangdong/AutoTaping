// 职责：BackupService 单元测试——备份包包含数据库文件、超出保留份数的旧备份被删除（S9 一键备份）
// 层：Tests
// 依赖：AutoTaping.Data（BackupService）、Tests/Infrastructure/TestDatabase、System.IO.Compression

using System.IO;
using System.IO.Compression;
using AutoTaping.Core.Models;
using AutoTaping.Data;
using AutoTaping.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="BackupService"/> 的测试。备份目录放在测试库目录下，随测试一起删除。
/// </summary>
public sealed class BackupServiceTests
{
    [Fact]
    public void CreateBackup_ProducesZipContainingDatabase()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var backupDirectory = CreateBackupDirectory(database.DatabasePath);
        var service = CreateService(database, backupDirectory);

        var result = service.CreateBackup();

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.SizeBytes > 0);
        Assert.True(File.Exists(result.FilePath));

        using var archive = ZipFile.OpenRead(result.FilePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "data/autotaping.db");
    }

    [Fact]
    public void CreateBackup_DeletesBackupsBeyondKeepCount()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var backupDirectory = CreateBackupDirectory(database.DatabasePath);

        // 先造两个「旧备份」，保留份数设为 1，新备份完成后旧备份应被清理
        var oldFile1 = Path.Combine(backupDirectory, "autotaping_backup_20260101_000000.zip");
        var oldFile2 = Path.Combine(backupDirectory, "autotaping_backup_20260102_000000.zip");
        File.WriteAllBytes(oldFile1, new byte[] { 0x50, 0x4B });
        File.WriteAllBytes(oldFile2, new byte[] { 0x50, 0x4B });
        File.SetLastWriteTimeUtc(oldFile1, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(oldFile2, DateTime.UtcNow.AddDays(-1));

        var service = CreateService(database, backupDirectory, keepCount: 1);

        var result = service.CreateBackup();

        Assert.True(result.Succeeded, result.Message);
        Assert.False(File.Exists(oldFile1));
        Assert.False(File.Exists(oldFile2));

        var remaining = Directory.GetFiles(backupDirectory, "autotaping_backup_*.zip");
        Assert.Single(remaining);
    }

    private static BackupService CreateService(TestDatabase database, string backupDirectory, int keepCount = 30)
        => new(database.ConnectionFactory,
               database.Clock,
               new BackupOptions { BackupDirectory = backupDirectory, KeepCount = keepCount },
               NullLogger<BackupService>.Instance);

    private static string CreateBackupDirectory(string databasePath)
    {
        var directory = Path.Combine(Path.GetDirectoryName(databasePath)!, "backup");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
