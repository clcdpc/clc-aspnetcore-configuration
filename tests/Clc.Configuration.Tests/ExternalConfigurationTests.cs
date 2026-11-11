using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Clc.Configuration.Tests;

public sealed class ExternalConfigurationTests
{
    [Fact]
    public void ConventionalPathUsesTheOperatingSystemCommonApplicationDataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(Path.IsPathFullyQualified(root));

        var expected = Path.GetFullPath(Path.Combine(root, "clc-ssrs-web", "Config", "application.json"));
        Assert.Equal(expected, ExternalConfigurationLoader.ResolvePath("clc-ssrs-web"));
    }

    [Fact]
    public void ResolvesTheFixedPathFromOneApplicationDirectoryName()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var expected = Path.GetFullPath(Path.Combine(temporaryDirectory.Path, "clc-ssrs-web", "Config", "application.json"));

        Assert.Equal(expected, ExternalConfigurationLoader.ResolvePath("clc-ssrs-web", temporaryDirectory.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("C:relative")]
    [InlineData("unsafe:name")]
    [InlineData("name.")]
    public void RejectsInvalidApplicationDirectoryNames(string applicationDirectoryName)
    {
        using var temporaryDirectory = new TemporaryDirectory();

        Assert.Throws<ArgumentException>(
            () => ExternalConfigurationLoader.ResolvePath(applicationDirectoryName, temporaryDirectory.Path));
    }

    [Fact]
    public void ExternalFileIsAuthoritativeAndListenerSettingsArePreserved()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporaryDirectory.Path, "appsettings.json"), """
            {
              "AllowedHosts": "appsettings.invalid",
              "Private": { "FromAppsettings": "SECRET_APPSETTINGS_SENTINEL" },
              "Items": [ "appsettings-zero", "appsettings-one" ]
            }
            """);

        var filePath = Path.Combine(temporaryDirectory.Path, "application.json");
        File.WriteAllText(filePath, """
            {
              "AllowedHosts": "external.invalid",
              "Private": { "FromExternal": "external" },
              "Items": [ "external-zero" ],
              "Probe": "external-snapshot"
            }
            """);

        var builder = CreateBuilder(
            temporaryDirectory.Path,
            ["--urls", "http://127.0.0.1:51234", "--Private:FromCommandLine", "SECRET_COMMAND_LINE_SENTINEL"]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Private:FromPriorProvider"] = "SECRET_PROVIDER_SENTINEL",
            ["Items:1"] = "stale-array-entry",
            ["http_ports"] = "8080",
            ["https_ports"] = "8443",
            [WebHostDefaults.PreferHostingUrlsKey] = "true"
        });

        ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath);

        Assert.Equal("external.invalid", builder.Configuration["AllowedHosts"]);
        Assert.Equal("external", builder.Configuration["Private:FromExternal"]);
        Assert.Null(builder.Configuration["Private:FromAppsettings"]);
        Assert.Null(builder.Configuration["Private:FromCommandLine"]);
        Assert.Null(builder.Configuration["Private:FromPriorProvider"]);
        Assert.Equal("external-zero", builder.Configuration["Items:0"]);
        Assert.Null(builder.Configuration["Items:1"]);
        Assert.Equal(["0"], builder.Configuration.GetSection("Items").GetChildren().Select(child => child.Key));
        Assert.Equal("http://127.0.0.1:51234", builder.Configuration[WebHostDefaults.ServerUrlsKey]);
        Assert.Equal("8080", builder.Configuration["http_ports"]);
        Assert.Equal("8443", builder.Configuration["https_ports"]);
        Assert.Equal("true", builder.Configuration[WebHostDefaults.PreferHostingUrlsKey]);
        Assert.True(builder.Environment.IsDevelopment());

        File.WriteAllText(filePath, "{\"Probe\":\"changed-after-startup\"}");
        Assert.Equal("external-snapshot", builder.Configuration["Probe"]);

        using var app = builder.Build();
        Assert.Equal("external-snapshot", app.Configuration["Probe"]);
    }

    [Fact]
    public void ValidUtf8WithoutBomLoadsAsOneSnapshot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = temporaryDirectory.WriteConfiguration("{\"Probe\":\"utf8\"}");
        var builder = CreateBuilder(temporaryDirectory.Path);

        ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath);

        Assert.Equal("utf8", builder.Configuration["Probe"]);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public void SupportedBomEncodingsAreReadWithoutChangingTheFile(string encodingName)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var original = "{\"Probe\":\"encoded-snapshot\"}";
        var bytes = EncodeWithBom(original, encodingName);
        var filePath = temporaryDirectory.WriteConfiguration(bytes);
        var builder = CreateBuilder(temporaryDirectory.Path);

        ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath);

        Assert.Equal("encoded-snapshot", builder.Configuration["Probe"]);
        Assert.Equal(bytes, File.ReadAllBytes(filePath));
    }

    [Theory]
    [InlineData("utf8-truncated")]
    [InlineData("utf8-invalid")]
    [InlineData("utf16le-surrogate")]
    [InlineData("utf16be-surrogate")]
    [InlineData("utf32le-truncated")]
    [InlineData("utf32be-invalid-scalar")]
    public void MalformedEncodingsFailWithRedactedErrors(string failureKind)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var bytes = MalformedEncoding(failureKind);
        var filePath = temporaryDirectory.WriteConfiguration(bytes);
        var builder = CreateBuilder(temporaryDirectory.Path);

        var error = Assert.Throws<InvalidOperationException>(
            () => ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath));

        Assert.Contains("unsupported text encoding", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_ENCODING_SENTINEL", error.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(filePath));
    }

    [Fact]
    public void MissingFileFailsClearlyWithoutCreatingAReplacement()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = Path.Combine(temporaryDirectory.Path, "missing", "application.json");
        var builder = CreateBuilder(temporaryDirectory.Path);

        var error = Assert.Throws<InvalidOperationException>(
            () => ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath));

        Assert.Contains("Required external application configuration file is missing", error.Message);
        Assert.Contains(filePath, error.Message);
        Assert.Null(error.InnerException);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void MalformedJsonFailsWithActionableRedactedDiagnostics()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = temporaryDirectory.WriteConfiguration(
            "{\"Credentials\":{\"Secret\":\"SECRET_JSON_SENTINEL\"}, SECRET_PARSE_SENTINEL }");
        var builder = CreateBuilder(temporaryDirectory.Path);

        var error = Assert.Throws<InvalidOperationException>(
            () => ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath));

        Assert.Contains("invalid JSON", error.Message);
        Assert.Contains("restart", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_", error.ToString());
    }

    [Theory]
    [InlineData("{\"Key\":\"SECRET_ONE\",\"Key\":\"SECRET_TWO\"}")]
    [InlineData("{\"Section\":{\"Key\":\"SECRET_ONE\",\"key\":\"SECRET_TWO\"}}")]
    [InlineData("{\"Section\":{\"Nested\":[{\"Key\":\"SECRET_ONE\",\"KEY\":\"SECRET_TWO\"}]}}")]
    [InlineData("{\"Section:Nested\":\"SECRET_ONE\"}")]
    public void DuplicateOrAmbiguousJsonKeysFailWithoutExposingValues(string json)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = temporaryDirectory.WriteConfiguration(json);
        var builder = CreateBuilder(temporaryDirectory.Path);

        var error = Assert.Throws<InvalidOperationException>(
            () => ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath));

        Assert.Contains("duplicate keys", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_", error.ToString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"not-an-object\"")]
    [InlineData("42")]
    public void RootMustBeAJsonObject(string json)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = temporaryDirectory.WriteConfiguration(json);
        var builder = CreateBuilder(temporaryDirectory.Path);

        var error = Assert.Throws<InvalidOperationException>(
            () => ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath));

        Assert.Contains("invalid JSON", error.Message);
        Assert.Null(error.InnerException);
    }

    private static WebApplicationBuilder CreateBuilder(string contentRoot, string[]? args = null)
    {
        return WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ContentRootPath = contentRoot,
            Args = args ?? []
        });
    }

    private static byte[] EncodeWithBom(string text, string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf8" => new UTF8Encoding(true, true),
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            "utf32le" => new UTF32Encoding(false, true, true),
            "utf32be" => new UTF32Encoding(true, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName))
        };

        return encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
    }

    private static byte[] MalformedEncoding(string failureKind)
    {
        const string json = "{\"AllowedHosts\":\"SECRET_ENCODING_SENTINEL\"}";
        return failureKind switch
        {
            "utf8-truncated" => [0xef, 0xbb],
            "utf8-invalid" => Encoding.UTF8.GetBytes(json).Append((byte)0xff).ToArray(),
            "utf16le-surrogate" => new UnicodeEncoding(false, true, true).GetPreamble()
                .Concat(new UnicodeEncoding(false, false, true).GetBytes(json)).Concat(new byte[] { 0x00, 0xd8 }).ToArray(),
            "utf16be-surrogate" => new UnicodeEncoding(true, true, true).GetPreamble()
                .Concat(new UnicodeEncoding(true, false, true).GetBytes(json)).Concat(new byte[] { 0xd8, 0x00 }).ToArray(),
            "utf32le-truncated" => new UTF32Encoding(false, true, true).GetPreamble()
                .Concat(new UTF32Encoding(false, false, true).GetBytes(json)).Append((byte)0x01).ToArray(),
            "utf32be-invalid-scalar" => new UTF32Encoding(true, true, true).GetPreamble()
                .Concat(new UTF32Encoding(true, false, true).GetBytes(json)).Concat(new byte[] { 0x00, 0x11, 0x00, 0x00 }).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("clc-configuration-tests-").FullName;

        internal string Path => _path;

        internal string WriteConfiguration(string json)
        {
            var filePath = System.IO.Path.Combine(_path, "application.json");
            File.WriteAllText(filePath, json);
            return filePath;
        }

        internal string WriteConfiguration(byte[] bytes)
        {
            var filePath = System.IO.Path.Combine(_path, "application.json");
            File.WriteAllBytes(filePath, bytes);
            return filePath;
        }

        public void Dispose()
        {
            Directory.Delete(_path, recursive: true);
        }
    }
}
