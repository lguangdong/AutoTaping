// 职责：登录框的 ViewModel——用户名/口令输入、登录校验、失败原因提示
// 层：App/ViewModels
// 依赖：IAuthService、CommunityToolkit.Mvvm

using AutoTaping.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoTaping.App.ViewModels;

/// <summary>
/// 登录框 ViewModel。口令校验全部交给 <see cref="IAuthService"/>，本类只负责输入、错误提示与通知关闭窗口。
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly IAuthService _auth;
    private readonly ILogger<LoginViewModel> _log;

    /// <summary>登录名（首次显示时预填本次运行最后一次登录成功的用户名）。</summary>
    [ObservableProperty]
    private string _userName;

    /// <summary>明文口令（只存在于内存，登录后立即随窗口释放）。</summary>
    [ObservableProperty]
    private string _password = "";

    /// <summary>失败原因（面向操作工的一句话，成功时为空）。</summary>
    [ObservableProperty]
    private string _errorMessage = "";

    /// <summary>构造登录 ViewModel。</summary>
    /// <param name="auth">认证服务。</param>
    /// <param name="log">日志。</param>
    public LoginViewModel(IAuthService auth, ILogger<LoginViewModel> log)
    {
        _auth = auth;
        _log = log;
        _userName = auth.LastUserName ?? "";
    }

    /// <summary>登录成功，界面据此关闭登录框（ViewModel 不直接操作窗口）。</summary>
    public event EventHandler? LoginSucceeded;

    /// <summary>
    /// 执行登录。异常在这里兜底并转成中文提示，界面不会因为登录失败而崩溃。
    /// </summary>
    [RelayCommand]
    private void Login()
    {
        try
        {
            var result = _auth.Login(UserName, Password);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            ErrorMessage = "";
            Password = "";
            LoginSucceeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "登录过程发生未处理异常：{UserName}", UserName);
            ErrorMessage = "登录失败，请检查数据库是否可用后重试。";
        }
    }
}
