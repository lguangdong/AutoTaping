// 职责：产出仓储 CRUD 测试（写入/当日计数/流水号/区间统计/CSV 导出）
// 层：Tests
// 依赖：TestDatabase、AutoTaping.Core.Models

using System.IO;
using AutoTaping.Core.Models;
using AutoTaping.Data;
using AutoTaping.Tests.Infrastructure;

namespace AutoTaping.Tests;

/// <summary>
/// <c>OutputRepository</c> 的 CRUD 测试，对应 S1 验收标准第 4、5 条。
/// </summary>
public sealed class OutputRepositoryTests
{
    [Fact]
    public void OutputRepository_SupportsInsertCountSequenceAndStatistics()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var repository = database.CreateOutputRepository();

        var now = DateTime.Now;
        Assert.Equal(1, repository.NextSequenceNo(now));

        repository.Insert(new OutputRecord
        {
            SequenceNo = repository.NextSequenceNo(now),
            RecipeId = 1,
            RecipeName = DatabaseInitializer.SeedRecipeName,
            Result = OutputRecord.ResultOk,
            CycleTimeMs = 3000,
            DeltaX = 0.03,
            DeltaY = -0.02,
            DeltaTheta = 0.15,
            MatchScore1 = 0.92,
            MatchScore2 = 0.89,
            Shift = "DAY",
            CreatedAt = now
        });

        repository.Insert(new OutputRecord
        {
            SequenceNo = repository.NextSequenceNo(now),
            RecipeId = 1,
            RecipeName = DatabaseInitializer.SeedRecipeName,
            Result = OutputRecord.ResultNg,
            CycleTimeMs = 4000,
            NgReason = "MARK 识别失败",
            Shift = "DAY",
            CreatedAt = now
        });

        Assert.Equal(1, repository.CountToday(OutputRecord.ResultOk));
        Assert.Equal(1, repository.CountToday(OutputRecord.ResultNg));
        Assert.Equal(0, repository.CountToday("UNKNOWN"));
        Assert.Equal(3, repository.NextSequenceNo(now));

        var statistics = repository.GetStatistics(now.Date, now.Date.AddDays(1).AddSeconds(-1));
        Assert.Equal(2, statistics.TotalCount);
        Assert.Equal(1, statistics.OkCount);
        Assert.Equal(1, statistics.NgCount);
        Assert.Equal(3500, statistics.AverageCycleTimeMs, 3);
        Assert.Equal(0.5, statistics.Yield, 3);

        // 空区间不返回脏数据
        var empty = repository.GetStatistics(now.AddDays(-10), now.AddDays(-9));
        Assert.Equal(0, empty.TotalCount);
        Assert.Equal(0, empty.Yield, 3);
    }

    [Fact]
    public void OutputRepository_ExportsCsvWithExpectedHeader()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var repository = database.CreateOutputRepository();

        var now = DateTime.Now;
        repository.Insert(new OutputRecord
        {
            SequenceNo = 1,
            RecipeId = 1,
            RecipeName = DatabaseInitializer.SeedRecipeName,
            Result = OutputRecord.ResultOk,
            CycleTimeMs = 3120,
            DeltaX = 0.03,
            DeltaY = -0.02,
            Shift = "DAY",
            CreatedAt = now
        });

        var csvPath = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "outputs.csv");
        repository.ExportCsv(now.Date, now.Date.AddDays(1).AddSeconds(-1), csvPath);

        var bytes = File.ReadAllBytes(csvPath);
        Assert.Equal<byte>([0xEF, 0xBB, 0xBF], bytes.Take(3));

        var lines = File.ReadAllLines(csvPath);
        Assert.Equal("序号,时间,配方,结果,节拍(ms),ΔX(mm),ΔY(mm),Δθ(°),NG原因,班次", lines[0]);
        Assert.Contains("OK", lines[1]);
        Assert.Contains("0.03", lines[1]);
    }
}
