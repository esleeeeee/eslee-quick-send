using Microsoft.Win32;

namespace Eslee.QuickSend.Windows.Startup;

/// <summary>
/// Registers QuickSend to start with the current user's Windows sign-in.
/// </summary>
/// <remarks>
/// Registration is per-user (<c>HKCU\...\Run</c>), so no administrator rights are needed
/// and uninstalling for one user never touches another. Writing the same value name twice
/// overwrites it, which is what keeps duplicate registrations from accumulating.
/// </remarks>
public sealed class AutoStartService(string executablePath)
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "eslee QuickSend";

    /// <summary>Argument that tells a launched instance to start in the tray with no window.</summary>
    public const string StartupArgument = "--startup";

    /// <summary>The exact command written to the Run key.</summary>
    public static string BuildCommand(string executablePath) => $"\"{executablePath}\" {StartupArgument}";

    /// <summary>True when this process was started by the auto-start registration.</summary>
    public static bool LaunchedByAutoStart(IEnumerable<string> commandLineArguments) =>
        commandLineArguments.Skip(1).Any(static argument =>
            string.Equals(argument, StartupArgument, StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && value.Contains(executablePath, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the per-user Run key.");
        if (enabled) key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
