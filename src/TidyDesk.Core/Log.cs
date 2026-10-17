namespace TidyDesk.Core;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>Simple daily log files: logs\TidyDesk-yyyy-MM-dd.log</summary>
public sealed class FileLog : ILog
{
    private readonly string _dir;
    private readonly object _gate = new();

    public FileLog(string dir, int retentionDays = 30)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        Cleanup(retentionDays);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                File.AppendAllText(Path.Combine(_dir, $"{AppPaths.AppName}-{now:yyyy-MM-dd}.log"), line);
            }
            catch (IOException)
            {
                // Failing to write a log must never stop the app.
            }
        }
    }

    private void Cleanup(int retentionDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, retentionDays));
            foreach (var file in Directory.EnumerateFiles(_dir, $"{AppPaths.AppName}-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file); // only the app's own old log files
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
