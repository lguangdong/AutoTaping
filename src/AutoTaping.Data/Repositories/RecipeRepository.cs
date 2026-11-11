// 职责：配方仓储的 SQLite + Dapper 实现（含软删除、激活切换、JSON 导入导出）
// 层：Data/Repositories
// 依赖：SqliteConnectionFactory、RecipeJson、IRecipeRepository、Dapper

using System.IO;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Data.Repositories;

/// <summary>
/// 配方仓储实现。表结构见 <c>Recipe</c>。删除一律软删除（<c>IsDeleted = 1</c>），
/// 参数以 JSON 存于 <c>JsonData</c> 列（见 06 文档第 4 节）。
/// </summary>
public sealed class RecipeRepository : IRecipeRepository
{
    private const string SelectColumns = """
        Id, Name, ProductCode, TapeSpec, JsonData, Version, IsActive,
        CreatedAt, CreatedBy, UpdatedAt, UpdatedBy
        """;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ILogger<RecipeRepository> _log;

    /// <summary>构造配方仓储。</summary>
    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时钟（写入创建/更新时间）。</param>
    /// <param name="log">日志。</param>
    public RecipeRepository(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILogger<RecipeRepository> log)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public IReadOnlyList<Recipe> GetAll()
    {
        using var connection = _connectionFactory.Create();
        var rows = connection.Query<RecipeRow>(
            $"SELECT {SelectColumns} FROM Recipe WHERE IsDeleted = 0 ORDER BY IsActive DESC, Name ASC;");

        return rows.Select(Map).ToList();
    }

    /// <inheritdoc />
    public Recipe? GetById(int id)
    {
        using var connection = _connectionFactory.Create();
        var row = connection.QuerySingleOrDefault<RecipeRow>(
            $"SELECT {SelectColumns} FROM Recipe WHERE Id = @Id AND IsDeleted = 0;",
            new { Id = id });

        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public Recipe? GetActive()
    {
        using var connection = _connectionFactory.Create();
        var row = connection.QueryFirstOrDefault<RecipeRow>(
            $"""
            SELECT {SelectColumns} FROM Recipe
            WHERE IsActive = 1 AND IsDeleted = 0
            ORDER BY Id ASC LIMIT 1;
            """);

        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public int Insert(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var now = SqliteTime.ToDb(_clock.Now);

        connection.Execute(
            """
            INSERT INTO Recipe (Name, ProductCode, TapeSpec, JsonData, Version, IsActive, IsDeleted,
                                CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
            VALUES (@Name, @ProductCode, @TapeSpec, @JsonData, @Version, @IsActive, 0,
                    @Now, @CreatedBy, @Now, @UpdatedBy);
            """,
            new
            {
                recipe.Name,
                recipe.ProductCode,
                recipe.TapeSpec,
                JsonData = RecipeJson.Serialize(recipe.Parameters),
                Version = recipe.Version <= 0 ? 1 : recipe.Version,
                IsActive = recipe.IsActive ? 1 : 0,
                Now = now,
                recipe.CreatedBy,
                UpdatedBy = string.IsNullOrEmpty(recipe.UpdatedBy) ? recipe.CreatedBy : recipe.UpdatedBy
            },
            transaction);

        var newId = (int)connection.ExecuteScalar<long>("SELECT last_insert_rowid();", transaction: transaction);
        transaction.Commit();

        recipe.Id = newId;
        _log.LogInformation("新增配方 {RecipeId} {Name}，激活 {IsActive}", newId, recipe.Name, recipe.IsActive);
        return newId;
    }

    /// <inheritdoc />
    public void Update(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var affected = connection.Execute(
            """
            UPDATE Recipe
            SET Name = @Name,
                ProductCode = @ProductCode,
                TapeSpec = @TapeSpec,
                JsonData = @JsonData,
                Version = Version + 1,
                IsActive = @IsActive,
                UpdatedAt = @Now,
                UpdatedBy = @UpdatedBy
            WHERE Id = @Id AND IsDeleted = 0;
            """,
            new
            {
                recipe.Id,
                recipe.Name,
                recipe.ProductCode,
                recipe.TapeSpec,
                JsonData = RecipeJson.Serialize(recipe.Parameters),
                IsActive = recipe.IsActive ? 1 : 0,
                Now = SqliteTime.ToDb(_clock.Now),
                recipe.UpdatedBy
            },
            transaction);

        transaction.Commit();

        if (affected == 0)
        {
            // 全表匹配不到：配方被删除或 Id 不存在。不静默通过，避免界面显示"已保存"其实没写进去
            throw new KeyNotFoundException($"配方不存在或已删除（Id={recipe.Id}），保存失败");
        }

        _log.LogInformation("更新配方 {RecipeId} {Name}，版本号自增", recipe.Id, recipe.Name);
    }

    /// <inheritdoc />
    public void SetActive(int id)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var now = SqliteTime.ToDb(_clock.Now);

        // 事务内先清后置，保证任何时刻最多一条激活配方
        connection.Execute(
            "UPDATE Recipe SET IsActive = 0, UpdatedAt = @Now WHERE IsActive = 1 AND Id <> @Id;",
            new { Now = now, Id = id },
            transaction);

        var affected = connection.Execute(
            "UPDATE Recipe SET IsActive = 1, UpdatedAt = @Now WHERE Id = @Id AND IsDeleted = 0;",
            new { Now = now, Id = id },
            transaction);

        transaction.Commit();

        if (affected == 0)
        {
            throw new KeyNotFoundException($"配方不存在或已删除（Id={id}），无法设为激活配方");
        }

        _log.LogInformation("切换激活配方为 {RecipeId}", id);
    }

    /// <inheritdoc />
    public void SoftDelete(int id)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        var affected = connection.Execute(
            "UPDATE Recipe SET IsDeleted = 1, IsActive = 0, UpdatedAt = @Now WHERE Id = @Id AND IsDeleted = 0;",
            new { Now = SqliteTime.ToDb(_clock.Now), Id = id },
            transaction);

        transaction.Commit();

        if (affected == 0)
        {
            throw new KeyNotFoundException($"配方不存在或已删除（Id={id}）");
        }

        _log.LogInformation("软删除配方 {RecipeId}", id);
    }

    /// <inheritdoc />
    public string ExportToFile(int id, string targetPath)
    {
        var recipe = GetById(id)
                     ?? throw new KeyNotFoundException($"配方不存在或已删除（Id={id}），无法导出");

        var json = RecipeJson.SerializeDocument(recipe);
        var fullPath = Path.GetFullPath(targetPath);

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        _log.LogInformation("导出配方 {RecipeId} 到 {Path}", id, fullPath);
        return fullPath;
    }

    /// <inheritdoc />
    public int ImportFromFile(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("配方文件不存在", sourcePath);
        }

        var document = RecipeJson.TryParseDocument(File.ReadAllText(sourcePath))
                       ?? throw new InvalidDataException($"配方文件格式非法：{sourcePath}");

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            throw new InvalidDataException($"配方文件缺少 name 字段：{sourcePath}");
        }

        var recipe = new Recipe
        {
            Name = document.Name.Trim(),
            ProductCode = document.ProductCode ?? "",
            TapeSpec = document.TapeSpec ?? "",
            Parameters = document.Parameters ?? new RecipeParameters(),
            Version = 1,
            IsActive = false,
            CreatedBy = "import",
            UpdatedBy = "import"
        };

        var newId = Insert(recipe);
        _log.LogInformation("从 {Path} 导入配方 {Name}（Id={RecipeId}）", sourcePath, recipe.Name, newId);
        return newId;
    }

    private Recipe Map(RecipeRow row)
    {
        var parameters = RecipeJson.TryDeserialize(row.JsonData);
        if (parameters is null)
        {
            // 单条配方 JSON 损坏不拖垮整张列表：记录后按默认参数返回，界面仍可打开并覆盖保存
            _log.LogWarning("配方 {RecipeId} 的参数 JSON 损坏，已按默认参数加载", row.Id);
            parameters = new RecipeParameters();
        }

        return new Recipe
        {
            Id = row.Id,
            Name = row.Name,
            ProductCode = row.ProductCode,
            TapeSpec = row.TapeSpec,
            Parameters = parameters,
            Version = row.Version,
            IsActive = row.IsActive != 0,
            CreatedAt = SqliteTime.FromDb(row.CreatedAt),
            CreatedBy = row.CreatedBy,
            UpdatedAt = SqliteTime.FromDb(row.UpdatedAt),
            UpdatedBy = row.UpdatedBy
        };
    }

    /// <summary>Recipe 表的一行（时间列为 TEXT，需显式转换）。</summary>
    private sealed class RecipeRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string TapeSpec { get; set; } = "";
        public string JsonData { get; set; } = "";
        public int Version { get; set; }
        public long IsActive { get; set; }
        public string CreatedAt { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string UpdatedBy { get; set; } = "";
    }
}
