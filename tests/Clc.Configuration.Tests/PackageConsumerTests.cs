using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Clc.Configuration.Tests;

public sealed class PackageConsumerTests
{
    private const string FixtureConsumerProgram = """
        using System;
        using System.IO;
        using System.Reflection;
        using System.Threading.Tasks;
        using Clc.Configuration;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Hosting;

        public static class Program
        {
            public static async Task<int> Main(string[] args)
            {
                if (args.Length != 1)
                {
                    throw new ArgumentException("Expected the isolated configuration fixture path.");
                }

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                // The package intentionally exposes only the fixed ProgramData convention.
                // This test-only harness uses its internal file-path seam for an isolated fixture.
                var loader = typeof(ClcConfigurationBuilderExtensions).Assembly.GetType(
                    "Clc.Configuration.ExternalConfigurationLoader",
                    throwOnError: true)!;
                var fixturePath = Path.Combine(args[0], "clc-package-consumer", "Config", "application.json");
                var addFromFile = loader.GetMethod("AddFromFile", BindingFlags.Static | BindingFlags.NonPublic)!;
                addFromFile.Invoke(null, [builder, "clc-package-consumer", fixturePath]);

                await using var app = builder.Build();
                if (app.Configuration["Consumer:Fixture"] != "loaded-from-package")
                {
                    throw new InvalidOperationException("The isolated package fixture was not loaded.");
                }

                await app.StartAsync();
                await app.StopAsync();
                Console.WriteLine("Loaded the isolated fixture and started the host.");
                return 0;
            }
        }
        """;

    [Fact]
    public async Task PackedNuGetPackageLoadsAnIsolatedFixtureAndStartsAnIndependentConsumer()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var packageFeed = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "feed")).FullName;
        var packageCache = Path.Combine(temporaryDirectory.Path, "nuget-cache");
        var consumerDirectory = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "consumer")).FullName;
        CopyConsumerProject(consumerDirectory);
        File.WriteAllText(Path.Combine(consumerDirectory, "Program.cs"), FixtureConsumerProgram);

        var fixtureRoot = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "fixture")).FullName;
        var fixturePath = Path.Combine(fixtureRoot, "clc-package-consumer", "Config", "application.json");
        Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
        File.WriteAllText(fixturePath, """
            { "Consumer": { "Fixture": "loaded-from-package" } }
            """);

        var configuration = GetBuildConfiguration();
        var repositoryRoot = FindRepositoryRoot();
        var libraryProject = Path.Combine(repositoryRoot, "src", "Clc.Configuration", "Clc.Configuration.csproj");
        var consumerProject = Path.Combine(consumerDirectory, "Clc.Configuration.Consumer.csproj");

        await RunDotnetAsync(
            repositoryRoot,
            ["pack", libraryProject, "--configuration", configuration, "--no-build", "--no-restore", "--output", packageFeed],
            packageCache);

        Assert.False(Directory.Exists(packageCache));
        await RunDotnetAsync(
            consumerDirectory,
            ["restore", consumerProject, "--source", packageFeed, "--packages", packageCache],
            packageCache);

        var packagePath = Assert.Single(Directory.GetFiles(packageFeed, "*.nupkg"));
        var cachedAssemblyPath = Path.Combine(
            packageCache,
            "clc.configuration",
            "0.1.0",
            "lib",
            "net10.0",
            "Clc.Configuration.dll");
        Assert.True(File.Exists(cachedAssemblyPath), "Restore did not install the package in the isolated cache.");
        Assert.Equal(
            GetPackedAssemblyHash(packagePath),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cachedAssemblyPath))));

        await RunDotnetAsync(
            consumerDirectory,
            ["build", consumerProject, "--configuration", configuration, "--no-restore"],
            packageCache);

        var runResult = await RunDotnetAsync(
            consumerDirectory,
            ["run", "--project", consumerProject, "--configuration", configuration, "--no-build", "--no-restore", "--", fixtureRoot],
            packageCache);

        var output = runResult.StandardOutput + runResult.StandardError;
        Assert.Contains("Loaded the isolated fixture and started the host.", output);
        Assert.DoesNotContain("SECRET", output);
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string packageCache)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.Environment["NUGET_PACKAGES"] = packageCache;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), "Could not start the dotnet process.");
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var outputTask = ReadToEndAsync(process.StandardOutput, standardOutput);
        var errorTask = ReadToEndAsync(process.StandardError, standardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(outputTask, errorTask);

        var result = new ProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
        Assert.True(
            result.ExitCode == 0,
            $"dotnet {string.Join(' ', arguments)} exited with {result.ExitCode}.{Environment.NewLine}{result.StandardOutput}{result.StandardError}");
        return result;
    }

    private static string GetPackedAssemblyHash(string packagePath)
    {
        using var package = ZipFile.OpenRead(packagePath);
        var assembly = package.GetEntry("lib/net10.0/Clc.Configuration.dll");
        Assert.NotNull(assembly);
        using var stream = assembly!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static async Task ReadToEndAsync(StreamReader reader, StringBuilder destination)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            destination.Append(buffer, 0, count);
        }
    }

    private static void CopyConsumerProject(string consumerDirectory)
    {
        var templateDirectory = Path.Combine(FindRepositoryRoot(), "samples", "Clc.Configuration.Consumer");
        File.Copy(
            Path.Combine(templateDirectory, "Clc.Configuration.Consumer.csproj"),
            Path.Combine(consumerDirectory, "Clc.Configuration.Consumer.csproj"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Clc.Configuration.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the Clc.Configuration repository root.");
    }

    private static string GetBuildConfiguration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("clc-configuration-package-test-").FullName;

        internal string Path => _path;

        public void Dispose()
        {
            Directory.Delete(_path, recursive: true);
        }
    }
}
