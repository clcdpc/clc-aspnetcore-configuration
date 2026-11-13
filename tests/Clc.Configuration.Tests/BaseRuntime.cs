using System.Runtime.InteropServices;

namespace Clc.Configuration.Tests;

public sealed partial class PackageConsumerTests
{
    // A separate dotnet root contains the real base runtime and host, with no SDK or ASP.NET.
    // Invoking its dotnet directly prevents the installed ASP.NET runtime from masking a dependency.
    private static string CreateBaseRuntime(string destination)
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var root = Directory.GetParent(runtime)!.Parent!.Parent!.FullName;
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        Directory.CreateDirectory(destination);
        var host = Path.Combine(destination, executable);
        File.Copy(Path.Combine(root, executable), host);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(host, File.GetUnixFileMode(Path.Combine(root, executable))); }

        CopyTree(Path.Combine(root, "host", "fxr"), Path.Combine(destination, "host", "fxr"));
        CopyTree(runtime, Path.Combine(destination, "shared", "Microsoft.NETCore.App", Path.GetFileName(runtime)));
        return host;
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
