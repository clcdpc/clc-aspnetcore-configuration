using System.Diagnostics;
using System.Text;

namespace Clc.Configuration.Tests;

public sealed class PackageConsumerTests
{
    [Fact]
    public async Task PackedNuGetPackageBuildsAndRunsInAnIndependentConsumer()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var packageFeed = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "feed")).FullName;
        var consumerDirectory = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "consumer")).FullName;
        CopyConsumerTemplate(consumerDirectory);

        var configuration = GetBuildConfiguration();
        var repositoryRoot = FindRepositoryRoot();
        var libraryProject = Path.Combine(repositoryRoot, "src", "Clc.Configuration", "Clc.Configuration.csproj");
        var consumerProject = Path.Combine(consumerDirectory, "Clc.Configuration.Consumer.csproj");

        await RunDotnetAsync(
            repositoryRoot,
            ["pack", libraryProject, "--configuration", configuration, "--no-build", "--no-restore", "--output", packageFeed]);
        await RunDotnetAsync(consumerDirectory, ["restore", consumerProject, "--source", packageFeed]);
        await RunDotnetAsync(consumerDirectory, ["build", consumerProject, "--configuration", configuration, "--no-restore"]);

        var runResult = await RunDotnetAsync(
            consumerDirectory,
            ["run", "--project", consumerProject, "--configuration", configuration, "--no-build", "--no-restore"],
            expectSuccess: false);

        var output = runResult.StandardOutput + runResult.StandardError;
        Assert.DoesNotContain("SECRET", output);
        Assert.Contains("Required external application configuration file is missing", output);

        var expectedPath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "clc-configuration-consumer",
            "Config",
            "application.json"));
        Assert.Contains(expectedPath, output);
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool expectSuccess = true)
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
            expectSuccess == (result.ExitCode == 0),
            $"dotnet {string.Join(' ', arguments)} exited with {result.ExitCode}.{Environment.NewLine}{result.StandardOutput}{result.StandardError}");
        return result;
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

    private static void CopyConsumerTemplate(string consumerDirectory)
    {
        var templateDirectory = Path.Combine(FindRepositoryRoot(), "samples", "Clc.Configuration.Consumer");
        File.Copy(Path.Combine(templateDirectory, "Clc.Configuration.Consumer.csproj"),
            Path.Combine(consumerDirectory, "Clc.Configuration.Consumer.csproj"));
        File.Copy(Path.Combine(templateDirectory, "Program.cs"), Path.Combine(consumerDirectory, "Program.cs"));
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
