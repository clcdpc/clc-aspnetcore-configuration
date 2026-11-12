using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace Clc.Configuration;

/// <summary>Loads application configuration from the CLC ProgramData convention.</summary>
public static class ClcConfigurationBuilderExtensions
{
    /// <summary>
    /// Replaces the builder's application configuration providers with the application's
    /// fixed ProgramData <c>application.json</c> snapshot.
    /// </summary>
    /// <param name="builder">The ASP.NET Core application builder.</param>
    /// <param name="applicationDirectoryName">One directory name beneath the operating system's ProgramData directory.</param>
    /// <returns>The same builder, with the external configuration loaded.</returns>
    public static WebApplicationBuilder AddClcConfiguration(
        this WebApplicationBuilder builder,
        string applicationDirectoryName)
    {
        AddClcConfiguration((IHostApplicationBuilder)builder, applicationDirectoryName);
        return builder;
    }

    /// <summary>
    /// Replaces a Generic Host or worker builder's application configuration providers with
    /// the same authoritative startup snapshot used by the web integration.
    /// </summary>
    /// <param name="builder">The application host builder, before services read configuration.</param>
    /// <param name="applicationDirectoryName">One directory name beneath OS CommonApplicationData.</param>
    /// <returns>The same builder, with the external configuration loaded.</returns>
    public static IHostApplicationBuilder AddClcConfiguration(
        this IHostApplicationBuilder builder,
        string applicationDirectoryName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var path = ExternalConfigurationLoader.ResolvePath(applicationDirectoryName);
        ExternalConfigurationLoader.AddFromFile(builder, applicationDirectoryName, path);
        return builder;
    }
}
