// 职责：报警仓储 CRUD 测试（写入/查询/确认/关闭/计数/CSV 导出）
// 层：Tests
// 依赖：TestDatabase、AutoTaping.Core.Models

using System.IO;
using AutoTaping.Core.Models;
using AutoTaping.Tests.Infrastructure;

namespace AutoTaping.Tests;

/// <summary>
/// <c>AlarmRepository</c> 的 CRUD 测试，对应 S1 验收标准第 5 条。
/// </summary>
public sealed class AlarmRepositoryTests
{
    [Fact]
    public void AlarmRepository_SupportsInsertQueryAckAndClose()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var repository = database.CreateAlarmRepository();

        var now = DateTime.Now;

        repository.Insert(new AlarmRecord
        {
            Code = "AL301",
            Message = "真空吸不住",
            Level = AlarmLevel.Critical,
            Source = "贴附头",
            StartTime = now
        });

        repository.Insert(new AlarmRecord
        {
            Code = "AL303",
            Message = "胶带余量低",
            Level = AlarmLevel.Warning,
            Source = "上料",
            StartTime = now.AddSeconds(1)
        });

        var all = repository.Query(now.AddMinutes(-1), now.AddMinutes(1));
        Assert.Equal(2, all.Count);
        Assert.Equal("AL303", all[0].Code);   // 按开始时间倒序

        var critical = Assert.Single(all, r => r.Code == "AL301");
        Assert.Equal(AlarmLevel.Critical, critical.Level);
        Assert.False(critical.IsAcked);
        Assert.Null(critical.AckUser);

        // 未确认严重报警数（启动互锁用）
        Assert.Equal(1, repository.CountUnacked((int)AlarmLevel.Critical));
        Assert.Equal(2, repository.CountUnacked((int)AlarmLevel.Info));

        // 确认：写入确认时间与确认人，从"未确认"里消失
        repository.Ack(critical.Id, "tech01");
        Assert.Single(repository.Query(now.AddMinutes(-1), now.AddMinutes(1), onlyUnacked: true));

        // 重复确认必须报错，不能静默成功
        Assert.Throws<KeyNotFoundException>(() => repository.Ack(critical.Id, "tech01"));

        // 关闭全部生效中报警：EndTime 全部写入
        repository.CloseAllActive();
        Assert.Equal(0, repository.CountUnacked((int)AlarmLevel.Info));
    }

    [Fact]
    public void AlarmRepository_ExportsCsvWithBom()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var repository = database.CreateAlarmRepository();

        repository.Insert(new AlarmRecord
        {
            Code = "AL104",
            Message = "轴运动超时,检查机械卡阻",
            Level = AlarmLevel.Critical,
            Source = "运控卡",
            StartTime = DateTime.Now
        });

        var csvPath = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "alarms.csv");
        repository.ExportCsv(DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(5), csvPath);

        var bytes = File.ReadAllBytes(csvPath);
        Assert.Equal<byte>([0xEF, 0xBB, 0xBF], bytes.Take(3));   // UTF-8 BOM，Excel 中文不乱码

        var text = File.ReadAllText(csvPath);
        Assert.Contains("代码,内容,等级,来源", text);
        Assert.Contains("AL104", text);
        Assert.Contains("严重", text);
        Assert.Contains("\"轴运动超时,检查机械卡阻\"", text);          // 含逗号字段必须加引号
    }
}
