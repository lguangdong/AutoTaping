// 职责：系统设置页 ViewModel——版本信息、用户管理（增删改 / 改密 / 禁用）、一键备份、数据维护、自检、退出程序
// 层：App/ViewModels
// 依赖：IAuthService/IBackupService/IMaintenanceService/ISelfCheckService/IOperationLogRepository/ICalibrationRepository/
//       IIoService/IAutoFlowCoordinator/IClock/IUserNotifier（Core/Abstractions）、UserManagementService（Core/Services）、
//       User/UserOperationResult/BackupResult/MaintenanceResult/SelfCheckReport/DeviceState（Core/Models、StateMachine）、
//       UserManagementService、IUserEditor/IAppController（App/Infrastructure）、DatabaseInitializer（Data）、
//       IConfiguration、CommunityToolkit.Mvvm

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using AutoTaping.App.Infrastructure;
using AutoTaping.App.Navigation;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using AutoTaping.Core.StateMachine;
using AutoTaping.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 系统设置页 ViewModel（08-页面清单 页面 8，仅工程师可进）。四个分区：
/// 版本信息、用户管理、数据维护（备份 / 清理 / 自检 / 退出）、最近操作日志。
/// <para>
/// 线程约定：数据库与文件操作全部 <c>await Task.Run(...)</c>，界面属性只在 UI 线程赋值（01-架构设计 第 3 节）；
/// 权限在命令内再校验一次（00-项目总览 第 5 节：防止绕过界面置灰）。
/// </para>
/// </summary>
public sealed partial class SystemViewModel : PageViewModel
{
    /// <summary>最近操作日志的显示条数（08-页面清单 页面 8：最近 50 条）。</summary>
    private const int RecentLogCount = 50;

    /// <summary>最近操作日志的查询天数。</summary>
    private const int RecentLogDays = 7;

    /// <summary>1 MB 的字节数（数据库大小显示）。</summary>
    private const double BytesPerMb = 1024.0 * 1024.0;

    /// <summary>1 KB 的字节数（备份文件较小时用 KB 显示）。</summary>
    private const double BytesPerKb = 1024.0;

    /// <summary>没有生效标定时的显示文本。</summary>
    private const string NoCalibrationText = "无生效标定（请到视觉标定页完成九点标定）";

    /// <summary>数据库不可用时的显示文本。</summary>
    private const string DatabaseUnavailableText = "数据库不可用";

    private readonly IAuthService _auth;
    private readonly UserManagementService _users;
    private readonly IBackupService _backup;
    private readonly IMaintenanceService _maintenance;
    private readonly ISelfCheckService _selfCheck;
    private readonly IOperationLogRepository _operationLog;
    private readonly ICalibrationRepository _calibrations;
    private readonly IIoService _io;
    private readonly IAutoFlowCoordinator _coordinator;
    private readonly IClock _clock;
    private readonly IUserNotifier _notifier;
    private readonly IUserEditor _userEditor;
    private readonly IAppController _appController;
    private readonly DatabaseInitializer _databaseInitializer;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SystemViewModel> _log;
    private readonly DateTime _startedAt;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>软件版本号（取自程序集版本，与主窗口标题一致）。</summary>
    [ObservableProperty]
    private string _softwareVersion = "";

    /// <summary>硬件模式：模拟（Virtual）或真实硬件。</summary>
    [ObservableProperty]
    private string _hardwareModeText = "";

    /// <summary>数据库大小与路径。</summary>
    [ObservableProperty]
    private string _databaseText = "";

    /// <summary>最后标定的时间与残差。</summary>
    [ObservableProperty]
    private string _lastCalibrationText = "";

    /// <summary>本次运行时长。</summary>
    [ObservableProperty]
    private string _uptimeText = "";

    /// <summary>备份输出目录。</summary>
    [ObservableProperty]
    private string _backupDirectoryText = "";

    /// <summary>最近一次自检结果摘要。</summary>
    [ObservableProperty]
    private string _selfCheckText = "尚未自检";

    /// <summary>页面底部状态提示。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>当前是否正在执行耗时操作（备份 / 维护 / 自检），用于防止重复点击。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>用户列表中选中的账户（改角色 / 改密 / 禁用 / 删除的操作对象）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleEnabledText))]
    [NotifyCanExecuteChangedFor(nameof(ChangeRoleCommand), nameof(ResetPasswordCommand),
                                nameof(ToggleEnabledCommand), nameof(DeleteUserCommand))]
    private User? _selectedUser;

    /// <summary>构造系统设置页 ViewModel。</summary>
    /// <param name="auth">认证与权限服务。</param>
    /// <param name="users">用户管理服务（业务规则 + 审计）。</param>
    /// <param name="backup">一键备份服务。</param>
    /// <param name="maintenance">每日维护服务。</param>
    /// <param name="selfCheck">系统自检服务。</param>
    /// <param name="operationLog">操作日志仓储（最近操作记录）。</param>
    /// <param name="calibrations">标定仓储（最后标定时间）。</param>
    /// <param name="io">IO 服务（退出前关真空 / 吹气 / 压合）。</param>
    /// <param name="coordinator">自动流程协调器（退出前检查是否在运行）。</param>
    /// <param name="clock">时钟。</param>
    /// <param name="notifier">用户提示通道。</param>
    /// <param name="userEditor">用户编辑对话框。</param>
    /// <param name="appController">应用控制（退出程序）。</param>
    /// <param name="databaseInitializer">数据库初始化器（提供数据库路径）。</param>
    /// <param name="configuration">配置（硬件模式）。</param>
    /// <param name="log">日志。</param>
    public SystemViewModel(
        IAuthService auth,
        UserManagementService users,
        IBackupService backup,
        IMaintenanceService maintenance,
        ISelfCheckService selfCheck,
        IOperationLogRepository operationLog,
        ICalibrationRepository calibrations,
        IIoService io,
        IAutoFlowCoordinator coordinator,
        IClock clock,
        IUserNotifier notifier,
        IUserEditor userEditor,
        IAppController appController,
        DatabaseInitializer databaseInitializer,
        IConfiguration configuration,
        ILogger<SystemViewModel> log)
    {
        _auth = auth;
        _users = users;
        _backup = backup;
        _maintenance = maintenance;
        _selfCheck = selfCheck;
        _operationLog = operationLog;
        _calibrations = calibrations;
        _io = io;
        _coordinator = coordinator;
        _clock = clock;
        _notifier = notifier;
        _userEditor = userEditor;
        _appController = appController;
        _databaseInitializer = databaseInitializer;
        _configuration = configuration;
        _log = log;
        _startedAt = clock.Now;
    }

    /// <inheritdoc />
    public override PageKey Key => PageKey.System;

    /// <inheritdoc />
    public override string Title => "系统设置";

    /// <inheritdoc />
    public override string Icon => "⚙";

    /// <inheritdoc />
    public override Permission RequiredPermission => Permission.SystemSettings;

    /// <summary>用户列表（含禁用账户）。</summary>
    public ObservableCollection<User> Users { get; } = new();

    /// <summary>最近操作日志。</summary>
    public ObservableCollection<OperationLog> RecentLogs { get; } = new();

    /// <summary>启用 / 禁用按钮文字（随选中账户的当前状态变化）。</summary>
    public string ToggleEnabledText
        => SelectedUser is null || SelectedUser.IsEnabled ? "禁 用" : "启 用";

    /// <summary>进入本页：加载版本信息、用户列表与最近操作日志。</summary>
    /// <param name="ct">取消令牌。</param>
    public override async Task<bool> OnEnteringAsync(CancellationToken ct)
    {
        await RefreshAllAsync(ct);
        return true;
    }

    /// <summary>刷新用户列表、操作日志与版本信息。</summary>
    [RelayCommand]
    private async Task RefreshAsync() => await RefreshAllAsync(_lifetime.Token);

    // ==================== 用户管理 ====================

    /// <summary>新增用户。</summary>
    [RelayCommand]
    private async Task CreateUserAsync()
    {
        if (!EnsureEngineer() || IsBusy)
        {
            return;
        }

        var input = _userEditor.Show("新增用户", string.Empty,
                                     userNameEditable: true, roleEditable: true,
                                     passwordRequired: true, role: UserRole.Operator);
        if (input is null)
        {
            return;
        }

        var operatorName = GetOperatorName();
        await ExecuteUserOperationAsync(() => _users.Create(input.UserName, input.Password, input.Role, operatorName));
    }

    /// <summary>修改选中用户的角色（对话框中同时可重置口令，留空表示不改口令）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnSelectedUser))]
    private async Task ChangeRoleAsync()
    {
        if (!EnsureEngineer() || IsBusy || SelectedUser is not { } user)
        {
            return;
        }

        var input = _userEditor.Show($"修改角色 - {user.UserName}", user.UserName,
                                     userNameEditable: false, roleEditable: true,
                                     passwordRequired: false, role: user.Role);
        if (input is null)
        {
            return;
        }

        var operatorName = GetOperatorName();
        await ExecuteUserOperationAsync(() =>
        {
            var roleResult = _users.ChangeRole(user.Id, input.Role, operatorName);
            if (!roleResult.Succeeded || input.Password.Length == 0)
            {
                return roleResult;
            }

            var passwordResult = _users.ResetPassword(user.Id, input.Password, operatorName);
            return passwordResult.Succeeded
                ? UserOperationResult.Success($"{roleResult.Message}{passwordResult.Message}")
                : passwordResult;
        });
    }

    /// <summary>重置选中用户的口令。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnSelectedUser))]
    private async Task ResetPasswordAsync()
    {
        if (!EnsureEngineer() || IsBusy || SelectedUser is not { } user)
        {
            return;
        }

        var input = _userEditor.Show($"重置口令 - {user.UserName}", user.UserName,
                                     userNameEditable: false, roleEditable: false,
                                     passwordRequired: true, role: user.Role);
        if (input is null)
        {
            return;
        }

        var operatorName = GetOperatorName();
        await ExecuteUserOperationAsync(() => _users.ResetPassword(user.Id, input.Password, operatorName));
    }

    /// <summary>启用 / 禁用选中用户。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnSelectedUser))]
    private async Task ToggleEnabledAsync()
    {
        if (!EnsureEngineer() || IsBusy || SelectedUser is not { } user)
        {
            return;
        }

        var enable = !user.IsEnabled;
        var action = enable ? "启用" : "禁用";
        if (!_notifier.Confirm($"{action}用户",
                               $"即将{action}用户「{user.UserName}」。\n禁用后该账户无法登录，正在使用该账户的操作会被强制退出登录。\n确认继续？"))
        {
            return;
        }

        var operatorName = GetOperatorName();
        await ExecuteUserOperationAsync(() => _users.SetEnabled(user.Id, enable, operatorName));
    }

    /// <summary>删除选中用户（历史操作日志与产出记录保留，只删除账户本身）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateOnSelectedUser))]
    private async Task DeleteUserAsync()
    {
        if (!EnsureEngineer() || IsBusy || SelectedUser is not { } user)
        {
            return;
        }

        if (!_notifier.Confirm("删除用户",
                               $"即将删除用户「{user.UserName}」（{user.Role}）。\n产出记录与操作日志中的历史记录会保留。\n此操作不可撤销，确认继续？"))
        {
            return;
        }

        var operatorName = GetOperatorName();
        await ExecuteUserOperationAsync(() => _users.Delete(user.Id, operatorName));
    }

    // ==================== 数据维护 ====================

    /// <summary>一键备份：数据库 + 配方 + 模板 + 配置文件。</summary>
    [RelayCommand]
    private async Task BackupAsync()
    {
        if (!EnsureEngineer() || IsBusy)
        {
            return;
        }

        if (!_notifier.Confirm("一键备份",
                               $"即将把数据库、配方、模板与配置文件打包到：\n{_backup.BackupDirectory}\n" +
                               "文件较大时可能需要几十秒，期间请勿关闭软件。确认开始？"))
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在备份…";

        try
        {
            var result = await Task.Run(() => _backup.CreateBackup()).ConfigureAwait(true);
            if (result.Succeeded)
            {
                StatusText = $"已备份到 {result.FilePath}（{FormatSize(result.SizeBytes)}）";
                _notifier.Info($"备份完成：{result.FilePath}\n大小 {FormatSize(result.SizeBytes)}");
            }
            else
            {
                StatusText = result.Message;
                _notifier.Error(result.Message);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "一键备份失败");
            StatusText = "备份失败，请查看日志";
            _notifier.Error($"备份失败：{ex.GetBaseException().Message}。请检查 data 目录权限与磁盘空间。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>立即清理旧数据（先归档 CSV，再删除；需二次确认）。</summary>
    [RelayCommand]
    private async Task CleanupAsync()
    {
        if (!EnsureEngineer() || IsBusy)
        {
            return;
        }

        if (!_notifier.Confirm("清理旧数据",
                               "即将把超过保留期的产出记录、报警记录与操作日志归档为 CSV 后删除，" +
                               "并裁剪仅保留最近 20 条标定记录（含标定原图）。\n此操作不可撤销，确认继续？"))
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在清理旧数据…";

        try
        {
            var result = await Task.Run(() => _maintenance.RunNow()).ConfigureAwait(true);
            StatusText = $"维护完成（{result.RunAt:yyyy-MM-dd HH:mm:ss}）：归档 {result.ArchivedTotalCount} 条，"
                         + $"删除标定 {result.DeletedCalibrationCount} 条、日志文件 {result.DeletedLogFileCount} 个";
            _notifier.Info(StatusText);
            _log.LogInformation("手动清理旧数据完成：归档 {Total} 条", result.ArchivedTotalCount);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "手动清理旧数据失败");
            StatusText = "清理失败，请查看日志";
            _notifier.Error($"清理旧数据失败：{ex.GetBaseException().Message}。请检查数据库与磁盘状态。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>立即执行一次系统自检（磁盘 / 数据库 / 硬件）。</summary>
    [RelayCommand]
    private async Task SelfCheckAsync()
    {
        if (!EnsureEngineer() || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var report = await Task.Run(() => _selfCheck.Run()).ConfigureAwait(true);
            SelfCheckText = FormatSelfCheck(report);
            StatusText = SelfCheckText;

            if (report.AllOk)
            {
                _notifier.Info($"自检通过：{SelfCheckText}");
            }
            else
            {
                _notifier.Warn($"自检发现异常：{SelfCheckText}");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "系统自检失败");
            SelfCheckText = "自检失败，请查看日志";
            _notifier.Error("自检失败，请查看日志。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>退出程序：运行中拒绝 → 二次确认 → 关真空/吹气/压合 → 请求退出。</summary>
    [RelayCommand]
    private void ExitApplication()
    {
        if (!EnsureEngineer())
        {
            return;
        }

        if (_coordinator.State is DeviceState.Running or DeviceState.Paused)
        {
            _notifier.Warn("设备正在运行，请先停止自动流程后再退出程序。");
            return;
        }

        if (!_notifier.Confirm("退出程序",
                               "即将关闭软件。请确认：设备已停止、工件已取下、急停开关在可及范围内。\n" +
                               "退出后需要重新登录并回零才能继续生产。确认退出？"))
        {
            return;
        }

        try
        {
            // 08-页面清单 页面 8：退出前关真空 / 吹气 / 压合，避免吸嘴带料或气缸压着工件
            _io.WriteDo(IoPointNames.VacuumValve, false);
            _io.WriteDo(IoPointNames.BlowValve, false);
            _io.WriteDo(IoPointNames.PressCylinder, false);
        }
        catch (Exception ex)
        {
            // 硬件异常不得阻止退出（01-架构设计 第 6 节），但必须留痕
            _log.LogError(ex, "退出前关闭气动输出失败");
        }

        _log.LogInformation("用户 {User} 请求退出程序", GetOperatorName());
        _appController.RequestShutdown();
    }

    // ==================== 私有实现 ====================

    /// <summary>页面级权限二次校验（00-项目总览 第 5 节：防止绕过界面置灰）。</summary>
    private bool EnsureEngineer()
    {
        if (_auth.HasPermission(Permission.SystemSettings))
        {
            return true;
        }

        _log.LogWarning("越权尝试访问系统设置功能：用户 {User}", _auth.CurrentUser?.UserName);
        _notifier.Warn("系统设置需要工程师权限，请切换到工程师账户后再试。");
        return false;
    }

    /// <summary>执行一次用户管理操作：后台执行 → 提示结果 → 刷新列表。</summary>
    private async Task ExecuteUserOperationAsync(Func<UserOperationResult> operation)
    {
        IsBusy = true;

        try
        {
            var result = await Task.Run(operation).ConfigureAwait(true);
            StatusText = result.Message;

            if (result.Succeeded)
            {
                _notifier.Info(result.Message);
            }
            else
            {
                _notifier.Warn(result.Message);
            }

            await RefreshAllAsync(_lifetime.Token);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "用户管理操作失败");
            _notifier.Error("用户管理操作失败，请检查数据库后重试。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>后台读取页面数据并写回界面（读库失败只提示，不让页面崩溃）。</summary>
    private async Task RefreshAllAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await Task.Run(() => LoadSnapshot(ct), ct).ConfigureAwait(true);
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
            _log.LogDebug("系统设置页刷新已取消");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "读取系统设置数据失败");
            _notifier.Error($"读取系统信息失败：{ex.GetBaseException().Message}。请检查数据库后重试。");
        }
    }

    /// <summary>读取一次页面数据（后台线程，不得触碰绑定属性）。</summary>
    private SystemSnapshot LoadSnapshot(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = _clock.Now;
        var users = _users.GetAll();
        var logs = _operationLog.Query(now.AddDays(-RecentLogDays), now);
        var calibration = _calibrations.GetActive();
        var databaseBytes = TryGetFileSize(_databaseInitializer.DatabasePath);

        return new SystemSnapshot(
            users,
            logs.Take(RecentLogCount).ToList(),
            calibration,
            databaseBytes,
            now);
    }

    /// <summary>把快照写入绑定属性（必须在 UI 线程调用）。</summary>
    private void ApplySnapshot(SystemSnapshot snapshot)
    {
        ReplaceUsers(snapshot.Users);
        ReplaceLogs(snapshot.RecentLogs);

        SoftwareVersion = typeof(SystemViewModel).Assembly.GetName().Version?.ToString(3) ?? "未知";
        HardwareModeText = IsVirtualMode() ? "模拟（Virtual）" : "真实硬件";
        DatabaseText = snapshot.DatabaseBytes < 0
            ? DatabaseUnavailableText
            : $"{FormatSize(snapshot.DatabaseBytes)}（{_databaseInitializer.DatabasePath}）";
        LastCalibrationText = FormatCalibration(snapshot.Calibration);
        UptimeText = FormatDuration(snapshot.Now - _startedAt);
        BackupDirectoryText = _backup.BackupDirectory;
    }

    private void ReplaceUsers(IReadOnlyList<User> users)
    {
        var selectedId = SelectedUser?.Id;

        Users.Clear();
        foreach (var user in users)
        {
            Users.Add(user);
        }

        // 刷新后尽量保持原选中行，避免操作完跳回第一行
        SelectedUser = selectedId is null ? null : Users.FirstOrDefault(user => user.Id == selectedId);
    }

    private void ReplaceLogs(IReadOnlyList<OperationLog> logs)
    {
        RecentLogs.Clear();
        foreach (var log in logs)
        {
            RecentLogs.Add(log);
        }
    }

    private bool CanOperateOnSelectedUser() => SelectedUser is not null;

    private string GetOperatorName() => _auth.CurrentUser?.UserName ?? string.Empty;

    /// <summary>是否模拟模式（Hardware:UseVirtual）。</summary>
    private bool IsVirtualMode()
        => bool.TryParse(_configuration["Hardware:UseVirtual"], out var useVirtual) && useVirtual;

    /// <summary>取文件大小（字节）；文件不存在返回 0，读取失败返回 -1。</summary>
    private long TryGetFileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "读取数据库文件大小失败：{Path}", path);
            return -1;
        }
    }

    private static string FormatCalibration(Calibration? calibration)
    {
        if (calibration is null)
        {
            return NoCalibrationText;
        }

        return $"{calibration.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}"
               + $"　RMS {calibration.RmsError.ToString("F3", CultureInfo.InvariantCulture)} mm"
               + $"（{calibration.PointCount} 点）";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return "--";
        }

        return bytes < BytesPerMb
            ? $"{bytes / BytesPerKb:F1} KB"
            : $"{bytes / BytesPerMb:F1} MB";
    }

    private static string FormatSelfCheck(SelfCheckReport report)
    {
        var disk = report.FreeDiskGb < 0
            ? "无法读取"
            : $"{report.FreeDiskGb.ToString("F1", CultureInfo.InvariantCulture)} GB";

        return $"磁盘 {(report.DiskSpaceOk ? "正常" : "不足")}（剩余 {disk}）"
               + $"｜数据库 {(report.DatabaseWritable ? "可写" : "不可写")}"
               + $"｜运控卡 {(report.MotionConnected ? "已连接" : "未连接")}"
               + $"｜相机 {(report.CameraConnected ? "已连接" : "未连接")}"
               + $"｜气压 {(report.AirPressureOk ? "正常" : "不足")}"
               + $"（{report.CheckedAt:HH:mm:ss}）";
    }

    /// <summary>运行时长格式化为 HH:mm:ss（超过 24 小时按总小时数显示）。</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>页面一次刷新读到的数据（后台线程产出，UI 线程消费）。</summary>
    private sealed record SystemSnapshot(
        IReadOnlyList<User> Users,
        IReadOnlyList<OperationLog> RecentLogs,
        Calibration? Calibration,
        long DatabaseBytes,
        DateTime Now);
}
