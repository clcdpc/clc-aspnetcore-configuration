using System.Net.Http.Json;
using Clc.Configuration.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Clc.Configuration.Tests;

public sealed class WebApplicationFactoryIntegrationTests
{
    [Fact]
    public async Task CreateClientUsesIsolatedSettingsAndRetainsListenerSettingsWithoutOperationalFile()
    {
        var applicationDirectoryName = $"clc-factory-test-{Guid.NewGuid():N}";
        var configurationPath = ExternalConfigurationLoader.ResolvePath(applicationDirectoryName);
        var applicationDirectory = Directory.GetParent(Path.GetDirectoryName(configurationPath)!)!.FullName;
        Assert.False(File.Exists(configurationPath));
        Assert.False(Directory.Exists(applicationDirectory));

        var priorSettings = new Dictionary<string, string?>
        {
            ["Application:Value"] = "ambient-value",
            ["Ambient:Only"] = "must-be-removed",
            [WebHostDefaults.ServerUrlsKey] = "http://127.0.0.1:51234",
            ["http_ports"] = "8080",
            ["https_ports"] = "8443",
            [WebHostDefaults.PreferHostingUrlsKey] = "true"
        };
        var testSettings = new Dictionary<string, string?>
        {
            ["Application:Value"] = "isolated-value",
            ["Testing:Only"] = "loaded-by-factory"
        };

        using var factory = new IsolatedConfigurationFactory(priorSettings, testSettings);
        using var client = factory.CreateClient();
        var values = await client.GetFromJsonAsync<Dictionary<string, string?>>("/");

        Assert.NotNull(values);
        Assert.Equal("isolated-value", values["Application:Value"]);
        Assert.Equal("loaded-by-factory", values["Testing:Only"]);
        Assert.Null(values["Ambient:Only"]);
        Assert.Equal("http://127.0.0.1:51234", values[WebHostDefaults.ServerUrlsKey]);
        Assert.Equal("8080", values["http_ports"]);
        Assert.Equal("8443", values["https_ports"]);
        Assert.Equal("true", values[WebHostDefaults.PreferHostingUrlsKey]);
        Assert.False(File.Exists(configurationPath));
        Assert.False(Directory.Exists(applicationDirectory));
    }

    private sealed class IsolatedConfigurationFactory(
        IReadOnlyDictionary<string, string?> priorSettings,
        IReadOnlyDictionary<string, string?> testSettings)
        : WebApplicationFactory<TestEntryPoint>
    {
        protected override IHostBuilder CreateHostBuilder() =>
            Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<TestStartup>();
                    webBuilder.ConfigureAppConfiguration((_, configuration) =>
                        configuration.AddInMemoryCollection(priorSettings));
                });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddClcConfigurationForTesting(testSettings));
        }
    }

    private sealed class TestEntryPoint
    {
    }

    private sealed class TestStartup(IConfiguration configuration)
    {
        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void Configure(IApplicationBuilder app)
        {
            app.Run(async context =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Application:Value"] = configuration["Application:Value"],
                    ["Testing:Only"] = configuration["Testing:Only"],
                    ["Ambient:Only"] = configuration["Ambient:Only"],
                    [WebHostDefaults.ServerUrlsKey] = configuration[WebHostDefaults.ServerUrlsKey],
                    ["http_ports"] = configuration["http_ports"],
                    ["https_ports"] = configuration["https_ports"],
                    [WebHostDefaults.PreferHostingUrlsKey] = configuration[WebHostDefaults.PreferHostingUrlsKey]
                };
                await context.Response.WriteAsJsonAsync(values);
            });
        }
    }
}
