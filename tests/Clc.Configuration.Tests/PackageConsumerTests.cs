using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Clc.Configuration.Tests;

public sealed class PackageConsumerTests
{
    private const string FixtureConsumerProgram = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Threading.Tasks;
        using Clc.Configuration;
        using Clc.Configuration.Testing;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Hosting;
        using Microsoft.Extensions.Configuration;

        public static class Program
        {
            public static async Task<int> Main(string[] args)
            {
                if (args.Length != 0)
                {
                    throw new ArgumentException("This isolated package consumer does not accept arguments.");
                }

                var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (string.IsNullOrWhiteSpace(commonApplicationData) || !Path.IsPathFullyQualified(commonApplicationData))
                {
                    throw new InvalidOperationException("The operating-system CommonApplicationData directory is unavailable.");
                }

                var applicationDirectoryName = $"clc-package-consumer-{Guid.NewGuid():N}";
                var applicationDirectory = Path.Combine(commonApplicationData, applicationDirectoryName);
                var configurationPath = Path.Combine(applicationDirectory, "Config", "application.json");
                if (Directory.Exists(applicationDirectory) || File.Exists(applicationDirectory) || File.Exists(configurationPath))
                {
                    throw new IOException("The unique package consumer path already exists; refusing to overwrite it.");
                }

                // Probe the fixed production path only with a unique absent name; this call is read-only.
                var productionBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
                try
                {
                    productionBuilder.AddClcConfiguration(applicationDirectoryName);
                    throw new InvalidOperationException("The public startup extension unexpectedly loaded a missing file.");
                }
                catch (ClcConfigurationException exception)
                {
                    if (exception.Reason != ClcConfigurationErrorReason.MissingFile || exception.FilePath != configurationPath)
                    {
                        throw;
                    }
                }

                if (Directory.Exists(applicationDirectory) || File.Exists(configurationPath))
                {
                    throw new InvalidOperationException("The public startup extension created a configuration fixture.");
                }

                using (var jsonFixture = ClcConfigurationTesting.CreateJsonFile(
                    "{\"DeploymentProbe\":\"loaded-without-host\"}"))
                {
                    var deploymentConfiguration = ClcConfigurationReader.LoadFromFile(
                        "clc-package-test",
                        jsonFixture.FilePath);
                    if (deploymentConfiguration["DeploymentProbe"] != "loaded-without-host")
                    {
                        throw new InvalidOperationException("The packaged hostless reader did not load its fixture.");
                    }
                }

                var testConfiguration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["urls"] = "http://127.0.0.1:0",
                        ["Removed:Provider"] = "should-be-removed"
                    })
                    .AddClcConfigurationForTesting(new Dictionary<string, string?>
                    {
                        ["Testing:Fixture"] = "loaded-from-testing-helper"
                    })
                    .Build();
                if (testConfiguration["Testing:Fixture"] != "loaded-from-testing-helper" ||
                    testConfiguration["urls"] != "http://127.0.0.1:0" ||
                    testConfiguration["Removed:Provider"] is not null)
                {
                    throw new InvalidOperationException("The packaged testing helper did not replace and preserve its providers.");
                }

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Consumer:Fixture"] = "ambient-value",
                    ["Ambient:Only"] = "should-be-removed",
                    ["urls"] = "http://127.0.0.1:0"
                });
                builder.Configuration.AddClcConfigurationForTesting(new Dictionary<string, string?>
                {
                    ["Consumer:Fixture"] = "loaded-from-package"
                });

                await using var app = builder.Build();
                if (app.Configuration["Consumer:Fixture"] != "loaded-from-package" ||
                    app.Configuration["Ambient:Only"] is not null ||
                    app.Configuration["urls"] != "http://127.0.0.1:0")
                {
                    throw new InvalidOperationException("The packed testing helper did not replace providers and retain listener settings.");
                }

                await app.StartAsync();
                await app.StopAsync();
                Console.WriteLine("Loaded packed test settings and started an isolated host.");
                return 0;
            }
        }
        """;

    [Fact]
    public async Task PackedNuGetPackageLoadsHostlessFixturesAndStartsAnIsolatedConsumer()
    {
        using var temporaryDirectory = new TemporaryDirectory();

        var packageFeed = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "feed")).FullName;
        var packageCache = Path.Combine(temporaryDirectory.Path, "nuget-cache");
        var consumerDirectory = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "consumer")).FullName;

        var configuration = GetBuildConfiguration();
        var repositoryRoot = FindRepositoryRoot();
        var libraryProject = Path.Combine(repositoryRoot, "src", "Clc.Configuration", "Clc.Configuration.csproj");
        var expectedPackageVersion = await ResolvePackageVersionAsync(libraryProject, repositoryRoot, packageCache);
        CopyConsumerProject(consumerDirectory, expectedPackageVersion);
        File.WriteAllText(Path.Combine(consumerDirectory, "Program.cs"), FixtureConsumerProgram);
        Assert.Equal(expectedPackageVersion, GetConsumerPackageVersion(consumerDirectory));
        var consumerProject = Path.Combine(consumerDirectory, "Clc.Configuration.Consumer.csproj");

        await RunDotnetAsync(
            repositoryRoot,
            [
                "pack", libraryProject, "--configuration", configuration, "--no-build", "--no-restore", "--output", packageFeed,
                $"-p:Version={expectedPackageVersion}", $"-p:PackageVersion={expectedPackageVersion}"
            ],
            packageCache);

        Assert.False(Directory.Exists(packageCache));
        await RunDotnetAsync(
            consumerDirectory,
            [
                "restore", consumerProject, "--source", packageFeed, "--packages", packageCache,
                $"-p:Version={expectedPackageVersion}", $"-p:PackageVersion={expectedPackageVersion}"
            ],
            packageCache);

        var packagePath = Assert.Single(Directory.GetFiles(packageFeed, "*.nupkg"));
        Assert.Equal(expectedPackageVersion, GetPackageMetadataValue(packagePath, "version"));
        Assert.Equal("Clc.Configuration", GetPackageMetadataValue(packagePath, "id"));
        Assert.Equal("Central Library Consortium", GetPackageMetadataValue(packagePath, "authors"));

        using (var package = ZipFile.OpenRead(packagePath))
        {
            var metadata = GetPackageMetadata(package);
            var license = metadata.Elements().Single(element => element.Name.LocalName == "license");
            Assert.Equal("expression", (string?)license.Attribute("type"));
            Assert.Equal("MIT", license.Value);
            Assert.Equal(
                "https://github.com/clcdpc/clc-configuration",
                (string?)metadata.Elements().Single(element => element.Name.LocalName == "repository").Attribute("url"));
            Assert.Equal("git", (string?)metadata.Elements().Single(element => element.Name.LocalName == "repository").Attribute("type"));
            Assert.Equal("README.md", metadata.Elements().Single(element => element.Name.LocalName == "readme").Value);
            Assert.NotNull(package.GetEntry("README.md"));
            Assert.NotNull(package.GetEntry("LICENSE"));
        }

        var cachedAssemblyPath = Path.Combine(
            packageCache,
            "clc.configuration",
            expectedPackageVersion.ToLowerInvariant(),
            "lib",
            "net10.0",
            "Clc.Configuration.dll");
        Assert.True(File.Exists(cachedAssemblyPath), "Restore did not install the package in the isolated cache.");
        Assert.Equal(
            GetPackedAssemblyHash(packagePath),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cachedAssemblyPath))));

        await RunDotnetAsync(
            consumerDirectory,
            [
                "build", consumerProject, "--configuration", configuration, "--no-restore",
                $"-p:Version={expectedPackageVersion}", $"-p:PackageVersion={expectedPackageVersion}"
            ],
            packageCache);

        var runResult = await RunDotnetAsync(
            consumerDirectory,
            [
                "run", "--project", consumerProject, "--configuration", configuration, "--no-build", "--no-restore",
                $"-p:Version={expectedPackageVersion}", $"-p:PackageVersion={expectedPackageVersion}"
            ],
            packageCache);

        var output = runResult.StandardOutput + runResult.StandardError;
        Assert.Contains("Loaded packed test settings and started an isolated host.", output);
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

    private static async Task<string> ResolvePackageVersionAsync(
        string libraryProject,
        string repositoryRoot,
        string packageCache)
    {
        var suppliedVersion = Environment.GetEnvironmentVariable("PACKAGE_VERSION");
        if (!string.IsNullOrWhiteSpace(suppliedVersion))
        {
            return suppliedVersion;
        }

        var result = await RunDotnetAsync(
            repositoryRoot,
            ["msbuild", libraryProject, "-getProperty:PackageVersion"],
            packageCache);
        var resolvedVersion = result.StandardOutput.Trim();
        Assert.False(string.IsNullOrWhiteSpace(resolvedVersion), "MSBuild did not resolve a package version.");
        return resolvedVersion;
    }

    private static string GetConsumerPackageVersion(string consumerDirectory)
    {
        var project = XDocument.Load(Path.Combine(consumerDirectory, "Clc.Configuration.Consumer.csproj"));
        var packageReference = project
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "PackageReference"
                && (string?)element.Attribute("Include") == "Clc.Configuration");
        return (string?)packageReference.Attribute("Version")
            ?? packageReference.Elements().Single(element => element.Name.LocalName == "Version").Value;
    }

    private static string GetPackageMetadataValue(string packagePath, string elementName)
    {
        using var package = ZipFile.OpenRead(packagePath);
        return GetPackageMetadata(package).Elements().Single(element => element.Name.LocalName == elementName).Value;
    }

    private static XElement GetPackageMetadata(ZipArchive package)
    {
        var nuspec = Assert.Single(
            package.Entries,
            entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var stream = nuspec.Open();
        var document = XDocument.Load(stream);
        return document.Root!.Elements().Single(element => element.Name.LocalName == "metadata");
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

    private static void CopyConsumerProject(string consumerDirectory, string packageVersion)
    {
        var templateDirectory = Path.Combine(FindRepositoryRoot(), "samples", "Clc.Configuration.Consumer");
        var copiedProjectPath = Path.Combine(consumerDirectory, "Clc.Configuration.Consumer.csproj");
        File.Copy(
            Path.Combine(templateDirectory, "Clc.Configuration.Consumer.csproj"),
            copiedProjectPath);

        var project = XDocument.Load(copiedProjectPath);
        var packageReference = project
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "PackageReference"
                && (string?)element.Attribute("Include") == "Clc.Configuration");
        packageReference.SetAttributeValue("Version", packageVersion);
        project.Save(copiedProjectPath);
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
