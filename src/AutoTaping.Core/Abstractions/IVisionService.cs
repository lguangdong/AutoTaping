// 职责：视觉识别纠偏抽象——流程引擎通过本接口使用视觉，不直接依赖 Vision 具体类（01-架构设计 第 1 节）
// 层：Core/Abstractions
// 依赖：VisionParameters、Calibration、VisionCorrection（Core/Models）

using AutoTaping.Core.Models;

namespace AutoTaping.Core.Abstractions;

/// <summary>
/// 视觉识别纠偏服务。把「识别两个 MARK + 计算纠偏」封装成一个入口，供 Core 的流程引擎调用；
/// 具体算法实现位于 <c>AutoTaping.Vision.VisionService</c>（视觉算法只依赖 OpenCvSharp）。
/// </summary>
public interface IVisionService : IDisposable
{
    /// <summary>设置本次流程使用的视觉参数与当前生效标定（流程启动时调用一次）。</summary>
    /// <param name="vision">配方视觉参数（ROI / 模板路径 / 阈值 / 基准像素 / 纠偏上限）。</param>
    /// <param name="calibration">当前生效标定（仿射矩阵）；<see langword="null"/> 表示无标定（识别时返回失败）。</param>
    /// <param name="recipeName">配方名，用于替换模板路径中的 <c>{recipe}</c> 占位符。</param>
    void Configure(VisionParameters vision, Calibration? calibration, string recipeName);

    /// <summary>识别一帧灰度图的两个 MARK 并计算纠偏量。</summary>
    /// <param name="mono8">灰度原始数据（Mono8，一像素一字节）。</param>
    /// <param name="width">图像宽度（像素）。</param>
    /// <param name="height">图像高度（像素）。</param>
    VisionCorrection Correct(byte[] mono8, int width, int height);
}
