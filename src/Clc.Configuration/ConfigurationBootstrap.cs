using Microsoft.Extensions.Hosting;

namespace Clc.Configuration;

internal static class ConfigurationBootstrap
{
    internal static void AddFromFile(
        IHostApplicationBuilder builder,
        string applicationDirectoryName,
        string configurationPath,
        string? exampleConfigurationPath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ClcApplicationPaths.ValidateApplicationDirectoryName(applicationDirectoryName);

        if (!File.Exists(configurationPath))
        {
            var examplePath = ResolveExamplePath(builder.Environment.ContentRootPath, exampleConfigurationPath);
            CreateConfiguration(examplePath, configurationPath);
            throw Error(ClcConfigurationErrorReason.ConfigurationCreated, configurationPath);
        }

        ExternalConfigurationLoader.AddFromFile(builder, applicationDirectoryName, configurationPath,
            rejectPlaceholders: true);
    }

    private static string ResolveExamplePath(string contentRootPath, string? exampleConfigurationPath)
    {
        if (exampleConfigurationPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(exampleConfigurationPath);
            if (Path.IsPathRooted(exampleConfigurationPath) && !Path.IsPathFullyQualified(exampleConfigurationPath))
            {
                throw new ArgumentException(
                    "The example path must be fully qualified or relative to the host content root, not drive-relative.",
                    nameof(exampleConfigurationPath));
            }
        }

        return Path.GetFullPath(Path.Combine(contentRootPath,
            exampleConfigurationPath ?? Path.Combine("config", "example.json")));
    }

    private static void CreateConfiguration(string examplePath, string configurationPath)
    {
        FileStream source;
        try
        {
            source = new FileStream(examplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Error(ClcConfigurationErrorReason.ExampleFileMissing, examplePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Error(ClcConfigurationErrorReason.ExampleFileUnreadable, examplePath);
        }

        using (source)
        {
            var created = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
                // CreateNew enforces no overwrite, including an existing file that File.Exists missed.
                using var destination = new FileStream(configurationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
                source.CopyTo(destination);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (created)
                {
                    // Best-effort cleanup of our incomplete copy; never remove a pre-existing file.
                    try { File.Delete(configurationPath); }
                    catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
                }

                throw Error(ClcConfigurationErrorReason.BootstrapFailed, configurationPath);
            }
        }
    }

    private static ClcConfigurationException Error(ClcConfigurationErrorReason reason, string path) =>
        new(reason, path, null, null);
}
