// 职责：集中存放建表 SQL（与 docs/06-数据模型与数据库 第 2 节逐字对齐，含索引与 WAL）
// 层：Data
// 依赖：无

namespace AutoTaping.Data;

/// <summary>
/// 数据库结构定义。修改表结构必须同步更新 <c>docs/06-数据模型与数据库.md</c>。
/// 全部语句带 <c>IF NOT EXISTS</c>，可重复执行。
/// </summary>
internal static class SqliteSchema
{
    /// <summary>建表与建索引语句（9 张表：06 文档的 8 张 + S7 新增的停机记录表）。</summary>
    internal const string CreateAll = """
        PRAGMA journal_mode = WAL;

        -- ============ 配方 ============
        CREATE TABLE IF NOT EXISTS Recipe (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            Name          TEXT    NOT NULL,
            ProductCode   TEXT    NOT NULL DEFAULT '',
            TapeSpec      TEXT    NOT NULL DEFAULT '',
            JsonData      TEXT    NOT NULL,
            Version       INTEGER NOT NULL DEFAULT 1,
            IsActive      INTEGER NOT NULL DEFAULT 0,
            IsDeleted     INTEGER NOT NULL DEFAULT 0,
            CreatedAt     TEXT    NOT NULL,
            CreatedBy     TEXT    NOT NULL DEFAULT '',
            UpdatedAt     TEXT    NOT NULL,
            UpdatedBy     TEXT    NOT NULL DEFAULT ''
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_Recipe_Name ON Recipe(Name) WHERE IsDeleted = 0;

        -- ============ 报警 ============
        CREATE TABLE IF NOT EXISTS AlarmRecord (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Code        TEXT    NOT NULL,
            Message     TEXT    NOT NULL,
            Level       INTEGER NOT NULL,
            Source      TEXT    NOT NULL DEFAULT '',
            StartTime   TEXT    NOT NULL,
            AckTime     TEXT,
            AckUser     TEXT,
            EndTime     TEXT,
            IsAcked     INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS IX_Alarm_Time ON AlarmRecord(StartTime DESC);
        CREATE INDEX IF NOT EXISTS IX_Alarm_Acked ON AlarmRecord(IsAcked, Level);

        -- ============ 产出记录（每件一条，追溯核心）============
        CREATE TABLE IF NOT EXISTS OutputRecord (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            SequenceNo    INTEGER NOT NULL,
            RecipeId      INTEGER NOT NULL,
            RecipeName    TEXT    NOT NULL,
            Result        TEXT    NOT NULL,
            CycleTimeMs   INTEGER NOT NULL,
            DeltaX        REAL    NOT NULL DEFAULT 0,
            DeltaY        REAL    NOT NULL DEFAULT 0,
            DeltaTheta    REAL    NOT NULL DEFAULT 0,
            MatchScore1   REAL    NOT NULL DEFAULT 0,
            MatchScore2   REAL    NOT NULL DEFAULT 0,
            NgReason      TEXT,
            Shift         TEXT    NOT NULL,
            CreatedAt     TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Output_Time ON OutputRecord(CreatedAt DESC);
        CREATE INDEX IF NOT EXISTS IX_Output_Result ON OutputRecord(Result, CreatedAt DESC);

        -- ============ 操作日志 ============
        CREATE TABLE IF NOT EXISTS OperationLog (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            UserName  TEXT    NOT NULL,
            Action    TEXT    NOT NULL,
            Target    TEXT    NOT NULL DEFAULT '',
            OldValue  TEXT,
            NewValue  TEXT,
            CreatedAt TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_OpLog_Time ON OperationLog(CreatedAt DESC);

        -- ============ 标定记录 ============
        CREATE TABLE IF NOT EXISTS Calibration (
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            MatrixA     REAL NOT NULL, MatrixB REAL NOT NULL, MatrixC REAL NOT NULL,
            MatrixD     REAL NOT NULL, MatrixE REAL NOT NULL, MatrixF REAL NOT NULL,
            RmsError    REAL NOT NULL,
            PointCount  INTEGER NOT NULL,
            ImageFolder TEXT NOT NULL,
            IsActive    INTEGER NOT NULL DEFAULT 0,
            CreatedAt   TEXT NOT NULL,
            CreatedBy   TEXT NOT NULL DEFAULT ''
        );

        -- ============ 用户 ============
        CREATE TABLE IF NOT EXISTS User (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            UserName      TEXT    NOT NULL UNIQUE,
            PasswordHash  TEXT    NOT NULL,
            Salt          TEXT    NOT NULL,
            Role          INTEGER NOT NULL,
            IsEnabled     INTEGER NOT NULL DEFAULT 1,
            CreatedAt     TEXT    NOT NULL,
            LastLoginAt   TEXT
        );

        -- ============ 班次产量 ============
        CREATE TABLE IF NOT EXISTS ShiftProduction (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            ShiftDate       TEXT NOT NULL,
            ShiftName       TEXT NOT NULL,
            TotalCount      INTEGER NOT NULL DEFAULT 0,
            OkCount         INTEGER NOT NULL DEFAULT 0,
            NgCount         INTEGER NOT NULL DEFAULT 0,
            RunSeconds      INTEGER NOT NULL DEFAULT 0,
            DownSeconds     INTEGER NOT NULL DEFAULT 0,
            LastUpdateAt    TEXT NOT NULL,
            UNIQUE(ShiftDate, ShiftName)
        );

        -- ============ 停机记录（S7 新增：产量页「停机分析」，分类口径见 05 文档 7.2 节）============
        CREATE TABLE IF NOT EXISTS DowntimeRecord (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            Reason          TEXT    NOT NULL,   -- WAIT_MATERIAL / FAULT / MANUAL_PAUSE / CHANGE_RECIPE / POWER_OFF
            StartTime       TEXT    NOT NULL,
            EndTime         TEXT,               -- 未知（断电补记）为 NULL
            DurationSeconds INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS IX_Downtime_Time ON DowntimeRecord(StartTime DESC);

        -- ============ 系统状态（断电检测）============
        CREATE TABLE IF NOT EXISTS SystemState (
            Key        TEXT PRIMARY KEY,
            Value      TEXT NOT NULL,
            UpdatedAt  TEXT NOT NULL
        );
        """;
}
