// 职责：操作日志仓储 CRUD 测试（写入与区间查询、旧值/新值留痕）
// 层：Tests
// 依赖：TestDatabase

using AutoTaping.Tests.Infrastructure;

namespace AutoTaping.Tests;

/// <summary>
/// <c>OperationLogRepository</c> 的 CRUD 测试，对应 S1 验收标准第 5 条。
/// </summary>
public sealed class OperationLogRepositoryTests
{
    [Fact]
    public void OperationLogRepository_SupportsInsertAndQuery()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var repository = database.CreateOperationLogRepository();

        repository.Insert("tech01", "修改参数", "示例配方-请修改.process.holdPressureMs", "400", "500");
        repository.Insert("admin", "登录", "admin", null, null);

        var logs = repository.Query(DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(5));

        Assert.Equal(2, logs.Count);

        var change = Assert.Single(logs, log => log.Action == "修改参数");
        Assert.Equal("tech01", change.UserName);
        Assert.Equal("示例配方-请修改.process.holdPressureMs", change.Target);
        Assert.Equal("400", change.OldValue);
        Assert.Equal("500", change.NewValue);
        Assert.NotEqual(default, change.CreatedAt);

        var login = Assert.Single(logs, log => log.Action == "登录");
        Assert.Null(login.OldValue);
        Assert.Null(login.NewValue);

        // 区间外查不到
        Assert.Empty(repository.Query(DateTime.Now.AddDays(-10), DateTime.Now.AddDays(-9)));
    }
}
