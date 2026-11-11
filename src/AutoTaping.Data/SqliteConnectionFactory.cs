// 职责：SQLite 连接工厂——按配置的数据库路径创建已打开的连接，并保证目录存在
// 层：Data
// 依赖：Microsoft.Data.Sqlite

using System.IO;
using Microsoft.Data.Sqlite;

namespace AutoTaping.Data;

/// <summary>
/// SQLite 连接工厂。所有仓储通过它拿连接，保证连接串、超时策略、数据库路径只有一处定义。
/// 采用「每次操作新建连接」的方式（SQLite 打开成本极低），配合 WAL 支持并发读。
/// </summary>
public sealed class SqliteConnectionFactory
{
    /// <summary>
    /// 忙等超时（秒）。WAL 下仍可能出现「写者与写者」竞争（采集线程与流程线程同时落库），
    /// 该值让 SQLite 在遇到锁时短暂重试而不是立刻抛 <c>SQLITE_BUSY</c>。
    /// 属于驱动级常量而非工艺/硬件参数，因此不放进 appsettings.json。
    /// </summary>
    private const int BusyTimeoutSeconds = 5;

    private readonly string _connectionString;

    /// <summary>构造连接工厂，并确保数据库文件所在目录存在。</summary>
    /// <param name="databasePath">数据库文件路径（可为相对路径，内部按当前工作目录转绝对路径）。</param>
    public SqliteConnectionFactory(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);

        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            // 首次启动时 data/ 目录可能不存在；ReadWriteCreate 只建文件不建目录
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = BusyTimeoutSeconds
        }.ToString();
    }

    /// <summary>数据库文件的绝对路径（用于日志与版本信息页展示）。</summary>
    public string DatabasePath { get; }

    /// <summary>创建并打开一个连接。调用方负责释放（<see langword="using"/>）。</summary>
    public SqliteConnection Create()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
