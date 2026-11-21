using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shima.Core.Contracts;

namespace Shima.Infrastructure.Storage;

public sealed class SanitizedFileLogger : ILogger
{
    private static readonly Regex EmailPattern = new(
        @"[\w.+-]+@[\w-]+\.[\w.-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _logFilePath;
    private readonly LogLevel _minimumLevel;
    private readonly object _sync = new();

    public SanitizedFileLogger(string logFilePath, LogLevel minimumLevel = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        _logFilePath = Path.GetFullPath(logFilePath);
        _minimumLevel = minimumLevel;
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (level < _minimumLevel)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append(" [")
            .Append(level)
            .Append("] ")
            .Append(Sanitize(message));

        if (exception is not null)
        {
            builder.AppendLine().Append(Sanitize(exception.ToString()));
        }

        builder.AppendLine();

        lock (_sync)
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(_logFilePath, builder.ToString(), Utf8NoBom);
        }
    }

    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            text = text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            var forwardSlashProfile = userProfile.Replace('\\', '/');
            text = text.Replace(forwardSlashProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return EmailPattern.Replace(text, "[redacted-email]");
    }
}
