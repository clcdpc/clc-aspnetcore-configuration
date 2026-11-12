# CLC Configuration

`Clc.Configuration` is a small ASP.NET Core library for loading an application's authoritative configuration from one `application.json` file under the operating system's ProgramData directory.

The package is `Clc.Configuration` and the current package version is `0.1.0-beta.1`.

After the package is available from the application's configured NuGet feed, add that package version to the application project.

## Use it during startup

Add the package to an ASP.NET Core application, then call `AddClcConfiguration` immediately after creating the builder and before registering services that read application configuration:

```csharp
using Clc.Configuration;

public static class Program
{
    private const string ApplicationDirectoryName = "clc-ssrs-web";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddClcConfiguration(ApplicationDirectoryName);

        builder.Services.AddOptions<MyOptions>()
            .Bind(builder.Configuration.GetSection("MyOptions"));

        var app = builder.Build();
        app.Run();
    }
}
```

The application directory name belongs in `Program.cs` so the deployment convention is visible alongside startup. It must be one portable directory name. Absolute paths, path separators, traversal names, invalid filename characters, and Windows reserved device names (including names followed by extensions) are rejected.

## File convention

The library resolves the OS-level `Environment.SpecialFolder.CommonApplicationData` directory and reads:

```text
<CommonApplicationData>/<application directory name>/Config/application.json
```

For example, `AddClcConfiguration("clc-ssrs-web")` reads the `clc-ssrs-web/Config/application.json` file beneath ProgramData on Windows and the common application data directory on Linux. The same path is used in every ASP.NET Core environment. The library does not select environment-specific files, create a replacement file, or provide a production path override.

Provision `application.json` before starting the application and grant the application identity read access. The library does not ship or copy operational settings into the application directory.

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

For a consumer app that exposes a test host entry point and shares its application composition, a `WebApplicationFactory` can replace configuration explicitly:

```csharp
using Clc.Configuration.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

public sealed class PortalTestEntryPoint
{
    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(web => web.UseStartup<PortalStartup>());
}

public sealed class PortalFactory : WebApplicationFactory<PortalTestEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddClcConfigurationForTesting(new Dictionary<string, string?>
            {
                ["MyOptions:Enabled"] = "true",
                ["MyOptions:Endpoint"] = "https://example.test"
            })
        );
    }
}
```

`PortalTestEntryPoint` and `PortalStartup` are application-owned examples; this test entry point reuses application composition while omitting the production file-loading call. Add `Microsoft.AspNetCore.Mvc.Testing` in the consumer test project. A factory that directly runs a production `Program` which calls `AddClcConfiguration` still executes that normal startup call and reads the conventional file. The helper does not intercept startup. `AddClcConfiguration` remains source-compatible and has no path override.

## Build and test

The focused tests cover path resolution, directory-name validation, provider authority, listener-setting retention, startup snapshots, hostless and temporary-file reads, encoding and parser boundaries, redacted errors, disposable fixture cleanup, and package-backed public API usage. The package smoke test also starts a consumer through the public `AddClcConfiguration` entry point at the conventional CommonApplicationData path. It provisions only a uniquely named fixture and restores through an isolated NuGet package cache; that one smoke test needs permission to create and remove its fixture. Ordinary tests do not write to ProgramData.

```text
dotnet restore Clc.Configuration.slnx
dotnet build Clc.Configuration.slnx --configuration Release --no-restore
dotnet test Clc.Configuration.slnx --configuration Release --no-build
```

## License and NuGet package

This library is licensed under MIT. The repository's `LICENSE` file identifies Central Library Consortium as the copyright holder, and the package includes that file, the README, MIT license metadata, and the `clcdpc/clc-configuration` Git repository link.

The NuGet ID `Clc.Configuration` returned no search results from the NuGet.org search API on 2026-11-11. This only checks indexed packages; it does not confirm that the ID or its prefix is unreserved. Before the first release, verify availability and the `Clc.` prefix policy in the CLC NuGet organization account.

## NuGet trusted publishing setup

The release workflow follows the CLC `rest-client` pattern. Its trusted-publishing login identity is the NuGet username `mfields`; the package owner is the `clcdpc` NuGet organization. Those values serve different roles. The package author metadata is `Central Library Consortium` and does not grant ownership.

Before publishing the first package:

1. In the `clcdpc` NuGet organization account, confirm the `Clc.Configuration` ID and `Clc.` prefix are available and that the package will be owned by `clcdpc`. The existing `Clc.Rest.Client` package is owned by `clcdpc`; verify the new package's owner list after its first upload as well.
2. Add a NuGet.org trusted-publishing policy owned by the `clcdpc` organization for GitHub Actions. Set repository owner `clcdpc`, repository `clc-configuration`, workflow file `build-test-publish.yaml`, environment `nuget-production`, and scope the policy to `Clc.Configuration` where package scopes are available. Use the existing CLC login identity `mfields` in the workflow. No long-lived NuGet API key or GitHub secret is required.
3. In GitHub repository settings, create the `nuget-production` environment. Restrict deployments to the workflow's version tags (`v*.*.*` and `v*.*.*-*`) and add any required reviewer protection for the CLC release process. The workflow grants `id-token: write` only to this publish job.

The NuGet policy and GitHub environment above are setup requirements for this repository; they have not been created or verified here. For current policy steps, see [NuGet.org Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing).

## Creating a beta release

After this change is merged and the setup above is complete, publish the initial `0.1.0-beta.1` package by tagging the merged commit with the exact project version:

```text
git switch master
git pull --ff-only
git tag v0.1.0-beta.1
git push origin v0.1.0-beta.1
```

No GitHub Release object is needed. The tag starts the Windows and Linux build/test checks, package validation, and artifact upload. The release tag must equal `v` plus the `<Version>` in the library project. The packed consumer restores the same version referenced in the sample project, using an isolated package cache. After checks pass, a separate publish job downloads the package and SHA-256 sidecar, verifies the checksum, enters `nuget-production`, and obtains a short-lived key through NuGet trusted publishing. Pull requests and ordinary branch pushes cannot publish. For later versions, update the library project, sample consumer package reference, and this README together before tagging the matching version.

## Adoption notes

Each application should add the package, declare its own directory name in `Program.cs`, call the extension before dependent service registration, provision its external `application.json`, and retain application-specific binding and validation in that application. This first package version does not migrate any existing application.
