using Clc.Configuration.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Clc.Configuration.Tests;

public sealed class GenericHostConfigurationTests
{
    [Fact]
    public void WorkerLoadsTheSameAuthoritativeSnapshotAsWebStartup()
    {
        using var fixture = ClcConfigurationTesting.CreateJsonFile("""
            { "Worker": { "Value": "external" }, "Items": [ "external-only" ], "urls": "http://external.test" }
            """);
        var contentRoot = Path.GetDirectoryName(fixture.FilePath)!;
        var worker = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = ["--Worker:Value", "command-line"],
            EnvironmentName = "Testing",
            ContentRootPath = contentRoot
        });
        var web = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], EnvironmentName = "Testing", ContentRootPath = contentRoot
        });
        IHostApplicationBuilder[] builders = [worker, web];
        foreach (var builder in builders)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:Value"] = "ambient",
                ["Ambient:Only"] = "removed",
                ["Items:1"] = "removed-array-entry",
                ["urls"] = "http://ambient.test",
                ["http_ports"] = "8080",
                ["https_ports"] = "8443",
                ["PreferHostingUrls"] = "true"
            });
            ExternalConfigurationLoader.AddFromFile(builder, "clc-worker-test", fixture.FilePath);
        }

        Assert.Equal(web.Configuration.AsEnumerable().OrderBy(x => x.Key),
            worker.Configuration.AsEnumerable().OrderBy(x => x.Key));
        Assert.Equal("external", worker.Configuration["Worker:Value"]);
        Assert.Null(worker.Configuration["Ambient:Only"]);
        Assert.Null(worker.Configuration["Items:1"]);
        Assert.Equal("http://external.test", worker.Configuration["urls"]);
        Assert.Equal("8080", worker.Configuration["http_ports"]);
        Assert.Equal("8443", worker.Configuration["https_ports"]);
        Assert.Equal("true", worker.Configuration["PreferHostingUrls"]);
        Assert.Equal("Testing", worker.Environment.EnvironmentName);
        Assert.Equal(contentRoot, worker.Environment.ContentRootPath.TrimEnd(Path.DirectorySeparatorChar));

        File.WriteAllText(fixture.FilePath, "{\"Worker\":{\"Value\":\"changed\"}}");
        Assert.Equal("external", worker.Configuration["Worker:Value"]);
    }

    [Fact]
    public void PublicWorkerEntryPointUsesTheConventionalPathWithoutCreatingIt()
    {
        var name = $"clc-worker-test-{Guid.NewGuid():N}";
        var paths = ClcApplicationPaths.Resolve(name);
        var builder = Host.CreateApplicationBuilder();

        var failure = Assert.Throws<ClcConfigurationException>(() => builder.AddClcConfiguration(name));

        Assert.Equal(ClcConfigurationErrorReason.MissingFile, failure.Reason);
        Assert.Equal(paths.ConfigurationFilePath, failure.FilePath);
        Assert.False(Directory.Exists(paths.ApplicationDataDirectory));
    }

    [Fact]
    public void WorkerLoadFailureDoesNotClearExistingProviders()
    {
        using var fixture = ClcConfigurationTesting.CreateJsonFile("not json");
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Before"] = "retained" });
        var providers = builder.Configuration.Sources.ToArray();

        Assert.Throws<ClcConfigurationException>(() =>
            ExternalConfigurationLoader.AddFromFile(builder, "clc-worker-test", fixture.FilePath));

        Assert.Equal(providers, builder.Configuration.Sources);
        Assert.Equal("retained", builder.Configuration["Before"]);
    }

    [Fact]
    public void BothPublicOverloadsRejectNullBuilders()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ClcConfigurationBuilderExtensions.AddClcConfiguration((IHostApplicationBuilder)null!, "clc-worker-test"));
        Assert.Throws<ArgumentNullException>(() =>
            ClcConfigurationBuilderExtensions.AddClcConfiguration((WebApplicationBuilder)null!, "clc-web-test"));
    }
}
