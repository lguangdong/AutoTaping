// 职责：单个配方参数的定义（键名 / 中文名 / 单位 / 分组 / 范围 / 默认值 / 权限），供参数页与校验器共用
// 层：Core/Models
// 依赖：ParameterGroup、Permission

namespace AutoTaping.Core.Models;

/// <summary>
/// 配方参数定义。键名与 <c>docs/09-报警与参数清单.md</c> 第四节参数表的「键名」列完全一致，
/// 范围取该表或 <c>docs/00-项目总览.md</c> 第 1.2 节的示例行程。
/// </summary>
/// <param name="Key">参数键名（camelCase，如 <c>holdPressureMpa</c>）。</param>
/// <param name="DisplayName">中文名称（参数页显示）。</param>
/// <param name="Unit">单位（如 <c>MPa</c>）；无单位传空串。</param>
/// <param name="Group">所属分组。</param>
/// <param name="Min">允许的最小值（含）。</param>
/// <param name="Max">允许的最大值（含）。</param>
/// <param name="DefaultValue">代码内置默认值（「恢复默认」用）。</param>
/// <param name="Decimals">显示与提示使用的小数位数。</param>
/// <param name="RequiredPermission">修改本参数所需的权限（查看不需要权限）。</param>
/// <param name="OutOfRangeHint">超范围提示中附加的原因说明；无特殊说明传 <see langword="null"/>。</param>
public sealed record ParameterDescriptor(
    string Key,
    string DisplayName,
    string Unit,
    ParameterGroup Group,
    double Min,
    double Max,
    double DefaultValue,
    int Decimals,
    Permission RequiredPermission = Permission.RecipeEdit,
    string? OutOfRangeHint = null)
{
    /// <summary>按小数位格式化后的最小值文本。</summary>
    public string MinText => Min.ToString($"F{Decimals}", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>按小数位格式化后的最大值文本。</summary>
    public string MaxText => Max.ToString($"F{Decimals}", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>范围提示文本，形如 <c>0.10 ~ 0.40 MPa</c>（09 文档：错误提示必须带范围）。</summary>
    public string RangeText => string.IsNullOrEmpty(Unit)
        ? $"{MinText} ~ {MaxText}"
        : $"{MinText} ~ {MaxText} {Unit}";
}
