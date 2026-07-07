using System.Reflection;
using Semver;

namespace Flextime.Daemon;

public static class VersionHelper
{
    public static string? GetVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var assemblyVersionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var version = assemblyVersionAttribute?.InformationalVersion ?? assembly.GetName().Version?.ToString();

        if (string.IsNullOrEmpty(version))
        {
            return null;
        }
        
        var semanticVersion = SemVersion.Parse(version);

        // Shorten a full commit hash in the build metadata, if present.
        return semanticVersion.Metadata.Length > 7
            ? semanticVersion.WithMetadataParsedFrom(semanticVersion.Metadata[..7]).ToString()
            : semanticVersion.ToString();
    }
}