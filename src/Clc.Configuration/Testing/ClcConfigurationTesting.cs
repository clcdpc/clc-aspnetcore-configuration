using Microsoft.Extensions.Configuration;

namespace Clc.Configuration.Testing;

/// <summary>Explicit test helpers for replacing providers and creating isolated JSON fixtures.</summary>
public static class ClcConfigurationTesting
{
    /// <summary>
    /// Replaces a configuration builder's providers with isolated in-memory test settings while
    /// retaining the hosting listener controls used by the normal configuration extension.
    /// </summary>
    /// <remarks>
    /// This method is intended for consumer test hosts, including callbacks passed to
    /// <c>WebApplicationFactory.ConfigureWebHost</c>. It does not change process-wide state.
    /// </remarks>
    public static IConfigurationBuilder AddClcConfigurationForTesting(
        this IConfigurationBuilder builder,
        IReadOnlyDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);

        var snapshot = CopySettings(settings);
        ExternalConfigurationLoader.ReplaceConfiguration(builder, snapshot);
        return builder;
    }

    private static Dictionary<string, string?> CopySettings(IReadOnlyDictionary<string, string?> settings)
    {
        var snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings)
        {
            if (!snapshot.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    "Test configuration contains keys that are ambiguous under case-insensitive configuration lookup.",
                    nameof(settings));
            }
        }

        return snapshot;
    }

    /// <summary>Creates a uniquely named, disposable JSON file for parser and validation tests.</summary>
    public static ClcConfigurationTestFile CreateJsonFile(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return CreateJsonFile(System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Creates a uniquely named, disposable file from encoded JSON bytes.</summary>
    public static ClcConfigurationTestFile CreateJsonFile(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var directory = Directory.CreateTempSubdirectory("clc-configuration-test-").FullName;
        var path = Path.Combine(directory, "application.json");
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(content);
            return new ClcConfigurationTestFile(directory, path);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }
}

/// <summary>A temporary configuration file that removes its isolated directory when disposed.</summary>
public sealed class ClcConfigurationTestFile : IDisposable
{
    private string? _directory;

    internal ClcConfigurationTestFile(string directory, string filePath)
    {
        _directory = directory;
        FilePath = filePath;
    }

    /// <summary>The absolute path to the temporary <c>application.json</c> file.</summary>
    public string FilePath { get; }

    /// <summary>Deletes the file and its unique temporary directory. Repeated calls are safe.</summary>
    public void Dispose()
    {
        var directory = Interlocked.Exchange(ref _directory, null);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
