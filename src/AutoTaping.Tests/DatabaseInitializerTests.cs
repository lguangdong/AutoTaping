// 职责：数据层建库验收测试（9 张表、WAL、种子数据、幂等、删库重建、跨重启持久化）
// 层：Tests
// 依赖：TestDatabase、AutoTaping.Data

using System.IO;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using AutoTaping.Data;
using AutoTaping.Tests.Infrastructure;
using Dapper;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="DatabaseInitializer"/> 的验收测试，对应 S1 验收标准第 1/2/3/4 条。
/// </summary>
public sealed class DatabaseInitializerTests
{
    /// <summary>
    /// 文档要求的 8 张表（docs/06-数据模型与数据库 第 2 节）+ S7 新增的停机记录表
    /// （docs/06 第 2 节已同步，产量页「停机分析」的数据来源）。
    /// </summary>
    private static readonly string[] ExpectedTables =
    [
        "Recipe", "AlarmRecord", "OutputRecord", "OperationLog",
        "Calibration", "User", "ShiftProduction", "SystemState", "DowntimeRecord"
    ];

    [Fact]
    public void Initialize_CreatesAllTablesInWalModeWithSeedData()
    {
        using var database = new TestDatabase();

        database.Initialize();

        Assert.True(File.Exists(database.DatabasePath), "首次启动应创建 data/autotaping.db");

        using var connection = database.OpenConnection();

        var tables = connection.Query<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';").ToList();
        foreach (var expected in ExpectedTables)
        {
            Assert.Contains(expected, tables);
        }

        // WAL 必须生效（断电安全的前提）
        var journalMode = connection.ExecuteScalar<string>("PRAGMA journal_mode;");
        Assert.Equal("wal", journalMode, ignoreCase: true);

        // 种子：admin / 123456，工程师角色
        var admin = connection.QuerySingle<SeedUserRow>(
            "SELECT UserName, PasswordHash, Salt, Role, IsEnabled FROM User WHERE UserName = @UserName;",
            new { UserName = DatabaseInitializer.SeedAdminUserName });

        Assert.Equal(DatabaseInitializer.SeedAdminUserName, admin.UserName);
        Assert.Equal((long)UserRole.Engineer, admin.Role);
        Assert.Equal(1, admin.IsEnabled);
        Assert.Equal(24, admin.Salt.Length);                                     // 16 字节 Base64
        Assert.True(PasswordHasher.Verify(admin.Salt, DatabaseInitializer.SeedAdminPassword, admin.PasswordHash),
                    "种子口令应为 123456");
        Assert.False(PasswordHasher.Verify(admin.Salt, "654321", admin.PasswordHash));

        // 种子：示例配方，且为激活状态
        var recipe = connection.QuerySingle<SeedRecipeRow>(
            "SELECT Name, ProductCode, IsActive, JsonData FROM Recipe WHERE IsDeleted = 0;");

        Assert.Equal(DatabaseInitializer.SeedRecipeName, recipe.Name);
        Assert.Equal("DEMO", recipe.ProductCode);
        Assert.Equal(1, recipe.IsActive);
        Assert.Contains("process", recipe.JsonData);
    }

    /// <summary>种子用户断言行（Dapper 行映射用，需可写属性）。</summary>
    private sealed class SeedUserRow
    {
        public string UserName { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string Salt { get; set; } = "";
        public long Role { get; set; }
        public long IsEnabled { get; set; }
    }

    /// <summary>种子配方断言行。</summary>
    private sealed class SeedRecipeRow
    {
        public string Name { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public long IsActive { get; set; }
        public string JsonData { get; set; } = "";
    }

    [Fact]
    public void Initialize_IsIdempotent_WhenCalledRepeatedly()
    {
        using var database = new TestDatabase();

        database.Initialize();
        database.CreateInitializer().Initialize();

        using var connection = database.OpenConnection();
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT COUNT(1) FROM User;"));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT COUNT(1) FROM Recipe;"));
    }

    [Fact]
    public void Initialize_RebuildsDatabase_AfterFileDeleted()
    {
        using var database = new TestDatabase();
        database.Initialize();

        // 模拟运维删库：连 -wal / -shm 一起删掉
        database.SimulateRestart();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = database.DatabasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        Assert.False(File.Exists(database.DatabasePath));

        database.CreateInitializer().Initialize();

        using var connection = database.OpenConnection();
        var tables = connection.Query<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';").ToList();
        Assert.Equal(ExpectedTables.Length, tables.Count);
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT COUNT(1) FROM User;"));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT COUNT(1) FROM Recipe;"));
    }

    [Fact]
    public void OutputRecord_IsStillThere_AfterRestart()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var repository = database.CreateOutputRepository();
        repository.Insert(new OutputRecord
        {
            SequenceNo = 1,
            RecipeId = 1,
            RecipeName = DatabaseInitializer.SeedRecipeName,
            Result = OutputRecord.ResultOk,
            CycleTimeMs = 3120,
            Shift = "DAY",
            CreatedAt = DateTime.Now
        });

        // 模拟重启：连接池清空、连接工厂重建，然后重新建库（幂等，不应清数据）
        database.SimulateRestart();
        database.CreateInitializer().Initialize();

        var reopened = database.CreateOutputRepository();
        Assert.Equal(1, reopened.CountToday(OutputRecord.ResultOk));
        Assert.Equal(2, reopened.NextSequenceNo(DateTime.Today));

        var statistics = reopened.GetStatistics(DateTime.Today, DateTime.Today.AddDays(1).AddSeconds(-1));
        Assert.Equal(1, statistics.TotalCount);
        Assert.Equal(3120, statistics.AverageCycleTimeMs, 3);
    }

    /// <summary>
    /// 启动时规整配方 JSON：历史遗留的废弃键（旧的两块 ROI、tapePath）必须被清掉，
    /// 有效参数保留。幂等——没有变化时不会反复改写。
    /// </summary>
    [Fact]
    public void Initialize_NormalizesLegacyRecipeJson()
    {
        using var database = new TestDatabase();
        database.Initialize();

        // 手工塞一条"老版本"配方：vision 下还是 mark1Roi/mark2Roi，根节点还残留 tapePath
        const string legacyJson = """
        {
          "vision": {
            "mark1Roi": { "x": 300, "y": 400, "w": 200, "h": 200 },
            "mark2Roi": { "x": 780, "y": 400, "w": 200, "h": 200 },
            "matchThreshold": 0.8
          },
          "tapePath": { "mode": "Line", "points": [] }
        }
        """;

        long recipeId;
        using (var connection = database.OpenConnection())
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            connection.Execute(
                """
                INSERT INTO Recipe (Name, ProductCode, TapeSpec, JsonData, Version, IsActive, IsDeleted,
                                    CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
                VALUES ('legacy-roi', 'X', '', @JsonData, 1, 0, 0, @Now, 'test', @Now, 'test');
                """,
                new { JsonData = legacyJson, Now = now });

            recipeId = connection.ExecuteScalar<long>("SELECT last_insert_rowid();");
        }

        // 再跑一次初始化（幂等入口，会执行 JSON 规整）
        database.Initialize();

        using var reopened = database.OpenConnection();
        var json = reopened.ExecuteScalar<string>(
            "SELECT JsonData FROM Recipe WHERE Id = @Id;", new { Id = recipeId });

        Assert.NotNull(json);
        Assert.DoesNotContain("mark1Roi", json);
        Assert.DoesNotContain("mark2Roi", json);
        Assert.DoesNotContain("tapePath", json);

        // 有效参数与新增字段都要在
        Assert.Contains("matchThreshold", json);
        Assert.Contains("searchRoi", json);
    }
}
