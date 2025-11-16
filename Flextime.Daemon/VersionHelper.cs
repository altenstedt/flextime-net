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

        return semanticVersion.WithMetadataParsedFrom(semanticVersion.Metadata[..7]).ToString();
    }
}