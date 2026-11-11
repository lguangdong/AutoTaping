// 职责：用户仓储的 SQLite + Dapper 实现（登录取用户、写最后登录时间）
// 层：Data/Repositories
// 依赖：SqliteConnectionFactory、IUserRepository、Dapper

using AutoTaping.Core.Abstractions;
using AutoTaping.Core.Models;
using Dapper;

namespace AutoTaping.Data.Repositories;

/// <summary>
/// 用户仓储实现。表结构见 <c>User</c>（06-数据模型与数据库 第 2 节）。
/// 口令只以盐 + 哈希保存，本类不接触明文口令。
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private const string SelectColumns = """
        Id, UserName, PasswordHash, Salt, Role, IsEnabled, CreatedAt, LastLoginAt
        """;

    private readonly SqliteConnectionFactory _connectionFactory;

    /// <summary>构造用户仓储。</summary>
    /// <param name="connectionFactory">连接工厂。</param>
    public UserRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public User? GetByUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        using var connection = _connectionFactory.Create();

        // 登录名大小写不敏感：现场键盘输入习惯不一致，登录名不是口令，不需要区分大小写
        var row = connection.QuerySingleOrDefault<UserRow>(
            $"""
            SELECT {SelectColumns} FROM User
            WHERE UserName = @UserName COLLATE NOCASE
            LIMIT 1;
            """,
            new { UserName = userName.Trim() });

        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public void UpdateLastLogin(int userId, DateTime time)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        connection.Execute(
            "UPDATE User SET LastLoginAt = @LastLoginAt WHERE Id = @Id;",
            new { Id = userId, LastLoginAt = SqliteTime.ToDb(time) },
            transaction);

        transaction.Commit();
    }

    /// <inheritdoc />
    public IReadOnlyList<User> GetAll()
    {
        using var connection = _connectionFactory.Create();

        // 角色倒序（工程师在前）+ 用户名升序：用户管理列表按权限高低排列，便于工程师先看到关键账户
        var rows = connection.Query<UserRow>(
            $"""
            SELECT {SelectColumns} FROM User
            ORDER BY Role DESC, UserName ASC;
            """);

        return rows.Select(Map).ToList();
    }

    /// <inheritdoc />
    public User? GetById(int userId)
    {
        using var connection = _connectionFactory.Create();

        var row = connection.QuerySingleOrDefault<UserRow>(
            $"""
            SELECT {SelectColumns} FROM User
            WHERE Id = @Id
            LIMIT 1;
            """,
            new { Id = userId });

        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public bool ExistsUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return false;
        }

        using var connection = _connectionFactory.Create();

        return connection.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM User WHERE UserName = @UserName COLLATE NOCASE;",
            new { UserName = userName.Trim() }) > 0;
    }

    /// <inheritdoc />
    public int Insert(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        connection.Execute(
            """
            INSERT INTO User (UserName, PasswordHash, Salt, Role, IsEnabled, CreatedAt, LastLoginAt)
            VALUES (@UserName, @PasswordHash, @Salt, @Role, @IsEnabled, @CreatedAt, @LastLoginAt);
            """,
            new
            {
                UserName = user.UserName,
                PasswordHash = user.PasswordHash,
                Salt = user.Salt,
                Role = (int)user.Role,
                IsEnabled = user.IsEnabled ? 1 : 0,
                CreatedAt = SqliteTime.ToDb(user.CreatedAt),
                LastLoginAt = SqliteTime.ToDb(user.LastLoginAt)
            },
            transaction);

        var id = (int)connection.ExecuteScalar<long>("SELECT last_insert_rowid();", transaction: transaction);
        transaction.Commit();

        return id;
    }

    /// <inheritdoc />
    public void Update(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        // 登录名是审计与操作日志的关联键，一旦创建不可修改，因此不在更新列里
        var affected = connection.Execute(
            """
            UPDATE User
            SET PasswordHash = @PasswordHash,
                Salt         = @Salt,
                Role         = @Role,
                IsEnabled    = @IsEnabled
            WHERE Id = @Id;
            """,
            new
            {
                Id = user.Id,
                PasswordHash = user.PasswordHash,
                Salt = user.Salt,
                Role = (int)user.Role,
                IsEnabled = user.IsEnabled ? 1 : 0
            },
            transaction);

        transaction.Commit();

        if (affected == 0)
        {
            // 与 IAlarmRepository.Ack 的处理一致：记录不存在属于并发下的正常情况，由上层转成提示
            throw new KeyNotFoundException($"用户 Id={user.Id} 不存在，无法更新");
        }
    }

    /// <inheritdoc />
    public void Delete(int userId)
    {
        using var connection = _connectionFactory.Create();
        using var transaction = connection.BeginTransaction();

        connection.Execute("DELETE FROM User WHERE Id = @Id;", new { Id = userId }, transaction);
        transaction.Commit();
    }

    private static User Map(UserRow row) => new()
    {
        Id = row.Id,
        UserName = row.UserName,
        PasswordHash = row.PasswordHash,
        Salt = row.Salt,
        Role = (UserRole)row.Role,
        IsEnabled = row.IsEnabled != 0,
        CreatedAt = SqliteTime.FromDb(row.CreatedAt),
        LastLoginAt = string.IsNullOrEmpty(row.LastLoginAt) ? null : SqliteTime.FromDb(row.LastLoginAt)
    };

    /// <summary>User 表的一行（时间列为 TEXT，需显式转换）。</summary>
    private sealed class UserRow
    {
        public int Id { get; set; }
        public string UserName { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string Salt { get; set; } = "";
        public int Role { get; set; }
        public long IsEnabled { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? LastLoginAt { get; set; }
    }
}
