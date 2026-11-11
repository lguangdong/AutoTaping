// 职责：标定记录仓储抽象（保存九点标定结果、查询当前生效标定与历史）
// 层：Core/Abstractions
// 依赖：Calibration（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 标定记录仓储。对应 <c>Calibration</c> 表（见 <c>docs/06-数据模型与数据库.md</c> 第 2 节）。
/// 标定结果落库时同时置为当前生效标定（事务内先清后置，保证任何时刻最多一条激活）。
/// </summary>
public interface ICalibrationRepository
{
    /// <summary>保存标定结果并置为当前生效标定。时间由仓储写入。</summary>
    /// <param name="calibration">标定结果，<see cref="Calibration.AffineMatrix"/> 必须为 6 元素。</param>
    void Insert(Calibration calibration);

    /// <summary>获取当前生效的标定结果；没有返回 <see langword="null"/>。</summary>
    Calibration? GetActive();

    /// <summary>获取最近 N 条标定记录（按时间倒序），用于追溯标定历史。</summary>
    /// <param name="count">最多返回条数。</param>
    IReadOnlyList<Calibration> GetRecent(int count);
}
