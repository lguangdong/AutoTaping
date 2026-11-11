// 职责：配方实体（对应 SQLite Recipe 表），一个产品的全部工艺参数集合
// 层：Core/Models
// 依赖：无（纯数据类，不含任何数据库访问行为，见 10-编码约定 2.3）

namespace AutoTaping.Core.Models;

/// <summary>
/// 配方。表结构见 <c>docs/06-数据模型与数据库.md</c> 第 2 节，参数结构见第 4 节。
/// </summary>
public sealed class Recipe
{
    /// <summary>主键。新增时由数据库生成，调用方传入的值被忽略。</summary>
    public int Id { get; set; }

    /// <summary>配方名，未删除配方内唯一（如 <c>A123-3M双面胶</c>）。</summary>
    public string Name { get; set; } = "";

    /// <summary>产品料号。</summary>
    public string ProductCode { get; set; } = "";

    /// <summary>胶带规格。</summary>
    public string TapeSpec { get; set; } = "";

    /// <summary>完整工艺参数，落库时序列化为 <c>Recipe.JsonData</c> 列的 JSON。</summary>
    public RecipeParameters Parameters { get; set; } = new();

    /// <summary>配方版本号，每次保存（更新）自增，用于追溯"什么时候改过"。</summary>
    public int Version { get; set; }

    /// <summary>是否为当前激活配方。全局最多一条为 <see langword="true"/>。</summary>
    public bool IsActive { get; set; }

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>创建人用户名。</summary>
    public string CreatedBy { get; set; } = "";

    /// <summary>最后更新时间。</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>最后修改人用户名。</summary>
    public string UpdatedBy { get; set; } = "";
}
