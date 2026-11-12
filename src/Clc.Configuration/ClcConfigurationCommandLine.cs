namespace Clc.Configuration;

/// <summary>Opt-in, hostless command handling for applications using the CLC path convention.</summary>
public static class ClcConfigurationCommandLine
{
    private const string ApplicationDataDirectoryOption = "--clc-appdata-directory";

    /// <summary>Prints the application data directory for the standalone discovery command.</summary>
    /// <param name="args">The application's unchanged command-line arguments.</param>
    /// <param name="applicationDirectoryName">The same directory name used by normal startup.</param>
    /// <param name="exitCode">Zero for success or an unhandled command; 1 for resolution failure; 2 for invalid usage.</param>
    /// <param name="standardOutput">The output writer, or Console.Out when omitted.</param>
    /// <param name="standardError">The error writer, or Console.Error when omitted.</param>
    /// <returns>True when the discovery command was handled, including failures; otherwise false.</returns>
    /// <remarks>
    /// Call before logging, host construction, or other startup work, and return exitCode when true.
    /// The library never exits the process. Unrelated arguments are neither changed nor validated.
    /// </remarks>
    public static bool TryHandle(
        string[] args,
        string applicationDirectoryName,
        out int exitCode,
        TextWriter? standardOutput = null,
        TextWriter? standardError = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        exitCode = 0;
        if (!args.Any(argument =>
            string.Equals(argument, ApplicationDataDirectoryOption, StringComparison.Ordinal) ||
            (argument is not null &&
                (argument.StartsWith(ApplicationDataDirectoryOption + "=", StringComparison.Ordinal) ||
                 argument.StartsWith(ApplicationDataDirectoryOption + ":", StringComparison.Ordinal)))))
        {
            return false;
        }

        standardOutput ??= Console.Out;
        standardError ??= Console.Error;
        if (args.Length != 1 || args[0] != ApplicationDataDirectoryOption)
        {
            standardError.WriteLine($"Use {ApplicationDataDirectoryOption} alone, without a value or other arguments.");
            exitCode = 2;
            return true;
        }

        string directory;
        try
        {
            directory = ClcApplicationPaths.Resolve(applicationDirectoryName).ApplicationDataDirectory;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            // Do not echo arguments, exception details, or any configuration values.
            standardError.WriteLine("Unable to resolve the application's CommonApplicationData directory.");
            exitCode = 1;
            return true;
        }

        standardOutput.WriteLine(directory);
        return true;
    }
}
