// 职责：单元测试用的临时数据库夹具——每个测试独立目录、独立 db 文件，用完即删
// 层：Tests/Infrastructure
// 依赖：AutoTaping.Data、Microsoft.Extensions.Logging.Abstractions（NullLogger）

using System.IO;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Services;
using AutoTaping.Data;
using AutoTaping.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests.Infrastructure;

/// <summary>
/// 临时数据库夹具。<see cref="Initialize"/> 会在临时目录里建好 8 张表与种子数据。
/// 测试结束删除整个目录，避免污染 <c>data/</c>。
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly string _directory;

    /// <summary>创建夹具（只准备目录与路径，不建表）。</summary>
    public TestDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "autotaping-tests", Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(_directory, "autotaping.db");
        ConnectionFactory = new SqliteConnectionFactory(DatabasePath);
    }

    /// <summary>数据库文件绝对路径。</summary>
    public string DatabasePath { get; }

    /// <summary>测试用时钟（真实系统时钟，时间断言一律用相对区间）。</summary>
    public IClock Clock { get; } = SystemClock.Instance;

    /// <summary>连接工厂。</summary>
    public SqliteConnectionFactory ConnectionFactory { get; private set; }

    /// <summary>执行建库与种子数据。</summary>
    public void Initialize() => CreateInitializer().Initialize();

    /// <summary>新的初始化器实例（每次调用都新建，模拟一次应用启动）。</summary>
    public DatabaseInitializer CreateInitializer()
        => new(ConnectionFactory, Clock, NullLogger<DatabaseInitializer>.Instance);

    /// <summary>新的配方仓储实例。</summary>
    public RecipeRepository CreateRecipeRepository()
        => new(ConnectionFactory, Clock, NullLogger<RecipeRepository>.Instance);

    /// <summary>新的报警仓储实例。</summary>
    public AlarmRepository CreateAlarmRepository()
        => new(ConnectionFactory, Clock, NullLogger<AlarmRepository>.Instance);

    /// <summary>新的产出仓储实例。</summary>
    public OutputRepository CreateOutputRepository()
        => new(ConnectionFactory, Clock, NullLogger<OutputRepository>.Instance);

    /// <summary>新的操作日志仓储实例。</summary>
    public OperationLogRepository CreateOperationLogRepository()
        => new(ConnectionFactory, Clock, NullLogger<OperationLogRepository>.Instance);

    /// <summary>新的停机记录仓储实例（S7）。</summary>
    public DowntimeRepository CreateDowntimeRepository()
        => new(ConnectionFactory, Clock, NullLogger<DowntimeRepository>.Instance);

    /// <summary>新的班次汇总仓储实例（S7）。</summary>
    public ShiftProductionRepository CreateShiftProductionRepository()
        => new(ConnectionFactory, NullLogger<ShiftProductionRepository>.Instance);

    /// <summary>新的系统状态仓储实例。</summary>
    public SystemStateRepository CreateSystemStateRepository()
        => new(ConnectionFactory, Clock);

    /// <summary>新的标定仓储实例。</summary>
    public CalibrationRepository CreateCalibrationRepository()
        => new(ConnectionFactory, Clock, NullLogger<CalibrationRepository>.Instance);

    /// <summary>新的用户仓储实例（S9 用户管理）。</summary>
    public UserRepository CreateUserRepository()
        => new(ConnectionFactory);

    /// <summary>模拟进程重启：清空连接池并重建连接工厂（仍然指向同一个 db 文件）。</summary>
    public void SimulateRestart()
    {
        SqliteConnection.ClearAllPools();
        ConnectionFactory = new SqliteConnectionFactory(DatabasePath);
    }

    /// <summary>打开一个直连的 SQLite 连接，供断言使用。</summary>
    public SqliteConnection OpenConnection() => ConnectionFactory.Create();

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException ex)
        {
            // 临时目录删不掉不影响测试结论（Windows 上偶发文件句柄未释放）
            Console.WriteLine($"清理临时数据库目录失败：{ex.Message}");
        }
    }
}
