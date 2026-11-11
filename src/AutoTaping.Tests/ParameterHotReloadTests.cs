// 职责：参数热生效测试——WaitCarrierStep 在节拍起点用激活配方的最新参数刷新流程上下文
// 层：Tests
// 依赖：TestDatabase、VirtualIoService、AutoTaping.Core.Flow/Steps/Models

using AutoTaping.Core.Flow;
using AutoTaping.Core.Flow.Steps;
using AutoTaping.Core.Models;
using AutoTaping.Hardware.Io;
using AutoTaping.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoTaping.Tests;

/// <summary>
/// 参数热生效测试，对应 S7 验收标准第 7 条（切配方/改参数后下一个节拍使用新参数，无需重启软件）。
/// 刷新点选在 <see cref="WaitCarrierStep"/>：它是引擎每个节拍的起点（走完第 15 步回到本步）。
/// </summary>
public sealed class ParameterHotReloadTests
{
    [Fact]
    public async Task WaitMaterial_RefreshesRecipeParametersFromActiveRecipe()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var recipes = database.CreateRecipeRepository();
        var active = recipes.GetActive();
        Assert.NotNull(active);

        // 流程上下文用一份"旧参数"（安全高度 10），模拟流程启动时读到的值
        var staleParameters = new RecipeParameters();
        staleParameters.Process.SafeHeightMm = 10;
        var context = new FlowContext
        {
            Recipe = new Recipe
            {
                Id = active.Id,
                Name = active.Name,
                Parameters = staleParameters
            }
        };

        // 期间技术员把安全高度改成 60（保存到激活配方）
        active.Parameters.Process.SafeHeightMm = 60;
        recipes.Update(active);

        // 上料位有料，步骤会立即通过并刷新参数
        var io = new VirtualIoService(
            ["X00", "X05", "X06", "X07"],
            [],
            new Dictionary<string, bool> { ["X00"] = true, ["X05"] = true, ["X06"] = true, ["X07"] = true },
            NullLogger<VirtualIoService>.Instance);

        var step = new WaitCarrierStep(io, recipes, NullLogger<WaitCarrierStep>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await step.ExecuteAsync(context, cts.Token);

        // 下一个节拍读到的是新参数
        Assert.Equal(60, context.Recipe.Parameters.Process.SafeHeightMm, 6);
        Assert.Equal(60, recipes.GetActive()!.Parameters.Process.SafeHeightMm, 6);
    }

    [Fact]
    public async Task WaitMaterial_KeepsPreviousParameters_WhenNoActiveRecipe()
    {
        using var database = new TestDatabase();
        database.Initialize();

        var recipes = database.CreateRecipeRepository();
        var active = recipes.GetActive();
        Assert.NotNull(active);
        recipes.SoftDelete(active.Id);   // 配方被删除

        var parameters = new RecipeParameters();
        parameters.Process.TapeHeightMm = 3.5;
        var context = new FlowContext
        {
            Recipe = new Recipe { Id = active.Id, Name = active.Name, Parameters = parameters }
        };

        var io = new VirtualIoService(
            ["X00"],
            [],
            new Dictionary<string, bool> { ["X00"] = true },
            NullLogger<VirtualIoService>.Instance);

        var step = new WaitCarrierStep(io, recipes, NullLogger<WaitCarrierStep>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await step.ExecuteAsync(context, cts.Token);

        // 没有激活配方时沿用上一拍参数，不抛异常（互锁会在下次启动时拦截）
        Assert.Equal(3.5, context.Recipe.Parameters.Process.TapeHeightMm, 6);
    }
}
