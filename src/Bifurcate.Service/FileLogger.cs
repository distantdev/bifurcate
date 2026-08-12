using System.Text;
using Bifurcate.Core;
using Microsoft.Extensions.Logging;

namespace Bifurcate.Service;

public sealed class FileLoggerOptions
{
    public string Directory { get; init; } = "";

    public long MaxFileBytes { get; init; } = 1024 * 1024;

    public int RetainDays { get; init; } = 7;
}

/// <summary>
/// Plain text log next to the config, because the Event Log is a poor place to read a sequence of
/// events and a service has no console to write to.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLoggerOptions _options;
    private readonly Lock _gate = new();

    public FileLoggerProvider(FileLoggerOptions options)
    {
        _options = options;
        System.IO.Directory.CreateDirectory(_options.Directory);
        PruneOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        string line = string.Create(null, stackalloc char[256],
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {Abbreviate(level)}  {ShortCategory(category)}  {message}");

        StringBuilder text = new(line);
        if (exception is not null) { text.Append("  ").Append(exception.GetType().Name).Append(": ").Append(exception.Message); }
        text.AppendLine();

        lock (_gate)
        {
            try
            {
                string path = CurrentPath();
                RollIfTooLarge(path);
                File.AppendAllText(path, text.ToString());
            }
            catch (IOException)
            {
                // Losing a log line must never take the service down.
            }
        }
    }

    private string CurrentPath() =>
        Path.Combine(_options.Directory, $"{BifurcateInfo.ProductName.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd}.log");

    private void RollIfTooLarge(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length <= _options.MaxFileBytes) { return; }

        File.Move(path, path + ".old", overwrite: true);
    }

    private void PruneOldFiles()
    {
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-_options.RetainDays);
            foreach (string path in System.IO.Directory.EnumerateFiles(_options.Directory, "*.log*"))
            {
                if (File.GetLastWriteTime(path) < cutoff) { File.Delete(path); }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        LogLevel.None => "OFF",
        _ => "???",
    };

    private static string ShortCategory(string category)
    {
        int lastDot = category.LastIndexOf('.');
        return lastDot < 0 ? category : category[(lastDot + 1)..];
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) { return; }

            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddBifurcateFile(this ILoggingBuilder builder, string directory)
    {
        builder.AddProvider(new FileLoggerProvider(new FileLoggerOptions { Directory = directory }));
        return builder;
    }
}
