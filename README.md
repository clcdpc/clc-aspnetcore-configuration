# CLC Configuration

`Clc.Configuration` is a small ASP.NET Core library for loading an application's authoritative configuration from one `application.json` file under the operating system's ProgramData directory.

The package is `Clc.Configuration` and the current package version is `0.1.0`.

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

Startup fails immediately with an `InvalidOperationException` if the OS common application data directory is unavailable, the required file is missing or unreadable, or the file contains malformed JSON, duplicate keys, unsupported encoding, or invalid `Logging:LogLevel` values. The message identifies the configuration path and remediation while omitting values from the file and parser exceptions. The file is never rewritten.

## Build and test

The focused tests cover path resolution, directory-name validation, provider authority, listener-setting retention, startup snapshot behavior, encoding, malformed input, redacted errors, and a consumer that packs the NuGet package and starts a host through the public `AddClcConfiguration` entry point. The package smoke test provisions a uniquely named fixture under the actual CommonApplicationData directory and uses an isolated NuGet package cache, so the test identity needs permission to create and remove that fixture.

```text
dotnet restore Clc.Configuration.slnx
dotnet build Clc.Configuration.slnx --configuration Release --no-restore
dotnet test Clc.Configuration.slnx --configuration Release --no-build
```

## Adoption notes

Each application should add the package, declare its own directory name in `Program.cs`, call the extension before dependent service registration, provision its external `application.json`, and retain application-specific binding and validation in that application. This first package version does not migrate any existing application.
