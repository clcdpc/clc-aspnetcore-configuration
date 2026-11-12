using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Clc.Configuration;

internal static class ExternalConfigurationLoader
{
    internal const int MaximumConfigurationBytes = 4 * 1024 * 1024;
    private const int MaximumJsonDepth = 32;

    private static readonly string[] PreservedHostKeys =
    [
        WebHostDefaults.ServerUrlsKey,
        "http_ports",
        "https_ports",
        WebHostDefaults.PreferHostingUrlsKey
    ];

    internal static string ResolvePath(string applicationDirectoryName) =>
        ClcApplicationPaths.Resolve(applicationDirectoryName).ConfigurationFilePath;

    internal static string ResolvePath(string applicationDirectoryName, string programDataRoot) =>
        ClcApplicationPaths.Resolve(applicationDirectoryName, programDataRoot).ConfigurationFilePath;

    internal static void AddFromFile(WebApplicationBuilder builder, string applicationDirectoryName, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ClcApplicationPaths.ValidateApplicationDirectoryName(applicationDirectoryName);
        var configuration = ReadSnapshot(ValidateAndNormalizeFilePath(path));
        ReplaceConfiguration(builder.Configuration, configuration);
    }

    internal static IConfiguration LoadFromFile(string applicationDirectoryName, string path)
    {
        ClcApplicationPaths.ValidateApplicationDirectoryName(applicationDirectoryName);
        var configuration = ReadSnapshot(ValidateAndNormalizeFilePath(path));
        return new ConfigurationBuilder().AddInMemoryCollection(configuration).Build();
    }

    internal static void ReplaceConfiguration(
        IConfigurationBuilder builder,
        IReadOnlyDictionary<string, string?> configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var preservedHostSettings = CaptureHostSettings(builder);

        // The external configuration is authoritative. Hosting listener controls retain
        // their prior lower-precedence values unless the external configuration sets them.
        builder.Sources.Clear();
        builder.AddInMemoryCollection(preservedHostSettings);
        builder.AddInMemoryCollection(configuration);
    }

    private static string ValidateAndNormalizeFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The external configuration file path must be absolute.", nameof(path));
        }

        return Path.GetFullPath(path);
    }

    private static Dictionary<string, string?> CaptureHostSettings(IConfigurationBuilder builder)
    {
        if (builder is IConfiguration currentConfiguration)
        {
            return CaptureHostSettings(currentConfiguration);
        }

        var configuration = builder.Build();
        try
        {
            return CaptureHostSettings(configuration);
        }
        finally
        {
            (configuration as IDisposable)?.Dispose();
        }
    }

    private static Dictionary<string, string?> CaptureHostSettings(IConfiguration configuration)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in PreservedHostKeys)
        {
            var value = configuration[key];
            if (value is not null)
            {
                settings[key] = value;
            }
        }

        return settings;
    }

    private static Dictionary<string, string?> ReadSnapshot(string path)
    {
        byte[] input;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumConfigurationBytes)
            {
                throw CreateError(path, ClcConfigurationErrorReason.SizeLimitExceeded);
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = file.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaximumConfigurationBytes)
                {
                    throw CreateError(path, ClcConfigurationErrorReason.SizeLimitExceeded);
                }

                buffer.Write(chunk, 0, read);
            }

            input = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            throw CreateError(path, ClcConfigurationErrorReason.MissingFile);
        }
        catch (DirectoryNotFoundException)
        {
            throw CreateError(path, ClcConfigurationErrorReason.MissingFile);
        }
        catch (UnauthorizedAccessException)
        {
            throw CreateError(path, ClcConfigurationErrorReason.UnreadableFile);
        }
        catch (IOException)
        {
            throw CreateError(path, ClcConfigurationErrorReason.UnreadableFile);
        }

        byte[] jsonBytes;
        try
        {
            jsonBytes = NormalizeJsonEncoding(input);
        }
        catch (DecoderFallbackException)
        {
            throw CreateError(path, ClcConfigurationErrorReason.InvalidEncoding);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = MaximumJsonDepth
            });
        }
        catch (JsonException exception)
        {
            var reason = IsDepthLimitFailure(exception)
                ? ClcConfigurationErrorReason.DepthLimitExceeded
                : ClcConfigurationErrorReason.MalformedJson;
            throw CreateJsonError(path, reason, exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw CreateError(path, ClcConfigurationErrorReason.InvalidRootShape);
            }

            ValidateObject(path, document.RootElement);
        }

        Dictionary<string, string?> configuration;
        try
        {
            using var snapshot = new MemoryStream(jsonBytes, writable: false);
            using var loaded = (ConfigurationRoot)new ConfigurationBuilder()
                .AddJsonStream(snapshot)
                .Build();

            configuration = loaded.AsEnumerable().ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            throw CreateJsonError(path, ClcConfigurationErrorReason.MalformedJson, exception);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            // Framework configuration binding errors can include supplied values or key paths.
            // Do not retain parser exceptions or copy their text into a public diagnostic.
            throw CreateError(path, ClcConfigurationErrorReason.MalformedJson);
        }

        if (!HasValidLoggingLevels(configuration))
        {
            throw CreateError(path, ClcConfigurationErrorReason.InvalidFrameworkLogLevel);
        }

        return configuration;
    }

    private static byte[] NormalizeJsonEncoding(byte[] input)
    {
        string text;
        if (input.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }))
        {
            text = new UTF32Encoding(true, false, true).GetString(input, 4, input.Length - 4);
        }
        else if (input.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }))
        {
            text = new UTF32Encoding(false, false, true).GetString(input, 4, input.Length - 4);
        }
        else if (input.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            text = new UTF8Encoding(false, true).GetString(input, 3, input.Length - 3);
        }
        else if (input.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            text = new UnicodeEncoding(true, false, true).GetString(input, 2, input.Length - 2);
        }
        else if (input.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            text = new UnicodeEncoding(false, false, true).GetString(input, 2, input.Length - 2);
        }
        else
        {
            text = new UTF8Encoding(false, true).GetString(input);
        }

        return new UTF8Encoding(false, true).GetBytes(text);
    }

    private static void ValidateObject(string path, JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            var name = GetPropertyName(path, property);
            if (name.Length == 0 || name.Contains(':'))
            {
                throw CreateError(path, ClcConfigurationErrorReason.AmbiguousKey);
            }

            if (!names.Add(name))
            {
                throw CreateError(path, ClcConfigurationErrorReason.DuplicateKey);
            }

            ValidateValue(path, property.Value);
        }
    }

    private static string GetPropertyName(string path, JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (ClcConfigurationException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // JsonDocument accepts escaped unpaired surrogates while parsing, then throws
            // when the property name is decoded. Keep this boundary around only that decode
            // so unrelated validation or programming failures are not reclassified.
            throw CreateError(path, ClcConfigurationErrorReason.MalformedJson);
        }
    }

    private static void ValidateValue(string path, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(path, element);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateValue(path, item);
            }
        }
    }

    private static bool HasValidLoggingLevels(IReadOnlyDictionary<string, string?> configuration)
    {
        foreach (var setting in configuration)
        {
            if (setting.Value is null || !IsFrameworkLoggingLevel(setting.Key))
            {
                continue;
            }

            if (!Enum.TryParse<LogLevel>(setting.Value, ignoreCase: true, out var level) ||
                !Enum.IsDefined(level) ||
                !Enum.GetNames<LogLevel>().Contains(setting.Value, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFrameworkLoggingLevel(string key)
    {
        var segments = key.Split(':');
        var isGlobalLevel = segments.Length >= 3 &&
                segments[0].Equals("Logging", StringComparison.OrdinalIgnoreCase) &&
                segments[1].Equals("LogLevel", StringComparison.OrdinalIgnoreCase);
        var isProviderLevel = segments.Length >= 4 &&
                segments[0].Equals("Logging", StringComparison.OrdinalIgnoreCase) &&
                segments[2].Equals("LogLevel", StringComparison.OrdinalIgnoreCase);

        return isGlobalLevel || isProviderLevel;
    }

    private static bool IsDepthLimitFailure(JsonException exception)
    {
        // The JSON reader exposes numeric location information, but its depth reason is
        // described only in the parser text. Inspect that text internally and never return it.
        return exception.Message.Contains("depth", StringComparison.OrdinalIgnoreCase) &&
            (exception.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("exceed", StringComparison.OrdinalIgnoreCase));
    }

    private static ClcConfigurationException CreateJsonError(
        string path,
        ClcConfigurationErrorReason reason,
        JsonException exception)
    {
        long? line = exception.LineNumber is long lineNumber ? lineNumber + 1 : null;
        long? column = exception.BytePositionInLine is long bytePosition ? bytePosition + 1 : null;
        return CreateError(path, reason, line, column);
    }

    private static ClcConfigurationException CreateError(
        string path,
        ClcConfigurationErrorReason reason,
        long? lineNumber = null,
        long? columnNumber = null) =>
        new(reason, path, lineNumber, columnNumber);
}
