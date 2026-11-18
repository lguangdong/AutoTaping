// 职责：步骤 5 拍照 + 识别 + 纠偏——拍照失败重试 2 次，识别/纠偏失败分别报警 AL201/AL202/AL203（05 第 4.2 节第 5 步）
// 层：Core/Flow/Steps
// 依赖：ICamera、IVisionService（Core/Abstractions）、VisionCorrectionError（Core/Models）、FlowException

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Exceptions;
using AutoTaping.Core.Models;

namespace AutoTaping.Core.Flow.Steps;

/// <summary>拍照识别纠偏步骤（5s 超时）。拍照超时内部重试 2 次；MARK 识别失败 / 纠偏超限不重试，立即中止。</summary>
public sealed class CaptureAndCorrectStep : FlowStep
{
    /// <summary>拍照失败内部重试次数（05 第 5 节：拍照超时重试 2 次）。</summary>
    private const int CameraRetryCount = 2;

    /// <summary>单次取图超时（ms），与 appsettings 的相机超时一致。</summary>
    private const int CaptureTimeoutMs = 3_000;

    /// <summary>重试取图间隔（ms）。</summary>
    private const int RetryDelayMs = 200;

    private readonly ICamera _camera;
    private readonly IVisionService _vision;

    /// <summary>构造拍照纠偏步骤。</summary>
    public CaptureAndCorrectStep(ICamera camera, IVisionService vision)
    {
        _camera = camera;
        _vision = vision;
    }

    /// <inheritdoc />
    public override string Name => "CaptureAndCorrect";

    /// <inheritdoc />
    public override int TimeoutMs => 5_000;

    /// <inheritdoc />
    public override async Task ExecuteAsync(FlowContext ctx, CancellationToken ct)
    {
        var frame = await GrabWithRetryAsync(ct);
        if (!frame.Success || frame.Mono8Data is null)
        {
            throw new FlowException(Name, "拍照失败，请检查相机", "AL201", AlarmLevel.Critical, "视觉",
                                    pauseInsteadOfAlarm: false, innerException: null);
        }

        // 留存最近一帧：识别失败时这张图是排障的唯一证据
        ctx.UpdateLastFrame(frame.Mono8Data, frame.Width, frame.Height);

        var correction = _vision.Correct(frame.Mono8Data, frame.Width, frame.Height);

        // 成败都写回上下文：界面需要显示失败原因（流程后续步骤只在成功时使用该结果）
        ctx.LastCorrection = correction;

        if (!correction.Success)
        {
            switch (correction.Error)
            {
                case VisionCorrectionError.MarkNotFound:
                    throw new FlowException(Name, correction.ErrorDetail ?? "MARK 识别失败",
                                            "AL202", AlarmLevel.Warning, "视觉",
                                            pauseInsteadOfAlarm: false, innerException: null);
                case VisionCorrectionError.CorrectionExceeded:
                    throw new FlowException(Name, correction.ErrorDetail ?? "纠偏量超限",
                                            "AL203", AlarmLevel.Critical, "视觉",
                                            pauseInsteadOfAlarm: false, innerException: null);
                default:
                    throw new FlowException(Name, correction.ErrorDetail ?? "无生效标定，无法纠偏",
                                            "AL202", AlarmLevel.Warning, "视觉",
                                            pauseInsteadOfAlarm: false, innerException: null);
            }
        }
    }

    /// <summary>拍照并内部重试 2 次（仅拍照超时重试，识别失败不重试）。</summary>
    private async Task<CaptureResult> GrabWithRetryAsync(CancellationToken ct)
    {
        CaptureResult frame = new() { Success = false };

        for (var attempt = 0; attempt <= CameraRetryCount; attempt++)
        {
            frame = await _camera.TriggerAndGrabAsync(CaptureTimeoutMs, ct);
            if (frame.Success)
            {
                return frame;
            }

            if (attempt < CameraRetryCount)
            {
                await Task.Delay(RetryDelayMs, ct);
            }
        }

        return frame;
    }
}
