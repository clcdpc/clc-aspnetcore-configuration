namespace Clc.Configuration;

/// <summary>Stable categories for file loading and validation failures.</summary>
public enum ClcConfigurationErrorReason
{
    MissingFile,
    UnreadableFile,
    InvalidEncoding,
    MalformedJson,
    InvalidRootShape,
    DuplicateKey,
    AmbiguousKey,
    SizeLimitExceeded,
    DepthLimitExceeded,
    InvalidFrameworkLogLevel
}

/// <summary>A redacted external configuration failure that remains compatible with InvalidOperationException handlers.</summary>
public sealed class ClcConfigurationException : InvalidOperationException
{
    internal ClcConfigurationException(
        ClcConfigurationErrorReason reason,
        string filePath,
        long? lineNumber,
        long? columnNumber)
        : base(CreateMessage(reason, filePath, lineNumber, columnNumber))
    {
        Reason = reason;
        FilePath = filePath;
        LineNumber = lineNumber;
        ColumnNumber = columnNumber;
    }

    /// <summary>The stable failure category.</summary>
    public ClcConfigurationErrorReason Reason { get; }

    /// <summary>The normalized path to the file that was checked.</summary>
    public string FilePath { get; }

    /// <summary>The one-based line reported by the JSON parser, when available.</summary>
    public long? LineNumber { get; }

    /// <summary>The one-based UTF-8 byte column reported by the JSON parser, when available.</summary>
    public long? ColumnNumber { get; }

    /// <summary>Returns only the safe public diagnostic, without an inner exception or parser details.</summary>
    public override string ToString() => $"{GetType().FullName}: {Message}";

    private static string CreateMessage(
        ClcConfigurationErrorReason reason,
        string path,
        long? lineNumber,
        long? columnNumber)
    {
        var safePath = EscapePath(path);
        var message = reason switch
        {
            ClcConfigurationErrorReason.MissingFile =>
                $"Required external application configuration file is missing: '{safePath}'. Provision it and grant the caller's identity read access.",
            ClcConfigurationErrorReason.UnreadableFile =>
                $"External application configuration file cannot be read: '{safePath}'. Check read access for the caller's identity.",
            ClcConfigurationErrorReason.InvalidEncoding =>
                $"External application configuration file uses unsupported text encoding or contains invalid encoded bytes: '{safePath}'. Use valid UTF-8, UTF-16, or UTF-32 text and restart the application.",
            ClcConfigurationErrorReason.MalformedJson =>
                $"External application configuration file contains malformed JSON: '{safePath}'. Correct the JSON and restart the application.",
            ClcConfigurationErrorReason.InvalidRootShape =>
                $"External application configuration file must have a JSON object at its root: '{safePath}'. Correct the file and restart the application.",
            ClcConfigurationErrorReason.DuplicateKey =>
                $"External application configuration file contains duplicate keys (compared case-insensitively): '{safePath}'. Remove duplicates and restart the application.",
            ClcConfigurationErrorReason.AmbiguousKey =>
                $"External application configuration file contains an empty or ambiguous key: '{safePath}'. Remove empty keys and names containing ':' and restart the application.",
            ClcConfigurationErrorReason.SizeLimitExceeded =>
                $"External application configuration file exceeds the 4 MiB size limit: '{safePath}'. Reduce its size and restart the application.",
            ClcConfigurationErrorReason.DepthLimitExceeded =>
                $"External application configuration file exceeds the maximum JSON depth of 32: '{safePath}'. Simplify the JSON and restart the application.",
            ClcConfigurationErrorReason.InvalidFrameworkLogLevel =>
                $"External application configuration file contains invalid framework logging levels: '{safePath}'. Use named levels from Trace through None and restart the application.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };

        if (lineNumber is not null && columnNumber is not null)
        {
            message = message.TrimEnd('.') + $" (line {lineNumber.Value}, column {columnNumber.Value}).";
        }

        return message;
    }

    private static string EscapePath(string path)
    {
        var safePath = new System.Text.StringBuilder(path.Length);
        foreach (var character in path)
        {
            if (char.IsControl(character))
            {
                safePath.Append("\\u").Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                safePath.Append(character);
            }
        }

        return safePath.ToString();
    }
}
