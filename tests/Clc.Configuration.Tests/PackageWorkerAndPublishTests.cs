using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace Clc.Configuration.Tests;

public sealed partial class PackageConsumerTests
{
    private const string WorkerFixtureProgram = """
        using Clc.Configuration;
        using Clc.Configuration.Testing;

        const string applicationDirectoryName = "APPLICATION_DIRECTORY_NAME_PLACEHOLDER";
        var paths = ClcApplicationPaths.Resolve(applicationDirectoryName);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [], EnvironmentName = "Production"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ambient:Only"] = "must-disappear", ["http_ports"] = "8080"
        });
        try
        {
            builder.AddClcConfiguration(applicationDirectoryName);
            throw new InvalidOperationException("A unique missing conventional file unexpectedly loaded.");
        }
        catch (ClcConfigurationException error)
        {
            if (error.Reason != ClcConfigurationErrorReason.MissingFile || error.FilePath != paths.ConfigurationFilePath)
            {
                throw;
            }
        }
        // The public bootstrap integration is also available from the packed assembly.
        // Its missing-example failure must not create anything under machine ProgramData.
        try
        {
            builder.AddClcConfigurationWithBootstrap(applicationDirectoryName,
                exampleConfigurationPath: Path.Combine(AppContext.BaseDirectory, "absent-example.json"));
            throw new InvalidOperationException("A missing example unexpectedly bootstrapped configuration.");
        }
        catch (ClcConfigurationException error) when (error.Reason == ClcConfigurationErrorReason.ExampleFileMissing)
        {
        }
        builder.Configuration.AddClcConfigurationForTesting(new Dictionary<string, string?>
        {
            ["Worker:Value"] = "package-backed-worker"
        });
        builder.Services.AddHostedService<ProbeWorker>();
        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();
        if (Directory.Exists(paths.ApplicationDataDirectory))
        {
            throw new InvalidOperationException("The worker created a conventional configuration directory.");
        }

        internal sealed class ProbeWorker(IConfiguration configuration, IHostEnvironment environment) : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken)
            {
                if (configuration["Worker:Value"] != "package-backed-worker" ||
                    configuration["Ambient:Only"] is not null || configuration["http_ports"] != "8080" ||
                    environment.EnvironmentName != "Production")
                {
                    throw new InvalidOperationException("Worker configuration or host identity was not preserved.");
                }
                Console.WriteLine("Started package-backed Generic Host worker.");
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
        """;

    [Fact]
    public async Task PackedNuGetPackageSupportsWorkersAndGuardsPublishOutput()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var packageFeed = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "feed")).FullName;
        var packageCache = Path.Combine(temporaryDirectory.Path, "nuget-cache");
        var consumerDirectory = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "worker")).FullName;
        var configuration = GetBuildConfiguration();
        var repositoryRoot = FindRepositoryRoot();
        var expectedVersion = ResolvePackageVersion();
        var projectPath = Path.Combine(consumerDirectory, "Clc.Configuration.Worker.csproj");
        var template = Path.Combine(repositoryRoot, "samples", "Clc.Configuration.Worker");
        File.Copy(Path.Combine(template, "Clc.Configuration.Worker.csproj"), projectPath);
        File.Copy(Path.Combine(template, "Program.cs"), Path.Combine(consumerDirectory, "Program.cs"));
        Directory.CreateDirectory(Path.Combine(consumerDirectory, "config"));
        File.Copy(Path.Combine(template, "config", "example.json"), Path.Combine(consumerDirectory, "config", "example.json"));
        var project = XDocument.Load(projectPath);
        project.Descendants("PackageReference").Single().SetAttributeValue("Version", expectedVersion);
        project.Save(projectPath);

        await RunDotnetAsync(repositoryRoot,
            ["pack", Path.Combine(repositoryRoot, "src", "Clc.Configuration", "Clc.Configuration.csproj"),
             "--configuration", configuration, "--no-build", "--no-restore", "--output", packageFeed,
             $"-p:Version={expectedVersion}", $"-p:PackageVersion={expectedVersion}"], packageCache);
        using (var package = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(packageFeed, "*.nupkg"))))
        {
            Assert.NotNull(package.GetEntry("buildTransitive/Clc.Configuration.targets"));
        }
        await RunDotnetAsync(consumerDirectory,
            ["restore", projectPath, "--source", packageFeed, "--packages", packageCache], packageCache);

        var publishRoot = Path.Combine(temporaryDirectory.Path, "publish");
        async Task<ProcessResult> Publish(string name, string? enabled, int expectedExitCode = 0)
        {
            var arguments = new List<string>
            {
                "publish", projectPath, "--configuration", configuration, "--no-restore",
                "--output", Path.Combine(publishRoot, name), $"-p:GuardCase={name}"
            };
            if (enabled is not null) { arguments.Add($"-p:ClcConfigurationValidatePublish={enabled}"); }
            return await RunDotnetAsync(consumerDirectory, arguments, packageCache, expectedExitCode);
        }

        // Compile and publish the actual sample with only the NuGet package reference.
        await Publish("sample", null);
        Assert.Equal(File.ReadAllText(Path.Combine(template, "config", "example.json")),
            File.ReadAllText(Path.Combine(publishRoot, "sample", "config", "example.json")));
        var sampleAssembly = Path.Combine(publishRoot, "sample", "Clc.Configuration.Worker.dll");
        var discovery = await RunDotnetAsync(temporaryDirectory.Path,
            [sampleAssembly, "--clc-appdata-directory"], packageCache);
        Assert.Equal(ClcApplicationPaths.Resolve("clc-configuration-worker").ApplicationDataDirectory + Environment.NewLine,
            discovery.StandardOutput);
        Assert.Empty(discovery.StandardError);
        using (var runtime = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(publishRoot, "sample", "Clc.Configuration.Worker.runtimeconfig.json"))))
        {
            Assert.Contains(runtime.RootElement.GetProperty("runtimeOptions").GetProperty("frameworks").EnumerateArray(),
                framework => framework.GetProperty("name").GetString() == "Microsoft.AspNetCore.App");
        }

        // The published fixture runs a real hosted service, but never provisions machine ProgramData.
        var name = $"clc-worker-package-{Guid.NewGuid():N}";
        Assert.False(Directory.Exists(ClcApplicationPaths.Resolve(name).ApplicationDataDirectory));
        File.WriteAllText(Path.Combine(consumerDirectory, "Program.cs"),
            WorkerFixtureProgram.Replace("APPLICATION_DIRECTORY_NAME_PLACEHOLDER", name));
        await Publish("worker", "true");
        var worker = await RunDotnetAsync(temporaryDirectory.Path,
            [Path.Combine(publishRoot, "worker", "Clc.Configuration.Worker.dll")], packageCache);
        Assert.Contains("Started package-backed Generic Host worker.", worker.StandardOutput);
        Assert.False(Directory.Exists(ClcApplicationPaths.Resolve(name).ApplicationDataDirectory));

        var fixtureDirectory = Directory.CreateDirectory(Path.Combine(consumerDirectory, "Fixtures")).FullName;
        const string sentinel = "{\"PublishGuard\":\"HARMLESS_SENTINEL\"}";
        File.WriteAllText(Path.Combine(fixtureDirectory, "application.json"), sentinel);
        File.WriteAllText(Path.Combine(fixtureDirectory, "payload.json"), "{}");
        File.WriteAllText(Path.Combine(fixtureDirectory, "application.Example.json"), "{}");
        File.WriteAllText(Path.Combine(fixtureDirectory, "example.json"), "{}");
        project.Root!.Add(XElement.Parse("""
            <ItemGroup>
              <Content Remove="config/example.json" />
              <None Remove="Fixtures/**/*" />
              <Content Remove="Fixtures/**/*" />
              <Content Include="Fixtures/application.Example.json" Link="application.Example.json" CopyToPublishDirectory="PreserveNewest" />
              <Content Include="Fixtures/example.json" Link="Config/example.json" CopyToPublishDirectory="PreserveNewest" />
              <Content Include="Fixtures/application.json" Link="application.json" CopyToPublishDirectory="PreserveNewest"
                       Condition="'$(GuardCase)' == 'root' Or '$(GuardCase)' == 'default-on' Or '$(GuardCase)' == 'invalid-optout' Or '$(GuardCase)' == 'explicit-off'" />
              <Content Include="Fixtures/payload.json" Link="nested/ApPlIcAtIoN.JsOn" CopyToPublishDirectory="PreserveNewest"
                       Condition="'$(GuardCase)' == 'renamed-output'" />
              <Content Include="Fixtures/application.json" Link="nested/renamed.json" CopyToPublishDirectory="PreserveNewest"
                       Condition="'$(GuardCase)' == 'renamed-source'" />
              <Content Include="Fixtures/application.json" Link="application.json" CopyToPublishDirectory="Never"
                       Condition="'$(GuardCase)' == 'excluded'" />
            </ItemGroup>
            """));
        project.Save(projectPath);

        foreach (var allowed in new[] { "examples", "excluded" })
        {
            await Publish(allowed, "true");
            Assert.True(File.Exists(Path.Combine(publishRoot, allowed, "application.Example.json")));
            Assert.True(File.Exists(Path.Combine(publishRoot, allowed, "Config", "example.json")));
            Assert.False(File.Exists(Path.Combine(publishRoot, allowed, "application.json")));
        }
        // The guard does not alter an ordinary build or silently drop files.
        await RunDotnetAsync(consumerDirectory,
            ["build", projectPath, "--configuration", configuration, "--no-restore",
             "-p:GuardCase=root", "-p:ClcConfigurationValidatePublish=true"], packageCache);
        foreach (var forbidden in new[] { "root", "renamed-output", "renamed-source" })
        {
            var result = await Publish(forbidden, "true", expectedExitCode: 1);
            Assert.Contains("CLC_CONFIG001", result.StandardOutput + result.StandardError);
            Assert.DoesNotContain("HARMLESS_SENTINEL", result.StandardOutput + result.StandardError);
        }
        var defaultFailure = await Publish("default-on", null, expectedExitCode: 1);
        Assert.Contains("CLC_CONFIG001", defaultFailure.StandardOutput + defaultFailure.StandardError);
        var typoFailure = await Publish("invalid-optout", "fasle", expectedExitCode: 1);
        Assert.Contains("CLC_CONFIG001", typoFailure.StandardOutput + typoFailure.StandardError);
        await Publish("explicit-off", "false");
        Assert.Equal(sentinel, File.ReadAllText(Path.Combine(publishRoot, "explicit-off", "application.json")));
        Assert.Equal(sentinel, File.ReadAllText(Path.Combine(fixtureDirectory, "application.json")));
    }
}
