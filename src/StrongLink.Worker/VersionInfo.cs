using System.Reflection;

namespace StrongLink.Worker;

public static class VersionInfo
{
    public static string Version
    {
        get
        {
            var informationalVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (informationalVersion != null)
            {
                // Strip build metadata (everything after '+')
                var plusIndex = informationalVersion.IndexOf('+');
                if (plusIndex > 0)
                {
                    return informationalVersion.Substring(0, plusIndex);
                }
                return informationalVersion;
            }

            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        }
    }
}
