using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Clc.Configuration.Testing;

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
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con.txt")]
    [InlineData("NUL.log")]
    [InlineData("AUX")]
    [InlineData("PRN.txt")]
    [InlineData("COM1")]
    [InlineData("com9.txt")]
    [InlineData("LPT1")]
    [InlineData("lPt9.log")]
    [InlineData("COM¹")]
    [InlineData("LPT².txt")]
    public void RejectsInvalidApplicationDirectoryNames(string applicationDirectoryName)
    {
        using var temporaryDirectory = new TemporaryDirectory();

        Assert.Throws<ArgumentException>(
            () => ExternalConfigurationLoader.ResolvePath(applicationDirectoryName, temporaryDirectory.Path));
    }

    [Theory]
    [InlineData("Console")]
    [InlineData("convention")]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("COM10.txt")]
    [InlineData("LPT10")]
    [InlineData("NULS.txt")]
    [InlineData("report.txt")]
    public void AcceptsPortableDirectoryNamesThatAreNotWindowsDeviceNames(string applicationDirectoryName)
    {
        using var temporaryDirectory = new TemporaryDirectory();

        var expected = Path.GetFullPath(Path.Combine(
            temporaryDirectory.Path,
            applicationDirectoryName,
            "Config",
            "application.json"));

        Assert.Equal(expected, ExternalConfigurationLoader.ResolvePath(applicationDirectoryName, temporaryDirectory.Path));
    }

    [Theory]
    [InlineData("""{ "Logging": { "LogLevel": { "Default": "SECRET_LOG_LEVEL_SENTINEL" } } }""", "SECRET_LOG_LEVEL_SENTINEL")]
    [InlineData("""{ "lOgGiNg": { "Console": { "lOgLeVeL": { "Default": "SECRET_LOG_LEVEL_SENTINEL" } } } }""", "SECRET_LOG_LEVEL_SENTINEL")]
    [InlineData("""{ "Logging": { "LogLevel": { "Microsoft": { "Hosting": { "Lifetime": "SECRET_NESTED_GLOBAL_LOG_LEVEL_SENTINEL" } } } } }""", "SECRET_NESTED_GLOBAL_LOG_LEVEL_SENTINEL")]
    [InlineData("""{ "Logging": { "Console": { "LogLevel": { "Microsoft": { "Hosting": { "Lifetime": "SECRET_NESTED_PROVIDER_LOG_LEVEL_SENTINEL" } } } } } }""", "SECRET_NESTED_PROVIDER_LOG_LEVEL_SENTINEL")]
    [InlineData("""{ "Logging": { "LogLevel": { "Microsoft": { "Hosting": [ "Information", "SECRET_ARRAY_LOG_LEVEL_SENTINEL" ] } } } }""", "SECRET_ARRAY_LOG_LEVEL_SENTINEL")]
    public void InvalidFrameworkLogLevelFailsNormalStartupWithoutEchoingTheValue(string json, string sentinel)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = temporaryDirectory.WriteConfiguration(json);
        var builder = CreateBuilder(temporaryDirectory.Path);

        var error = Assert.Throws<ClcConfigurationException>(() => StartApplication(builder, filePath));

        Assert.Equal(ClcConfigurationErrorReason.InvalidFrameworkLogLevel, error.Reason);
        Assert.Contains("framework logging levels", error.Message);
        Assert.Contains(filePath, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(sentinel, error.ToString());
    }

    [Theory]
    [InlineData("""{ "Logging": { "LogLevel": { "Default": "Trace", "Microsoft": "Warning" } }, "Probe": "loaded" }""")]
    [InlineData("""{ "Logging": { "Console": { "LogLevel": { "Default": "Debug", "System": "Information" } } }, "Probe": "loaded" }""")]
    [InlineData("""{ "lOgGiNg": { "lOgLeVeL": { "dEfAuLt": "tRaCe", "mIcRoSoFt": "wArNiNg" }, "cOnSoLe": { "lOgLeVeL": { "Default": "eRrOr" } } }, "Probe": "loaded" }""")]
    public void AcceptsValidFrameworkLogLevelsCaseInsensitively(string json)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = temporaryDirectory.WriteConfiguration(json);
        var builder = CreateBuilder(temporaryDirectory.Path);

        ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath);

        Assert.Equal("loaded", builder.Configuration["Probe"]);
    }

    [Theory]
    [InlineData("""{ "Application": { "Logging": { "LogLevel": { "Default": "SECRET_APP_LEVEL" } } }, "Probe": "loaded" }""", "Application:Logging:LogLevel:Default")]
    [InlineData("""{ "ApplicationLogging": { "LogLevel": { "Default": "SECRET_APP_LEVEL" } }, "Probe": "loaded" }""", "ApplicationLogging:LogLevel:Default")]
    [InlineData("""{ "LoggingOptions": { "LogLevel": { "Default": "SECRET_APP_LEVEL" } }, "Probe": "loaded" }""", "LoggingOptions:LogLevel:Default")]
    [InlineData("""{ "Logging": { "Feature": { "Options": { "LogLevel": { "Default": "SECRET_APP_LEVEL" } } } }, "Probe": "loaded" }""", "Logging:Feature:Options:LogLevel:Default")]
    public void IgnoresUnrelatedApplicationLoggingNames(string json, string unrelatedLevelKey)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = temporaryDirectory.WriteConfiguration(json);
        var builder = CreateBuilder(temporaryDirectory.Path);

        ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath);

        Assert.Equal("loaded", builder.Configuration["Probe"]);
        Assert.Equal("SECRET_APP_LEVEL", builder.Configuration[unrelatedLevelKey]);
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

        var error = Assert.Throws<ClcConfigurationException>(
            () => ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath));

        Assert.Equal(ClcConfigurationErrorReason.InvalidEncoding, error.Reason);
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

        var error = Assert.Throws<ClcConfigurationException>(
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

        var error = Assert.Throws<ClcConfigurationException>(
            () => ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath));

        Assert.Equal(ClcConfigurationErrorReason.MalformedJson, Assert.IsType<ClcConfigurationException>(error).Reason);
        Assert.Contains("restart", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_", error.ToString());
    }

    [Theory]
    [InlineData("{\"Key\":\"SECRET_ONE\",\"Key\":\"SECRET_TWO\"}", ClcConfigurationErrorReason.DuplicateKey)]
    [InlineData("{\"Section\":{\"Key\":\"SECRET_ONE\",\"key\":\"SECRET_TWO\"}}", ClcConfigurationErrorReason.DuplicateKey)]
    [InlineData("{\"Section\":{\"Nested\":[{\"Key\":\"SECRET_ONE\",\"KEY\":\"SECRET_TWO\"}]}}", ClcConfigurationErrorReason.DuplicateKey)]
    [InlineData("{\"Section\":{\"Nested\":[[{\"Key\":\"SECRET_ONE\",\"KEY\":\"SECRET_TWO\"}]]}}", ClcConfigurationErrorReason.DuplicateKey)]
    [InlineData("{\"Section:Nested\":\"SECRET_ONE\"}", ClcConfigurationErrorReason.AmbiguousKey)]
    [InlineData("{\"\":\"SECRET_EMPTY_KEY\"}", ClcConfigurationErrorReason.AmbiguousKey)]
    public void DuplicateOrAmbiguousJsonKeysFailWithoutExposingValues(
        string json,
        ClcConfigurationErrorReason expectedReason)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = temporaryDirectory.WriteConfiguration(json);
        var builder = CreateBuilder(temporaryDirectory.Path);

        var error = Assert.Throws<ClcConfigurationException>(
            () => ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath));

        Assert.Equal(expectedReason, error.Reason);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_", error.ToString());
        Assert.DoesNotContain("Section", error.ToString());
    }

    [Theory]
    [InlineData("\\uD800", false)]
    [InlineData("\\uDC00", false)]
    [InlineData("\\uD800", true)]
    public void MalformedEscapedPropertyNamesFailNormalStartupWithSafeDiagnostics(
        string escapedSurrogate,
        bool nestedInArray)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var json = CreateMalformedEscapedPropertyNameJson(escapedSurrogate, nestedInArray);
        var filePath = temporaryDirectory.WriteConfiguration(json);
        var parserMessage = GetPropertyNameParserMessage(json, nestedInArray);
        var builder = CreateBuilder(temporaryDirectory.Path);

        var error = Assert.Throws<ClcConfigurationException>(
            () => ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath));

        AssertMalformedPropertyNameDiagnostics(error, filePath, parserMessage);
    }

    [Theory]
    [InlineData("\\uD800", false)]
    [InlineData("\\uDC00", false)]
    [InlineData("\\uD800", true)]
    public void MalformedEscapedPropertyNamesFailHostlessReadWithSafeDiagnostics(
        string escapedSurrogate,
        bool nestedInArray)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var json = CreateMalformedEscapedPropertyNameJson(escapedSurrogate, nestedInArray);
        var filePath = temporaryDirectory.WriteConfiguration(json);
        var parserMessage = GetPropertyNameParserMessage(json, nestedInArray);

        var error = Assert.Throws<ClcConfigurationException>(
            () => ClcConfigurationReader.LoadFromFile("clc-test-app", filePath));

        AssertMalformedPropertyNameDiagnostics(error, filePath, parserMessage);
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

        var error = Assert.Throws<ClcConfigurationException>(
            () => ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath));

        Assert.Contains("JSON object at its root", error.Message);
        Assert.Equal(ClcConfigurationErrorReason.InvalidRootShape, error.Reason);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void HostlessAndStartupReadersUseTheSameValidatedSnapshot()
    {
        using var fixture = ClcConfigurationTesting.CreateJsonFile("""
            // deployment validation accepts comments and trailing commas
            { "Portal": { "Enabled": true, }, "Probe": "same-snapshot", }
            """);
        var filePath = fixture.FilePath;

        var hostless = ClcConfigurationReader.LoadFromFile("clc-test-app", filePath);
        var builder = CreateBuilder(Path.GetDirectoryName(filePath)!);
        ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", filePath);

        Assert.Equal("True", hostless["Portal:Enabled"]);
        Assert.Equal("True", builder.Configuration["Portal:Enabled"]);
        Assert.Equal("same-snapshot", hostless["Probe"]);
        Assert.Equal("same-snapshot", builder.Configuration["Probe"]);

        File.WriteAllText(filePath, "{\"Probe\":\"changed-after-validation\"}");
        Assert.Equal("same-snapshot", hostless["Probe"]);
        Assert.Equal("same-snapshot", builder.Configuration["Probe"]);
    }

    [Fact]
    public void PublicTestingHelperReplacesProvidersAndRetainsListenerPrecedence()
    {
        var configurationBuilder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Prior:Secret"] = "SECRET_PRIOR_PROVIDER",
                [WebHostDefaults.ServerUrlsKey] = "http://127.0.0.1:50123",
                ["http_ports"] = "8081",
                ["https_ports"] = "8444",
                [WebHostDefaults.PreferHostingUrlsKey] = "true"
            });
        var settings = new Dictionary<string, string?>
        {
            ["Test:Value"] = "in-memory",
            [WebHostDefaults.ServerUrlsKey] = "http://127.0.0.1:50999"
        };

        configurationBuilder.AddClcConfigurationForTesting(settings);
        var configuration = configurationBuilder.Build();

        Assert.Equal("in-memory", configuration["Test:Value"]);
        Assert.Null(configuration["Prior:Secret"]);
        Assert.Equal("http://127.0.0.1:50999", configuration[WebHostDefaults.ServerUrlsKey]);
        Assert.Equal("8081", configuration["http_ports"]);
        Assert.Equal("8444", configuration["https_ports"]);
        Assert.Equal("true", configuration[WebHostDefaults.PreferHostingUrlsKey]);
    }

    [Fact]
    public void PublicJsonFixtureIsUniqueAndDeletesItsOwnedDirectory()
    {
        var first = ClcConfigurationTesting.CreateJsonFile("{\"Fixture\":\"one\"}");
        var second = ClcConfigurationTesting.CreateJsonFile("{\"Fixture\":\"two\"}");
        var firstPath = first.FilePath;
        var secondPath = second.FilePath;
        var firstDirectory = Path.GetDirectoryName(firstPath)!;
        var secondDirectory = Path.GetDirectoryName(secondPath)!;

        try
        {
            Assert.NotEqual(firstPath, secondPath);
            Assert.NotEqual(firstDirectory, secondDirectory);
            Assert.Equal("one", ClcConfigurationReader.LoadFromFile("clc-test-app", firstPath)["Fixture"]);
            Assert.Equal("two", ClcConfigurationReader.LoadFromFile("clc-test-app", secondPath)["Fixture"]);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }

        Assert.False(File.Exists(firstPath));
        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(File.Exists(secondPath));
        Assert.False(Directory.Exists(secondDirectory));
    }

    [Fact]
    public void StructuredJsonErrorIncludesOnlySafePathAndNumericLocation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = temporaryDirectory.WriteConfiguration(
            "{\n  \"Password\": \"SECRET_DIAGNOSTIC_SENTINEL\",\n  invalid }\n");

        var error = Assert.Throws<ClcConfigurationException>(
            () => ClcConfigurationReader.LoadFromFile("clc-test-app", filePath));

        Assert.Equal(ClcConfigurationErrorReason.MalformedJson, error.Reason);
        Assert.Equal(filePath, error.FilePath);
        Assert.True(error.LineNumber >= 1);
        Assert.True(error.ColumnNumber >= 1);
        Assert.Contains($"line {error.LineNumber}, column {error.ColumnNumber}", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_DIAGNOSTIC_SENTINEL", error.ToString());
        Assert.DoesNotContain("Password", error.ToString());
    }

    [Fact]
    public void MissingAndUnreadableFilesHaveDistinctStructuredReasons()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var missingPath = Path.Combine(temporaryDirectory.Path, "missing.json");
        var missing = Assert.Throws<ClcConfigurationException>(
            () => ClcConfigurationReader.LoadFromFile("clc-test-app", missingPath));

        var directoryPath = Path.Combine(temporaryDirectory.Path, "directory.json");
        Directory.CreateDirectory(directoryPath);
        var unreadable = Assert.Throws<ClcConfigurationException>(
            () => ClcConfigurationReader.LoadFromFile("clc-test-app", directoryPath));

        Assert.Equal(ClcConfigurationErrorReason.MissingFile, missing.Reason);
        Assert.Equal(ClcConfigurationErrorReason.UnreadableFile, unreadable.Reason);
        Assert.Null(missing.InnerException);
        Assert.Null(unreadable.InnerException);
    }

    [Fact]
    public void SizeLimitAcceptsTheBoundaryAndRejectsTheFirstByteOverIt()
    {
        using var atLimit = ClcConfigurationTesting.CreateJsonFile(
            Encoding.UTF8.GetBytes(new string(' ', ExternalConfigurationLoader.MaximumConfigurationBytes - 2) + "{}"));
        using var overLimit = ClcConfigurationTesting.CreateJsonFile(
            Encoding.UTF8.GetBytes(new string(' ', ExternalConfigurationLoader.MaximumConfigurationBytes - 1) + "{}"));

        Assert.Empty(ClcConfigurationReader.LoadFromFile("clc-test-app", atLimit.FilePath).GetChildren());
        var error = Assert.Throws<ClcConfigurationException>(
            () => ClcConfigurationReader.LoadFromFile("clc-test-app", overLimit.FilePath));
        Assert.Equal(ClcConfigurationErrorReason.SizeLimitExceeded, error.Reason);
        Assert.Contains("4 MiB", error.Message);
    }

    [Fact]
    public void DepthLimitAcceptsTheBoundaryAndRejectsTheNextLevel()
    {
        using var atLimit = ClcConfigurationTesting.CreateJsonFile(
            CreateNestedArrayJson(31));
        using var overLimit = ClcConfigurationTesting.CreateJsonFile(
            CreateNestedArrayJson(32));
        const int acceptedArrayDepth = 31;

        var key = "Nested:" + string.Join(':', Enumerable.Repeat("0", acceptedArrayDepth));
        Assert.Equal("0", ClcConfigurationReader.LoadFromFile("clc-test-app", atLimit.FilePath)[key]);
        var error = Assert.Throws<ClcConfigurationException>(
            () => ClcConfigurationReader.LoadFromFile("clc-test-app", overLimit.FilePath));
        Assert.Equal(ClcConfigurationErrorReason.DepthLimitExceeded, error.Reason);
    }

    private static string CreateNestedArrayJson(int arrayDepth) =>
        "{\"Nested\":" + new string('[', arrayDepth) + "0" + new string(']', arrayDepth) + "}";

    private static string CreateMalformedEscapedPropertyNameJson(string escapedSurrogate, bool nestedInArray)
    {
        var property = "\"SECRET_PROPERTY_NAME_SENTINEL" + escapedSurrogate + "\":\"SECRET_VALUE_SENTINEL\"";
        return nestedInArray ? "{\"Nested\":[{" + property + "}]}" : "{" + property + "}";
    }

    private static string GetPropertyNameParserMessage(string json, bool nestedInArray)
    {
        using var document = JsonDocument.Parse(json);
        var element = nestedInArray
            ? document.RootElement.GetProperty("Nested").EnumerateArray().Single()
            : document.RootElement;
        var property = Assert.Single(element.EnumerateObject());
        var parserException = Assert.Throws<InvalidOperationException>(() => _ = property.Name);
        Assert.False(string.IsNullOrEmpty(parserException.Message));
        return parserException.Message;
    }

    private static void AssertMalformedPropertyNameDiagnostics(
        ClcConfigurationException error,
        string filePath,
        string parserMessage)
    {
        Assert.Equal(ClcConfigurationErrorReason.MalformedJson, error.Reason);
        Assert.Equal(filePath, error.FilePath);
        Assert.Contains(filePath, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_PROPERTY_NAME_SENTINEL", error.Message);
        Assert.DoesNotContain("SECRET_VALUE_SENTINEL", error.Message);
        Assert.DoesNotContain(parserMessage, error.Message);
        Assert.DoesNotContain("SECRET_PROPERTY_NAME_SENTINEL", error.ToString());
        Assert.DoesNotContain("SECRET_VALUE_SENTINEL", error.ToString());
        Assert.DoesNotContain(parserMessage, error.ToString());
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

    private static void StartApplication(WebApplicationBuilder builder, string configurationPath)
    {
        ExternalConfigurationLoader.AddFromFile(builder, "clc-test-app", configurationPath);
        using var app = builder.Build();
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
