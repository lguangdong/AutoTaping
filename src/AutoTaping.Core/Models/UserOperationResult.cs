// 职责：用户管理操作的结果值对象（成功标志 + 面向操作工的中文说明）
// 层：Core/Models
// 依赖：无（纯数据）

namespace AutoTaping.Core.Models;

/// <summary>
/// 用户管理操作结果（新增 / 改角色 / 改密 / 启用禁用 / 删除）。
/// 失败原因必须说清「为什么被拒绝 + 该怎么办」，不得只说"操作失败"（07-UI设计规范 第 8 节）。
/// </summary>
/// <param name="Succeeded">是否执行成功。</param>
/// <param name="Message">成功提示或失败原因。</param>
public sealed record UserOperationResult(bool Succeeded, string Message)
{
    /// <summary>构造成功结果。</summary>
    /// <param name="message">给操作看的成功提示。</param>
    public static UserOperationResult Success(string message) => new(true, message);

    /// <summary>构造失败结果。</summary>
    /// <param name="message">面向操作工的失败原因与建议。</param>
    public static UserOperationResult Failure(string message) => new(false, message);
}
