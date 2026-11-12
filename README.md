# CLC Configuration

`Clc.Configuration` is a small .NET hosting library for loading an application's authoritative configuration from one `application.json` file under the operating system's ProgramData directory.

The package ID is `Clc.Configuration`. See [NuGet.org for available versions](https://www.nuget.org/packages/Clc.Configuration#versions-body), then add the desired version to the application project.

## Use it during startup

Add the package to an ASP.NET Core application, then call `AddClcConfiguration` immediately after creating the builder and before registering services that read application configuration:

```csharp
using Clc.Configuration;

public static class Program
{
    private const string ApplicationDirectoryName = "clc-ssrs-web";

    public static int Main(string[] args)
    {
        // Optional: opt in before logging, host construction, or configuration reads.
        if (ClcConfigurationCommandLine.TryHandle(args, ApplicationDirectoryName, out var exitCode))
        {
            return exitCode;
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.AddClcConfiguration(ApplicationDirectoryName);

        builder.Services.AddOptions<MyOptions>()
            .Bind(builder.Configuration.GetSection("MyOptions"));

        var app = builder.Build();
        app.Run();
        return 0;
    }
}
```

The application directory name belongs in `Program.cs` so the deployment convention is visible alongside startup. It must be one portable directory name. Absolute paths, path separators, traversal names, invalid filename characters, and Windows reserved device names (including names followed by extensions) are rejected.

## Generic Host and Windows workers

The same extension supports `IHostApplicationBuilder`, including `HostApplicationBuilder`:

```csharp
using Clc.Configuration;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddClcConfiguration("clc-scheduler-worker");
builder.Services.AddHostedService<Worker>();
using var host = builder.Build();
await host.RunAsync();
```

`Worker` is application-owned. Call the extension before service registration that reads configuration. The web and worker overloads use the same file parser, provider replacement, retained listener controls, and restart-only snapshot. Host environment and content root have already been established by the host builder. No web server is constructed by this overload, and no production path override is added. The early `--clc-appdata-directory` opt-in also works for workers; see the [Worker sample](samples/Clc.Configuration.Worker).

For a Windows service, retain the application's usual `AddWindowsService` registration and `Microsoft.Extensions.Hosting.WindowsServices` dependency. This library does not install or manage services. It still references `Microsoft.AspNetCore.App`: framework-dependent workers require the matching ASP.NET Core 10 shared runtime as well as .NET 10, even though they do not host HTTP. The package supplies the framework-reference metadata, not a runtime installation. Migrating a worker that currently merges environment files or variables requires explicitly adopting the authoritative-file contract below.

## File convention

The library resolves the OS-level `Environment.SpecialFolder.CommonApplicationData` directory and reads:

```text
<CommonApplicationData>/<application directory name>/Config/application.json
```

For example, `AddClcConfiguration("clc-ssrs-web")` reads the `clc-ssrs-web/Config/application.json` file beneath ProgramData on Windows and the common application data directory on Linux. The same path is used in every ASP.NET Core environment. The library does not select environment-specific files, create a replacement file, or provide a production path override.

Provision `application.json` before starting the application and grant the application identity read access. The library does not ship or copy operational settings into the application directory.

## Application data paths and command-line discovery

`ClcApplicationPaths.Resolve(ApplicationDirectoryName)` returns absolute `ApplicationDataDirectory`, `ConfigurationDirectory`, and `ConfigurationFilePath` properties using the same resolver and directory-name validation as normal startup. The application data directory is `<CommonApplicationData>/<application directory name>`, not the executable or content directory. Resolution neither reads configuration nor creates files or directories; it works before `application.json` is provisioned. It does not establish existence, access permissions, or deployment readiness. Logs, keys, ACLs, and provisioning remain application-owned.

Applications can explicitly opt in with the early `TryHandle` call shown above. On `true`, return its exit code immediately, including failures. Do not put this call after logger initialization or `WebApplication.CreateBuilder`; `AddClcConfiguration` does not automatically handle commands or terminate the process.

Run a trusted, complete published application on the target machine:

```text
dotnet "<publish directory>/MyApplication.dll" --clc-appdata-directory
```

The exact, case-sensitive standalone flag prints only the absolute application data directory plus a newline on stdout and exits successfully. It does not start the host, load configuration, or initialize logging. Extra arguments, duplicate flags, or `=value` / `:value` forms produce a fixed stderr message and exit code `2`; path resolution failure produces a fixed stderr message and exit code `1`. Other arguments are untouched and return `false` so the app can continue normally. The library never calls `Environment.Exit`.

PowerShell can capture the result without loading .NET assemblies into its own process:

```powershell
$appDataDirectory = & dotnet $applicationDll --clc-appdata-directory
if ($LASTEXITCODE -ne 0) { throw 'Application data directory discovery failed.' }
$configurationPath = Join-Path $appDataDirectory 'Config/application.json'
```

Use a known command-capable application version, not an older binary that may ignore an unsupported flag. Invocation requires that published app's compatible runtime (unless self-contained) and reports the invoked binary's convention on the current OS, not another running deployment's state. It can run separately while IIS hosts the app; no service identity, configuration secrets, or running web server are required for discovery.

## Provider behavior

The file is read once during the call and parsed into an in-memory startup snapshot with no file watcher or reload. Its keys are authoritative. Existing application configuration providers are cleared, including `appsettings*.json`, environment variables, command-line application values, and providers previously added to the builder. Values for these hosting listener controls are retained from the builder as a lower-precedence source, so an `application.json` entry with the same key remains authoritative:

- `urls`
- `http_ports`
- `https_ports`
- `PreferHostingUrls`

Keep application settings, including `AllowedHosts` and any `Kestrel` endpoint settings the application needs, in the external file or configure them directly in code. The library does not add middleware, bind options, or validate application-specific rules.

For example, an application can keep its host and domain options together in the one file:

```json
{
  "AllowedHosts": "portal.example.org",
  "MyOptions": {
    "Enabled": true
  }
}
```

The JSON root must be an object. Duplicate property names are rejected case-insensitively at every object depth, and property names containing `:` are rejected because they collide with configuration key paths. JSON comments and trailing commas are accepted. UTF-8 is supported with or without a BOM; UTF-16 and UTF-32 are supported with their BOMs. Invalid text encoding, malformed JSON, and files over 4 MiB stop startup.

## Failure behavior

Startup fails immediately if the OS common application data directory is unavailable or the required file cannot be loaded. File validation failures use `ClcConfigurationException`, which derives from `InvalidOperationException` for existing handlers. Its `Reason` distinguishes missing and unreadable files, invalid encoding, malformed JSON, invalid root shape, duplicate keys, ambiguous keys, size and depth limits, and invalid framework log levels. Diagnostics include the normalized file path and safe numeric parser location when available; they omit configuration values, JSON property paths and names, document excerpts, parser messages, and inner exceptions. The file is never rewritten.

## Hostless deployment validation

`ClcConfigurationReader` exposes the same parser and file validation without constructing a web host:

```csharp
using Clc.Configuration;
using Microsoft.Extensions.Configuration;

try
{
    // Pass the absolute path selected by the existing deployment workflow.
    var configuration = ClcConfigurationReader.LoadFromFile("clc-ssrs-web", configurationPath);
    var database = configuration.GetSection("Database").Get<DatabaseOptions>()
        ?? throw new InvalidOperationException("Database settings are required.");

    ValidateDatabaseForThisApplication(database);
    return CreateDeploymentSuccessResponse();
}
catch (ClcConfigurationException error)
{
    return CreateDeploymentFailureResponse(error.Reason, error.Message);
}
```

`Load("clc-ssrs-web")` reads the conventional CommonApplicationData path; `LoadFromFile` accepts an explicit absolute path for the existing deployment validation flow and parser tests. Both return an in-memory snapshot. The example's `DatabaseOptions`, validation method, and response methods are application-owned; the library does not validate application sections, database access, key compatibility, or configuration revisions.

This operation does not construct a web host, start listeners or background services, initialize logging, generate keys, or modify files. It checks the file under the caller's identity. A successful check does not prove that the IIS identity can read or decrypt every value, or that external services are available. Normal startup still reads and validates the conventional file under the actual runtime identity.

CLC.Deployment remains responsible for orchestrating deployment validation; this reader is only the shared file-loading and validation API.

## Consumer testing helpers

`Clc.Configuration.Testing` provides opt-in in-memory replacement and disposable JSON fixtures. Both use isolated state and preserve the normal listener-setting precedence. Ordinary parser and application tests can stay in the system temporary directory without writing to ProgramData:

```csharp
using Clc.Configuration;
using Clc.Configuration.Testing;

using var fixture = ClcConfigurationTesting.CreateJsonFile("""
    { "MyOptions": { "Enabled": true } }
    """);
var configuration = ClcConfigurationReader.LoadFromFile("clc-ssrs-web", fixture.FilePath);
```

For a consumer app with shared startup composition, override the factory's host builder. Use the app's real executable entry point as `TEntryPoint`; the factory supplies its own host builder and does not invoke production `Program`:

```csharp
using Clc.Configuration;
using Clc.Configuration.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

public sealed class PortalFactory : WebApplicationFactory<Program>
{
    protected override IHostBuilder CreateHostBuilder() =>
        Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web => web.UseStartup<PortalStartup>());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddClcConfigurationForTesting(new Dictionary<string, string?>
            {
                ["MyOptions:Enabled"] = "true",
                ["MyOptions:Endpoint"] = "https://example.test"
            }));
    }
}
```

`Program` is the actual entry point in the consumer executable, and `PortalStartup` is the same application composition used by production. Keep `AddClcConfiguration` in production `Program` before building the app; the test factory uses `PortalStartup` directly, so it does not call production `Program` or read the conventional operational file. If the app uses top-level statements, expose its generated entry point with `public partial class Program { }`. Add `Microsoft.AspNetCore.Mvc.Testing` to the consumer test project. `AddClcConfiguration` remains source-compatible and has no path override.

## Publish guard

The NuGet package includes a **default-enabled** MSBuild publish guard, imported transitively. `dotnet publish` fails with `CLC_CONFIG001` if an SDK-resolved publish item has the filename `application.json`, case-insensitively, at any directory depth. Both source and destination names are checked, so linking or renaming an operational file does not bypass the guard. The guard fails before files are copied rather than silently excluding them; ordinary builds are unaffected.

Exclude operational files in the consuming project, for example:

```xml
<ItemGroup>
  <Content Update="application.json" CopyToPublishDirectory="Never" />
</ItemGroup>
```

Use the item's actual path and item type (`None` instead of `Content` when appropriate). Intentionally publishable examples such as `application.Example.json` and `Config/example.json` remain allowed. They must not contain deployment credentials.

A project with a deliberate exception can explicitly opt out:

```xml
<PropertyGroup>
  <ClcConfigurationValidatePublish>false</ClcConfigurationValidatePublish>
</PropertyGroup>
```

Only `false` disables the guard. It checks the SDK publish item list, not JSON contents, arbitrary secret files, or files copied by custom deployment steps. Use a clean publish destination: it does not remove or inspect stale files from earlier publishes. Certificate policy, deployment validation, and operational provisioning remain outside this guard.

## Build and test

The focused tests cover path resolution, directory-name validation, provider authority, listener-setting retention, startup snapshots, hostless and temporary-file reads, encoding and parser boundaries, malformed escaped property names, redacted errors, disposable fixture cleanup, and package-backed public API usage. A real `WebApplicationFactory.CreateClient` integration test confirms that isolated test settings replace earlier providers while listener settings remain available; it does not create a conventional configuration file. The packed-package smoke test publishes a package-backed consumer and runs discovery and malformed-command checks as separate processes on Windows and Linux, before any conventional configuration exists. It verifies exact stdout/stderr, exit codes, and that discovery leaves the application data directory absent. It also verifies package metadata, checks the public startup extension against a unique missing path without creating it, loads a temporary JSON fixture through the hostless reader, and starts a consumer host with isolated test settings. A second packed consumer compiles/publishes the Worker sample, checks its runtime framework metadata, and starts a real Generic Host hosted service with isolated settings. Publish fixtures cover default-on rejection, explicit opt-out, allowed examples, excluded operational files, case-insensitive nested names, and renamed source/output paths. Both suites run on Windows and Linux; no Windows service is installed. All test fixtures stay in temporary directories; tests do not write to machine ProgramData.

```text
dotnet restore Clc.Configuration.slnx
dotnet build Clc.Configuration.slnx --configuration Release --no-restore
dotnet test Clc.Configuration.slnx --configuration Release --no-build
```

## License and NuGet package

This library is licensed under MIT. The repository's `LICENSE` file identifies Central Library Consortium as the copyright holder, and the package includes that file, the README, MIT license metadata, and the `clcdpc/clc-configuration` Git repository link.

Before the first release, verify availability of the `Clc.Configuration` ID and the `Clc.` prefix policy in the CLC NuGet organization account.

## NuGet trusted publishing setup

The release workflow follows the CLC `rest-client` pattern. Its trusted-publishing login identity is the NuGet username `mfields`; the package owner is the `clcdpc` NuGet organization. Those values serve different roles. The package author metadata is `Central Library Consortium` and does not grant ownership.

Before publishing the first package:

1. In the `clcdpc` NuGet organization account, confirm the `Clc.Configuration` ID and `Clc.` prefix are available and that the package will be owned by `clcdpc`. The existing `Clc.Rest.Client` package is owned by `clcdpc`; verify the new package's owner list after its first upload as well.
2. Add a NuGet.org trusted-publishing policy owned by the `clcdpc` organization for GitHub Actions. Set repository owner `clcdpc`, repository `clc-configuration`, workflow file `build-test-publish.yaml`, environment `nuget-production`, and scope the policy to `Clc.Configuration` where package scopes are available. Use the existing CLC login identity `mfields` in the workflow. No long-lived NuGet API key or GitHub secret is required.
3. In GitHub repository settings, create the `nuget-production` environment. Restrict deployments to tags beginning with `v` and add any required reviewer protection for the CLC release process. The workflow grants `id-token: write` only to this publish job.

The NuGet policy and GitHub environment above are setup requirements for this repository; they have not been created or verified here. For current policy steps, see [NuGet.org Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing).

## Releasing from a version tag

The Git tag is the source of both the assembly version and NuGet package version. Use `v<major>.<minor>.<patch>` for a stable release and optionally add a SemVer prerelease suffix such as `-beta.2`. For example, `v1.2.3-beta.2` produces package version `1.2.3-beta.2`. The workflow validates each `v` tag before restore and build; malformed tags fail with an error.

```text
git tag <release-tag>
git push origin <release-tag>
```

No GitHub Release object is needed. The tag starts Windows and Linux build/test checks, package validation, and artifact upload. Untagged pull request and `master` builds use `0.0.0-ci.<run number>.<attempt>`; local builds use `0.0.0-local` unless a version is supplied. The packed consumer smoke test uses the same resolved version in its package reference and its isolated package cache. After checks pass, a separate publish job downloads the package and SHA-256 sidecar, verifies the checksum and tag, enters `nuget-production`, and obtains a short-lived key through NuGet trusted publishing. Pull requests and ordinary branch pushes cannot publish.

## Adoption notes

Each application should add the package, declare its own directory name in `Program.cs`, call the extension before dependent service registration, provision its external `application.json`, and retain application-specific binding and validation in that application. The package does not migrate any existing application.
