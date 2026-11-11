// 职责：配方参数范围校验（边界值、整组校验），错误提示必须带范围 + 原因（09 文档第五节）
// 层：Core/Services
// 依赖：ParameterCatalog/ParameterDescriptor/RecipeParameters（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Services;

/// <summary>
/// 参数校验器。规则见 <c>docs/09-报警与参数清单.md</c> 第五节与第七节约束 4：
/// 参数超范围时必须禁用保存按钮（不是保存后提示），提示文本必须带「范围 + 原因」。
/// </summary>
public static class ParameterValidator
{
    /// <summary>校验单个参数。键名不存在时也视为非法（避免拼错键名静默通过）。</summary>
    /// <param name="key">参数键名（如 <c>holdPressureMpa</c>）。</param>
    /// <param name="value">待校验的值。</param>
    /// <param name="error">校验失败原因；通过时为空串。</param>
    /// <returns>在允许范围内返回 <see langword="true"/>。</returns>
    public static bool TryValidate(string key, double value, out string error)
    {
        var descriptor = ParameterCatalog.Find(key);
        if (descriptor is null)
        {
            error = $"未知参数：{key}";
            return false;
        }

        return TryValidate(descriptor, value, out error);
    }

    /// <summary>按参数定义校验单个值。</summary>
    /// <param name="descriptor">参数定义（含范围与原因说明）。</param>
    /// <param name="value">待校验的值。</param>
    /// <param name="error">校验失败原因；通过时为空串。</param>
    public static bool TryValidate(ParameterDescriptor descriptor, double value, out string error)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            error = $"{descriptor.DisplayName}必须是有效数值";
            return false;
        }

        if (value < descriptor.Min || value > descriptor.Max)
        {
            error = string.IsNullOrEmpty(descriptor.OutOfRangeHint)
                ? $"{descriptor.DisplayName}应在 {descriptor.RangeText}"
                : $"{descriptor.DisplayName}应在 {descriptor.RangeText}（{descriptor.OutOfRangeHint}）";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>校验整份配方的全部标量参数（保存前整体校验）。</summary>
    /// <param name="parameters">配方参数。</param>
    /// <returns>失败原因列表；全部通过时为空列表。</returns>
    public static IReadOnlyList<string> ValidateRecipe(RecipeParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var errors = new List<string>();

        foreach (var descriptor in ParameterCatalog.All)
        {
            if (!ParameterCatalog.TryGetValue(parameters, descriptor.Key, out var value))
            {
                continue;
            }

            if (!TryValidate(descriptor, value, out var error))
            {
                errors.Add(error);
            }
        }

        return errors;
    }
}
