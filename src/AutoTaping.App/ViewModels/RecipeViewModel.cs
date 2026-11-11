// 职责：参数配方页 ViewModel——参数分组编辑、范围校验、保存审计、加载/另存为/导入/导出/恢复默认
// 层：App/ViewModels
// 依赖：IRecipeRepository/IOperationLogRepository/IAuthService/IUserNotifier/IClock（Core/Abstractions）、
//       ParameterCatalog/ParameterValidator（Core/Services）、Recipe/RecipeParameters/TapePathPoint（Core/Models）、
//       IFilePicker/IUserPrompt（App/Infrastructure）、CommunityToolkit.Mvvm

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using AutoTaping.App.Infrastructure;
using AutoTaping.App.Navigation;
using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using AutoTaping.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 参数配方页 ViewModel（08-页面清单 页面 3）。权限：查看=全部角色，编辑=技术员及以上；
/// 每次保存对每个变更项写一条 <c>OperationLog</c>（旧值/新值/用户），保存成功后提示「下一个节拍生效」
/// （配置层热生效由 <c>WaitMaterialStep</c> 在每个节拍起点刷新配方参数实现）。
/// </summary>
public sealed partial class RecipeViewModel : PageViewModel
{
    /// <summary>变更记录面板显示的条数（08-页面清单 页面 3 的「参数变更记录」小窗）。</summary>
    private const int ChangeLogLimit = 5;

    /// <summary>查询变更记录的回溯天数（操作日志保留 365 天，这里只看近期）。</summary>
    private const int ChangeLogLookbackDays = 30;

    /// <summary>导入/导出的文件过滤器。</summary>
    private const string RecipeFileFilter = "配方文件 (*.json)|*.json|所有文件 (*.*)|*.*";

    private readonly IRecipeRepository _recipes;
    private readonly IOperationLogRepository _operationLogs;
    private readonly IAuthService _auth;
    private readonly IUserNotifier _notifier;
    private readonly IFilePicker _filePicker;
    private readonly IUserPrompt _prompt;
    private readonly IClock _clock;
    private readonly ILogger<RecipeViewModel> _log;

    /// <summary>全部参数行（一次性创建，切分组时只换显示集合，避免丢失已编辑的值）。</summary>
    private readonly List<ParameterItemViewModel> _allItems = new();

    /// <summary>加载时的参数值快照（用于生成审计日志的旧值）。</summary>
    private readonly Dictionary<string, double> _originalValues = new(StringComparer.Ordinal);

    /// <summary>加载时的贴附路径快照（用于生成审计日志的旧值）。</summary>
    private string _originalTapePathText = "";

    /// <summary>正在加载数据：抑制「有未保存修改」标记。</summary>
    private bool _isLoading;

    /// <summary>编辑区当前展示的配方（用户取消切换时用来把下拉框选择退回）。</summary>
    private Recipe? _editingRecipe;

    /// <summary>构造参数配方页 ViewModel。</summary>
    /// <param name="recipes">配方仓储。</param>
    /// <param name="operationLogs">操作日志仓储（保存审计）。</param>
    /// <param name="auth">认证与权限服务（编辑权限二次校验）。</param>
    /// <param name="notifier">用户提示通道。</param>
    /// <param name="filePicker">文件选择（导入 / 导出）。</param>
    /// <param name="prompt">文本输入（另存为的名称）。</param>
    /// <param name="clock">时钟（导出文件名日期、变更记录区间）。</param>
    /// <param name="log">日志。</param>
    public RecipeViewModel(
        IRecipeRepository recipes,
        IOperationLogRepository operationLogs,
        IAuthService auth,
        IUserNotifier notifier,
        IFilePicker filePicker,
        IUserPrompt prompt,
        IClock clock,
        ILogger<RecipeViewModel> log)
    {
        _recipes = recipes;
        _operationLogs = operationLogs;
        _auth = auth;
        _notifier = notifier;
        _filePicker = filePicker;
        _prompt = prompt;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public override PageKey Key => PageKey.Recipe;

    /// <inheritdoc />
    public override string Title => "参数配方";

    /// <inheritdoc />
    public override string Icon => "☰";

    /// <inheritdoc />
    public override Permission RequiredPermission => Permission.None;

    /// <summary>配方下拉列表。</summary>
    public ObservableCollection<Recipe> Recipes { get; } = new();

    /// <summary>参数分组树（左侧）。</summary>
    public ObservableCollection<ParameterGroupItemViewModel> Groups { get; } = new();

    /// <summary>当前分组的参数行（右侧）。</summary>
    public ObservableCollection<ParameterItemViewModel> Parameters { get; } = new();

    /// <summary>贴附路径点（仅「贴附路径」分组显示）。</summary>
    public ObservableCollection<TapePathPointItemViewModel> TapePathPoints { get; } = new();

    /// <summary>参数变更记录（最近若干条 <c>修改参数</c> 操作日志）。</summary>
    public ObservableCollection<OperationLog> ChangeLogs { get; } = new();

    /// <summary>贴附路径模式选项。</summary>
    public IReadOnlyList<TapePathMode> TapePathModes { get; } = Enum.GetValues<TapePathMode>();

    /// <summary>当前选中 / 正在编辑的配方。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRecipeText))]
    private Recipe? _selectedRecipe;

    /// <summary>当前选中的参数分组。</summary>
    [ObservableProperty]
    private ParameterGroupItemViewModel? _selectedGroup;

    /// <summary>当前用户是否可编辑参数（技术员及以上）。</summary>
    [ObservableProperty]
    private bool _canEdit;

    /// <summary>是否有未保存的修改。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnsavedHintText))]
    private bool _isDirty;

    /// <summary>未保存的变更项数。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnsavedHintText))]
    private int _changedCount;

    /// <summary>是否存在超出允许范围的参数（存在时禁用保存）。</summary>
    [ObservableProperty]
    private bool _hasValidationError;

    /// <summary>状态提示（最近一次操作结果）。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>当前编辑区域是否为「贴附路径」分组。</summary>
    [ObservableProperty]
    private bool _isTapePathGroup;

    /// <summary>贴附路径模式。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArcMode))]
    private TapePathMode _tapePathMode = TapePathMode.Line;

    /// <summary>圆弧圆心 X 文本（mm）。</summary>
    [ObservableProperty]
    private string _arcCenterXText = "0.00";

    /// <summary>圆弧圆心 Y 文本（mm）。</summary>
    [ObservableProperty]
    private string _arcCenterYText = "0.00";

    /// <summary>圆弧半径文本（mm）。</summary>
    [ObservableProperty]
    private string _arcRadiusText = "0.00";

    /// <summary>圆弧起始角文本（°）。</summary>
    [ObservableProperty]
    private string _arcStartText = "0.00";

    /// <summary>圆弧终止角文本（°）。</summary>
    [ObservableProperty]
    private string _arcEndText = "0.00";

    /// <summary>未保存提示文字（08-页面清单 页面 3 底部提示）。</summary>
    public string UnsavedHintText => IsDirty ? $"⚠ 有 {ChangedCount} 项参数未保存" : "参数已与数据库一致";

    /// <summary>当前激活配方文字（界面提示用）。</summary>
    public string ActiveRecipeText => SelectedRecipe is null ? "未选择配方" : SelectedRecipe.Name;

    /// <summary>是否圆弧模式（只有圆弧模式才启用圆弧参数输入）。</summary>
    public bool IsArcMode => TapePathMode == TapePathMode.Arc;

    /// <summary>当前用户是否只能查看（无参数编辑权限）。</summary>
    public bool IsReadOnly => !CanEdit;

    /// <summary>进入本页：加载配方列表与当前激活配方的参数。</summary>
    /// <param name="ct">取消令牌。</param>
    public override Task<bool> OnEnteringAsync(CancellationToken ct)
    {
        CanEdit = _auth.HasPermission(Permission.RecipeEdit);
        BuildGroups();
        ReloadRecipes(selectActive: true);
        return Task.FromResult(true);
    }

    partial void OnSelectedRecipeChanged(Recipe? value)
    {
        if (_isLoading || value is null)
        {
            return;
        }

        // 切换到别的配方前先处理未保存修改（08-页面清单 页面 3：切配方弹窗询问）
        if (IsDirty
            && !_notifier.Confirm("切换配方", $"当前配方有 {ChangedCount} 项参数未保存，切换将丢失这些修改。确认切换？"))
        {
            // 用户取消：把下拉框选择退回正在编辑的配方，避免界面与编辑区不一致
            _isLoading = true;
            try
            {
                SelectedRecipe = _editingRecipe;
            }
            finally
            {
                _isLoading = false;
            }

            return;
        }

        LoadRecipeToEditor(value);
    }

    partial void OnSelectedGroupChanged(ParameterGroupItemViewModel? value)
    {
        RebuildParameterRows();
        IsTapePathGroup = value?.Group == ParameterGroup.TapePath;
    }

    partial void OnHasValidationErrorChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnCanEditChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReadOnly));
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDirtyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnTapePathModeChanged(TapePathMode value) => MarkDirty();

    /// <summary>「加载」：把当前配方设为生产配方（切换激活配方并留痕）。</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        var recipe = SelectedRecipe;
        if (recipe is null)
        {
            _notifier.Warn("请先选择配方。");
            return;
        }

        if (recipe.IsActive)
        {
            _notifier.Info($"「{recipe.Name}」已经是当前生产配方。");
            return;
        }

        if (IsDirty && !_notifier.Confirm("切换配方", "当前配方有未保存修改，切换生产配方将丢失这些修改。确认切换？"))
        {
            return;
        }

        var user = _auth.CurrentUser?.UserName ?? "";
        var previous = Recipes.FirstOrDefault(item => item.IsActive)?.Name ?? "无";

        try
        {
            await Task.Run(() =>
            {
                _recipes.SetActive(recipe.Id);
                _operationLogs.Insert(user, "切换配方", recipe.Name, previous, recipe.Name);
            });

            _log.LogInformation("切换生产配方：{Old} -> {New}，操作人 {User}", previous, recipe.Name, user);
            _notifier.Info($"已切换生产配方为「{recipe.Name}」，下一个节拍生效。");
            ReloadRecipes(selectActive: false);
            SelectRecipeById(recipe.Id);
            await ReloadChangeLogsAsync(recipe.Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "切换生产配方失败：{Recipe}", recipe.Name);
            _notifier.Error($"切换配方失败：{ex.GetBaseException().Message}。请检查数据库后重试。");
        }
    }

    /// <summary>「保存」：校验通过后写库 + 对每个变更项写审计日志。</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var recipe = SelectedRecipe;
        if (recipe is null)
        {
            return;
        }

        if (!CanEdit)
        {
            _notifier.Warn("修改参数需要技术员权限，请切换到技术员或工程师账户。");
            return;
        }

        if (HasValidationError)
        {
            _notifier.Warn("有参数超出允许范围，请先修正再保存。");
            return;
        }

        var changes = ApplyEditsToModel(recipe);
        if (changes.Count == 0)
        {
            IsDirty = false;
            ChangedCount = 0;
            StatusText = "参数没有变化";
            _notifier.Info("参数没有变化，无需保存。");
            return;
        }

        var user = _auth.CurrentUser?.UserName ?? "";

        try
        {
            await Task.Run(() =>
            {
                _recipes.Update(recipe);

                foreach (var change in changes)
                {
                    _operationLogs.Insert(user, "修改参数", $"{recipe.Name}.{change.Key}", change.OldValue, change.NewValue);
                }
            });

            _log.LogInformation("保存配方 {Recipe}：{Count} 项参数变更，操作人 {User}", recipe.Name, changes.Count, user);

            CaptureOriginals(recipe);
            IsDirty = false;
            ChangedCount = 0;
            StatusText = $"已保存 {changes.Count} 项参数，{_clock.Now:HH:mm:ss}";
            _notifier.Info("参数已保存，下一个节拍生效（无需重启软件）。");
            await ReloadChangeLogsAsync(recipe.Name);
            ReloadRecipes(selectActive: false);
            SelectRecipeById(recipe.Id);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "保存配方 {Recipe} 失败", recipe.Name);
            _notifier.Error($"保存失败：{ex.GetBaseException().Message}。参数未写入，请检查数据库后重试。");
        }
    }

    /// <summary>「另存为」：以新名称保存为一条新配方（名称唯一性校验）。</summary>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var recipe = SelectedRecipe;
        if (recipe is null)
        {
            return;
        }

        if (!RequireEditPermission())
        {
            return;
        }

        if (HasValidationError)
        {
            _notifier.Warn("有参数超出允许范围，请先修正再另存为。");
            return;
        }

        var defaultName = $"{recipe.Name}-副本";
        var name = _prompt.PromptText("另存为", "请输入新配方名称（不得与现有配方重名）", defaultName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        name = name.Trim();
        if (Recipes.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _notifier.Warn($"配方名称「{name}」已存在，请换一个名称。");
            return;
        }

        ApplyEditsToModel(recipe);

        var user = _auth.CurrentUser?.UserName ?? "";
        var copy = new Recipe
        {
            Name = name,
            ProductCode = recipe.ProductCode,
            TapeSpec = recipe.TapeSpec,
            Parameters = recipe.Parameters,
            Version = 1,
            IsActive = false,
            CreatedBy = user,
            UpdatedBy = user
        };

        try
        {
            var newId = await Task.Run(() =>
            {
                var id = _recipes.Insert(copy);
                _operationLogs.Insert(user, "另存为配方", name, recipe.Name, name);
                return id;
            });

            _log.LogInformation("配方另存为：{Source} -> {Target}（Id={Id}），操作人 {User}", recipe.Name, name, newId, user);
            _notifier.Info($"已另存为「{name}」。");
            ReloadRecipes(selectActive: false);
            SelectRecipeById(newId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "配方另存为失败：{Name}", name);
            _notifier.Error($"另存为失败：{ex.GetBaseException().Message}。名称可能重复，请换一个名称后重试。");
        }
    }

    /// <summary>「导入」：选 JSON 文件导入为新配方（结构非法时提示原因）。</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (!RequireEditPermission())
        {
            return;
        }

        var path = _filePicker.PickOpenFile("导入配方", RecipeFileFilter);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var user = _auth.CurrentUser?.UserName ?? "";
        try
        {
            var newId = await Task.Run(() =>
            {
                var id = _recipes.ImportFromFile(path);
                _operationLogs.Insert(user, "导入配方", Path.GetFileName(path), null, Path.GetFileName(path));
                return id;
            });

            _log.LogInformation("导入配方 {Path}，新 Id={Id}，操作人 {User}", path, newId, user);
            _notifier.Info($"已导入配方（Id={newId}）。请检查参数后「加载」设为生产配方。");
            ReloadRecipes(selectActive: false);
            SelectRecipeById(newId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "导入配方失败：{Path}", path);
            _notifier.Error($"导入失败：{ex.GetBaseException().Message}。请确认文件是本软件导出的配方 JSON。");
        }
    }

    /// <summary>「导出」：导出当前配方为 JSON 文件（默认文件名 配方名_yyyyMMdd.json）。</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var recipe = SelectedRecipe;
        if (recipe is null)
        {
            return;
        }

        if (!RequireEditPermission())
        {
            return;
        }

        var defaultFileName = $"{recipe.Name}_{_clock.Now:yyyyMMdd}.json";
        var path = _filePicker.PickSaveFile("导出配方", RecipeFileFilter, defaultFileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var user = _auth.CurrentUser?.UserName ?? "";
        try
        {
            var fullPath = await Task.Run(() =>
            {
                var written = _recipes.ExportToFile(recipe.Id, path);
                _operationLogs.Insert(user, "导出配方", recipe.Name, null, written);
                return written;
            });

            _log.LogInformation("导出配方 {Recipe} 到 {Path}，操作人 {User}", recipe.Name, fullPath, user);
            _notifier.Info($"配方已导出到：{fullPath}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "导出配方失败：{Recipe}", recipe.Name);
            _notifier.Error($"导出失败：{ex.GetBaseException().Message}。请确认目标目录可写。");
        }
    }

    /// <summary>「恢复默认」：把所有参数恢复为代码内置默认值（二次确认，需再点保存才写库）。</summary>
    [RelayCommand]
    private void RestoreDefault()
    {
        if (!RequireEditPermission())
        {
            return;
        }

        if (!_notifier.Confirm("恢复默认", "将把所有参数恢复为出厂默认值（尚未写库，需点「保存」生效）。确认继续？"))
        {
            return;
        }

        var defaults = new RecipeParameters();

        foreach (var item in _allItems)
        {
            if (ParameterCatalog.TryGetValue(defaults, item.Key, out var value))
            {
                item.LoadValue(value);
            }
        }

        TapePathMode = defaults.TapePath.Mode;
        ArcCenterXText = defaults.TapePath.ArcCenter?.X.ToString("F2", CultureInfo.InvariantCulture) ?? "0.00";
        ArcCenterYText = defaults.TapePath.ArcCenter?.Y.ToString("F2", CultureInfo.InvariantCulture) ?? "0.00";
        ArcRadiusText = defaults.TapePath.ArcRadius.ToString("F2", CultureInfo.InvariantCulture);
        ArcStartText = defaults.TapePath.ArcStartDeg.ToString("F2", CultureInfo.InvariantCulture);
        ArcEndText = defaults.TapePath.ArcEndDeg.ToString("F2", CultureInfo.InvariantCulture);

        TapePathPoints.Clear();
        for (var i = 0; i < defaults.TapePath.Points.Count; i++)
        {
            AddPointRow(defaults.TapePath.Points[i], i + 1);
        }

        MarkDirty();
        _log.LogInformation("参数已恢复默认（未保存）：配方 {Recipe}", SelectedRecipe?.Name);
    }

    /// <summary>「添加路径点」：在贴附路径末尾追加一个点。</summary>
    [RelayCommand]
    private void AddPoint()
    {
        if (!RequireEditPermission())
        {
            return;
        }

        AddPointRow(new TapePathPoint(0, 0, 2), TapePathPoints.Count + 1);
        MarkDirty();
    }

    /// <summary>「删除路径点」：至少保留 2 个点（直线/折线都需要 2 点）。</summary>
    /// <param name="point">待删除的路径点。</param>
    [RelayCommand]
    private void RemovePoint(TapePathPointItemViewModel? point)
    {
        if (point is null || !RequireEditPermission())
        {
            return;
        }

        if (TapePathPoints.Count <= 2)
        {
            _notifier.Warn("贴附路径至少需要 2 个点。");
            return;
        }

        TapePathPoints.Remove(point);
        for (var i = 0; i < TapePathPoints.Count; i++)
        {
            TapePathPoints[i] = Reindex(TapePathPoints[i], i + 1);
        }

        MarkDirty();
    }

    /// <summary>重建一个路径点行（用于删除后的序号重排，保持变更订阅与只读状态）。</summary>
    /// <param name="row">原行。</param>
    /// <param name="index">新序号（从 1 开始）。</param>
    private TapePathPointItemViewModel Reindex(TapePathPointItemViewModel row, int index)
    {
        row.TryGetPoint(out var point);
        var rebuilt = new TapePathPointItemViewModel(point, index, row.IsReadOnly);
        rebuilt.ValueChanged += OnParameterValueChanged;
        return rebuilt;
    }

    // ==================== 内部实现 ====================

    private bool CanSave() => CanEdit && IsDirty && !HasValidationError;

    private bool RequireEditPermission()
    {
        if (CanEdit)
        {
            return true;
        }

        _notifier.Warn("该操作需要技术员权限，请切换到技术员或工程师账户。");
        return false;
    }

    /// <summary>建立左侧分组树（一次即可）。</summary>
    private void BuildGroups()
    {
        if (Groups.Count > 0)
        {
            return;
        }

        foreach (var group in Enum.GetValues<ParameterGroup>())
        {
            Groups.Add(new ParameterGroupItemViewModel(group, ParameterCatalog.GetByGroup(group).Count));
        }

        SelectedGroup = Groups.FirstOrDefault();
    }

    /// <summary>重新加载配方下拉列表。</summary>
    private void ReloadRecipes(bool selectActive)
    {
        var keepId = SelectedRecipe?.Id ?? 0;

        _isLoading = true;
        try
        {
            Recipes.Clear();
            foreach (var recipe in _recipes.GetAll())
            {
                Recipes.Add(recipe);
            }

            var active = Recipes.FirstOrDefault(recipe => recipe.IsActive);
            SelectedRecipe = selectActive
                ? active ?? Recipes.FirstOrDefault()
                : Recipes.FirstOrDefault(recipe => recipe.Id == keepId) ?? active ?? Recipes.FirstOrDefault();
        }
        finally
        {
            _isLoading = false;
        }

        if (SelectedRecipe is not null)
        {
            LoadRecipeToEditor(SelectedRecipe);
        }
    }

    private void SelectRecipeById(int id)
    {
        var target = Recipes.FirstOrDefault(recipe => recipe.Id == id);
        if (target is null)
        {
            return;
        }

        _isLoading = true;
        try
        {
            SelectedRecipe = target;
        }
        finally
        {
            _isLoading = false;
        }

        LoadRecipeToEditor(target);
    }

    /// <summary>把配方参数载入编辑区（重建参数行与路径点）。</summary>
    private void LoadRecipeToEditor(Recipe recipe)
    {
        _editingRecipe = recipe;

        foreach (var item in _allItems)
        {
            item.ValueChanged -= OnParameterValueChanged;
        }

        _allItems.Clear();
        Parameters.Clear();

        foreach (var descriptor in ParameterCatalog.All)
        {
            ParameterCatalog.TryGetValue(recipe.Parameters, descriptor.Key, out var value);
            var item = new ParameterItemViewModel(descriptor, value, isReadOnly: !CanEdit);
            item.ValueChanged += OnParameterValueChanged;
            _allItems.Add(item);
        }

        LoadTapePath(recipe.Parameters.TapePath);
        CaptureOriginals(recipe);

        IsDirty = false;
        ChangedCount = 0;
        HasValidationError = false;
        StatusText = recipe.IsActive ? "当前生产配方" : "未加载为生产配方";

        RebuildParameterRows();
        _ = ReloadChangeLogsAsync(recipe.Name);
    }

    /// <summary>按当前分组刷新右侧参数行（复用同一批参数行实例，保留已编辑的值）。</summary>
    private void RebuildParameterRows()
    {
        Parameters.Clear();

        var group = SelectedGroup?.Group ?? ParameterGroup.Process;
        foreach (var item in _allItems.Where(item => ParameterCatalog.Find(item.Key)?.Group == group))
        {
            Parameters.Add(item);
        }
    }

    private void LoadTapePath(TapePathParameters tapePath)
    {
        TapePathMode = tapePath.Mode;
        ArcCenterXText = (tapePath.ArcCenter?.X ?? 0).ToString("F2", CultureInfo.InvariantCulture);
        ArcCenterYText = (tapePath.ArcCenter?.Y ?? 0).ToString("F2", CultureInfo.InvariantCulture);
        ArcRadiusText = tapePath.ArcRadius.ToString("F2", CultureInfo.InvariantCulture);
        ArcStartText = tapePath.ArcStartDeg.ToString("F2", CultureInfo.InvariantCulture);
        ArcEndText = tapePath.ArcEndDeg.ToString("F2", CultureInfo.InvariantCulture);

        TapePathPoints.Clear();
        for (var i = 0; i < tapePath.Points.Count; i++)
        {
            AddPointRow(tapePath.Points[i], i + 1);
        }
    }

    private void AddPointRow(TapePathPoint point, int index)
    {
        var row = new TapePathPointItemViewModel(point, index, isReadOnly: !CanEdit);
        row.ValueChanged += OnParameterValueChanged;
        TapePathPoints.Add(row);
    }

    /// <summary>记录加载时的参数快照（审计日志的旧值来源）。</summary>
    private void CaptureOriginals(Recipe recipe)
    {
        _originalValues.Clear();
        foreach (var descriptor in ParameterCatalog.All)
        {
            if (ParameterCatalog.TryGetValue(recipe.Parameters, descriptor.Key, out var value))
            {
                _originalValues[descriptor.Key] = value;
            }
        }

        _originalTapePathText = DescribeTapePath(recipe.Parameters.TapePath);
    }

    /// <summary>参数或路径点被修改：刷新校验状态与未保存提示。</summary>
    private void OnParameterValueChanged(object? sender, EventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        MarkDirty();
    }

    private void MarkDirty()
    {
        if (_isLoading)
        {
            return;
        }

        HasValidationError = _allItems.Any(item => item.HasError) || TapePathPoints.Any(row => row.HasError);
        ChangedCount = CountPendingChanges();
        IsDirty = ChangedCount > 0 || HasValidationError;
    }

    /// <summary>统计与加载快照不一致的参数项数（用于「有 N 项未保存」提示）。</summary>
    private int CountPendingChanges()
    {
        var count = 0;

        foreach (var item in _allItems)
        {
            if (!_originalValues.TryGetValue(item.Key, out var original))
            {
                continue;
            }

            if (!item.TryGetValue(out var current, out _) || Math.Abs(current - original) > 1e-9)
            {
                count++;
            }
        }

        if (!string.Equals(_originalTapePathText, DescribeTapePathFromEditor(), StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>把编辑区的内容写回配方参数，返回需要写审计日志的变更项。</summary>
    private List<ParameterChange> ApplyEditsToModel(Recipe recipe)
    {
        var changes = new List<ParameterChange>();

        foreach (var item in _allItems)
        {
            if (!item.TryGetValue(out var value, out _))
            {
                continue;
            }

            _originalValues.TryGetValue(item.Key, out var original);
            if (Math.Abs(value - original) > 1e-9)
            {
                changes.Add(new ParameterChange(
                    item.Key,
                    ParameterCatalog.Format(ParameterCatalog.Find(item.Key)!, original),
                    ParameterCatalog.Format(ParameterCatalog.Find(item.Key)!, value)));
            }

            ParameterCatalog.SetValue(recipe.Parameters, item.Key, value);
        }

        var newTapePathText = DescribeTapePathFromEditor();
        if (!string.Equals(_originalTapePathText, newTapePathText, StringComparison.Ordinal))
        {
            changes.Add(new ParameterChange("tapePath", _originalTapePathText, newTapePathText));
        }

        recipe.Parameters.TapePath = BuildTapePathFromEditor();
        recipe.UpdatedBy = _auth.CurrentUser?.UserName ?? "";
        return changes;
    }

    private TapePathParameters BuildTapePathFromEditor()
    {
        var points = new List<TapePathPoint>();
        foreach (var row in TapePathPoints)
        {
            if (row.TryGetPoint(out var point))
            {
                points.Add(point);
            }
        }

        if (points.Count < 2)
        {
            // 兜底：路径点非法时保留默认两点，避免把坏数据写进库（保存前已校验，这里只是防御）
            points =
            [
                new TapePathPoint(0, 0, 2),
                new TapePathPoint(30, 0, 2)
            ];
        }

        var arcMode = TapePathMode == TapePathMode.Arc;

        return new TapePathParameters
        {
            Mode = TapePathMode,
            Points = points,
            ArcCenter = arcMode
                ? new TapePathPoint(ReadOrZero(ArcCenterXText), ReadOrZero(ArcCenterYText), 0)
                : null,
            ArcRadius = ReadOrZero(ArcRadiusText),
            ArcStartDeg = ReadOrZero(ArcStartText),
            ArcEndDeg = ReadOrZero(ArcEndText)
        };
    }

    private string DescribeTapePathFromEditor() => DescribeTapePath(BuildTapePathFromEditor());

    private static string DescribeTapePath(TapePathParameters tapePath)
        => $"{tapePath.Mode}，{tapePath.Points.Count} 点，R={tapePath.ArcRadius:F2}mm";

    private static double ReadOrZero(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>重新读取最近的操作日志里与本配方相关的参数变更记录。</summary>
    private Task ReloadChangeLogsAsync(string recipeName)
    {
        try
        {
            var now = _clock.Now;
            var prefix = recipeName + ".";

            var logs = _operationLogs
                .Query(now.AddDays(-ChangeLogLookbackDays), now)
                .Where(log => string.Equals(log.Action, "修改参数", StringComparison.Ordinal)
                              && log.Target.StartsWith(prefix, StringComparison.Ordinal))
                .Take(ChangeLogLimit)
                .ToList();

            ChangeLogs.Clear();
            foreach (var log in logs)
            {
                ChangeLogs.Add(log);
            }
        }
        catch (Exception ex)
        {
            // 变更记录只是辅助信息，读失败不影响参数编辑
            _log.LogWarning(ex, "读取参数变更记录失败：{Recipe}", recipeName);
        }

        return Task.CompletedTask;
    }

    /// <summary>一个待写审计日志的参数变更。</summary>
    /// <param name="Key">参数键名。</param>
    /// <param name="OldValue">变更前值。</param>
    /// <param name="NewValue">变更后值。</param>
    private sealed record ParameterChange(string Key, string OldValue, string NewValue);
}
