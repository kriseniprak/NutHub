using System.Globalization;
using System.Text;

namespace NutHub.FileLogging;

/// <summary>
/// The log files of one day: "nuthub-YYYYMMDD.log", continued in "nuthub-YYYYMMDD.1.log"... when a file reaches the
/// size cap. Files older than the retention are deleted when a new day starts. Not thread-safe: only the background
/// writer of <see cref="FileLoggerProvider"/> uses it.
/// </summary>
internal sealed class RollingLogFile : IDisposable
{
    public const string Prefix = "nuthub-";

    // A runaway component must not fill the disk: past this many parts in one day, lines are dropped until midnight.
    private const int MaxPartsPerDay = 20;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _retainDays;
    private readonly TimeProvider _time;
    private StreamWriter? _writer;
    private DateOnly _day;
    private int _part;
    private long _size;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private DateOnly? _fullDay;

    public RollingLogFile(string directory, long maxBytes, int retainDays, TimeProvider time)
    {
        _directory = directory;
        _maxBytes = Math.Max(64 * 1024, maxBytes);
        _retainDays = Math.Max(1, retainDays);
        _time = time;
    }

    /// <summary>The file being written, or null.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>Appends text (already terminated by a new line) stamped with <paramref name="timestamp"/>.</summary>
    /// <returns>False when the text could not be written (disk full, no permission, daily cap reached).</returns>
    public bool Write(DateTimeOffset timestamp, string text)
    {
        var day = DateOnly.FromDateTime(timestamp.DateTime);
        if (_fullDay == day)
        {
            return false;
        }

        if (_writer is null || day != _day || _size >= _maxBytes)
        {
            if (!Open(day))
            {
                return false;
            }
        }

        try
        {
            _writer!.Write(text);
            _size += Encoding.UTF8.GetByteCount(text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Close();
            _retryAfter = _time.GetUtcNow() + RetryDelay;
            return false;
        }
    }

    public void Flush()
    {
        try
        {
            _writer?.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close();
            _retryAfter = _time.GetUtcNow() + RetryDelay;
        }
    }

    public void Dispose() => Close();

    private bool Open(DateOnly day)
    {
        if (_writer is not null && day == _day)
        {
            _part++; // size cap reached
        }
        else if (day != _day)
        {
            _part = 0;
        }

        Close();
        if (_time.GetUtcNow() < _retryAfter)
        {
            return false;
        }

        bool newDay = day != _day;
        _day = day;
        try
        {
            Directory.CreateDirectory(_directory);
            if (newDay)
            {
                DeleteExpired(day);
            }

            while (true)
            {
                if (_part >= MaxPartsPerDay)
                {
                    _fullDay = day;
                    return false;
                }

                string path = PathFor(day, _part);
                var info = new FileInfo(path);
                if (info.Exists && info.Length >= _maxBytes)
                {
                    _part++; // continuing after a restart on a day whose file is already full
                    continue;
                }

                var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                                            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024);
                _size = stream.Length;
                CurrentPath = path;
                _fullDay = null;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Close();
            _retryAfter = _time.GetUtcNow() + RetryDelay;
            return false;
        }
    }

    private void Close()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Nothing sensible to do: the next write reopens the file.
        }

        _writer = null;
        CurrentPath = null;
    }

    private string PathFor(DateOnly day, int part) =>
        Path.Combine(_directory, Prefix + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                                 (part == 0 ? "" : "." + part.ToString(CultureInfo.InvariantCulture)) + ".log");

    private void DeleteExpired(DateOnly today)
    {
        DateOnly oldestKept = today.AddDays(-(_retainDays - 1));
        foreach (string file in Directory.EnumerateFiles(_directory, Prefix + "*.log"))
        {
            string name = Path.GetFileName(file);
            if (name.Length < Prefix.Length + 8 ||
                !DateOnly.TryParseExact(name.AsSpan(Prefix.Length, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out DateOnly fileDay) ||
                fileDay >= oldestKept)
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open elsewhere or protected: tried again tomorrow.
            }
        }
    }
}
