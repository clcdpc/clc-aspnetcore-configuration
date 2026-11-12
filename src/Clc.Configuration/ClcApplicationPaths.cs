namespace Clc.Configuration;

/// <summary>The conventional application data and configuration paths on this machine.</summary>
public sealed class ClcApplicationPaths
{
    private static readonly HashSet<string> WindowsReservedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"
    };

    private ClcApplicationPaths(string applicationDataDirectory)
    {
        ApplicationDataDirectory = applicationDataDirectory;
        ConfigurationDirectory = Path.Combine(applicationDataDirectory, "Config");
        ConfigurationFilePath = Path.Combine(ConfigurationDirectory, "application.json");
    }

    /// <summary>The application's data directory beneath OS CommonApplicationData, not its executable directory.</summary>
    public string ApplicationDataDirectory { get; }

    /// <summary>The application's conventional Config directory.</summary>
    public string ConfigurationDirectory { get; }

    /// <summary>The application's conventional application.json file path.</summary>
    public string ConfigurationFilePath { get; }

    /// <summary>Resolves absolute paths without reading configuration or creating files or directories.</summary>
    /// <param name="applicationDirectoryName">One portable directory name beneath OS CommonApplicationData.</param>
    /// <remarks>Resolution does not check existence, access permissions, or deployment readiness.</remarks>
    public static ClcApplicationPaths Resolve(string applicationDirectoryName)
    {
        ValidateApplicationDirectoryName(applicationDirectoryName);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData) || !Path.IsPathFullyQualified(programData))
        {
            throw new InvalidOperationException("The operating-system ProgramData directory is unavailable.");
        }

        return Resolve(applicationDirectoryName, programData);
    }

    // For isolated path tests only; production has no root override.
    internal static ClcApplicationPaths Resolve(string applicationDirectoryName, string programDataRoot)
    {
        ValidateApplicationDirectoryName(applicationDirectoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataRoot);
        if (!Path.IsPathFullyQualified(programDataRoot))
        {
            throw new ArgumentException("The ProgramData root must be an absolute path.", nameof(programDataRoot));
        }

        return new ClcApplicationPaths(Path.GetFullPath(Path.Combine(programDataRoot, applicationDirectoryName)));
    }

    internal static void ValidateApplicationDirectoryName(string applicationDirectoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectoryName);

        var extensionSeparator = applicationDirectoryName.IndexOf('.');
        var deviceName = extensionSeparator >= 0
            ? applicationDirectoryName[..extensionSeparator]
            : applicationDirectoryName;
        var isNumberedDeviceName = deviceName.Length == 4 &&
            (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            deviceName[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';

        if (applicationDirectoryName is "." or ".." ||
            Path.IsPathRooted(applicationDirectoryName) ||
            applicationDirectoryName.Contains('/') ||
            applicationDirectoryName.Contains('\\') ||
            applicationDirectoryName.EndsWith(' ') ||
            applicationDirectoryName.EndsWith('.') ||
            applicationDirectoryName.Any(char.IsControl) ||
            applicationDirectoryName.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 ||
            WindowsReservedDirectoryNames.Contains(deviceName) ||
            isNumberedDeviceName)
        {
            throw new ArgumentException(
                "The application directory name must be one portable directory name, not a path.",
                nameof(applicationDirectoryName));
        }
    }

}
