namespace Shima.Core.Contracts;

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public interface ILogger
{
    void Log(LogLevel level, string message, Exception? exception = null);

    void Debug(string message) => Log(LogLevel.Debug, message);

    void Info(string message) => Log(LogLevel.Information, message);

    void Warn(string message, Exception? exception = null) => Log(LogLevel.Warning, message, exception);

    void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);

    void Critical(string message, Exception? exception = null) => Log(LogLevel.Critical, message, exception);
}
