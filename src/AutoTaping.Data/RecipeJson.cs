// 职责：Recipe.JsonData 列与配方 JSON 文件（导入/导出）的序列化与反序列化
// 层：Data
// 依赖：System.Text.Json、Core/Models

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoTaping.Core.Models;

namespace AutoTaping.Data;

/// <summary>
/// 配方参数 JSON 读写。键名采用 camelCase，与 <c>docs/06-数据模型与数据库.md</c> 第 4 节一致；
/// 枚举以字符串形式保存（如 <c>"Line"</c>）；缺字段时保留默认值，不抛异常。
/// </summary>
internal static class RecipeJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 不转义中文：导出的配方文件工程师要能直接打开看、手工改（默认编码器会把中文转成 \uXXXX）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>序列化配方参数为 JSON 文本（写入 JsonData 列或导出文件）。</summary>
    public static string Serialize(RecipeParameters parameters)
        => JsonSerializer.Serialize(parameters, Options);

    /// <summary>
    /// 反序列化配方参数。JSON 为空返回默认参数；JSON 损坏返回 <see langword="null"/>，由调用方决定如何记录与降级。
    /// </summary>
    public static RecipeParameters? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RecipeParameters();
        }

        try
        {
            return JsonSerializer.Deserialize<RecipeParameters>(json, Options) ?? new RecipeParameters();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>序列化配方导入/导出文件内容。</summary>
    public static string SerializeDocument(Recipe recipe)
        => JsonSerializer.Serialize(
            new RecipeFileDocument(recipe.Name, recipe.ProductCode, recipe.TapeSpec, recipe.Parameters),
            Options);

    /// <summary>解析配方导入文件；结构非法返回 <see langword="null"/>。</summary>
    public static RecipeFileDocument? TryParseDocument(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RecipeFileDocument>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 配方导入/导出文件的顶层结构，对应 <c>docs/09-报警与参数清单.md</c> 第 6 节的默认配方格式。
    /// </summary>
    /// <param name="Name">配方名。</param>
    /// <param name="ProductCode">产品料号。</param>
    /// <param name="TapeSpec">胶带规格。</param>
    /// <param name="Parameters">工艺参数。</param>
    internal sealed record RecipeFileDocument(
        string? Name,
        string? ProductCode,
        string? TapeSpec,
        RecipeParameters? Parameters);
}
