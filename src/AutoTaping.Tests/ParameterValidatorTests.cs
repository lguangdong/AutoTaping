// 职责：ParameterValidator / ParameterCatalog 单元测试——边界值、错误提示带范围、整份配方校验
// 层：Tests
// 依赖：AutoTaping.Core（ParameterValidator、ParameterCatalog、RecipeParameters）

using AutoTaping.Core.Models;
using AutoTaping.Core.Services;

namespace AutoTaping.Tests;

/// <summary>
/// 参数校验测试，对应 S7 验收标准第 1 条与 10-编码约定第十一节（边界值：最小-1、最小、最大、最大+1）。
/// </summary>
public sealed class ParameterValidatorTests
{
    [Theory]
    [InlineData(0.09, false)]   // 最小 - 0.01
    [InlineData(0.10, true)]    // 最小
    [InlineData(0.25, true)]    // 默认值
    [InlineData(0.40, true)]    // 最大
    [InlineData(0.5, false)]    // S7 验收标准第 1 条：改成 0.5 必须被拒绝
    public void HoldPressure_ValidatesRange(double value, bool expected)
        => Assert.Equal(expected, ParameterValidator.TryValidate("holdPressureMpa", value, out _));

    [Fact]
    public void HoldPressure_ErrorTextContainsRange()
    {
        Assert.False(ParameterValidator.TryValidate("holdPressureMpa", 0.5, out var error));

        // 09 文档第五节与第七节约束 4：提示必须带范围
        Assert.Contains("0.10", error);
        Assert.Contains("0.40", error);
        Assert.Contains("MPa", error);
    }

    [Theory]
    [InlineData(-0.01, false)]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(10.01, false)]
    public void TapeHeight_ValidatesRange(double value, bool expected)
        => Assert.Equal(expected, ParameterValidator.TryValidate("tapeHeightMm", value, out _));

    [Theory]
    [InlineData(0.49, false)]
    [InlineData(0.5, true)]
    [InlineData(0.95, true)]
    [InlineData(0.96, false)]
    public void MatchThreshold_ValidatesRange(double value, bool expected)
        => Assert.Equal(expected, ParameterValidator.TryValidate("matchThreshold", value, out _));

    [Fact]
    public void MatchThreshold_ErrorTextContainsReason()
    {
        Assert.False(ParameterValidator.TryValidate("matchThreshold", 0.2, out var error));

        // 09 文档：过低会误识别，过高会频繁失败
        Assert.Contains("误识别", error);
    }

    [Fact]
    public void UnknownKey_IsRejected()
    {
        Assert.False(ParameterValidator.TryValidate("notARealKey", 1, out var error));
        Assert.Contains("未知参数", error);
    }

    [Fact]
    public void DefaultRecipe_PassesValidation()
    {
        // 恢复默认（new RecipeParameters()）后必须整体合法，否则「恢复默认」按钮会立刻禁用保存
        Assert.Empty(ParameterValidator.ValidateRecipe(new RecipeParameters()));
    }

    [Fact]
    public void ValidateRecipe_ReportsEveryOutOfRangeParameter()
    {
        var parameters = new RecipeParameters();
        parameters.Process.HoldPressureMpa = 0.9;
        parameters.Process.BlowOffMs = 10;
        parameters.Vision.MatchThreshold = 0.99;

        var errors = ParameterValidator.ValidateRecipe(parameters);

        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, error => error.Contains("贴附压力"));
        Assert.Contains(errors, error => error.Contains("吹气时间"));
        Assert.Contains(errors, error => error.Contains("匹配阈值"));
    }

    [Fact]
    public void Catalog_RoundTripsEveryDescriptor()
    {
        var parameters = new RecipeParameters();

        foreach (var descriptor in ParameterCatalog.All)
        {
            Assert.True(ParameterCatalog.TryGetValue(parameters, descriptor.Key, out var value), descriptor.Key);
            Assert.True(ParameterCatalog.SetValue(parameters, descriptor.Key, descriptor.Max), descriptor.Key);
            Assert.True(ParameterCatalog.TryGetValue(parameters, descriptor.Key, out var updated), descriptor.Key);
            Assert.Equal(descriptor.Max, updated, 6);
            Assert.True(ParameterValidator.TryValidate(descriptor, value, out _), descriptor.Key);
        }
    }

    [Fact]
    public void Catalog_CoversEveryScalarGroup()
    {
        foreach (var group in Enum.GetValues<ParameterGroup>())
        {
            if (group == ParameterGroup.TapePath)
            {
                // 贴附路径由「模式 + 路径点 + 圆弧参数」组成，不是标量参数，参数页用独立编辑区
                Assert.Empty(ParameterCatalog.GetByGroup(group));
                continue;
            }

            Assert.NotEmpty(ParameterCatalog.GetByGroup(group));
        }
    }
}
