// 职责：登录结果值对象（成功标志 + 面向操作工的中文失败原因）
// 层：Core/Models
// 依赖：无

namespace AutoTaping.Core.Models;

/// <summary>
/// 登录结果。失败原因必须是操作工能看懂的一句话（10-编码约定 6.3：不得把异常文本直接显示给操作工）。
/// </summary>
/// <param name="Succeeded">是否登录成功。</param>
/// <param name="Message">失败原因；成功时为空字符串。</param>
public sealed record AuthResult(bool Succeeded, string Message)
{
    /// <summary>构造成功结果。</summary>
    public static AuthResult Success() => new(true, string.Empty);

    /// <summary>构造失败结果。</summary>
    /// <param name="message">面向操作工的失败原因。</param>
    public static AuthResult Failure(string message) => new(false, message);
}
