// 职责：数据库初始化——开 WAL、建 8 张表与索引、写入种子数据（admin 用户 + 示例配方）
// 层：Data
// 依赖：SqliteConnectionFactory、SqliteSchema、RecipeJson、PasswordHasher、Dapper

using System.IO;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Data;

/// <summary>
/// 数据库初始化器。每次启动调用 <see cref="Initialize"/>，内部全部语句都是幂等的：
/// 表已存在则跳过，种子数据按唯一键判重，因此「删库重启」与「重复启动」都能得到一致结果。
/// </summary>
public sealed class DatabaseInitializer
{
    /// <summary>种子管理员账户名。</summary>
    public const string SeedAdminUserName = "admin";

    /// <summary>种子管理员初始口令（首次登录后应由 S9 的用户管理强制修改）。</summary>
    public const string SeedAdminPassword = "123456";

    /// <summary>种子示例配方名。</summary>
    public const string SeedRecipeName = "示例配方-请修改";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ILogger<DatabaseInitializer> _log;

    /// <summary>构造初始化器。</summary>
    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时钟（用于种子数据时间戳）。</param>
    /// <param name="log">日志。</param>
    public DatabaseInitializer(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILogger<DatabaseInitializer> log)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _log = log;
    }

    /// <summary>数据库文件绝对路径。</summary>
    public string DatabasePath => _connectionFactory.DatabasePath;

    /// <summary>执行建库与种子数据。首次启动会创建数据库文件；重复调用安全。</summary>
    /// <exception cref="SqliteException">数据库文件无法打开或建表失败（磁盘、权限、文件损坏）。</exception>
    public void Initialize()
    {
        var isNewDatabase = !File.Exists(_connectionFactory.DatabasePath);

        using var connection = _connectionFactory.Create();

        var journalMode = EnableWal(connection);
        CreateSchema(connection);

        using (var transaction = connection.BeginTransaction())
        {
            var adminCreated = SeedAdminUser(connection, transaction);
            var recipeCreated = SeedSampleRecipe(connection, transaction);
            var calibrationCreated = SeedSampleCalibration(connection, transaction);
            var normalizedRecipes = NormalizeRecipeJson(connection, transaction);
            transaction.Commit();

            _log.LogInformation(
                "数据库初始化完成：路径 {Path}，新建库 {IsNew}，日志模式 {JournalMode}，种子 admin 用户 {AdminCreated}，种子配方 {RecipeCreated}，种子标定 {CalibrationCreated}",
                _connectionFactory.DatabasePath, isNewDatabase, journalMode, adminCreated, recipeCreated, calibrationCreated);

            if (normalizedRecipes > 0)
            {
                _log.LogInformation("已规整 {Count} 条配方 JSON（清理已废弃的字段键）", normalizedRecipes);
            }
        }
    }

    /// <summary>
    /// 规整配方 JSON：用当前模型重新序列化，清除历史遗留的废弃键（如旧的两块搜索区域、tapePath）。
    /// 幂等——内容无变化不写库；解析失败的配方保持原样，避免破坏用户数据。
    /// </summary>
    /// <param name="connection">数据库连接（调用方已开事务）。</param>
    /// <param name="transaction">当前事务。</param>
    /// <returns>被改写的配方条数。</returns>
    private static int NormalizeRecipeJson(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = connection.Query<RecipeJsonRow>(
            "SELECT Id, JsonData FROM Recipe;", transaction: transaction).ToList();

        var updated = 0;
        foreach (var row in rows)
        {
            var parameters = RecipeJson.TryDeserialize(row.JsonData);
            if (parameters is null)
            {
                continue;
            }

            var normalized = RecipeJson.Serialize(parameters);
            if (string.Equals(normalized, row.JsonData, StringComparison.Ordinal))
            {
                continue;
            }

            connection.Execute(
                "UPDATE Recipe SET JsonData = @JsonData WHERE Id = @Id;",
                new { row.Id, JsonData = normalized },
                transaction);

            updated++;
        }

        return updated;
    }

    /// <summary>配方 JSON 列的投影（仅用于规整）。</summary>
    private sealed class RecipeJsonRow
    {
        /// <summary>配方主键。</summary>
        public int Id { get; set; }

        /// <summary>配方参数 JSON 文本。</summary>
        public string JsonData { get; set; } = string.Empty;
    }

    /// <summary>开启 WAL 并返回实际生效的日志模式（网络盘等场景可能退回 delete）。</summary>
    private string EnableWal(SqliteConnection connection)
    {
        var mode = connection.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            // 不阻断启动（01-架构设计 第 5 节），但必须留下痕迹：断电安全性会退化
            _log.LogWarning("WAL 模式未能生效，当前 journal_mode={Mode}，断电后数据一致性风险上升", mode);
        }

        return mode ?? "unknown";
    }

    private void CreateSchema(SqliteConnection connection)
    {
        connection.Execute(SqliteSchema.CreateAll);
    }

    /// <summary>种子：admin / 123456（工程师角色）。已存在同名用户则跳过。</summary>
    private bool SeedAdminUser(SqliteConnection connection, SqliteTransaction transaction)
    {
        var exists = connection.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM User WHERE UserName = @UserName;",
            new { UserName = SeedAdminUserName },
            transaction) > 0;

        if (exists)
        {
            return false;
        }

        var salt = PasswordHasher.CreateSalt();
        var now = SqliteTime.ToDb(_clock.Now);

        connection.Execute(
            """
            INSERT INTO User (UserName, PasswordHash, Salt, Role, IsEnabled, CreatedAt, LastLoginAt)
            VALUES (@UserName, @PasswordHash, @Salt, @Role, 1, @CreatedAt, NULL);
            """,
            new
            {
                UserName = SeedAdminUserName,
                PasswordHash = PasswordHasher.Hash(salt, SeedAdminPassword),
                Salt = salt,
                Role = (int)UserRole.Engineer,
                CreatedAt = now
            },
            transaction);

        _log.LogInformation("已创建种子用户 {UserName}（角色 {Role}）", SeedAdminUserName, UserRole.Engineer);
        return true;
    }

    /// <summary>种子：示例配方（激活状态），参数全部取 06/09 文档的默认值。已存在同名配方则跳过。</summary>
    private bool SeedSampleRecipe(SqliteConnection connection, SqliteTransaction transaction)
    {
        var exists = connection.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM Recipe WHERE Name = @Name AND IsDeleted = 0;",
            new { Name = SeedRecipeName },
            transaction) > 0;

        if (exists)
        {
            return false;
        }

        var now = SqliteTime.ToDb(_clock.Now);
        var parameters = new RecipeParameters();

        connection.Execute(
            """
            INSERT INTO Recipe (Name, ProductCode, TapeSpec, JsonData, Version, IsActive, IsDeleted,
                                CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
            VALUES (@Name, @ProductCode, @TapeSpec, @JsonData, 1, 1, 0, @Now, @CreatedBy, @Now, @UpdatedBy);
            """,
            new
            {
                Name = SeedRecipeName,
                ProductCode = "DEMO",
                TapeSpec = "3M-467MP",
                JsonData = RecipeJson.Serialize(parameters),
                Now = now,
                CreatedBy = SeedAdminUserName,
                UpdatedBy = SeedAdminUserName
            },
            transaction);

        _log.LogInformation("已创建种子配方 {RecipeName}（激活）", SeedRecipeName);
        return true;
    }

    /// <summary>
    /// 种子：示例标定（虚拟模式）。矩阵与虚拟相机「平台移动 → MARK 像素移动」的模拟映射一致
    /// （4px/mm，基准平台 (150,200)），使自动流程在无硬件、未标定时也能跑通演示。
    /// 真实硬件接入（S8）后必须重新做九点标定。已存在生效标定则跳过。
    /// </summary>
    private bool SeedSampleCalibration(SqliteConnection connection, SqliteTransaction transaction)
    {
        var exists = connection.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM Calibration WHERE IsActive = 1;",
            transaction: transaction) > 0;

        if (exists)
        {
            return false;
        }

        var now = SqliteTime.ToDb(_clock.Now);

        // 虚拟相机映射：X = 0.25*u + 50，Y = -0.25*v + 325（详见 VirtualCamera.ComputeMarkPositions）
        connection.Execute(
            """
            INSERT INTO Calibration
                (MatrixA, MatrixB, MatrixC, MatrixD, MatrixE, MatrixF,
                 RmsError, PointCount, ImageFolder, IsActive, CreatedAt, CreatedBy)
            VALUES
                (0.25, 0, 50, 0, -0.25, 325,
                 0.01, 9, 'images/calibration/virtual-seed', 1, @CreatedAt, @CreatedBy);
            """,
            new { CreatedAt = now, CreatedBy = SeedAdminUserName },
            transaction);

        _log.LogInformation("已创建种子标定（虚拟模式示例），RMS 0.01mm，请接入真实硬件后重新标定");
        return true;
    }
}
