# AutoTaping 项目长期记忆

## 项目性质
.NET 8 + WPF 的自动贴胶机上位机（设备级 HMI）。`docs/` 下 13 份文档是唯一权威规格，按
`docs/11-开发任务分解.md` 的 S0~S9 推进：每阶段必须可编译 + 可运行 + 通过该阶段验收才进下一阶段。
S0~S9 已全部完成（S8 真实硬件仅代码层，待现场验收）。逐阶段细节/决策看 `memory/2026-11-0X.md`。

## 铁律（docs/10-编码约定与AI须知.md）
- 只做当前阶段交付物清单内的事，不提前实现后续阶段，不"顺手优化"。
- 接口签名以 docs/02、03、04、06 为准，不得私自变更。
- Core 层不得引用 WPF、不得写 SQL、不得 MessageBox；一律走 `Core/Abstractions` 接口。
- 禁止：`async void`（事件处理器除外）、`Thread.Sleep`、`.Result`/`.Wait()`、空 catch、
  EF Core、Prism/MahApps/MaterialDesign、Modbus 库、第三方图表库。
- 每个 .cs 头 3 行注释（职责/层/依赖）；public 类型有 XML 注释；一个文件一个 public 类型
  （小型 record/enum 最多 3 个同文件）。
- 结构化日志占位符；硬件参数进 appsettings.json、工艺参数进配方、纯算法常量用带注释 const。
- CSV 导出 UTF-8 with BOM；数据库时间列统一 TEXT `yyyy-MM-dd HH:mm:ss`。

## 阶段成果一览
| 阶段 | 交付 | 测试数 |
|---|---|---|
| S0 | 6 项目骨架、DI + Serilog + 全局异常、样式字典 | - |
| S1 | SQLite + Dapper、9 表、5 仓储、断电检测 | 12 |
| S2 | IMotionCard/ICamera/IIoService + Virtual 实现 + DI 按 UseVirtual 选择 | 17 |
| S3 | 主窗口壳（状态栏/8 导航/内容区/操作条）、8 页面壳、LoginDialog、IdleMonitor 锁屏、权限 | 38 |
| S4 | DeviceMonitor(200ms)、ManualPage 点动/回零/定位、IoPage + 强制输出、页面生命周期 | 45 |
| S5 | Vision 层 7 算法类（标定/模板匹配/纠偏/旋转补偿）、VisionPage | 18 |
| S6 | DeviceStateMachine、InterlockChecker(10 条)、AutoFlowEngine + 15 步骤、AutoFlowCoordinator、AutoPage | 95 累计 |
| S7 | ParameterCatalog(31 参数)/Validator、AlarmService 去重恢复、OutputService、Recipe/Alarm/Output 三页 | 135 |
| S8 | LeadshineMotionCard(LTDMC P/Invoke)、MotionCardIoService、HikvisionCamera(HAS_MVS)、降级 | 135 |
| S9 | DailyMaintenanceService、BackupService、SelfCheckService、UserManagementService、SystemPage | 154 |

## 跨阶段仍然有效的关键决策
- **Core 不引用 Vision**：流程引擎走 `IVisionService`；旋转补偿在 MoveToTapePosStep 内联等价三角。
- **硬件真实/Virtual 降级**：DI 工厂「构造真实→Initialize 探测→失败 Dispose 返回 Virtual」，
  降级标记走单例 `HardwareStartupReport` 报 AL105/AL201；解析顺序（先 IMotionCard 后 IIoService）保证 IO 能感知运控卡降级。
- **参数热生效**放引擎循环第 2 步（WaitMaterialStep 每节拍刷 ctx.Recipe.Parameters），不在引擎内部。
- **停机分析落库** → 第 9 张表 DowntimeRecord；报警去重按"最近一次触发"滚动、命中只内存计数。
- **备份**必须 `SqliteConnection.BackupDatabase` 到临时库（文件复制被连接池句柄占用），临时库连接串 `Pooling=false`。
- **自检恢复**不判断内存标记（重启后要能清掉上次遗留报警）。
- **单步模式**：引擎 `StepCompleted` 事件 + 走完自动 Pause（不 return）。
- **后台线程**改绑定属性一律经 `IUiDispatcher` 切回 UI 线程。

## UI / MVVM 约定
- CommunityToolkit.Mvvm 8.4.2（`[ObservableProperty]` + `[RelayCommand]`，本地缓存可离线）。
- 切页：`PageViewModel` 基类（Key/Title/Icon/RequiredPermission）+ `IEnumerable<PageViewModel>` 注入 ShellViewModel，
  MainWindow.Resources 用 DataTemplate 映射 VM→UserControl；内容区只绑 `CurrentPage`。
- 页面生命周期：`OnEnteringAsync` 返回 false 可拒绝进入（手动页二次确认），`OnLeaveAsync` 做清理；
  跳转发 `RequestNavigation(PageKey)` 由 ShellViewModel 执行。**直接 `MoveTo(page)` 不走生命周期**，
  启动默认页必须额外调 `EnterInitialPageAsync`，否则刷新循环不启动（2026-11-09 踩坑）。
- 权限：Core `PermissionPolicy` 唯一映射；无权导航保留可点但置灰（DataTrigger），点击由 VM 再校验。
- 窗口生命周期归 App（`ShutdownMode=OnExplicitShutdown`）；ViewModel 抛事件不开窗口。
- 轮询：`PeriodicTimer`+`Task.Run`，页面激活期间运行、离开时 Cancel+Dispose，避免任务累积。
- 硬件状态统一读 `IDeviceMonitor.Current`，页面不得直接轮询 IMotionCard/IIoService。
- 按住点动：`App/Behaviors/PressHoldBehavior`（鼠标/触摸/捕获丢失都算松开）。
- **XAML 陷阱**：`RowDefinition.Height`/`ColumnDefinition.Width` 用 `GridLength` 资源，`Button.Width`/`Ellipse.Width` 用 `sys:Double`。
- 该机器 DPI 150%：1280×1024 DIP 窗口实际 1920×1536 px，UIA 坐标按 1.5 换算。

## 数据层
- 包版本锁定：Dapper 2.1.24、Microsoft.Data.Sqlite 8.0.10（本地缓存，可离线还原）。
- `SqliteConnectionFactory.Create()` 每次操作新建连接（WAL + Default Timeout=5）；写操作包事务。
- 仓储用私有嵌套 Row 类显式映射（时间列走 `SqliteTime.ToDb/FromDb`），避免 Dapper 直接映射到领域模型。
- `DatabaseInitializer.Initialize()` 幂等：启动/测试/删库重建均可反复调用。
- DI 注册：`App.BuildServiceProvider` → `RegisterDataLayer`。

## 用户背景与偏好
- 用户**转行**做工业软件/上位机，AutoTaping 是**实验/练手项目**（非生产交付）。
- 讲解要**先补概念、再落到代码**：MARK、九点标定、双 MARK 纠偏、旋转中心补偿、真空/破真空、保压、
  节拍/UPH、互锁、暂停 vs 停止 vs 急停等术语要展开，不能默认他懂现场。
- 关心「代码量/接手成本/学习路径」→ 给结构化清单 + 时间估算 + 阅读顺序效果好。
- **不要逐行讲代码**（2026-11-09 反馈）：给「主脉络 + 对应文件」的索引式速查表，细节他自己看，问到再展开。
- 无数据库基础（2026-11-09）：讲数据层要从"数据库是什么、一次查询怎么走完整条链"讲起，
  仓储/Dapper 这类名词必须解释"为什么存在"而不只是"是什么"。

## 环境注意
- 仅 .NET SDK 9.0.317（可 target net8.0）；无 sqlite3.exe、无 pwsh（仅 PowerShell 5.1）。
- `python.exe` 是 Store 占位程序（无输出），**不能**用来读写 SQLite；造脏数据只能 DB Browser 或临时 .NET 程序。
- 该目录不是 git 仓库，无法用 git diff。
- **NuGet**：`api.nuget.org` SSL 失败，`nuget.azure.cn` 可达。根目录 `NuGet.config` 指向本地 `packages-local`。
  新增包：`curl https://nuget.azure.cn/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg` 下到 packages-local。
- UI 冒烟测试套路：`Start-Process` 起 exe → UIAutomation 按 ProcessId 找窗口 → ValuePattern 填用户名 →
  PasswordBox `SetFocus()`+`SendKeys` → 断言 `bin/Debug/net8.0-windows/logs/*.log`。
  登录框 AutomationId：`UserNameInput` / `PasswordInput`。主窗口按钮树：0=报警徽标，1~8=导航，9~14=操作条。
- **中文 .ps1 必须 UTF-8 with BOM**，否则 PS 5.1 按 ANSI 解析乱码报语法错误
  （同文件改写用 `[IO.File]::ReadAllBytes/WriteAllBytes`，`Get-Content`/`Set-Content` 会被安全策略拦截）。
