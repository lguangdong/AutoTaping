// 职责：自动流程执行器——步骤循环 + 重试/超时 + 暂停恢复 + 单步模式（05-核心状态机 第 4.3 节）
// 层：Core/Flow
// 依赖：FlowStep、FlowContext、FlowException（Core/Exceptions）、ILogger

using AutoTaping.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.Flow;

/// <summary>
/// 自动流程执行器。按顺序执行步骤列表，循环回到第 2 步（WaitMaterial）实现连续生产；
/// 支持步骤级超时、失败重试、暂停打断后从当前步骤恢复、单步走一步即停。
/// 引擎自身不接触硬件 / 视觉，全部通过步骤与 <see cref="FlowContext"/> 协作。
/// </summary>
public sealed class AutoFlowEngine
{
    /// <summary>暂停等待轮询间隔（ms）。暂停是短时状态，50ms 轮询足够。</summary>
    private const int PausePollMs = 50;

    private readonly IReadOnlyList<FlowStep> _steps;
    private readonly ILogger<AutoFlowEngine> _log;
    private readonly object _sync = new();

    private int _currentIndex;
    private bool _paused;
    private CancellationTokenSource _pauseCts = new();

    /// <summary>构造执行器。</summary>
    /// <param name="steps">按执行顺序排列的步骤列表（步骤 1 在前）。</param>
    /// <param name="log">日志。</param>
    public AutoFlowEngine(IReadOnlyList<FlowStep> steps, ILogger<AutoFlowEngine> log)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            throw new ArgumentException("流程步骤列表不能为空", nameof(steps));
        }

        _steps = steps;
        _log = log;
    }

    /// <summary>是否处于单步模式（为 <see langword="true"/> 时每次启动只走一步，见 05 第 4.3 节）。</summary>
    public bool SingleStep { get; set; }

    /// <summary>每执行完一个步骤后触发（单步模式下协调器据此转 Paused）。在流程线程上同步触发。</summary>
    public event EventHandler? StepCompleted;

    /// <summary>当前步骤序号（从 0 开始，即 <see cref="Steps"/> 下标）。</summary>
    public int CurrentIndex
    {
        get { lock (_sync) { return _currentIndex; } }
    }

    /// <summary>当前步骤（供主画面高亮）。</summary>
    public FlowStep CurrentStep => _steps[CurrentIndex];

    /// <summary>步骤总数。</summary>
    public int TotalSteps => _steps.Count;

    /// <summary>步骤列表（只读）。</summary>
    public IReadOnlyList<FlowStep> Steps => _steps;

    /// <summary>暂停：打断当前步骤并置暂停标记（当前步骤恢复后重走）。</summary>
    public void Pause()
    {
        lock (_sync)
        {
            if (_paused)
            {
                return;
            }

            _paused = true;
            _pauseCts.Cancel();
        }
    }

    /// <summary>继续：清除暂停标记并重建暂停令牌（下次步骤使用新令牌）。</summary>
    public void Resume()
    {
        lock (_sync)
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
            _pauseCts.Dispose();
            _pauseCts = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// 运行流程。连续生产模式下会一直循环（直到被取消或抛出 <see cref="FlowException"/>）；
    /// 单步模式下执行完一步即返回。
    /// </summary>
    /// <param name="ctx">流程上下文。</param>
    /// <param name="ct">整体取消令牌（停止 / 急停 / 应用退出）。</param>
    public async Task RunAsync(FlowContext ctx, CancellationToken ct)
    {
        _currentIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            await WaitResumeAsync(ct);
            ct.ThrowIfCancellationRequested();

            var step = _steps[_currentIndex];
            _log.LogDebug("执行步骤 {Index}/{Total} {Name}", _currentIndex + 1, _steps.Count, step.Name);

            using var stepCts = CreateStepCts(ct);
            try
            {
                await ExecuteWithRetryAsync(step, ctx, stepCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 被暂停打断：不推进索引，回到循环顶部等待恢复后重走当前步骤（05 第 5.1 节「从当前步骤恢复」）
                _log.LogInformation("步骤 {Step} 被暂停打断，等待恢复", step.Name);
                continue;
            }

            _currentIndex = NextIndex(_currentIndex);
            StepCompleted?.Invoke(this, EventArgs.Empty);

            if (SingleStep)
            {
                // 单步：走完一步自动暂停，等待下一次启动（Resume）继续下一步
                _log.LogInformation("单步模式：已执行完一步 {Step}，自动暂停", step.Name);
                Pause();
            }
        }
    }

    /// <summary>等待暂停解除（<see cref="Resume"/> 或整体取消）。</summary>
    private async Task WaitResumeAsync(CancellationToken ct)
    {
        while (true)
        {
            bool paused;
            lock (_sync)
            {
                paused = _paused;
            }

            if (!paused)
            {
                return;
            }

            await Task.Delay(PausePollMs, ct);
        }
    }

    /// <summary>创建当前步骤的取消令牌：整体令牌 + 暂停令牌双源链接。</summary>
    private CancellationTokenSource CreateStepCts(CancellationToken ct)
    {
        lock (_sync)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(ct, _pauseCts.Token);
        }
    }

    /// <summary>带重试与超时执行单步（05 第 4.3 节）。</summary>
    private async Task ExecuteWithRetryAsync(FlowStep step, FlowContext ctx, CancellationToken stepCt)
    {
        for (var attempt = 0; attempt <= step.RetryCount; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stepCt);
            timeoutCts.CancelAfter(step.TimeoutMs);

            try
            {
                await step.ExecuteAsync(ctx, timeoutCts.Token);
                return;
            }
            catch (OperationCanceledException) when (!stepCt.IsCancellationRequested)
            {
                // 仅超时（步骤令牌未被暂停/停止取消）
                _log.LogWarning("步骤 {Step} 超时（第 {N} 次）", step.Name, attempt + 1);
            }
            catch (FlowException ex)
            {
                _log.LogWarning(ex, "步骤 {Step} 失败（第 {N} 次）", step.Name, attempt + 1);
            }
        }

        throw new FlowException($"步骤 [{step.Name}] 执行失败");
    }

    /// <summary>推进到下一步：走完最后一步后回到第 2 步（WaitMaterial），即 05 第 4.2 节「回到步骤 2」。</summary>
    private int NextIndex(int current)
    {
        var next = current + 1;
        return next >= _steps.Count ? 1 : next;
    }
}
