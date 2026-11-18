// 职责：应用启动引导——加载配置 → 初始化 Serilog → 挂全局异常处理 → 构建 DI 容器 → 初始化数据层 → 显示主窗口
// 层：App
// 依赖：Microsoft.Extensions.Configuration / DependencyInjection / Logging、Serilog、AutoTaping.Core.Abstractions、AutoTaping.Data

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AutoTaping.App.Infrastructure;
using AutoTaping.App.Navigation;
using AutoTaping.App.ViewModels;
using AutoTaping.App.Views;
using AutoTaping.App.Views.Dialogs;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using AutoTaping.Data;
using AutoTaping.Data.Repositories;
using AutoTaping.Hardware.Camera;
using AutoTaping.Hardware.Io;
using AutoTaping.Hardware.Motion;
using AutoTaping.Vision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace AutoTaping.App;

/// <summary>
/// 应用程序入口。只负责启动顺序、DI 容器装配与全局异常兜底，不含任何业务逻辑。
/// </summary>
public partial class App : Application
{
    /// <summary>配置文件名（随可执行文件输出）。</summary>
    private const string AppSettingsFileName = "appsettings.json";

    /// <summary>配置缺失时使用的日志目录。</summary>
    private const string DefaultLogDirectory = "logs";

    /// <summary>配置缺失时使用的日志保留天数（10-编码约定 7.3：保留 90 天）。</summary>
    private const int DefaultLogRetainDays = 90;

    /// <summary>按天滚动，最终文件名为 logs/autotaping-yyyyMMdd.log。</summary>
    private const string LogFilePattern = "autotaping-.log";

    /// <summary>界面未捕获异常连续发生该次数后退出进程（01-架构设计 第 6 节）。</summary>
    private const int MaxConsecutiveUiExceptionCount = 3;

    /// <summary>
    /// 两次界面异常间隔超过该毫秒数，即认为「连续」已中断，计数从 1 重新开始。
    /// 取 30s 的理由：现场偶发一次异常（误点、临时资源占用、网络抖动）后通常不会再紧接着复现，
    /// 而同一故障反复出现时两次间隔基本在秒级；这样既能识别"真·连续故障"，又不会把一整天里
    /// 三次互不相关的偶发异常累计成致命错误（01-架构设计 第 6 节要求的是「连续 3 次」）。
    /// </summary>
    private const long UiExceptionContinuousWindowMs = 30_000;

    /// <summary>致命错误退出码。</summary>
    private const int FatalExitCode = 1;

    /// <summary>配置缺失时使用的数据库相对路径（与 appsettings.json 的 Storage:DatabasePath 默认值一致）。</summary>
    private const string DefaultDatabaseRelativePath = "data/autotaping.db";

    /// <summary>配置缺失时使用的自动锁屏时长（分钟，08-页面清单 全局交互约定：5 分钟）。</summary>
    private const double DefaultAutoLockMinutes = 5;

    private ServiceProvider? _services;
    private IUserNotifier? _notifier;
    private PowerLossMonitor? _powerLossMonitor;
    private string? _configurationError;
    private int _consecutiveUiExceptionCount;

    /// <summary>
    /// 上一次界面异常的时刻（<see cref="Environment.TickCount64"/>）。
    /// 用单调时钟而不是 <see cref="DateTime"/>：操作工或对时服务改系统时间不会影响「连续」判定。
    /// 初值 0 时与当前时刻的差值必然大于窗口，因此首次异常计数从 1 开始。
    /// </summary>
    private long _lastUiExceptionTick;

    /// <summary>当前软件版本，形如 1.0.0（取自程序集版本）。</summary>
    private static string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. 配置先读：日志目录与保留天数都来自 appsettings.json
        var configuration = LoadConfiguration();

        // 2. Serilog 必须早于 DI 与主窗口初始化，否则早期异常无日志（11-开发任务分解 S0 常见坑）
        ConfigureLogging(configuration);

        // 3. 全局异常处理要在任何业务代码之前挂上
        RegisterGlobalExceptionHandlers();

        // 4. 构建 DI 容器
        _services = BuildServiceProvider(configuration);
        _notifier = _services.GetRequiredService<IUserNotifier>();

        if (_configurationError is not null)
        {
            // 对应报警 AL504「配置加载失败」，启动阶段先记日志，报警服务在 S7 接入后由报警页呈现
            Log.Warning("配置加载失败，已使用默认值启动：{Reason}", _configurationError);
        }

        Log.Information("应用启动：版本 {Version}，硬件模式 {HardwareMode}",
                        AppVersion, ResolveHardwareMode(configuration));

        // 5. 数据层：建库 + 种子数据 + 断电检测（失败不阻断启动，见 01-架构设计 第 5 节）
        InitializeDataLayer();

        // 5.5 硬件自检与采集线程：运控卡初始化失败只报警不阻断（01-架构设计 第 5 节），
        //     采集线程为手动页 / IO 页提供 200ms 快照（S4）
        InitializeHardware();

        // 5.6 S9：启动自检（磁盘 / 数据库 / 硬件）+ 每日维护任务（归档与清理）
        StartBackgroundServices();

        // 6. 登录：08-页面清单 全局交互约定要求先登录再进入主界面
        if (!ShowLoginDialog())
        {
            Log.Information("用户取消登录，应用退出");
            Shutdown();
            return;
        }

        // 7. 主窗口：登录成功后才创建，保证界面上的权限与用户信息都是有效的
        try
        {
            var mainWindow = _services.GetRequiredService<MainWindow>();
            var shell = _services.GetRequiredService<ShellViewModel>();
            shell.LockRequested += OnLockRequested;

            MainWindow = mainWindow;
            mainWindow.Closed += OnMainWindowClosed;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            // 主窗口都起不来属于致命错误：记日志 + 提示，然后退出（不静默崩溃）
            Log.Fatal(ex, "主窗口创建失败，应用无法继续");
            NotifyError("主窗口创建失败，请联系工程师查看日志。");
            Shutdown(FatalExitCode);
        }
    }

    /// <summary>显示登录框；用户取消返回 <see langword="false"/>。</summary>
    private bool ShowLoginDialog()
    {
        try
        {
            var dialog = _services?.GetRequiredService<LoginDialog>();
            return dialog is not null && dialog.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "登录窗口打开失败");
            NotifyError("登录窗口打开失败，请联系工程师查看日志。");
            return false;
        }
    }

    /// <summary>
    /// 无操作超时后回到登录页（08-页面清单 全局交互约定）：隐藏主窗口 → 弹出登录框 →
    /// 登录成功则恢复主窗口，取消则退出程序（锁屏状态下不允许继续操作设备）。
    /// </summary>
    private void OnLockRequested(object? sender, EventArgs e)
    {
        var mainWindow = MainWindow;
        mainWindow?.Hide();

        if (ShowLoginDialog())
        {
            mainWindow?.Show();
            mainWindow?.Activate();
            return;
        }

        Log.Information("锁屏后用户取消登录，应用退出");
        Shutdown();
    }

    /// <summary>主窗口关闭即退出整个应用（ShutdownMode 已改为 OnExplicitShutdown，必须显式关闭）。</summary>
    private void OnMainWindowClosed(object? sender, EventArgs e) => Shutdown();

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("应用退出：退出码 {ExitCode}", e.ApplicationExitCode);

        MarkCleanShutdown();

        _services?.Dispose();
        _services = null;
        _notifier = null;
        _powerLossMonitor = null;

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// 初始化 SQLite（建表 + 种子数据）并执行断电检测。
    /// 数据层不可用时只记日志 + 提示，不阻断启动：宁可产量暂存内存，也不能让操作工打不开软件。
    /// </summary>
    private void InitializeDataLayer()
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            var initializer = services.GetRequiredService<DatabaseInitializer>();
            initializer.Initialize();

            _powerLossMonitor = services.GetRequiredService<PowerLossMonitor>();

            // 上次非正常退出 → 补记停机原因（POWER_OFF）与 AL503（06 文档第 8 节、09 文档第二节）
            if (_powerLossMonitor.DetectAndMarkRunning())
            {
                var now = services.GetRequiredService<IClock>().Now;
                services.GetRequiredService<IOutputService>()
                        .RecordDowntime(DowntimeReasons.PowerOff, now, endTime: null);
                services.GetRequiredService<IAlarmService>()
                        .Raise("AL503", AlarmLevel.Info, "检测到上次非正常退出，请检查是否断电或强制结束进程", "系统");
                Log.Warning("上次为非常规退出，已补记停机原因 POWER_OFF 与报警 AL503");
            }
        }
        catch (Exception ex)
        {
            // 数据库打不开（磁盘满、权限、文件损坏）属于 01-架构设计 第 6 节「硬件/环境异常」：报警不退出
            Log.Error(ex, "数据层初始化失败，数据库不可用");
            NotifyError("数据库初始化失败，产量与报警可能无法保存，请联系工程师检查 data 目录与磁盘空间。");
        }
    }

    /// <summary>
    /// 硬件自检：真实模式下已由注册工厂完成「探测 + 降级」，这里补报警（AL105/AL201）、开始相机采集并启动采集线程。
    /// 失败只报警不阻断启动（01-架构设计 第 5 节：宁可功能缺失，也不能让操作工打不开软件）。
    /// </summary>
    private void InitializeHardware()
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            var report = services.GetRequiredService<HardwareStartupReport>();

            // 运控卡：注册工厂已探测并降级，这里再调 Initialize 是幂等的（真实实现内部有 _initialized 守卫）
            var motion = services.GetRequiredService<IMotionCard>();
            motion.Initialize();

            if (report.MotionDegraded)
            {
                Log.Error("运控卡初始化失败，已降级为 Virtual 模式");
                services.GetRequiredService<IAlarmService>()
                        .Raise("AL105", AlarmLevel.Critical, "运控卡通讯中断（已降级为模拟模式）", "运动控制");
                NotifyError("运控卡未连接，已降级为模拟模式，请检查 PCI 卡、驱动与供电。");
            }

            // 相机：注册工厂已探测并降级；就绪后开始连续采集（自动流程拍照识别依赖相机已就绪）
            var camera = services.GetRequiredService<ICamera>();
            camera.Initialize();
            if (camera.IsConnected)
            {
                camera.StartGrabbing();
            }

            if (report.CameraDegraded)
            {
                Log.Error("相机初始化失败，已降级为 Virtual 相机");
                services.GetRequiredService<IAlarmService>()
                        .Raise("AL201", AlarmLevel.Critical, "相机通讯中断（已降级为模拟相机）", "视觉");
                NotifyError("相机未连接，已降级为模拟相机，请检查网线、网口与相机供电。");
            }

            services.GetRequiredService<IDeviceMonitor>().Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "硬件初始化异常");
            NotifyError("硬件初始化异常，手动/自动动作可能不可用，请联系工程师查看日志。");
        }
    }

    /// <summary>
    /// 启动 S9 运维服务：先跑一次启动自检（磁盘 / 数据库 / 硬件），再启动周期巡检与每日维护任务。
    /// 任何一步失败都只记日志，不阻断启动（01-架构设计 第 5 节：宁可功能缺失，也不能让操作工打不开软件）。
    /// </summary>
    private void StartBackgroundServices()
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            var selfCheck = services.GetRequiredService<ISelfCheckService>();
            var report = selfCheck.Run();

            if (!report.AllOk)
            {
                // 具体报警（AL501 / AL502）由自检服务负责，这里只留一条便于现场排查的汇总日志
                Log.Warning("启动自检存在异常：磁盘 {DiskOk}（剩余 {FreeGb:F2}GB）、数据库可写 {DbOk}、硬件 {HardwareOk}",
                            report.DiskSpaceOk, report.FreeDiskGb, report.DatabaseWritable, report.HardwareOk);
            }

            selfCheck.Start();

            // 每日维护：先归档 CSV 再删过期数据，并清理过期日志（06 文档第 7 节）
            services.GetRequiredService<IMaintenanceService>().Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "运维服务启动失败（自检 / 每日维护），相关功能降级但软件可继续使用");
        }
    }

    /// <summary>正常退出时写回 <c>LastShutdownClean = 1</c>，否则下次启动会被判为断电。</summary>
    private void MarkCleanShutdown()
    {
        var monitor = _powerLossMonitor;
        if (monitor is null)
        {
            return;
        }

        try
        {
            monitor.MarkCleanShutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "写入正常退出标记失败，下次启动将误报上次非正常退出（AL503）");
        }
    }

    private IConfiguration LoadConfiguration()
    {
        try
        {
            return new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(AppSettingsFileName, optional: false, reloadOnChange: false)
                .Build();
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException)
        {
            // 配置缺失或损坏不得阻断启动（01-架构设计 第 5 节：永不阻断启动）
            _configurationError = ex.Message;
            return new ConfigurationBuilder().Build();
        }
    }

    private static void ConfigureLogging(IConfiguration configuration)
    {
        var logDirectory = configuration["Storage:LogPath"];
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            logDirectory = DefaultLogDirectory;
        }

        var retainDays = int.TryParse(configuration["Storage:RetainDays"], out var days) && days > 0
            ? days
            : DefaultLogRetainDays;

        var logPath = Path.Combine(AppContext.BaseDirectory, logDirectory, LogFilePattern);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            // 同时写 VS「输出 → 调试」窗口：调硬件时不用来回切日志文件。
            // 注意：只有调试器附加（VS 里 F5/Debug 启动）时才看得到；程序独立运行时以文件日志为准。
            .WriteTo.Debug(
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: logPath,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retainDays,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static ServiceProvider BuildServiceProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        services.AddSingleton(configuration);

        // Microsoft.Extensions.Logging 全部转给 Serilog，业务代码只依赖 ILogger<T>
        services.AddLogging(builder => builder
            .ClearProviders()
            .AddSerilog(Log.Logger, dispose: false));

        services.AddSingleton<IUserNotifier, WpfUserNotifier>();
        services.AddSingleton<MainWindow>();

        RegisterDataLayer(services, configuration);
        RegisterHardware(services, configuration);
        RegisterUi(services, configuration);
        RegisterOperations(services, configuration);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 注册 S9 运维服务：用户管理、一键备份、每日维护、系统自检、用户编辑对话框与应用退出控制。
    /// 配置项全部来自 appsettings.json（Maintenance / Backup / SelfCheck 段），缺失时用文档默认值兜底。
    /// </summary>
    private static void RegisterOperations(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ReadMaintenanceOptions(configuration));
        services.AddSingleton(ReadBackupOptions(configuration));
        services.AddSingleton(ReadSelfCheckOptions(configuration));

        services.AddSingleton<UserManagementService>();
        services.AddSingleton<IMaintenanceService, DailyMaintenanceService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<ISelfCheckService, SelfCheckService>();

        services.AddSingleton<IUserEditor, WpfUserEditor>();
        services.AddSingleton<IAppController, WpfAppController>();
    }

    /// <summary>注册数据层：连接工厂、时钟、初始化器、仓储、断电检测（全部单例，仓储无状态）。</summary>
    private static void RegisterDataLayer(IServiceCollection services, IConfiguration configuration)
    {
        var databasePath = ResolveDatabasePath(configuration);

        services.AddSingleton(new SqliteConnectionFactory(databasePath));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<DatabaseInitializer>();

        services.AddSingleton<IRecipeRepository, RecipeRepository>();
        services.AddSingleton<IAlarmRepository, AlarmRepository>();
        services.AddSingleton<IOutputRepository, OutputRepository>();
        services.AddSingleton<IDowntimeRepository, DowntimeRepository>();
        services.AddSingleton<IShiftProductionRepository, ShiftProductionRepository>();
        services.AddSingleton<IOperationLogRepository, OperationLogRepository>();
        services.AddSingleton<ISystemStateRepository, SystemStateRepository>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<ICalibrationRepository, CalibrationRepository>();

        services.AddSingleton<PowerLossMonitor>();
    }

    /// <summary>
    /// 注册硬件层。<c>Hardware:UseVirtual</c> 是总开关，<c>Hardware:MotionCard:Type</c> /
    /// <c>Hardware:Camera:Type</c> 可逐个设备覆盖：
    /// 填 <c>Virtual</c> → 该设备直接走虚拟实现；填 <c>Leadshine</c> / <c>Hikvision</c> / <c>Real</c> → 强制真实；
    /// 留空 → 跟随 <c>Hardware:UseVirtual</c>。
    /// 真实设备初始化失败时在注册工厂内降级为 Virtual，并经 <see cref="HardwareStartupReport"/> 标记，由
    /// <see cref="InitializeHardware"/> 报 AL105 / AL201（01-架构设计 第 5 节：永不阻断启动，只降级 + 报警）。
    /// 典型组合：只接了相机、没接运控卡 →
    /// <c>UseVirtual=false</c> + <c>MotionCard:Type=Virtual</c> + <c>Camera:Type=Hikvision</c>，
    /// 运控卡按配置走虚拟（不算故障，不报 AL105），相机走真实。
    /// </summary>
    private static void RegisterHardware(IServiceCollection services, IConfiguration configuration)
    {
        var useVirtual = bool.TryParse(configuration["Hardware:UseVirtual"], out var value) && value;

        var motionVirtual = ResolveDeviceVirtual(configuration, "Hardware:MotionCard:Type", useVirtual);
        var cameraVirtual = ResolveDeviceVirtual(configuration, "Hardware:Camera:Type", useVirtual);

        var report = new HardwareStartupReport();
        services.AddSingleton(report);

        var (minLimits, maxLimits) = ReadAxisSoftLimits(configuration);

        if (motionVirtual)
        {
            // IMotionCard：软限位来自 appsettings 的 Hardware:MotionCard:Axes
            services.AddSingleton<IMotionCard>(sp =>
            {
                var log = sp.GetRequiredService<ILogger<VirtualMotionCard>>();
                return new VirtualMotionCard(log, minLimits, maxLimits);
            });

            // IIoService：点位来自 Io:DiMap / Io:DoMap，安全回路初值来自 Io:InitialDiStates
            services.AddSingleton<IIoService>(sp =>
            {
                var log = sp.GetRequiredService<ILogger<VirtualIoService>>();
                return new VirtualIoService(ReadIoPointKeys(configuration, "Io:DiMap"),
                                            ReadIoPointKeys(configuration, "Io:DoMap"),
                                            ReadInitialDiStates(configuration),
                                            log);
            });
        }
        else
        {
            // 真实模式（S8）：DLL 非线程安全，运控卡与 IO 服务共用同一把锁（02 文档第 7 节）
            var dllLock = new SemaphoreSlim(1, 1);

            services.AddSingleton<IMotionCard>(sp =>
            {
                var options = ReadMotionCardOptions(configuration);
                var card = new LeadshineMotionCard(options, dllLock, sp.GetRequiredService<ILogger<LeadshineMotionCard>>());
                if (!card.Initialize())
                {
                    // 无卡 / 驱动缺失 / 位数不匹配 → 降级 Virtual（11 文档 S8 验收 1）
                    card.Dispose();
                    report.MotionDegraded = true;
                    Log.Error("运控卡初始化失败，降级为 Virtual 模式");
                    var virtualCard = new VirtualMotionCard(sp.GetRequiredService<ILogger<VirtualMotionCard>>(), minLimits, maxLimits);
                    virtualCard.Initialize();
                    return virtualCard;
                }

                return card;
            });

            services.AddSingleton<IIoService>(sp =>
            {
                // 运控卡已降级时 IO 一并走 Virtual，否则安全回路（X05/X06/X07）全部读不到，设备会误判开门/急停
                if (report.MotionDegraded)
                {
                    var log = sp.GetRequiredService<ILogger<VirtualIoService>>();
                    return new VirtualIoService(ReadIoPointKeys(configuration, "Io:DiMap"),
                                                ReadIoPointKeys(configuration, "Io:DoMap"),
                                                ReadInitialDiStates(configuration),
                                                log);
                }

                var options = ReadMotionCardOptions(configuration);
                return new MotionCardIoService(options.CardNo,
                                               ReadIoPointKeys(configuration, "Io:DiMap"),
                                               ReadIoPointKeys(configuration, "Io:DoMap"),
                                               dllLock,
                                               sp.GetRequiredService<ILogger<MotionCardIoService>>());
            });
        }

        if (cameraVirtual)
        {
            services.AddSingleton<ICamera>(sp => CreateVirtualCamera(
                configuration,
                sp.GetRequiredService<ILogger<VirtualCamera>>()));
        }
        else
        {
            services.AddSingleton<ICamera>(sp =>
            {
                var options = ReadHikvisionCameraOptions(configuration);
                var camera = new HikvisionCamera(options, sp.GetRequiredService<ILogger<HikvisionCamera>>());
                if (!camera.Initialize())
                {
                    camera.Dispose();
                    report.CameraDegraded = true;
                    Log.Error("相机初始化失败，降级为 Virtual 相机");
                    var virtualCamera = CreateVirtualCamera(
                        configuration, sp.GetRequiredService<ILogger<VirtualCamera>>());
                    virtualCamera.Initialize();
                    return virtualCamera;
                }

                return camera;
            });
        }

        // 采集线程：200ms 读一次轴与 IO，产出 DeviceSnapshot 供手动页 / IO 页消费（S4）
        services.AddSingleton<IDeviceMonitor, DeviceMonitor>();
    }

    /// <summary>
    /// 解析单台设备是否走 Virtual：<c>Virtual</c> → 虚拟；<c>Leadshine</c> / <c>Hikvision</c> / <c>Real</c> → 真实；
    /// 留空或无法识别 → 跟随全局 <c>Hardware:UseVirtual</c>（无法识别时额外记一条警告，方便发现拼写错误）。
    /// </summary>
    /// <param name="configuration">配置。</param>
    /// <param name="typeKey">设备类型配置键，如 <c>Hardware:Camera:Type</c>。</param>
    /// <param name="useVirtual">全局开关。</param>
    private static bool ResolveDeviceVirtual(IConfiguration configuration, string typeKey, bool useVirtual)
    {
        var type = configuration[typeKey]?.Trim();

        if (string.IsNullOrEmpty(type))
        {
            return useVirtual;
        }

        if (type.Equals("Virtual", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (type.Equals("Real", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Leadshine", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Hikvision", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Log.Warning("设备类型 {DeviceType}（{ConfigKey}）无法识别，按 Hardware:UseVirtual={UseVirtual} 处理",
                    type, typeKey, useVirtual);
        return useVirtual;
    }

    /// <summary>
    /// 注册 UI 层：认证服务、UI 线程调度、自动锁屏监测、8 个页面 ViewModel、主窗口壳与登录框。
    /// 页面 ViewModel 统一登记为 <see cref="PageViewModel"/>，ShellViewModel 通过
    /// <c>IEnumerable&lt;PageViewModel&gt;</c> 拿到全部页面并按 <see cref="PageKey"/> 排序生成导航。
    /// </summary>
    private static void RegisterUi(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();

        // 表现层基础设施：文件选择（配方导入导出 / CSV 导出）与文本输入（另存为），
        // ViewModel 只依赖接口，不直接使用 WPF 对话框
        services.AddSingleton<IFilePicker, WpfFilePicker>();
        services.AddSingleton<IUserPrompt, WpfUserPrompt>();

        // 手动页参数（速度/软限位/安全高度）与 IO 点位中文名：都来自 appsettings，界面不得写死
        services.AddSingleton(ReadManualOptions(configuration));
        services.AddSingleton(ReadIoPointMap(configuration));

        // 主画面四轴位置卡的行程量程（4 轴软限位），同样来自 appsettings
        services.AddSingleton(ReadDiagramOptions(configuration));

        // 生产指标（班次目标产量 / 标准节拍）：生产计划参数，来自 appsettings 的 Production 段
        services.AddSingleton(ReadProductionOptions(configuration));

        // 视觉页参数（标定步长/预览间隔/软限位）与视觉算法服务（无状态，单例）
        services.AddSingleton(ReadVisionOptions(configuration));
        services.AddSingleton<CalibrationService>();
        services.AddSingleton<CorrectionService>();

        // S6 自动流程：视觉服务（流程引擎入口）、报警服务、自动流程协调器（状态机 + 流程 + 急停）
        // S7：报警服务的去重/恢复/确认与产量统计服务（计数、班次、停机分析）
        services.AddSingleton<IVisionService, VisionService>();
        services.AddSingleton<IAlarmService, AlarmService>();
        services.AddSingleton<IOutputService, OutputService>();
        services.AddSingleton<IAutoFlowCoordinator, AutoFlowCoordinator>();

        var autoLockMinutes = ReadAutoLockMinutes(configuration);
        services.AddSingleton(sp => new IdleMonitor(autoLockMinutes, sp.GetRequiredService<ILogger<IdleMonitor>>()));

        services.AddSingleton<PageViewModel, AutoViewModel>();
        services.AddSingleton<PageViewModel, ManualViewModel>();
        services.AddSingleton<PageViewModel, RecipeViewModel>();
        services.AddSingleton<PageViewModel, VisionViewModel>();
        services.AddSingleton<PageViewModel, AlarmViewModel>();
        services.AddSingleton<PageViewModel, OutputViewModel>();
        services.AddSingleton<PageViewModel, IoViewModel>();
        services.AddSingleton<PageViewModel, SystemViewModel>();

        services.AddSingleton<ShellViewModel>();

        // 登录框每次弹出都用新实例：不能残留上一次的输入与错误提示
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LoginDialog>();
    }

    /// <summary>读取自动锁屏分钟数。配置缺失或非法时用默认值并告警，不阻断启动。</summary>
    private static double ReadAutoLockMinutes(IConfiguration configuration)
    {
        var raw = configuration["Security:AutoLockMinutes"];
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes >= 0)
        {
            return minutes;
        }

        Log.Warning("配置 Security:AutoLockMinutes 缺失或非法（{Raw}），使用默认值 {Default} 分钟",
                    raw, DefaultAutoLockMinutes);
        return DefaultAutoLockMinutes;
    }

    /// <summary>从 appsettings 读取 4 轴软限位（缺省用 00 文档示例值兜底），按 AxisId 顺序返回。</summary>
    private static (double[] Min, double[] Max) ReadAxisSoftLimits(IConfiguration configuration)
    {
        var min = new[] { 0.0, 0.0, 0.0, -180.0 };
        var max = new[] { 400.0, 400.0, 60.0, 180.0 };

        var axes = configuration.GetSection("Hardware:MotionCard:Axes").GetChildren();
        foreach (var axis in axes)
        {
            var id = axis["Id"];
            if (!Enum.TryParse<AxisId>(id, ignoreCase: true, out var axisId))
            {
                continue;
            }

            if (double.TryParse(axis["MinLimit"], out var minValue))
            {
                min[(int)axisId] = minValue;
            }

            if (double.TryParse(axis["MaxLimit"], out var maxValue))
            {
                max[(int)axisId] = maxValue;
            }
        }

        return (min, max);
    }

    /// <summary>读取 appsettings 中某个点位映射段的所有键名（如 Io:DiMap 的 X00~X07）。</summary>
    private static IReadOnlyList<string> ReadIoPointKeys(IConfiguration configuration, string sectionPath)
    {
        return configuration.GetSection(sectionPath).GetChildren().Select(c => c.Key).ToList();
    }

    /// <summary>
    /// 读取手动页参数：<c>Manual</c> 段的手动速度范围、<c>Process:SafeHeight</c> 与轴软限位。
    /// 缺失或非法时用 08-页面清单 / 00-项目总览的示例值兜底，不阻断启动。
    /// </summary>
    private static ManualOptions ReadManualOptions(IConfiguration configuration)
    {
        var (min, max) = ReadAxisSoftLimits(configuration);

        return new ManualOptions
        {
            DefaultSpeed = ReadDouble(configuration, "Manual:JogSpeedMmPerSec", 20.0),
            MinSpeed = ReadDouble(configuration, "Manual:MinSpeedMmPerSec", 1.0),
            MaxSpeed = ReadDouble(configuration, "Manual:MaxSpeedMmPerSec", 100.0),
            SafeHeightMm = ReadDouble(configuration, "Process:SafeHeight", 50.0),
            MinLimits = min,
            MaxLimits = max,
        };
    }

    /// <summary>读取生产指标配置（班次目标产量 / 标准节拍）；缺失或非法时用保守默认值。</summary>
    private static ProductionOptions ReadProductionOptions(IConfiguration configuration)
    {
        var target = 500;
        if (int.TryParse(configuration["Production:ShiftTargetCount"], out var parsedTarget))
        {
            target = parsedTarget;
        }

        var standardCycle = 0;
        if (int.TryParse(configuration["Production:StandardCycleMs"], out var parsedCycle))
        {
            standardCycle = parsedCycle;
        }

        return new ProductionOptions
        {
            ShiftTargetCount = target,
            StandardCycleMs = standardCycle,
        };
    }

    /// <summary>读取主画面的量程：直接复用 4 轴软限位读取结果（四轴位置卡的行程条）。</summary>
    private static DiagramOptions ReadDiagramOptions(IConfiguration configuration)
    {
        var (min, max) = ReadAxisSoftLimits(configuration);
        return new DiagramOptions
        {
            MinLimits = min,
            MaxLimits = max,
        };
    }

    /// <summary>读取视觉页参数：<c>Vision</c> 段的步长/预览间隔/取图超时与轴软限位。缺失时用默认值兜底。</summary>
    private static VisionOptions ReadVisionOptions(IConfiguration configuration)
    {
        var (min, max) = ReadAxisSoftLimits(configuration);

        return new VisionOptions
        {
            CalibrationStepMm = ReadDouble(configuration, "Vision:CalibrationStepMm", 50.0),
            PreviewIntervalMs = ReadInt(configuration, "Vision:PreviewIntervalMs", 100),
            CaptureTimeoutMs = ReadInt(configuration, "Vision:CaptureTimeoutMs", 3000),
            DefaultExposureUs = ReadDouble(configuration, "Hardware:Camera:ExposureTime", 5000.0),
            DefaultGainDb = ReadDouble(configuration, "Hardware:Camera:Gain", 0.0),
            MinLimits = min,
            MaxLimits = max,
        };
    }

    /// <summary>读取 Io:DiMap / Io:DoMap 的点位号与中文名（08-页面清单 页面 7 要求中文名来自这两段）。</summary>
    private static IoPointMap ReadIoPointMap(IConfiguration configuration) => new()
    {
        Di = ReadIoPointDefinitions(configuration, "Io:DiMap"),
        Do = ReadIoPointDefinitions(configuration, "Io:DoMap"),
    };

    private static IReadOnlyList<IoPointDefinition> ReadIoPointDefinitions(IConfiguration configuration, string sectionPath)
        => configuration.GetSection(sectionPath)
            .GetChildren()
            .Select(child => new IoPointDefinition(
                child.Key,
                string.IsNullOrWhiteSpace(child.Value) ? child.Key : child.Value!))
            .ToList();

    /// <summary>
    /// 读取虚拟 IO 的输入点位初值（<c>Io:InitialDiStates</c>）。安全门 X05、急停 X06、气压 X07 是
    /// 常闭/常开明确的信号，虚拟模式没有真实输入，必须按"安全状态"预置，否则手动页会被判为急停而全部禁用。
    /// </summary>
    private static IReadOnlyDictionary<string, bool> ReadInitialDiStates(IConfiguration configuration)
    {
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var child in configuration.GetSection("Io:InitialDiStates").GetChildren())
        {
            if (bool.TryParse(child.Value, out var value))
            {
                states[child.Key] = value;
            }
        }

        return states;
    }

    /// <summary>从 appsettings 读取雷赛运控卡整卡参数（卡号 + 4 轴，下标即 <see cref="AxisId"/>）。缺失/非法用默认值兜底。</summary>
    private static LeadshineMotionCardOptions ReadMotionCardOptions(IConfiguration configuration)
    {
        ushort cardNo = 0;
        if (ushort.TryParse(configuration["Hardware:MotionCard:CardNo"], out var parsedCardNo))
        {
            cardNo = parsedCardNo;
        }

        var axes = new LeadshineAxisOptions[4];
        foreach (var axis in configuration.GetSection("Hardware:MotionCard:Axes").GetChildren())
        {
            var id = axis["Id"];
            if (!Enum.TryParse<AxisId>(id, ignoreCase: true, out var axisId))
            {
                continue;
            }

            var pulsePerMm = ReadDouble(axis, "PulsePerMm", axisId == AxisId.R ? 0.0 : 10000.0);
            var pulsePerDeg = ReadDouble(axis, "PulsePerDeg", axisId == AxisId.R ? 1000.0 : 0.0);

            axes[(int)axisId] = new LeadshineAxisOptions
            {
                PulsePerUnit = pulsePerMm != 0 ? pulsePerMm : pulsePerDeg,
                MinSpeed = ReadDouble(axis, "MinSpeed", 1.0),
                MaxSpeed = ReadDouble(axis, "MaxSpeed", axisId == AxisId.R ? 180.0 : 300.0),
                AccTimeMs = ReadDouble(axis, "AccTimeMs", 80.0),
                DecTimeMs = ReadDouble(axis, "DecTimeMs", 80.0),
                MinLimit = ReadDouble(axis, "MinLimit", axisId == AxisId.R ? -180.0 : 0.0),
                MaxLimit = ReadDouble(axis, "MaxLimit", axisId == AxisId.R ? 180.0 : 400.0),
                HomeMode = (ushort)ReadInt(axis, "HomeMode", 1),
                HomeSpeed = ReadDouble(axis, "HomeSpeed", 20.0),
                HomeAcc = ReadDouble(axis, "HomeAcc", 200.0),
            };
        }

        return new LeadshineMotionCardOptions { CardNo = cardNo, Axes = axes };
    }

    /// <summary>
    /// 创建虚拟相机。样本图目录由 <c>Hardware:Camera:SampleImageDirectory</c> 指定，
    /// 目录可用时每次取图随机返回一张模拟采样图，否则退化为合成图（见 docs/03 第 4 节）。
    /// </summary>
    private static VirtualCamera CreateVirtualCamera(IConfiguration configuration, ILogger<VirtualCamera> log)
    {
        var directory = configuration["Hardware:Camera:SampleImageDirectory"] ?? "images/samples";
        var provider = SampleImageProvider.Create(directory, log);
        return new VirtualCamera(log, provider);
    }

    /// <summary>从 appsettings 的 Hardware:Camera 段读取海康相机参数。缺失/非法用默认值兜底。</summary>
    private static HikvisionCameraOptions ReadHikvisionCameraOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("Hardware:Camera");
        return new HikvisionCameraOptions
        {
            SerialNumber = section["SerialNumber"] ?? "",
            ExposureTimeUs = ReadDouble(section, "ExposureTime", 5000.0),
            GainDb = ReadDouble(section, "Gain", 0.0),
            TriggerMode = section["TriggerMode"] ?? "Software",
            TimeoutMs = ReadInt(section, "TimeoutMs", 3000),
        };
    }

    /// <summary>
    /// 读取每日维护配置（<c>Maintenance</c> 段）。默认值与 06 文档第 7 节的保留策略一致，
    /// 缺失时用默认值兜底并告警，不阻断启动。
    /// </summary>
    private static MaintenanceOptions ReadMaintenanceOptions(IConfiguration configuration) => new()
    {
        RunHourOfDay = ReadInt(configuration, "Maintenance:RunHourOfDay", 2),
        ArchiveDirectory = configuration["Maintenance:ArchiveDirectory"] ?? "data/archive",
        LogDirectory = configuration["Maintenance:LogDirectory"] ?? "logs",
        OutputRetainDays = ReadInt(configuration, "Maintenance:OutputRetainDays", 90),
        AlarmRetainDays = ReadInt(configuration, "Maintenance:AlarmRetainDays", 180),
        OperationLogRetainDays = ReadInt(configuration, "Maintenance:OperationLogRetainDays", 365),
        CalibrationKeepCount = ReadInt(configuration, "Maintenance:CalibrationKeepCount", 20),
        LogRetainDays = ReadInt(configuration, "Maintenance:LogRetainDays", 90)
    };

    /// <summary>读取一键备份配置（<c>Backup</c> 段）：备份目录与保留份数。</summary>
    private static BackupOptions ReadBackupOptions(IConfiguration configuration) => new()
    {
        BackupDirectory = configuration["Backup:BackupDirectory"] ?? "data/backup",
        KeepCount = ReadInt(configuration, "Backup:KeepCount", 30)
    };

    /// <summary>读取系统自检配置（<c>SelfCheck</c> 段）：数据目录、磁盘下限（AL501 用）、巡检周期。</summary>
    private static SelfCheckOptions ReadSelfCheckOptions(IConfiguration configuration) => new()
    {
        DataDirectory = configuration["SelfCheck:DataDirectory"] ?? "data",
        MinFreeDiskGb = ReadDouble(configuration, "SelfCheck:MinFreeDiskGb", 1.0),
        IntervalSeconds = ReadInt(configuration, "SelfCheck:IntervalSeconds", 60)
    };

    /// <summary>读取 double 配置项（不变文化），缺失或非法时用默认值并告警。</summary>
    private static double ReadDouble(IConfiguration configuration, string key, double fallback)
    {
        var raw = configuration[key];
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        Log.Warning("配置 {Key} 缺失或非法（{Raw}），使用默认值 {Default}", key, raw, fallback);
        return fallback;
    }

    /// <summary>读取 int 配置项（不变文化），缺失或非法时用默认值并告警。</summary>
    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        var raw = configuration[key];
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        Log.Warning("配置 {Key} 缺失或非法（{Raw}），使用默认值 {Default}", key, raw, fallback);
        return fallback;
    }

    /// <summary>把配置里的相对路径转成相对程序目录的绝对路径（避免依赖"当前工作目录"）。</summary>
    private static string ResolveDatabasePath(IConfiguration configuration)
    {
        var configured = configuration["Storage:DatabasePath"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = DefaultDatabaseRelativePath;
        }

        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
    }

    /// <summary>
    /// 启动日志用的硬件模式描述。按设备分别解析，便于一眼看出「运控卡虚拟 + 相机真实」这类混合配置
    /// （只接了相机、没接运控卡时的常用组合）。
    /// </summary>
    private static string ResolveHardwareMode(IConfiguration configuration)
    {
        var useVirtual = bool.TryParse(configuration["Hardware:UseVirtual"], out var value) && value;
        var motion = ResolveDeviceVirtual(configuration, "Hardware:MotionCard:Type", useVirtual);
        var camera = ResolveDeviceVirtual(configuration, "Hardware:Camera:Type", useVirtual);

        return $"运控卡={(motion ? "Virtual" : "Real")}，相机={(camera ? "Virtual" : "Real")}";
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>界面未捕获异常：记日志 + 弹窗 + 不退出；连续 3 次才退出进程。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var now = Environment.TickCount64;

        // 距上次界面异常已超过窗口 → 上一轮「连续」早已中断，本次重新从 1 计数；
        // 否则累加（这样 3 次相隔几秒的同一故障才会被判为致命，而不是一天里的 3 次偶发异常）
        _consecutiveUiExceptionCount = now - _lastUiExceptionTick <= UiExceptionContinuousWindowMs
            ? _consecutiveUiExceptionCount + 1
            : 1;
        _lastUiExceptionTick = now;

        Log.Error(e.Exception, "界面未处理异常（连续第 {Count} 次）", _consecutiveUiExceptionCount);

        NotifyError($"操作执行失败：{ToUserMessage(e.Exception)}。可以重试；若反复出现请联系工程师。");

        e.Handled = true;

        if (_consecutiveUiExceptionCount >= MaxConsecutiveUiExceptionCount)
        {
            Log.Fatal("界面未处理异常连续 {Count} 次，准备退出进程", _consecutiveUiExceptionCount);
            NotifyError("软件连续多次发生错误，即将退出，请重启软件。");
            Shutdown(FatalExitCode);
        }
    }

    /// <summary>非界面线程未捕获异常：进程即将终止，只能保证日志落盘。</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception,
                  "非界面线程未处理异常，进程即将终止：{IsTerminating}", e.IsTerminating);
        Log.CloseAndFlush();
    }

    /// <summary>未观察的 Task 异常：记录并标记已观察，避免拖垮进程。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未观察的后台任务异常");
        e.SetObserved();
    }

    /// <summary>异常转成操作工能看懂的一句话（不得直接给 ToString）。</summary>
    private static string ToUserMessage(Exception exception)
        => string.IsNullOrWhiteSpace(exception.GetBaseException().Message)
            ? "未知错误"
            : exception.GetBaseException().Message;

    private void NotifyError(string message)
    {
        var notifier = _notifier;
        if (notifier is null)
        {
            // 极早期异常（DI 尚未就绪）没有弹窗通道，日志已经记录
            Log.Warning("弹窗通道尚未就绪，未能提示用户：{Message}", message);
            return;
        }

        try
        {
            notifier.Error(message);
        }
        catch (InvalidOperationException ex)
        {
            // 应用正在退出时 Dispatcher 可能已不可用
            Log.Error(ex, "提示用户失败：{Message}", message);
        }
    }
}
