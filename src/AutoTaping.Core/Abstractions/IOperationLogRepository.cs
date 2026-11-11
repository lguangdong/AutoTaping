// 职责：操作日志仓储抽象（签名与 docs/06-数据模型与数据库 第 5 节完全一致，不得私自变更）
// 层：Core/Abstractions
// 依赖：OperationLog（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 操作日志仓储。参数修改、登录、标定等操作必须留痕（10-编码约定 7.3）。
/// </summary>
public interface IOperationLogRepository
{
    /// <summary>新增一条操作日志，记录时间由仓储写入。</summary>
    /// <param name="user">操作用户名。</param>
    /// <param name="action">操作动作，如 <c>修改参数</c>。</param>
    /// <param name="target">操作对象。</param>
    /// <param name="oldValue">变更前值，无则传 <see langword="null"/>。</param>
    /// <param name="newValue">变更后值，无则传 <see langword="null"/>。</param>
    void Insert(string user, string action, string target, string? oldValue, string? newValue);

    /// <summary>按时间区间查询操作日志（时间倒序）。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    IReadOnlyList<OperationLog> Query(DateTime from, DateTime to);
}
