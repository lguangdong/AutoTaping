// 职责：产出记录仓储抽象（签名与 docs/06-数据模型与数据库 第 5 节完全一致，不得私自变更）
// 层：Core/Abstractions
// 依赖：OutputRecord、OutputStatistics（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 产出记录仓储。班次归属、停机分类等业务规则由 S7 的 <c>OutputService</c> 负责。
/// </summary>
public interface IOutputRepository
{
    /// <summary>新增一条产出记录。</summary>
    /// <param name="record">待写入记录。</param>
    void Insert(OutputRecord record);

    /// <summary>统计今日指定结果的件数。</summary>
    /// <param name="result">结果标记：<see cref="OutputRecord.ResultOk"/> 或 <see cref="OutputRecord.ResultNg"/>。</param>
    int CountToday(string result);

    /// <summary>取指定日期的下一个当日流水号（从 1 开始）。</summary>
    /// <param name="date">日期，只用日期部分。</param>
    int NextSequenceNo(DateTime date);

    /// <summary>统计区间内的总产量、良品数与平均节拍。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    OutputStatistics GetStatistics(DateTime from, DateTime to);

    /// <summary>查询区间内的产出明细（按时间倒序，最多 <paramref name="maxCount"/> 条）。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    /// <param name="shift">班次过滤；<see langword="null"/> 表示全部班次。</param>
    /// <param name="maxCount">最多返回条数，必须大于 0。</param>
    IReadOnlyList<OutputRecord> Query(DateTime from, DateTime to, string? shift, int maxCount);

    /// <summary>导出区间内产出明细为 CSV（UTF-8 with BOM）。</summary>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    /// <param name="path">目标 CSV 文件路径。</param>
    void ExportCsv(DateTime from, DateTime to, string path);
}
