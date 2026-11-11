// 职责：配方仓储 CRUD 测试（新增/查询/更新/激活切换/软删除/导入导出）
// 层：Tests
// 依赖：TestDatabase、AutoTaping.Core.Models

using System.IO;
using AutoTaping.Core.Models;
using AutoTaping.Tests.Infrastructure;

namespace AutoTaping.Tests;

/// <summary>
/// <c>RecipeRepository</c> 的 CRUD 测试，对应 S1 验收标准第 5 条。
/// </summary>
public sealed class RecipeRepositoryTests
{
    [Fact]
    public void RecipeRepository_SupportsCrudAndActiveSwitch()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var repository = database.CreateRecipeRepository();

        // 种子配方已存在且激活
        var seed = repository.GetActive();
        Assert.NotNull(seed);
        Assert.Single(repository.GetAll());

        // Create
        var recipe = new Recipe
        {
            Name = "A123-3M双面胶",
            ProductCode = "A123",
            TapeSpec = "3M-467MP",
            Parameters = new RecipeParameters { Process = { HoldPressureMs = 500 } },
            Version = 1,
            CreatedBy = "tech01"
        };
        var id = repository.Insert(recipe);
        Assert.True(id > 0);
        Assert.Equal(id, recipe.Id);

        // Read
        var loaded = repository.GetById(id);
        Assert.NotNull(loaded);
        Assert.Equal("A123-3M双面胶", loaded.Name);
        Assert.Equal("A123", loaded.ProductCode);
        Assert.Equal(500, loaded.Parameters.Process.HoldPressureMs);
        Assert.Equal(50.0, loaded.Parameters.Process.SafeHeightMm, 3);   // JSON 未写的字段用默认值
        Assert.Equal(80.0, loaded.Parameters.Position.PickPosY, 3);      // 位置参数（头坐标）默认值
        Assert.Equal(44.0, loaded.Parameters.Position.CameraOffsetY, 3); // 相机偏置默认值（手眼标定）
        Assert.False(loaded.IsActive);
        Assert.Equal("tech01", loaded.CreatedBy);
        Assert.NotEqual(default, loaded.CreatedAt);

        // Update（版本号自增）
        loaded.Parameters.Process.HoldPressureMs = 800;
        loaded.UpdatedBy = "tech01";
        repository.Update(loaded);

        var updated = repository.GetById(id);
        Assert.NotNull(updated);
        Assert.Equal(800, updated.Parameters.Process.HoldPressureMs);
        Assert.Equal(2, updated.Version);

        // 激活切换（全局只允许一条激活）
        repository.SetActive(id);
        Assert.Equal(id, repository.GetActive()!.Id);
        Assert.Single(repository.GetAll(), r => r.IsActive);

        // 软删除：查不到、不在列表里、不再是激活配方
        repository.SoftDelete(id);
        Assert.Null(repository.GetById(id));
        Assert.Null(repository.GetActive());
        Assert.DoesNotContain(repository.GetAll(), r => r.Id == id);
    }

    [Fact]
    public void RecipeRepository_ExportsAndImportsJsonFile()
    {
        using var database = new TestDatabase();
        database.Initialize();
        var repository = database.CreateRecipeRepository();

        var id = repository.Insert(new Recipe
        {
            Name = "导入导出验证",
            ProductCode = "EXP-1",
            TapeSpec = "3M-467MP",
            Parameters = new RecipeParameters { Position = { PickPosY = 66.0 } },
            Version = 1,
            IsActive = false,
            CreatedBy = "tech01"
        });

        var exportPath = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "导出.json");
        var writtenPath = repository.ExportToFile(id, exportPath);

        Assert.True(File.Exists(writtenPath));
        var json = File.ReadAllText(writtenPath);
        Assert.Contains("\"name\"", json);
        Assert.Contains("导入导出验证", json);
        Assert.Contains("pickPosY", json);

        // 解除唯一名约束后才能用同名重新导入
        repository.SoftDelete(id);

        var importedId = repository.ImportFromFile(writtenPath);
        Assert.NotEqual(id, importedId);

        var imported = repository.GetById(importedId);
        Assert.NotNull(imported);
        Assert.Equal("导入导出验证", imported.Name);
        Assert.Equal("EXP-1", imported.ProductCode);
        Assert.Equal(66.0, imported.Parameters.Position.PickPosY, 3);
        Assert.False(imported.IsActive);
    }
}
