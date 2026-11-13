using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Clc.Configuration.Tests;

public sealed class ConfigurationBootstrapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstRunCreatesTheConventionalFileThenStopsUntilItIsEdited(bool web)
    {
        using var fixture = new BootstrapFixture();
        const string example = "{\"Token\":\"SECRET_SENTINEL_REPLACE_ME\"}";
        fixture.WriteExample(example);
        var builder = fixture.CreateBuilder(web);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Before"] = "retained" });
        var sources = builder.Configuration.Sources.ToArray();

        var created = Assert.Throws<ClcConfigurationException>(() => fixture.Load(builder));

        Assert.Equal(ClcConfigurationErrorReason.ConfigurationCreated, created.Reason);
        Assert.Equal(fixture.ConfigurationPath, created.FilePath);
        Assert.Contains("Startup has stopped", created.Message);
        Assert.Equal(example, File.ReadAllText(fixture.ConfigurationPath));
        Assert.Equal(example, File.ReadAllText(fixture.ExamplePath));
        Assert.Equal(sources, builder.Configuration.Sources);
        Assert.Null(created.InnerException);
        Assert.DoesNotContain("SECRET_SENTINEL", created.ToString());

        var unfinished = Assert.Throws<ClcConfigurationException>(() => fixture.Load(builder));
        Assert.Equal(ClcConfigurationErrorReason.UnreplacedPlaceholder, unfinished.Reason);
        Assert.DoesNotContain("SECRET_SENTINEL", unfinished.ToString());
        Assert.Null(unfinished.InnerException);
        Assert.Equal(sources, builder.Configuration.Sources);

        File.WriteAllText(fixture.ConfigurationPath, "{\"Token\":\"edited\"}");
        fixture.Load(builder);
        Assert.Equal("edited", builder.Configuration["Token"]);
        Assert.Null(builder.Configuration["Before"]);
    }

    [Fact]
    public void FirstRunStopsEvenWhenExampleHasNoPlaceholders()
    {
        using var fixture = new BootstrapFixture();
        fixture.WriteExample("{}");
        var error = Assert.Throws<ClcConfigurationException>(() => fixture.Load(fixture.CreateBuilder()));
        Assert.Equal(ClcConfigurationErrorReason.ConfigurationCreated, error.Reason);
        Assert.Equal("{}", File.ReadAllText(fixture.ConfigurationPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitExampleCanBeAbsoluteOrRelativeToContentRoot(bool absolute)
    {
        using var fixture = new BootstrapFixture();
        var relative = Path.Combine("templates", "custom.json");
        var path = Path.Combine(fixture.ContentRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"Source\":\"custom\"}");

        var error = Assert.Throws<ClcConfigurationException>(() =>
            fixture.Load(fixture.CreateBuilder(), absolute ? path : relative));

        Assert.Equal(ClcConfigurationErrorReason.ConfigurationCreated, error.Reason);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(fixture.ConfigurationPath));
    }

    [Fact]
    public void WindowsDriveRelativePathsCannotDependOnTheProcessDirectory()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new BootstrapFixture();
        foreach (var path in new[] { "C:example.json", @"\templates\example.json" })
        {
            Assert.Throws<ArgumentException>(() => fixture.Load(fixture.CreateBuilder(), path));
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.ConfigurationPath)));
    }

    [Fact]
    public void MissingExampleFailsBeforeCreatingDestinationDirectories()
    {
        using var fixture = new BootstrapFixture();
        var error = Assert.Throws<ClcConfigurationException>(() => fixture.Load(fixture.CreateBuilder()));
        Assert.Equal(ClcConfigurationErrorReason.ExampleFileMissing, error.Reason);
        Assert.Equal(fixture.ExamplePath, error.FilePath);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.ConfigurationPath)));
    }

    [Fact]
    public void DirectoryInsteadOfExampleIsReportedAsUnreadable()
    {
        using var fixture = new BootstrapFixture();
        Directory.CreateDirectory(fixture.ExamplePath);
        var error = Assert.Throws<ClcConfigurationException>(() => fixture.Load(fixture.CreateBuilder()));
        Assert.Equal(ClcConfigurationErrorReason.ExampleFileUnreadable, error.Reason);
        Assert.False(File.Exists(fixture.ConfigurationPath));
    }

    [Fact]
    public void DestinationFailureIsRedactedAndDoesNotRemoveExistingData()
    {
        using var fixture = new BootstrapFixture();
        fixture.WriteExample("{\"Token\":\"SECRET_SENTINEL\"}");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ConfigurationPath)!);
        Directory.CreateDirectory(fixture.ConfigurationPath);
        var sentinelPath = Path.Combine(fixture.ConfigurationPath, "keep.txt");
        File.WriteAllText(sentinelPath, "keep");

        var error = Assert.Throws<ClcConfigurationException>(() => fixture.Load(fixture.CreateBuilder()));

        Assert.Equal(ClcConfigurationErrorReason.BootstrapFailed, error.Reason);
        Assert.Equal(fixture.ConfigurationPath, error.FilePath);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET_SENTINEL", error.ToString());
        Assert.Equal("keep", File.ReadAllText(sentinelPath));
    }

    [Fact]
    public void ExistingConfigurationIsNotOverwrittenOrDependentOnAnExample()
    {
        using var fixture = new BootstrapFixture();
        const string existing = "{\"Value\":\"existing\",\"urls\":\"http://external.test\"}";
        fixture.WriteConfiguration(existing);
        var builder = fixture.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Value"] = "ambient", ["AmbientOnly"] = "removed", ["http_ports"] = "8080"
        });
        // No example exists. Even an unusable source option is irrelevant to an existing file.
        fixture.Load(builder, " ");
        Assert.Equal("existing", builder.Configuration["Value"]);
        Assert.Null(builder.Configuration["AmbientOnly"]);
        Assert.Equal("8080", builder.Configuration["http_ports"]);
        Assert.Equal("http://external.test", builder.Configuration["urls"]);
        Assert.Equal(existing, File.ReadAllText(fixture.ConfigurationPath));
        fixture.WriteExample("{\"Value\":\"replacement\"}");
        fixture.Load(builder);
        Assert.Equal(existing, File.ReadAllText(fixture.ConfigurationPath));
        File.WriteAllText(fixture.ConfigurationPath, "{\"Value\":\"later\"}");
        Assert.Equal("existing", builder.Configuration["Value"]);
    }

    [Theory]
    [InlineData("{\"Value\":\"REPLACE_ME\"}")]
    [InlineData("{\"Connection\":\"Server=private;Password=REPLACE_ME;\"}")]
    [InlineData("{\"Values\":[\"REPLACE_ME\"]}")]
    [InlineData("{\"REPLACE_ME\":\"value\"}")]
    [InlineData("{ /* REPLACE_ME */ \"Value\":\"value\"}")]
    public void LiteralMarkerAnywhereInExistingTextStopsOnlyTheOptedInHelper(string json)
    {
        using var fixture = new BootstrapFixture();
        fixture.WriteConfiguration(json);
        var builder = fixture.CreateBuilder();
        var error = Assert.Throws<ClcConfigurationException>(() => fixture.Load(builder));
        Assert.Equal(ClcConfigurationErrorReason.UnreplacedPlaceholder, error.Reason);
        Assert.Equal(json, File.ReadAllText(fixture.ConfigurationPath));
        Assert.DoesNotContain("Server=private", error.ToString());
        // Preserve the existing normal-startup and hostless-reader contract.
        ExternalConfigurationLoader.AddFromFile(builder, BootstrapFixture.AppName, fixture.ConfigurationPath);
        ClcConfigurationReader.LoadFromFile(BootstrapFixture.AppName, fixture.ConfigurationPath);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("utf32")]
    public void MarkerCheckingUsesTheSameDecodedSnapshotForSupportedEncodings(string name)
    {
        using var fixture = new BootstrapFixture();
        var encoding = name switch { "utf16" => Encoding.Unicode, "utf32" => Encoding.UTF32, _ => Encoding.UTF8 };
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ConfigurationPath)!);
        File.WriteAllText(fixture.ConfigurationPath, "{\"Token\":\"REPLACE_ME\"}", encoding);
        var error = Assert.Throws<ClcConfigurationException>(() => fixture.Load(fixture.CreateBuilder()));
        Assert.Equal(ClcConfigurationErrorReason.UnreplacedPlaceholder, error.Reason);
    }

    [Theory]
    [InlineData("replace_me")]
    [InlineData("Replace_Me")]
    [InlineData("REPLACE\\u005fME")]
    public void MarkerIsLiteralAndCaseSensitive(string value)
    {
        using var fixture = new BootstrapFixture();
        fixture.WriteConfiguration("{\"Value\":\"" + value + "\"}");
        fixture.Load(fixture.CreateBuilder());
    }

    [Fact]
    public void InvalidExistingJsonStillFailsWithoutReplacingIt()
    {
        using var fixture = new BootstrapFixture();
        fixture.WriteConfiguration("not json");
        fixture.WriteExample("{}");
        var error = Assert.Throws<ClcConfigurationException>(() => fixture.Load(fixture.CreateBuilder()));
        Assert.Equal(ClcConfigurationErrorReason.MalformedJson, error.Reason);
        Assert.Equal("not json", File.ReadAllText(fixture.ConfigurationPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicBootstrapOverloadsUseContentRootAndDoNotWriteWhenExampleIsAbsent(bool web)
    {
        using var fixture = new BootstrapFixture();
        var name = $"clc-bootstrap-public-{Guid.NewGuid():N}";
        var builder = fixture.CreateBuilder(web);
        var error = Assert.Throws<ClcConfigurationException>(() =>
        {
            if (builder is WebApplicationBuilder webBuilder) { webBuilder.AddClcConfigurationWithBootstrap(name); }
            else { builder.AddClcConfigurationWithBootstrap(name); }
        });
        Assert.Equal(ClcConfigurationErrorReason.ExampleFileMissing, error.Reason);
        Assert.Equal(fixture.ExamplePath, error.FilePath);
        Assert.False(Directory.Exists(ClcApplicationPaths.Resolve(name).ApplicationDataDirectory));
    }

    [Fact]
    public void PublicOverloadsRejectNullBuilders()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ClcConfigurationBuilderExtensions.AddClcConfigurationWithBootstrap((WebApplicationBuilder)null!, "clc-test"));
        Assert.Throws<ArgumentNullException>(() =>
            ClcConfigurationBuilderExtensions.AddClcConfigurationWithBootstrap((IHostApplicationBuilder)null!, "clc-test"));
    }

    private sealed class BootstrapFixture : IDisposable
    {
        internal const string AppName = "clc-bootstrap-test";
        private readonly string root = Directory.CreateTempSubdirectory("clc-bootstrap-test-").FullName;
        internal string ContentRoot => Directory.CreateDirectory(Path.Combine(root, "content")).FullName;
        internal string ExamplePath => Path.Combine(ContentRoot, "config", "example.json");
        internal string ConfigurationPath => ClcApplicationPaths.Resolve(AppName, Path.Combine(root, "appdata")).ConfigurationFilePath;

        internal IHostApplicationBuilder CreateBuilder(bool web = false) => web
            ? WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = ContentRoot })
            : Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = ContentRoot });

        internal void WriteExample(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExamplePath)!);
            File.WriteAllText(ExamplePath, json);
        }

        internal void WriteConfiguration(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
            File.WriteAllText(ConfigurationPath, json);
        }

        internal void Load(IHostApplicationBuilder builder, string? example = null) =>
            ConfigurationBootstrap.AddFromFile(builder, AppName, ConfigurationPath, example);

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
