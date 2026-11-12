using Microsoft.Extensions.Configuration;

namespace Clc.Configuration;

/// <summary>Loads a validated startup snapshot without creating or starting an application host.</summary>
public static class ClcConfigurationReader
{
    /// <summary>
    /// Loads the conventional ProgramData configuration file for an application.
    /// </summary>
    /// <param name="applicationDirectoryName">One portable directory name beneath the operating-system common application data directory.</param>
    /// <returns>An in-memory configuration snapshot.</returns>
    public static IConfiguration Load(string applicationDirectoryName)
    {
        var path = ExternalConfigurationLoader.ResolvePath(applicationDirectoryName);
        return LoadFromFile(applicationDirectoryName, path);
    }

    /// <summary>
    /// Loads and validates an explicitly selected file as an in-memory snapshot.
    /// Use this hostless overload for deployment validation and parser tests. Normal startup
    /// continues to use <see cref="ClcConfigurationBuilderExtensions.AddClcConfiguration"/>.
    /// </summary>
    /// <param name="applicationDirectoryName">One portable application directory name, validated using the startup convention.</param>
    /// <param name="filePath">The absolute path to the JSON file.</param>
    /// <returns>An in-memory configuration snapshot.</returns>
    public static IConfiguration LoadFromFile(string applicationDirectoryName, string filePath) =>
        ExternalConfigurationLoader.LoadFromFile(applicationDirectoryName, filePath);
}
