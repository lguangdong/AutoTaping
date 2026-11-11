// 职责：停机记录仓储抽象（S7 新增，配合 05 文档第 7.2 节的停机原因分类）
// 层：Core/Abstractions
// 依赖：DowntimeRecord（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 停机记录仓储。写入来源见 <c>docs/05-核心状态机与流程.md</c> 第 7.2 节：
/// 待料、故障停机、人工暂停、换型、断电（启动时补记）。
/// </summary>
public interface IDowntimeRepository
{
    /// <summary>新增一条停机记录，返回新主键。</summary>
    /// <param name="record">待写入记录。</param>
    int Insert(DowntimeRecord record);

    /// <summary>按时间区间查询停机记录（开始时间倒序）。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    IReadOnlyList<DowntimeRecord> Query(DateTime from, DateTime to);
}
