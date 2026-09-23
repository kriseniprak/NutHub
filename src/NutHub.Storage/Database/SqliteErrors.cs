using Microsoft.Data.Sqlite;

namespace NutHub.Storage.Database;

/// <summary>Classifies SQLite failures so the database can tell a damaged file from an ordinary error.</summary>
internal static class SqliteErrors
{
    // Primary result codes from sqlite3.h.
    public const int Interrupt = 9;
    public const int Corrupt = 11;
    public const int NotADatabase = 26;

    /// <summary>
    /// True when the file itself is unusable (damaged pages, a header that is not SQLite's). Only these justify moving
    /// the file aside; I/O errors, a full disk or a locked file are transient and must not cost the user their history.
    /// </summary>
    public static bool IsCorruption(Exception ex) =>
        ex is SqliteException sqlite && (sqlite.SqliteErrorCode & 0xFF) is Corrupt or NotADatabase;

    public static bool IsInterrupt(Exception ex) =>
        ex is SqliteException sqlite && (sqlite.SqliteErrorCode & 0xFF) == Interrupt;
}

/// <summary>The file was written by a newer NutHub whose schema this version does not know.</summary>
internal sealed class DatabaseTooNewException(int version, int supported)
    : Exception($"The database has schema version {version}; this version of NutHub supports up to {supported}.")
{
    public int Version { get; } = version;
}
