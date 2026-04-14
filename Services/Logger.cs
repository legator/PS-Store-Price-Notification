using Spectre.Console;

namespace PSPriceNotification.Services;

public static class Logger
{
    private static string _logFile = "data/ps_price_check.log";
    private static LogLevel _minLevel = LogLevel.Info;
    private static readonly object _lock = new();

    public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

    public static void Configure(string logFile, string level)
    {
        _logFile = logFile;
        Directory.CreateDirectory(Path.GetDirectoryName(logFile) ?? "data");
        _minLevel = level.ToUpperInvariant() switch
        {
            "DEBUG" => LogLevel.Debug,
            "WARNING" or "WARN" => LogLevel.Warn,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Info,
        };
    }

    public static void Info(string msg)  => Write(LogLevel.Info,  msg);
    public static void Warn(string msg)  => Write(LogLevel.Warn,  msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);
    public static void Debug(string msg) => Write(LogLevel.Debug, msg);

    private static void Write(LogLevel level, string msg)
    {
        if (level < _minLevel) return;

        // Markup colour + label for the console
        var (colour, label) = level switch
        {
            LogLevel.Debug => ("grey",    "DEBUG"),
            LogLevel.Info  => ("white",   "INFO "),
            LogLevel.Warn  => ("yellow",  "WARN "),
            LogLevel.Error => ("red",     "ERROR"),
            _              => ("white",   "     "),
        };

        var timestamp = $"[grey]{DateTime.Now:HH:mm:ss}[/]";
        var levelTag  = $"[{colour}]{label}[/]";

        var safeMsgEscaped = Markup.Escape(msg);

        var colouredMsg = level switch
        {
            LogLevel.Warn  => $"[yellow]{safeMsgEscaped}[/]",
            LogLevel.Error => $"[red]{safeMsgEscaped}[/]",
            LogLevel.Debug => $"[grey]{safeMsgEscaped}[/]",
            _              => safeMsgEscaped,
        };

        var plainLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {label}  {msg}";

        lock (_lock)
        {
            AnsiConsole.MarkupLine($"{timestamp}  {levelTag}  {colouredMsg}");
            try { File.AppendAllText(_logFile, plainLine + Environment.NewLine, System.Text.Encoding.UTF8); }
            catch { /* don't crash if log file is unavailable */ }
        }
    }
}

