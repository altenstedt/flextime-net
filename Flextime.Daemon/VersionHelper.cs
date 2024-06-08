using System.Reflection;

namespace Flextime.Daemon;

public static class VersionHelper
{
    public static string? GetVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var assemblyVersionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return assemblyVersionAttribute?.InformationalVersion ?? assembly.GetName().Version?.ToString();
    }
}