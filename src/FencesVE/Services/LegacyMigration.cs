using System.Diagnostics;
using System.IO;
using System.Threading;
using FencesVE.Models;
using Microsoft.Win32;

namespace FencesVE.Services;

/// <summary>
/// Przejscie z poprzedniej nazwy aplikacji na FencesVE.
/// <para>
/// Stara wersja trzymala uklad i magazyn w <c>%APPDATA%\&lt;stara nazwa&gt;</c>, a w rejestrze
/// wpis autostartu i menu pulpitu pod swoja nazwa. Bez przeniesienia tego wszystkiego
/// FencesVE wystartowalby jak przy pierwszym uruchomieniu, a pliki wciagniete do fence'ow
/// zostalyby w katalogu, do ktorego nic juz nie zaglada.
/// </para>
/// </summary>
public static class LegacyMigration
{
    private const string LegacyName = "OpenFences";
    private const string LegacyMutex = LegacyName + ".SingleInstance";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ShellRoot = @"Software\Classes\DesktopBackground\Shell";

    private static string LegacyConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        LegacyName);

    /// <summary>Czy stara wersja wciaz dziala - wtedy trzyma swoj katalog i zapisuje do niego uklad.</summary>
    public static bool IsLegacyRunning()
    {
        if (!Mutex.TryOpenExisting(LegacyMutex, out var mutex))
        {
            return false;
        }

        mutex.Dispose();
        return true;
    }

    /// <summary>Zamyka stara wersje. Siedzi w zasobniku bez glownego okna, wiec grzecznie sie nie da.</summary>
    public static void StopLegacy()
    {
        var self = Environment.ProcessId;

        foreach (var process in Process.GetProcessesByName(LegacyName))
        {
            using (process)
            {
                if (process.Id == self)
                {
                    continue;
                }

                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch
                {
                    // Proces mogl sie wlasnie zamknac sam.
                }
            }
        }
    }

    /// <summary>
    /// Przenosi dane i wpisy rejestru starej wersji. Wolane przed wczytaniem ukladu,
    /// juz po zajeciu muteksu - dwie instancje nie moga przenosic tego samego naraz.
    /// </summary>
    public static void Run(string configDirectory)
    {
        MoveConfigDirectory(configDirectory);
        MigrateStartupEntry();
        RemoveLegacyShellMenu();
    }

    /// <summary>
    /// Przestawia sciezki pozycji ze starego magazynu na nowy. Pozycja, ktorej plik
    /// wciaz lezy w starym katalogu (nie dal sie przeniesc), zostaje przy nim - tam dziala.
    /// </summary>
    public static bool FixStoredPaths(LayoutFile layout, string configDirectory)
    {
        var oldRoot = LegacyConfigDirectory + Path.DirectorySeparatorChar;
        var newRoot = configDirectory + Path.DirectorySeparatorChar;
        var changed = false;

        string? Rewrite(string? path)
        {
            if (path is null ||
                !path.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase) ||
                ShellService.Exists(path))
            {
                return null;
            }

            var candidate = newRoot + path[oldRoot.Length..];
            return ShellService.Exists(candidate) ? candidate : null;
        }

        foreach (var fence in layout.Fences)
        {
            foreach (var item in fence.Items)
            {
                if (Rewrite(item.Path) is { } moved)
                {
                    item.Path = moved;
                    changed = true;
                }
            }

            if (Rewrite(fence.PortalFolder) is { } portal)
            {
                fence.PortalFolder = portal;
                changed = true;
            }
        }

        return changed;
    }

    private static void MoveConfigDirectory(string configDirectory)
    {
        var legacy = LegacyConfigDirectory;

        try
        {
            if (!Directory.Exists(legacy))
            {
                return;
            }

            if (!Directory.Exists(configDirectory))
            {
                Directory.Move(legacy, configDirectory);
                return;
            }

            // Nowy katalog juz jest (np. log awarii sprzed migracji) - dokladamy to, czego
            // w nim brakuje, niczego nie nadpisujac.
            MoveMissing(legacy, configDirectory);
            TryDeleteEmpty(legacy);
        }
        catch
        {
            // Nieudane przeniesienie nie moze zablokowac startu. Pozycje ze starego katalogu
            // zostaja przy swoich sciezkach i dalej dzialaja.
        }
    }

    private static void MoveMissing(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));

            try
            {
                if (!File.Exists(destination) && !Directory.Exists(destination))
                {
                    File.Move(file, destination);
                }
            }
            catch
            {
                // Plik zajety - reszta idzie dalej.
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(directory));

            try
            {
                if (Directory.Exists(destination))
                {
                    MoveMissing(directory, destination);
                    TryDeleteEmpty(directory);
                }
                else if (!File.Exists(destination))
                {
                    Directory.Move(directory, destination);
                }
            }
            catch
            {
                // Jak wyzej.
            }
        }
    }

    private static void TryDeleteEmpty(string directory)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Pusty katalog wiecej nie zaszkodzi.
        }
    }

    /// <summary>Stary wpis autostartu wskazuje na stary plik - zastepujemy go wpisem FencesVE.</summary>
    private static void MigrateStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyName) is null)
            {
                return;
            }

            key.DeleteValue(LegacyName, throwOnMissingValue: false);
            StartupService.SetEnabled(true);
        }
        catch
        {
            // Autostart da sie wlaczyc recznie w ustawieniach.
        }
    }

    /// <summary>Nowe wpisy menu pulpitu zaklada potem zwykla synchronizacja przy starcie.</summary>
    private static void RemoveLegacyShellMenu()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ShellRoot, writable: true);
            root?.DeleteSubKeyTree($"{LegacyName}.NewFence", throwOnMissingSubKey: false);
            root?.DeleteSubKeyTree($"{LegacyName}.Settings", throwOnMissingSubKey: false);
        }
        catch
        {
            // Stary wpis najwyzej zostanie w "Pokaz wiecej opcji".
        }
    }
}
