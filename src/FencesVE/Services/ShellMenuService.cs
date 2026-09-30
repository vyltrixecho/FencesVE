using Microsoft.Win32;

namespace FencesVE.Services;

/// <summary>
/// Wpisy FencesVE w menu kontekstowym pulpitu (prawy przycisk na pustym miejscu).
/// <para>
/// Wszystko siedzi w HKCU - bez uprawnien administratora i tylko dla biezacego uzytkownika.
/// Klucz DesktopBackground\Shell to standardowe miejsce na polecenia tla pulpitu.
/// </para>
/// <para>
/// Uwaga do Windows 11: nowe, skrocone menu pokazuje wylacznie wpisy wbudowane i te
/// z aplikacji pakietowanych (MSIX z handlerem IExplorerCommand). Klasyczne wpisy
/// rejestrowe - takie jak te - trafiaja do "Pokaz wiecej opcji" (Shift+F10).
/// </para>
/// </summary>
public static class ShellMenuService
{
    private const string ShellRoot = @"Software\Classes\DesktopBackground\Shell";

    private const string NewFenceKey = "FencesVE.NewFence";
    private const string SettingsKey = "FencesVE.Settings";

    private static string? ExePath => Environment.ProcessPath;

    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ShellRoot}\{NewFenceKey}\command");
            return key?.GetValue(null) is string command && command.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Zaklada (albo odswieza) oba wpisy. Odswiezanie ma znaczenie, bo w komendzie
    /// zapisana jest bezwzgledna sciezka do .exe - po przeniesieniu pliku wpisy trzeba przepisac.
    /// </summary>
    public static bool Install()
    {
        var exe = ExePath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return false;
        }

        try
        {
            WriteEntry(NewFenceKey, "Nowy fence tutaj", exe, "--new-fence");
            WriteEntry(SettingsKey, "Konfiguruj FencesVE", exe, "--settings");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Uninstall()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ShellRoot, writable: true);
            root?.DeleteSubKeyTree(NewFenceKey, throwOnMissingSubKey: false);
            root?.DeleteSubKeyTree(SettingsKey, throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Dopilnowuje, zeby stan w rejestrze zgadzal sie z ustawieniem w aplikacji.</summary>
    public static void Sync(bool shouldBeInstalled)
    {
        if (shouldBeInstalled)
        {
            Install();
        }
        else if (IsInstalled())
        {
            Uninstall();
        }
    }

    private static void WriteEntry(string keyName, string label, string exe, string arguments)
    {
        using var entry = Registry.CurrentUser.CreateSubKey($@"{ShellRoot}\{keyName}")
                          ?? throw new InvalidOperationException($"Nie udalo sie utworzyc klucza {keyName}.");

        entry.SetValue("MUIVerb", label, RegistryValueKind.String);
        entry.SetValue("Icon", $"{exe},0", RegistryValueKind.String);
        entry.SetValue("Position", "Bottom", RegistryValueKind.String);

        using var command = entry.CreateSubKey("command")
                            ?? throw new InvalidOperationException($"Nie udalo sie utworzyc klucza command dla {keyName}.");

        command.SetValue(null, $"\"{exe}\" {arguments}", RegistryValueKind.String);
    }
}
