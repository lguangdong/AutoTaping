// 职责：8 个页面 ViewModel 的公共基类（页面标识、导航标题、图标、进入权限、进入/离开生命周期、跳转请求）
// 层：App/ViewModels
// 依赖：PageKey（App/Navigation）、Permission（Core/Models）、CommunityToolkit.Mvvm

using AutoTaping.App.Navigation;
using AutoTaping.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 页面 ViewModel 基类。主窗口壳只依赖本基类：用 <see cref="Key"/> 排序导航、
/// 用 <see cref="RequiredPermission"/> 决定导航是否可点，因此新增页面无需改 ShellViewModel。
/// <para>
/// 生命周期：<see cref="OnEnteringAsync"/>（可拒绝进入，如手动页的二次确认）→
/// <see cref="OnLeaveAsync"/>（清理点动、强制输出等临时状态）。两者都由 ShellViewModel 在切页时调用，
/// 页面自己不得直接切页（通过 <see cref="RequestNavigation"/> 发请求）。
/// </para>
/// </summary>
public abstract class PageViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    /// <summary>页面唯一标识（同时决定导航顺序）。</summary>
    public abstract PageKey Key { get; }

    /// <summary>导航与页面标题，用 00-项目总览术语表的用词。</summary>
    public abstract string Title { get; }

    /// <summary>导航图标（07-UI设计规范 第 5.2 节）。</summary>
    public abstract string Icon { get; }

    /// <summary>进入本页所需权限；<see cref="Permission.None"/> 表示全部角色可进。</summary>
    public abstract Permission RequiredPermission { get; }

    /// <summary>页面请求跳转到别的页面（如手动页的「退出手动」）。由 ShellViewModel 统一执行切页。</summary>
    public event EventHandler<PageKey>? NavigationRequested;

    /// <summary>
    /// 准备进入本页。返回 <see langword="false"/> 表示用户取消或权限不足，主窗口保持当前页
    /// （08-页面清单 页面 2：进入手动页必须弹确认框，默认「取消」）。
    /// </summary>
    /// <param name="ct">取消令牌（应用退出时取消）。</param>
    public virtual Task<bool> OnEnteringAsync(CancellationToken ct) => Task.FromResult(true);

    /// <summary>离开本页前的清理（停点动、清强制输出、抬 Z 轴等）。异常不得阻止切页，由实现自行记录。</summary>
    /// <param name="ct">取消令牌（应用退出时取消）。</param>
    public virtual Task OnLeaveAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>释放定时器与订阅（10-编码约定 五：页面卸载时必须释放）。幂等。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    /// <summary>子类释放资源（停刷新循环、取消令牌等）。</summary>
    protected virtual void DisposeCore()
    {
    }

    /// <summary>请求切页（子类调用）。</summary>
    /// <param name="target">目标页面。</param>
    protected void RequestNavigation(PageKey target) => NavigationRequested?.Invoke(this, target);
}
