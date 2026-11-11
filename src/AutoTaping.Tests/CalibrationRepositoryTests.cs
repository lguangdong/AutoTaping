// 职责：标定记录仓储单元测试（落库置 active、查询当前生效标定、历史倒序），对应 S5 交付物「标定结果落库」
// 层：Tests
// 依赖：TestDatabase、AutoTaping.Data

using AutoTaping.Core.Models;
using AutoTaping.Tests.Infrastructure;

namespace AutoTaping.Tests;

/// <summary>
/// <see cref="AutoTaping.Data.Repositories.CalibrationRepository"/> 的测试：
/// 标定结果落库后成为当前生效标定，且多次标定只保留一条 active。
/// </summary>
public sealed class CalibrationRepositoryTests
{
    private static Calibration CreateCalibration(double rms, string user = "admin")
        => new()
        {
            AffineMatrix = new double[] { 0.25, 0, 50, 0, -0.25, 325 },
            RmsError = rms,
            PointCount = 9,
            ImageFolder = $"images/calibration/{rms:F4}",
            CreatedBy = user
        };

    /// <summary>落库后 GetActive 能读回完整数据，且 IsActive 为 true。</summary>
    [Fact]
    public void Insert_ThenGetActive_ReturnsInsertedCalibration()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var repository = database.CreateCalibrationRepository();
        repository.Insert(CreateCalibration(0.02));

        var active = repository.GetActive();

        Assert.NotNull(active);
        Assert.True(active.IsActive);
        Assert.Equal(0.02, active.RmsError, 6);
        Assert.Equal(9, active.PointCount);
        Assert.Equal(new double[] { 0.25, 0, 50, 0, -0.25, 325 }, active.AffineMatrix);
        Assert.Equal("admin", active.CreatedBy);
    }

    /// <summary>第二次标定落库后，新记录成为 active，旧记录被置为非 active。</summary>
    [Fact]
    public void Insert_Twice_OnlyLatestIsActive()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var repository = database.CreateCalibrationRepository();
        repository.Insert(CreateCalibration(0.02));
        repository.Insert(CreateCalibration(0.03));

        var active = repository.GetActive();
        Assert.NotNull(active);
        Assert.Equal(0.03, active.RmsError, 6);

        var recent = repository.GetRecent(10);
        // 种子标定（0.01）+ 两次插入（0.02、0.03），共 3 条，仅最新一条 active
        Assert.Equal(3, recent.Count);
        Assert.Equal(1, recent.Count(c => c.IsActive));
        Assert.Equal(0.03, recent[0].RmsError, 6);
    }

    /// <summary>历史记录按时间倒序（最新在前）。</summary>
    [Fact]
    public void GetRecent_ReturnsNewestFirst()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var repository = database.CreateCalibrationRepository();
        for (var i = 1; i <= 3; i++)
        {
            repository.Insert(CreateCalibration(0.01 * i));
        }

        var recent = repository.GetRecent(2);
        Assert.Equal(2, recent.Count);
        Assert.Equal(0.03, recent[0].RmsError, 6);
        Assert.Equal(0.02, recent[1].RmsError, 6);
    }

    /// <summary>初始化后种子示例标定（虚拟模式）成为当前生效标定。</summary>
    [Fact]
    public void Initialize_SeedsDefaultCalibration()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var repository = database.CreateCalibrationRepository();

        var active = repository.GetActive();
        Assert.NotNull(active);
        Assert.True(active.IsActive);
        Assert.Equal(9, active.PointCount);
    }
}
