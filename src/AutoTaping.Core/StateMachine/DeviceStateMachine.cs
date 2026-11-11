// 职责：设备状态机——迁移表 + 守卫 + 模式切换，唯一权威实现（05-核心状态机 第 2/3 节）
// 层：Core/StateMachine
// 依赖：ILogger；无 UI、无硬件

using Microsoft.Extensions.Logging;

namespace AutoTaping.Core.StateMachine;

/// <summary>
/// 设备状态机。状态迁移表与守卫条件严格照 <c>docs/05-核心状态机与流程.md</c> 第 2 节实现，
/// 一切状态变更必须走 <see cref="TryTransition"/>，不得直接赋值 <see cref="State"/>（05 第 8 节约束 1）。
/// <para>
/// 守卫由协调器在启动时通过 <see cref="RegisterGuard"/> 注入，使本类保持纯逻辑、可单元测试。
/// 急停（任意状态 → <see cref="DeviceState.EStopped"/>）为特殊迁移，单独处理。
/// </para>
/// </summary>
public sealed class DeviceStateMachine
{
    /// <summary>合法迁移集合（不含"任意 → EStopped"，该迁移单独判定）。</summary>
    private static readonly HashSet<(DeviceState From, DeviceState To)> ValidTransitions = new()
    {
        (DeviceState.Initializing, DeviceState.Ready),
        (DeviceState.Initializing, DeviceState.NotReady),
        (DeviceState.Initializing, DeviceState.Alarming),
        (DeviceState.NotReady, DeviceState.Ready),
        (DeviceState.NotReady, DeviceState.Alarming),
        (DeviceState.Ready, DeviceState.Running),
        (DeviceState.Running, DeviceState.Paused),
        (DeviceState.Paused, DeviceState.Running),
        (DeviceState.Running, DeviceState.Ready),
        (DeviceState.Paused, DeviceState.Ready),
        (DeviceState.Running, DeviceState.Alarming),
        (DeviceState.Paused, DeviceState.Alarming),
        (DeviceState.Alarming, DeviceState.Ready),
        (DeviceState.Ready, DeviceState.NotReady),
        (DeviceState.EStopped, DeviceState.NotReady),
    };

    private readonly object _sync = new();
    private readonly Dictionary<(DeviceState From, DeviceState To), Func<bool>> _guards = new();
    private readonly ILogger<DeviceStateMachine> _log;

    /// <summary>当前设备状态（只读，通过 <see cref="TryTransition"/> 变更）。</summary>
    public DeviceState State { get; private set; } = DeviceState.Initializing;

    /// <summary>当前操作模式（只读，通过 <see cref="TrySwitchMode"/> 变更）。</summary>
    public OperationMode Mode { get; private set; } = OperationMode.Auto;

    /// <summary>状态迁移成功后触发（在锁内同步触发，订阅方不得做耗时操作）。</summary>
    public event EventHandler<StateChangedEventArgs>? StateChanged;

    /// <summary>创建状态机。</summary>
    /// <param name="log">日志。</param>
    public DeviceStateMachine(ILogger<DeviceStateMachine> log)
    {
        _log = log;
    }

    /// <summary>注册某条迁移的守卫；重复注册覆盖。守卫返回 <see langword="false"/> 时迁移被拒绝。</summary>
    /// <param name="from">起始状态。</param>
    /// <param name="to">目标状态。</param>
    /// <param name="guard">守卫条件（全部满足才允许迁移）。</param>
    public void RegisterGuard(DeviceState from, DeviceState to, Func<bool> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);

        lock (_sync)
        {
            _guards[(from, to)] = guard;
        }
    }

    /// <summary>
    /// 尝试迁移到目标状态。非法迁移或守卫不通过时返回 <see langword="false"/> 且状态不变，
    /// 不得静默失败（05 第 2 节：记录并提示）。
    /// </summary>
    /// <param name="target">目标状态。</param>
    public bool TryTransition(DeviceState target)
    {
        lock (_sync)
        {
            var from = State;

            if (target == DeviceState.EStopped)
            {
                // 任意状态（除 Exiting 与 EStopped 自身）都可进入急停（05 第 2 节）
                if (from is DeviceState.Exiting or DeviceState.EStopped)
                {
                    _log.LogWarning("非法状态迁移 {From} -> {To}，已拒绝", from, target);
                    return false;
                }
            }
            else if (!ValidTransitions.Contains((from, target)))
            {
                _log.LogWarning("非法状态迁移 {From} -> {To}，已拒绝", from, target);
                return false;
            }

            if (_guards.TryGetValue((from, target), out var guard) && !guard())
            {
                _log.LogWarning("状态迁移 {From} -> {To} 守卫不通过，已拒绝", from, target);
                return false;
            }

            State = target;
            _log.LogInformation("状态 {From} -> {To}", from, target);
            StateChanged?.Invoke(this, new StateChangedEventArgs(from, target));
            return true;
        }
    }

    /// <summary>切换操作模式。运行 / 暂停中禁止切换（05 第 1.1 节）。</summary>
    /// <param name="mode">目标模式。</param>
    public bool TrySwitchMode(OperationMode mode)
    {
        lock (_sync)
        {
            if (State is DeviceState.Running or DeviceState.Paused)
            {
                _log.LogWarning("状态 {State} 下禁止切换模式，已拒绝", State);
                return false;
            }

            if (Mode == mode)
            {
                return true;
            }

            Mode = mode;
            _log.LogInformation("操作模式 -> {Mode}", mode);
            return true;
        }
    }
}
