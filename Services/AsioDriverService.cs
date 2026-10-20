using System.IO;
using Microsoft.Win32;

namespace Sonator.Services;

public sealed record AsioDriver(string Name, string Clsid);

// Lists the ASIO drivers installed on this machine. Windows registers each driver under HKLM\SOFTWARE\ASIO,
// and 32-bit drivers also appear under the WOW6432Node view.
public sealed class AsioDriverService
{
    private static readonly string[] RegistryRoots = { @"SOFTWARE\ASIO", @"SOFTWARE\WOW6432Node\ASIO" };

    public IReadOnlyList<AsioDriver> GetInstalledDrivers()
    {
        var entries = new List<(string Name, string? Clsid)>();
        foreach (var root in RegistryRoots)
        {
            try
            {
                using var rootKey = Registry.LocalMachine.OpenSubKey(root);
                if (rootKey is null)
                {
                    continue;
                }

                foreach (var name in rootKey.GetSubKeyNames())
                {
                    using var driverKey = rootKey.OpenSubKey(name);
                    entries.Add((name, driverKey?.GetValue("CLSID") as string));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A registry view that cannot be read is skipped; the other root may still list drivers.
            }
        }

        return Merge(entries);
    }

    // Keeps one entry per driver name, drops entries without a CLSID, and sorts by name.
    internal static IReadOnlyList<AsioDriver> Merge(IEnumerable<(string Name, string? Clsid)> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Clsid) && seen.Add(entry.Name))
            .Select(entry => new AsioDriver(entry.Name, entry.Clsid!))
            .OrderBy(driver => driver.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
