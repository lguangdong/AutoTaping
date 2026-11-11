// 职责：OutputService 单元测试——日统计（总数/良率/平均节拍/运行时长）、小时聚合、班次汇总、停机分类
// 层：Tests
// 依赖：TestDatabase、AutoTaping.Core.Services、AutoTaping.Core.Models

using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using AutoTaping.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// 产量统计服务测试，对应 S7 验收标准第 5 条（跑 50 件后总数/良率/平均节拍正确）与停机分类。
/// </summary>
public sealed class OutputServiceTests
{
    [Fact]
    public void GetDaySummary_ComputesCountYieldAndCycleTime()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var day = new DateTime(2026, 11, 8);

        Insert(database, day.AddHours(9).AddMinutes(15), OutputRecord.ResultOk, 3000);
        Insert(database, day.AddHours(9).AddMinutes(45), OutputRecord.ResultOk, 4000);
        Insert(database, day.AddHours(17).AddMinutes(5), OutputRecord.ResultNg, 5000, ngReason: "MARK 识别失败");

        var service = CreateService(database);
        var summary = service.GetDaySummary(day);

        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(2, summary.OkCount);
        Assert.Equal(1, summary.NgCount);
        Assert.Equal(2.0 / 3.0, summary.Yield, 6);
        Assert.Equal(4000, summary.AverageCycleTimeMs, 3);
        Assert.Equal(12, summary.RunSeconds);   // (3000+4000+5000)/1000

        // 班次分解：09:15/09:45 属白班，17:05 属中班
        var dayShift = Assert.Single(summary.Shifts, shift => shift.ShiftName == ShiftSchedule.Day);
        Assert.Equal(2, dayShift.TotalCount);
        var swingShift = Assert.Single(summary.Shifts, shift => shift.ShiftName == ShiftSchedule.Swing);
        Assert.Equal(1, swingShift.TotalCount);
    }

    [Fact]
    public void GetDaySummary_EmptyDayReturnsZeroes()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var summary = CreateService(database).GetDaySummary(new DateTime(2026, 11, 8));

        Assert.Equal(0, summary.TotalCount);
        Assert.Equal(0, summary.Yield, 6);
        Assert.Equal(0, summary.AverageCycleTimeMs, 6);
        Assert.Equal(3, summary.Shifts.Count);   // 三个班次的行固定存在
    }

    [Fact]
    public void GetHourlyOutput_ReturnsTwentyFourBuckets()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var day = new DateTime(2026, 11, 8);

        Insert(database, day.AddHours(9).AddMinutes(5), OutputRecord.ResultOk, 3000);
        Insert(database, day.AddHours(9).AddMinutes(35), OutputRecord.ResultNg, 3000, ngReason: "真空吸不住");
        Insert(database, day.AddHours(14).AddMinutes(0), OutputRecord.ResultOk, 3000);

        var hourly = CreateService(database).GetHourlyOutput(day);

        Assert.Equal(24, hourly.Count);
        Assert.Equal(2, hourly[9].Count);
        Assert.Equal(1, hourly[9].OkCount);
        Assert.Equal(1, hourly[14].Count);
        Assert.Equal(0, hourly[10].Count);
    }

    [Fact]
    public void RecordDowntime_AggregatesPerReason()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var day = new DateTime(2026, 11, 8);

        var service = CreateService(database);
        service.RecordDowntime(DowntimeReasons.Fault, day.AddHours(10), day.AddHours(10).AddSeconds(30));
        service.RecordDowntime(DowntimeReasons.Fault, day.AddHours(11), day.AddHours(11).AddMinutes(1));
        service.RecordDowntime(DowntimeReasons.PowerOff, day.AddHours(12), endTime: null);

        var summary = service.GetDowntimeSummary(day, day.AddDays(1).AddSeconds(-1));

        // 五个文档定义的原因固定返回，顺序与 05 文档 7.2 节一致
        Assert.Equal(5, summary.Count);
        Assert.Equal(DowntimeReasons.WaitMaterial, summary[0].Reason);

        var fault = Assert.Single(summary, item => item.Reason == DowntimeReasons.Fault);
        Assert.Equal(2, fault.Count);
        Assert.Equal(90, fault.TotalSeconds);

        var powerOff = Assert.Single(summary, item => item.Reason == DowntimeReasons.PowerOff);
        Assert.Equal(1, powerOff.Count);
        Assert.Equal(0, powerOff.TotalSeconds);   // 断电补记时长未知
    }

    [Fact]
    public void RefreshShiftProduction_UpsertsCurrentShift()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var day = new DateTime(2026, 11, 8);

        Insert(database, day.AddHours(9).AddMinutes(10), OutputRecord.ResultOk, 3000);
        Insert(database, day.AddHours(9).AddMinutes(40), OutputRecord.ResultOk, 3000);

        var service = CreateService(database);
        var time = day.AddHours(10);
        service.RecordDowntime(DowntimeReasons.ManualPause, time, time.AddSeconds(20));
        service.RefreshShiftProduction(time);

        var shifts = database.CreateShiftProductionRepository().Query(day);
        var dayShift = Assert.Single(shifts);
        Assert.Equal(ShiftSchedule.Day, dayShift.ShiftName);
        Assert.Equal(2, dayShift.TotalCount);
        Assert.Equal(2, dayShift.OkCount);
        Assert.Equal(6, dayShift.RunSeconds);
        Assert.Equal(20, dayShift.DownSeconds);

        // 幂等：再算一次不会产生第二行
        service.RefreshShiftProduction(time);
        Assert.Single(database.CreateShiftProductionRepository().Query(day));
    }

    [Fact]
    public void GetRecords_FiltersByShiftAndLimitsCount()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var day = new DateTime(2026, 11, 8);

        Insert(database, day.AddHours(9).AddMinutes(0), OutputRecord.ResultOk, 3000);
        Insert(database, day.AddHours(9).AddMinutes(30), OutputRecord.ResultOk, 3000);
        Insert(database, day.AddHours(18).AddMinutes(0), OutputRecord.ResultOk, 3000);

        var service = CreateService(database);

        var dayRecords = service.GetRecords(day, day.AddDays(1).AddSeconds(-1), ShiftSchedule.Day, 100);
        Assert.Equal(2, dayRecords.Count);

        var limited = service.GetRecords(day, day.AddDays(1).AddSeconds(-1), shift: null, maxCount: 1);
        Assert.Single(limited);

        // 倒序：最新的一条在前
        Assert.Equal(day.AddHours(18), limited[0].CreatedAt);
    }

    [Theory]
    [InlineData(7, ShiftSchedule.Night)]
    [InlineData(8, ShiftSchedule.Day)]
    [InlineData(15, ShiftSchedule.Day)]
    [InlineData(16, ShiftSchedule.Swing)]
    [InlineData(23, ShiftSchedule.Swing)]
    public void GetShiftName_MatchesSchedule(int hour, string expected)
    {
        using var database = new TestDatabase();
        database.Initialize();

        var service = CreateService(database);
        Assert.Equal(expected, service.GetShiftName(new DateTime(2026, 11, 8, hour, 0, 0)));
    }

    private static OutputService CreateService(TestDatabase database)
        => new(
            database.CreateOutputRepository(),
            database.CreateDowntimeRepository(),
            database.CreateShiftProductionRepository(),
            database.Clock,
            NullLogger<OutputService>.Instance);

    private static void Insert(TestDatabase database, DateTime createdAt, string result, int cycleTimeMs,
                               string? ngReason = null)
    {
        var repository = database.CreateOutputRepository();

        repository.Insert(new OutputRecord
        {
            SequenceNo = repository.NextSequenceNo(createdAt),
            RecipeId = 1,
            RecipeName = "示例配方-请修改",
            Result = result,
            CycleTimeMs = cycleTimeMs,
            DeltaX = 0.03,
            DeltaY = -0.02,
            DeltaTheta = 0.15,
            MatchScore1 = 0.92,
            MatchScore2 = 0.89,
            NgReason = ngReason,
            Shift = ShiftSchedule.GetShiftName(createdAt),
            CreatedAt = createdAt
        });
    }
}
