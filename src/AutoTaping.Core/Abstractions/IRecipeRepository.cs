// 职责：配方仓储抽象（签名与 docs/06-数据模型与数据库 第 5 节完全一致，不得私自变更）
// 层：Core/Abstractions
// 依赖：Recipe（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 配方仓储。实现位于 <c>AutoTaping.Data</c>，Core 与 ViewModel 只依赖本接口（10-编码约定 三、第 5 条）。
/// </summary>
public interface IRecipeRepository
{
    /// <summary>获取全部未删除配方（按激活优先、名称排序）。</summary>
    IReadOnlyList<Recipe> GetAll();

    /// <summary>按主键获取配方；不存在或已软删除返回 <see langword="null"/>。</summary>
    /// <param name="id">配方主键。</param>
    Recipe? GetById(int id);

    /// <summary>获取当前激活配方；没有激活配方返回 <see langword="null"/>。</summary>
    Recipe? GetActive();

    /// <summary>新增配方，返回新主键。创建时间由仓储写入。</summary>
    /// <param name="recipe">待新增配方。</param>
    int Insert(Recipe recipe);

    /// <summary>更新配方（版本号自增，更新时间由仓储写入）。</summary>
    /// <param name="recipe">待更新配方，须带有效 <see cref="Recipe.Id"/>。</param>
    void Update(Recipe recipe);

    /// <summary>设置激活配方：事务内先把全部配方置为未激活，再把目标置为激活。</summary>
    /// <param name="id">目标配方主键。</param>
    void SetActive(int id);

    /// <summary>软删除配方（<c>IsDeleted = 1</c>），不得物理删除。</summary>
    /// <param name="id">目标配方主键。</param>
    void SoftDelete(int id);

    /// <summary>导出单个配方为 JSON 文件，返回实际写入的文件路径。</summary>
    /// <param name="id">配方主键。</param>
    /// <param name="targetPath">目标 JSON 文件路径。</param>
    string ExportToFile(int id, string targetPath);

    /// <summary>从 JSON 文件导入配方（作为新配方插入），返回新主键。</summary>
    /// <param name="sourcePath">源 JSON 文件路径。</param>
    int ImportFromFile(string sourcePath);
}
