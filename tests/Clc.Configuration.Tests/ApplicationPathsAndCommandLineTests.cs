namespace Clc.Configuration.Tests;

public sealed class ApplicationPathsAndCommandLineTests
{
    private const string Command = "--clc-appdata-directory";

    [Fact]
    public void PublicPathsShareTheProductionConventionWithoutCreatingAnything()
    {
        var name = $"clc-path-test-{Guid.NewGuid():N}";
        var expectedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), name));
        Assert.False(Directory.Exists(expectedRoot));

        var paths = ClcApplicationPaths.Resolve(name);

        Assert.Equal(expectedRoot, paths.ApplicationDataDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "Config"), paths.ConfigurationDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "Config", "application.json"), paths.ConfigurationFilePath);
        Assert.Equal(ExternalConfigurationLoader.ResolvePath(name), paths.ConfigurationFilePath);
        var failure = Assert.Throws<ClcConfigurationException>(() => ClcConfigurationReader.Load(name));
        Assert.Equal(ClcConfigurationErrorReason.MissingFile, failure.Reason);
        Assert.Equal(paths.ConfigurationFilePath, failure.FilePath);
        Assert.False(Directory.Exists(expectedRoot));
        Assert.False(File.Exists(expectedRoot));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("CON.txt")]
    [InlineData("name\nsecond-line")]
    public void PublicResolverRetainsDirectoryNameValidation(string name)
    {
        Assert.Throws<ArgumentException>(() => ClcApplicationPaths.Resolve(name));
    }

    [Fact]
    public void PublicResolverRejectsNullName()
    {
        Assert.Throws<ArgumentNullException>(() => ClcApplicationPaths.Resolve(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public void InternalTestResolverRequiresAnAbsoluteRoot(string root)
    {
        Assert.Throws<ArgumentException>(() => ClcApplicationPaths.Resolve("clc-path-test", root));
    }

    [Fact]
    public void CommandWritesOnlyTheApplicationDataDirectoryAndDoesNotCreateIt()
    {
        var name = $"clc-command-test-{Guid.NewGuid():N}";
        var paths = ClcApplicationPaths.Resolve(name);
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] args = [Command];

        Assert.True(ClcConfigurationCommandLine.TryHandle(args, name, out var exitCode, output, error));

        Assert.Equal(0, exitCode);
        Assert.Equal(paths.ApplicationDataDirectory + Environment.NewLine, output.ToString());
        Assert.Empty(error.ToString());
        Assert.Equal([Command], args);
        Assert.False(Directory.Exists(paths.ApplicationDataDirectory));
        Assert.False(File.Exists(paths.ConfigurationFilePath));
    }

    [Theory]
    [InlineData()]
    [InlineData("--urls", "http://127.0.0.1:5000")]
    [InlineData("--other-command", "SECRET")]
    [InlineData("--clc-appdata-directory-other")]
    public void UnrelatedArgumentsAreUntouchedAndDoNotResolvePaths(params string[] args)
    {
        var original = args.ToArray();
        using var output = new StringWriter();
        using var error = new StringWriter();

        // An invalid name must not affect normal startup when the command is absent.
        Assert.False(ClcConfigurationCommandLine.TryHandle(args, "../invalid", out var exitCode, output, error));

        Assert.Equal(0, exitCode);
        Assert.Equal(original, args);
        Assert.Empty(output.ToString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData(Command, "SECRET")]
    [InlineData("SECRET", Command)]
    [InlineData(Command, Command)]
    [InlineData(Command + "=SECRET")]
    [InlineData(Command + ":SECRET")]
    [InlineData(Command + "=")]
    public void MalformedDiscoveryIsHandledAsFailureWithoutEchoingArguments(params string[] args)
    {
        var original = args.ToArray();
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.True(ClcConfigurationCommandLine.TryHandle(args, "clc-test", out var exitCode, output, error));

        Assert.Equal(2, exitCode);
        Assert.Equal(original, args);
        Assert.Empty(output.ToString());
        Assert.Equal($"Use {Command} alone, without a value or other arguments.{Environment.NewLine}", error.ToString());
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Theory]
    [InlineData("../SECRET")]
    [InlineData("")]
    [InlineData(null)]
    public void ResolutionFailureIsHandledWithoutExposingExceptionDetails(string? name)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.True(ClcConfigurationCommandLine.TryHandle([Command], name!, out var exitCode, output, error));

        Assert.Equal(1, exitCode);
        Assert.Empty(output.ToString());
        Assert.Equal("Unable to resolve the application's CommonApplicationData directory." + Environment.NewLine, error.ToString());
    }
}
