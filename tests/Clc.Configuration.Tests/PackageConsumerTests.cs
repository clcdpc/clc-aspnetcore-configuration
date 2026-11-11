using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Clc.Configuration.Tests;

public sealed class PackageConsumerTests
{
    private const string FixtureConsumerProgram = """
        using System;
        using System.Threading.Tasks;
        using Clc.Configuration;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Hosting;

        public static class Program
        {
            public static async Task<int> Main(string[] args)
            {
                if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
                {
                    throw new ArgumentException("Expected the isolated application directory name.");
                }

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.AddClcConfiguration(args[0]);

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
        using var fixture = new CommonApplicationDataFixture();
        fixture.WriteConfiguration("""
            { "Consumer": { "Fixture": "loaded-from-package" } }
            """);

        var packageFeed = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "feed")).FullName;
        var packageCache = Path.Combine(temporaryDirectory.Path, "nuget-cache");
        var consumerDirectory = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "consumer")).FullName;
        CopyConsumerProject(consumerDirectory);
        File.WriteAllText(Path.Combine(consumerDirectory, "Program.cs"), FixtureConsumerProgram);

        var configuration = GetBuildConfiguration();
        var repositoryRoot = FindRepositoryRoot();
        var libraryProject = Path.Combine(repositoryRoot, "src", "Clc.Configuration", "Clc.Configuration.csproj");
        var expectedPackageVersion = GetProjectProperty(libraryProject, "Version");
        Assert.Equal(expectedPackageVersion, GetConsumerPackageVersion(consumerDirectory));
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
            ["build", consumerProject, "--configuration", configuration, "--no-restore"],
            packageCache);

        var runResult = await RunDotnetAsync(
            consumerDirectory,
            ["run", "--project", consumerProject, "--configuration", configuration, "--no-build", "--no-restore", "--", fixture.ApplicationDirectoryName],
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

    private static string GetProjectProperty(string projectPath, string propertyName)
    {
        var project = XDocument.Load(projectPath);
        var property = project
            .Descendants()
            .Single(element => element.Name.LocalName == propertyName);
        return property.Value;
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

    private sealed class CommonApplicationDataFixture : IDisposable
    {
        private readonly string _applicationDirectory;
        private readonly string _configurationDirectory;
        private readonly string _configurationPath;
        private bool _ownsApplicationDirectory;
        private bool _ownsConfigurationDirectory;
        private bool _ownsConfigurationFile;

        internal CommonApplicationDataFixture()
        {
            var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonApplicationData) || !Path.IsPathFullyQualified(commonApplicationData))
            {
                throw new InvalidOperationException("The operating-system CommonApplicationData directory is unavailable; package smoke-test coverage requires it.");
            }

            ApplicationDirectoryName = $"clc-configuration-package-{Guid.NewGuid():N}";
            _applicationDirectory = Path.Combine(commonApplicationData, ApplicationDirectoryName);
            _configurationDirectory = Path.Combine(_applicationDirectory, "Config");
            _configurationPath = Path.Combine(_configurationDirectory, "application.json");
            if (Directory.Exists(_applicationDirectory) || File.Exists(_applicationDirectory))
            {
                throw new IOException($"The unique package smoke-test fixture path already exists; refusing to overwrite it: '{_applicationDirectory}'.");
            }
        }

        internal string ApplicationDirectoryName { get; }

        internal void WriteConfiguration(string json)
        {
            Directory.CreateDirectory(_applicationDirectory);
            _ownsApplicationDirectory = true;
            Directory.CreateDirectory(_configurationDirectory);
            _ownsConfigurationDirectory = true;

            using var file = new FileStream(_configurationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            _ownsConfigurationFile = true;
            var bytes = Encoding.UTF8.GetBytes(json);
            file.Write(bytes);
        }

        public void Dispose()
        {
            if (_ownsConfigurationFile && File.Exists(_configurationPath))
            {
                File.Delete(_configurationPath);
            }

            if (_ownsConfigurationDirectory && Directory.Exists(_configurationDirectory))
            {
                DeleteIfEmpty(_configurationDirectory);
            }

            if (_ownsApplicationDirectory && Directory.Exists(_applicationDirectory))
            {
                DeleteIfEmpty(_applicationDirectory);
            }
        }

        private static void DeleteIfEmpty(string path)
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
    }

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
