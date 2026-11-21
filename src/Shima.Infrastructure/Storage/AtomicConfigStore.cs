using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shima.Infrastructure.Storage;

public interface IAtomicConfigStore<T>
{
    Task<T> LoadOrRecoverAsync(string relativePath, Func<T> defaultFactory);

    Task SaveAtomicAsync(string relativePath, T value);
}

public sealed class AtomicConfigStore<T> : IAtomicConfigStore<T>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _rootDirectory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public AtomicConfigStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public async Task<T> LoadOrRecoverAsync(string relativePath, Func<T> defaultFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(defaultFactory);

        var path = ResolvePath(relativePath);
        if (!File.Exists(path))
        {
            return defaultFactory();
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false);
            return value ?? defaultFactory();
        }
        catch (JsonException)
        {
            BackupCorruptFile(path);
            return defaultFactory();
        }
    }

    public async Task SaveAtomicAsync(string relativePath, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var path = ResolvePath(relativePath);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The configuration path must include a directory.");
        }

        Directory.CreateDirectory(directory);
        await _writeGate.WaitAsync().ConfigureAwait(false);

        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private string ResolvePath(string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(_rootDirectory, relativePath));
        var relative = Path.GetRelativePath(_rootDirectory, combined);
        if (Path.IsPathRooted(relative)
            || string.Equals(relative, "..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The relative path must stay inside the configuration root.",
                nameof(relativePath));
        }

        return combined;
    }

    private static void BackupCorruptFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The configuration path must include a directory.");
        }

        var baseName = Path.GetFileNameWithoutExtension(path);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(directory, $"{baseName}.corrupt.{timestamp}.json");

        var suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = Path.Combine(directory, $"{baseName}.corrupt.{timestamp}-{suffix++}.json");
        }

        File.Move(path, backupPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
