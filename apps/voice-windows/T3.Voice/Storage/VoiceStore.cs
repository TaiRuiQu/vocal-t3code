using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace T3.Voice;

public sealed class VoiceStore : IDisposable
{
    private readonly object gate = new();
    private IntPtr database;
    public string DataDirectory { get; }
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public VoiceStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        var result = Sqlite.Open(Path.Combine(DataDirectory, "voice.sqlite3"), out database, 0x10006, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException("无法打开语音助手数据库：" + Error());
        Sqlite.BusyTimeout(database, 5_000);
        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA synchronous=FULL");
        Execute("CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, body TEXT NOT NULL)");
        Execute("CREATE TABLE IF NOT EXISTS messages (id INTEGER PRIMARY KEY AUTOINCREMENT, role TEXT NOT NULL, text TEXT NOT NULL, created_at TEXT NOT NULL)");
        Execute("CREATE TABLE IF NOT EXISTS inbox (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, text TEXT NOT NULL, dedup TEXT NOT NULL UNIQUE, processed INTEGER NOT NULL DEFAULT 0)");
        Execute("CREATE TABLE IF NOT EXISTS tasks (id TEXT PRIMARY KEY, body TEXT NOT NULL)");
    }

    public AppSettings LoadSettings() => Get<AppSettings>("settings") ?? new();
    public void SaveSettings(AppSettings settings) => Put("settings", settings);
    public T? Get<T>(string key)
    {
        var rows = Query("SELECT body FROM kv WHERE key = ?", key);
        return rows.Count == 0 ? default : JsonSerializer.Deserialize<T>(rows[0][0], Json);
    }
    public void Put<T>(string key, T value) => Execute("INSERT INTO kv(key,body) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET body=excluded.body", key, JsonSerializer.Serialize(value, Json));
    public void Delete(string key) => Execute("DELETE FROM kv WHERE key = ?", key);
    public IReadOnlyList<KeyValuePair<string, T>> List<T>(string prefix) => Query("SELECT key,body FROM kv WHERE substr(key,1,length(?))=? ORDER BY key", prefix, prefix)
        .Select(row => new KeyValuePair<string, T>(row[0], JsonSerializer.Deserialize<T>(row[1], Json)!)).ToArray();

    public string? GetSecret(string key)
    {
        var encrypted = Get<string>("secret:" + key);
        return encrypted is null ? null : Encoding.UTF8.GetString(WindowsSecret.Unprotect(Convert.FromBase64String(encrypted)));
    }
    public void SetSecret(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) Delete("secret:" + key);
        else Put("secret:" + key, Convert.ToBase64String(WindowsSecret.Protect(Encoding.UTF8.GetBytes(value))));
    }

    public void AddMessage(string role, string text) => Execute("INSERT INTO messages(role,text,created_at) VALUES(?,?,?)", role, text, DateTimeOffset.UtcNow.ToString("O"));
    public UserInput SubmitInput(string text)
    {
        lock (gate)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                var pending = List<string>("question:").FirstOrDefault();
                AddMessage("user", text.Trim());
                var through = RecentMessages(1).Single().Id;
                var input = new UserInput(text.Trim(), pending.Key, pending.Value, through);
                Enqueue("user", JsonSerializer.Serialize(input, Json), "voice:" + Guid.NewGuid().ToString("N"));
                Execute("COMMIT");
                return input;
            }
            catch { Execute("ROLLBACK"); throw; }
        }
    }
    public IReadOnlyList<ChatMessage> RecentMessages(int limit = 60) => Query("SELECT id,role,text,created_at FROM (SELECT * FROM messages ORDER BY id DESC LIMIT ?) ORDER BY id", limit.ToString())
        .Select(row => new ChatMessage(long.Parse(row[0]), row[1], row[2], row[3])).ToArray();

    public bool Enqueue(string kind, string text, string deduplicationKey)
    {
        lock (gate)
        {
            Execute("INSERT OR IGNORE INTO inbox(kind,text,dedup) VALUES(?,?,?)", kind, text, deduplicationKey);
            return Sqlite.Changes(database) != 0;
        }
    }
    public IReadOnlyList<InboxMessage> PendingInbox() => Query("SELECT id,kind,text,dedup FROM inbox WHERE processed=0 ORDER BY id LIMIT 50")
        .Select(row => new InboxMessage(long.Parse(row[0]), row[1], row[2], row[3])).ToArray();
    public void CompleteInbox(long id) => Execute("UPDATE inbox SET processed=1 WHERE id=?", id.ToString());
    public void CompleteTurn(long inboxId, string provider, string? sessionId, string text)
    {
        lock (gate)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                if (sessionId is not null) Put("session:" + provider, sessionId);
                if (!string.IsNullOrWhiteSpace(text)) AddMessage("assistant", text);
                CompleteInbox(inboxId);
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
        }
    }
    public void SaveTask(RemoteTask task) => Execute("INSERT INTO tasks(id,body) VALUES(?,?) ON CONFLICT(id) DO UPDATE SET body=excluded.body", task.Id, JsonSerializer.Serialize(task, Json));
    public IReadOnlyList<RemoteTask> Tasks() => Query("SELECT body FROM tasks ORDER BY rowid DESC").Select(row => JsonSerializer.Deserialize<RemoteTask>(row[0], Json)!).ToArray();

    public void ObserveTask(RemoteTask task, string? eventText, string eventKey)
    {
        lock (gate)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                SaveTask(task);
                if (eventText is not null) Enqueue("remote", eventText, eventKey);
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
        }
    }

    public bool TryObserveTask(RemoteTask expected, RemoteTask observed, string? eventText, string eventKey)
    {
        lock (gate)
        {
            var current = Tasks().FirstOrDefault(task => task.Id == expected.Id);
            if (current is null || current.RunId != expected.RunId || current.Fingerprint != expected.Fingerprint || current.UpdatedAt != expected.UpdatedAt) return false;
            ObserveTask(observed, eventText, eventKey);
            return true;
        }
    }

    private string Error() => database == IntPtr.Zero ? "SQLite 未初始化" : Marshal.PtrToStringUTF8(Sqlite.ErrorMessage(database)) ?? "SQLite 错误";

    private void Execute(string sql, params string[] values)
    {
        lock (gate)
        {
            using var statement = Prepare(sql, values);
            var result = Sqlite.Step(statement.Handle);
            if (result is not 100 and not 101) throw new InvalidOperationException(Error());
        }
    }

    private List<string[]> Query(string sql, params string[] values)
    {
        lock (gate)
        {
            using var statement = Prepare(sql, values);
            var rows = new List<string[]>();
            int result;
            while ((result = Sqlite.Step(statement.Handle)) == 100)
            {
                var row = new string[Sqlite.ColumnCount(statement.Handle)];
                for (var index = 0; index < row.Length; index++) row[index] = Marshal.PtrToStringUTF8(Sqlite.ColumnText(statement.Handle, index)) ?? "";
                rows.Add(row);
            }
            if (result != 101) throw new InvalidOperationException(Error());
            return rows;
        }
    }

    private Statement Prepare(string sql, string[] values)
    {
        ObjectDisposedException.ThrowIf(database == IntPtr.Zero, this);
        if (Sqlite.Prepare(database, sql, -1, out var handle, IntPtr.Zero) != 0) throw new InvalidOperationException(Error());
        var statement = new Statement(handle);
        for (var index = 0; index < values.Length; index++)
        {
            if (Sqlite.BindText(handle, index + 1, values[index], -1, new IntPtr(-1)) == 0) continue;
            statement.Dispose();
            throw new InvalidOperationException(Error());
        }
        return statement;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (database == IntPtr.Zero) return;
            Sqlite.Close(database);
            database = IntPtr.Zero;
        }
    }

    private sealed class Statement(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public void Dispose() => Sqlite.Finalize(Handle);
    }

    private static class Sqlite
    {
        [DllImport("winsqlite3", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database, int flags, IntPtr vfs);
        [DllImport("winsqlite3", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Prepare(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3", EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int BindText(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, IntPtr destructor);
        [DllImport("winsqlite3", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)] internal static extern int Step(IntPtr statement);
        [DllImport("winsqlite3", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)] internal static extern int Finalize(IntPtr statement);
        [DllImport("winsqlite3", EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)] internal static extern int Close(IntPtr database);
        [DllImport("winsqlite3", EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ErrorMessage(IntPtr database);
        [DllImport("winsqlite3", EntryPoint = "sqlite3_column_count", CallingConvention = CallingConvention.Cdecl)] internal static extern int ColumnCount(IntPtr statement);
        [DllImport("winsqlite3", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ColumnText(IntPtr statement, int column);
        [DllImport("winsqlite3", EntryPoint = "sqlite3_changes", CallingConvention = CallingConvention.Cdecl)] internal static extern int Changes(IntPtr database);
        [DllImport("winsqlite3", EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)] internal static extern int BusyTimeout(IntPtr database, int milliseconds);
    }
}

internal static class WindowsSecret
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    public static byte[] Protect(byte[] bytes) => Transform(bytes, true);
    public static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect
                ? CryptProtectData(ref input, "T3 Voice", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取此 Windows 用户的凭据");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            Marshal.FreeHGlobal(input.Data);
        }
    }
    [DllImport("crypt32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32")] private static extern IntPtr LocalFree(IntPtr memory);
}
