// 职责：全局无操作监测——超过阈值未收到任何输入时触发一次 IdleTimeout（用于自动锁屏）
// 层：App/Infrastructure
// 依赖：System.Windows.Input.InputManager、DispatcherTimer

using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.Infrastructure;

/// <summary>
/// 空闲监测器。阈值来自 <c>appsettings.json</c> 的 <c>Security:AutoLockMinutes</c>
/// （08-页面清单 全局交互约定：无操作 5 分钟自动锁定，运行中不锁）。
/// 只在 UI 线程使用：<see cref="IdleTimeout"/> 与调用 <see cref="Reset"/> 的界面操作都在同一线程。
/// </summary>
public sealed class IdleMonitor : IDisposable
{
    /// <summary>
    /// 轮询间隔。无操作判定只做一次减法，开销极小；取 1s 是为了让「5 分钟」的误差不超过 1 秒，
    /// 现场操作工对锁屏时机敏感。属于纯实现常量，不是工艺或硬件参数。
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _threshold;
    private readonly ILogger<IdleMonitor> _log;
    private readonly DispatcherTimer _timer;
    private readonly object _sync = new();

    /// <summary>最近一次输入时刻（<see cref="Environment.TickCount64"/>，单调时钟，不受改系统时间影响）。</summary>
    private long _lastActivityTick;

    private bool _idleRaised;
    private bool _disposed;

    /// <summary>构造空闲监测器并开始计时。</summary>
    /// <param name="autoLockMinutes">无操作阈值（分钟）；小于等于 0 表示禁用自动锁屏。</param>
    /// <param name="log">日志。</param>
    public IdleMonitor(double autoLockMinutes, ILogger<IdleMonitor> log)
    {
        _log = log;
        _threshold = TimeSpan.FromMinutes(Math.Max(autoLockMinutes, 0));
        _lastActivityTick = Environment.TickCount64;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };

        if (autoLockMinutes <= 0)
        {
            _log.LogWarning("自动锁屏已禁用（Security:AutoLockMinutes={Minutes}），现场存在长时间无人值守风险",
                            autoLockMinutes);
            return;
        }

        InputManager.Current.PreProcessInput += OnPreProcessInput;
        _timer.Tick += OnTimerTick;
        _timer.Start();

        _log.LogInformation("自动锁屏已启用：无操作 {Minutes} 分钟后回到登录页", autoLockMinutes);
    }

    /// <summary>超过无操作阈值时触发一次；用户重新操作或调用 <see cref="Reset"/> 后才会再次触发。</summary>
    public event EventHandler? IdleTimeout;

    /// <summary>重置空闲计时（登录成功、锁屏处理完成后调用）。</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _lastActivityTick = Environment.TickCount64;
            _idleRaised = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 阈值 <= 0 时没有订阅任何事件，也就无需反注册
        if (!_timer.IsEnabled)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        // 任何输入（鼠标 / 键盘 / 触摸）都算"有人操作"；这里只更新时间戳，不做业务判断
        Reset();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        bool shouldRaise;

        lock (_sync)
        {
            if (_idleRaised)
            {
                return;
            }

            shouldRaise = Environment.TickCount64 - _lastActivityTick >= (long)_threshold.TotalMilliseconds;
            if (shouldRaise)
            {
                _idleRaised = true;
            }
        }

        if (!shouldRaise)
        {
            return;
        }

        _log.LogInformation("无操作已达 {Minutes} 分钟，请求自动锁屏", _threshold.TotalMinutes);
        IdleTimeout?.Invoke(this, EventArgs.Empty);
    }
}
