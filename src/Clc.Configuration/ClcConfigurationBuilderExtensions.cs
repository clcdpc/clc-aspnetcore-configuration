using Microsoft.AspNetCore.Builder;

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
        ArgumentNullException.ThrowIfNull(builder);

        var path = ExternalConfigurationLoader.ResolvePath(applicationDirectoryName);
        ExternalConfigurationLoader.AddFromFile(builder, applicationDirectoryName, path);
        return builder;
    }
}
