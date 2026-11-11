// 职责：口令加盐哈希（SHA256 + 16 字节随机盐），禁止明文或无盐哈希（06 文档第 10 节第 3 条）
// 层：Core/Services
// 依赖：System.Security.Cryptography（BCL）

using System.Security.Cryptography;
using System.Text;

namespace AutoTaping.Core.Services;

/// <summary>
/// 口令哈希工具。算法：<c>Base64(SHA256(UTF8(saltBase64 + password)))</c>，
/// 其中 <c>salt</c> 为 16 字节随机数的 Base64 表示。
/// </summary>
public static class PasswordHasher
{
    /// <summary>盐字节数（文档规定 16 字节）。</summary>
    private const int SaltBytes = 16;

    /// <summary>生成 16 字节随机盐，返回 Base64 字符串。</summary>
    public static string CreateSalt()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));

    /// <summary>计算口令哈希，返回 Base64 字符串。</summary>
    /// <param name="salt">盐（Base64）。</param>
    /// <param name="password">明文口令。</param>
    public static string Hash(string salt, string password)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(password);

        var raw = Encoding.UTF8.GetBytes(salt + password);
        return Convert.ToBase64String(SHA256.HashData(raw));
    }

    /// <summary>校验口令是否匹配（定长时间比较，避免时序侧信道）。</summary>
    /// <param name="salt">盐（Base64）。</param>
    /// <param name="password">待校验的明文口令。</param>
    /// <param name="expectedHash">数据库中保存的哈希（Base64）。</param>
    public static bool Verify(string salt, string password, string expectedHash)
    {
        if (string.IsNullOrEmpty(expectedHash))
        {
            return false;
        }

        var actual = Hash(salt, password);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual),
            Encoding.UTF8.GetBytes(expectedHash));
    }
}
