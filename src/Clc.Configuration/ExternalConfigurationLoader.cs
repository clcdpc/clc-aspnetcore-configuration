using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Clc.Configuration;

internal static class ExternalConfigurationLoader
{
    private const int MaximumConfigurationBytes = 4 * 1024 * 1024;
    private static readonly string[] PreservedHostKeys =
    [
        WebHostDefaults.ServerUrlsKey,
        "http_ports",
        "https_ports",
        WebHostDefaults.PreferHostingUrlsKey
    ];

    internal static string ResolvePath(string applicationDirectoryName)
    {
        ValidateApplicationDirectoryName(applicationDirectoryName);

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData) || !Path.IsPathFullyQualified(programData))
        {
            throw new InvalidOperationException("The operating-system ProgramData directory is unavailable.");
        }

        return ResolvePath(applicationDirectoryName, programData);
    }

    internal static string ResolvePath(string applicationDirectoryName, string programDataRoot)
    {
        ValidateApplicationDirectoryName(applicationDirectoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(programDataRoot);
        if (!Path.IsPathFullyQualified(programDataRoot))
        {
            throw new ArgumentException("The ProgramData root must be an absolute path.", nameof(programDataRoot));
        }

        return Path.GetFullPath(Path.Combine(programDataRoot, applicationDirectoryName, "Config", "application.json"));
    }

    internal static void AddFromFile(WebApplicationBuilder builder, string applicationDirectoryName, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidateApplicationDirectoryName(applicationDirectoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The external configuration file path must be absolute.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var configuration = ReadSnapshot(fullPath);
        var preservedHostSettings = CaptureHostSettings(builder.Configuration);

        // Discard all host defaults, appsettings, environment variables, command-line
        // application overrides, and other providers. Only the listener controls captured
        // above and this one external snapshot remain.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(preservedHostSettings);
        builder.Configuration.AddInMemoryCollection(configuration);
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
                throw InvalidConfiguration();
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = file.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaximumConfigurationBytes)
                {
                    throw InvalidConfiguration();
                }

                buffer.Write(chunk, 0, read);
            }

            input = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Required external application configuration file is missing: '{path}'. Provision it and grant the application identity read access.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new InvalidOperationException(
                $"Required external application configuration file is missing: '{path}'. Provision it and grant the application identity read access.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"External application configuration file cannot be read: '{path}'. Check access for the application identity.");
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException(
                $"External application configuration file contains invalid JSON, duplicate keys, or an unsupported text encoding: '{path}'. Correct the file and restart the application.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException(
                $"External application configuration file cannot be read: '{path}'. Check access for the application identity.");
        }

        try
        {
            var bytes = NormalizeJsonEncoding(input);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 32
            });

            ValidateRootAndKeys(document.RootElement);

            using var snapshot = new MemoryStream(bytes, writable: false);
            using var loaded = (ConfigurationRoot)new ConfigurationBuilder()
                .AddJsonStream(snapshot)
                .Build();

            return loaded.AsEnumerable().ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            // JSON and binder diagnostics can include supplied values. Keep only the file path.
            throw new InvalidOperationException(
                $"External application configuration file contains invalid JSON, duplicate keys, or an unsupported text encoding: '{path}'. Correct the file and restart the application.");
        }
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

    private static void ValidateRootAndKeys(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidConfiguration();
        }

        ValidateObject(root);
    }

    private static void ValidateObject(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Length == 0 || property.Name.Contains(':') || !names.Add(property.Name))
            {
                throw InvalidConfiguration();
            }

            ValidateValue(property.Value);
        }
    }

    private static void ValidateValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(element);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateValue(item);
            }
        }
    }

    private static void ValidateApplicationDirectoryName(string applicationDirectoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectoryName);

        if (applicationDirectoryName is "." or ".." ||
            Path.IsPathRooted(applicationDirectoryName) ||
            applicationDirectoryName.Contains('/') ||
            applicationDirectoryName.Contains('\\') ||
            applicationDirectoryName.EndsWith(' ') ||
            applicationDirectoryName.EndsWith('.') ||
            applicationDirectoryName.Any(char.IsControl) ||
            applicationDirectoryName.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
        {
            throw new ArgumentException(
                "The application directory name must be one portable directory name, not a path.",
                nameof(applicationDirectoryName));
        }
    }

    private static InvalidDataException InvalidConfiguration() => new();
}
