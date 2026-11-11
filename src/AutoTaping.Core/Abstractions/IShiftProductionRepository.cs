// 职责：班次产量汇总仓储抽象（ShiftProduction 表：每天每班一条）
// 层：Core/Abstractions
// 依赖：ShiftProduction（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 班次产量汇总仓储。<c>ShiftDate + ShiftName</c> 唯一（06 文档第 2 节），
/// 写入采用「有则更新、无则插入」，可反复重算。
/// </summary>
public interface IShiftProductionRepository
{
    /// <summary>写入（或覆盖）一条班次汇总。</summary>
    /// <param name="summary">班次汇总；时间由仓储按传入值写入。</param>
    void Upsert(ShiftProduction summary);

    /// <summary>取某一天的全部班次汇总（按班次时间顺序）。</summary>
    /// <param name="date">日期（只用日期部分）。</param>
    IReadOnlyList<ShiftProduction> Query(DateTime date);
}
