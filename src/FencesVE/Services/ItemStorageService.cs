using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace FencesVE.Services;

/// <summary>Co przeniesc i dokad - gdy zwykle przeniesienie odbilo sie od uprawnien.</summary>
public readonly record struct PendingMove(string Source, string Target);

/// <summary>Jak skonczylo sie przeniesienie z podniesionymi uprawnieniami.</summary>
public enum ElevatedMoveResult
{
    /// <summary>Proces pomocniczy wykonal prace - wynik trzeba sprawdzic po plikach.</summary>
    Done,

    /// <summary>Uzytkownik odmowil w oknie Windows - nie ma o czym mowic.</summary>
    Cancelled,

    /// <summary>Nie udalo sie nawet uruchomic pomocnika.</summary>
    Failed,
}

/// <summary>
/// Magazyn plikow wciagnietych do fence'ow: <c>%APPDATA%\FencesVE\items\&lt;id fence'a&gt;</c>.
/// <para>
/// Windows nie pozwala ukryc pojedynczej ikony pulpitu - albo widac cala liste, albo nic.
/// Zeby ta sama rzecz nie lezala w dwoch miejscach naraz, dodanie do fence'a przenosi plik
/// z pulpitu tutaj, a usuniecie z fence'a odklada go z powrotem na pulpit.
/// </para>
/// <para>
/// Kazdy ruch jest przenosinami, nigdy kopiowaniem - kopia zrobilaby dokladnie te dwie ikony,
/// ktorych chcemy uniknac. Gdy przeniesienie sie nie uda (plik zajety, brak uprawnien),
/// zwracamy sciezke bez zmian i pozycja zostaje przy oryginale.
/// </para>
/// </summary>
public sealed class ItemStorageService
{
    private readonly string _root;

    public ItemStorageService(string configDirectory) =>
        _root = Path.Combine(configDirectory, "items");

    public bool IsInStorage(string path) =>
        !string.IsNullOrEmpty(path) &&
        path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Czy plik lezy wprost na pulpicie - tylko takie przenosimy do magazynu.</summary>
    public static bool IsOnDesktop(string path)
    {
        var parent = TryGetDirectory(path);
        if (parent is null)
        {
            return false;
        }

        foreach (var folder in DesktopFolders())
        {
            if (string.Equals(folder, parent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Czy plik lezy na pulpicie wszystkich uzytkownikow. Wiekszosc instalatorow wrzuca skrot
    /// wlasnie tam, a ten katalog jest dla zwyklego konta tylko do odczytu - przeniesienie
    /// wymaga osobnego procesu z podniesionymi uprawnieniami.
    /// </summary>
    public static bool IsOnCommonDesktop(string path)
    {
        var parent = TryGetDirectory(path);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        return parent is not null &&
               !string.IsNullOrEmpty(common) &&
               string.Equals(common, parent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rezerwuje wolna nazwe w magazynie, nie ruszajac pliku - sam ruch robi proces z uprawnieniami.</summary>
    public string ReserveTarget(string path, string fenceId)
    {
        var folder = Path.Combine(_root, fenceId);
        Directory.CreateDirectory(folder);

        return UniquePath(folder, Path.GetFileName(path));
    }

    /// <summary>
    /// Uruchamia te sama aplikacje z prosba o podniesienie uprawnien, zeby dokonczyla
    /// przenosiny, na ktore zwykle konto nie ma prawa. Lista par idzie przez plik tymczasowy -
    /// wiersz polecen nie musi wtedy walczyc z cudzyslowami ani dlugoscia.
    /// </summary>
    public static ElevatedMoveResult RunElevatedMove(IReadOnlyList<PendingMove> moves)
    {
        if (moves.Count == 0)
        {
            return ElevatedMoveResult.Done;
        }

        var list = Path.Combine(Path.GetTempPath(), $"fencesve-move-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllLines(list, moves.SelectMany(m => new[] { m.Source, m.Target }), Encoding.UTF8);

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return ElevatedMoveResult.Failed;
            }

            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            info.ArgumentList.Add(MoveItemsArgument);
            info.ArgumentList.Add(list);

            using var process = Process.Start(info);
            if (process is null)
            {
                return ElevatedMoveResult.Failed;
            }

            process.WaitForExit(120_000);
            return ElevatedMoveResult.Done;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ElevatedMoveResult.Cancelled; // "Nie" w oknie Windows - bez straszenia bledem.
        }
        catch
        {
            return ElevatedMoveResult.Failed;
        }
        finally
        {
            try
            {
                File.Delete(list);
            }
            catch
            {
                // Plik w katalogu tymczasowym - Windows go posprzata.
            }
        }
    }

    public const string MoveItemsArgument = "--move-items";

    /// <summary>Tryb pomocniczy: proces z uprawnieniami wykonuje zebrane przenosiny i konczy prace.</summary>
    public static void RunMoveList(string listPath)
    {
        string[] lines;

        try
        {
            lines = File.ReadAllLines(listPath, Encoding.UTF8);
        }
        catch
        {
            return;
        }

        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            var source = lines[i];
            var target = lines[i + 1];

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            if (File.Exists(target) || Directory.Exists(target))
            {
                continue;
            }

            try
            {
                var folder = Path.GetDirectoryName(target);
                if (folder is { Length: > 0 })
                {
                    Directory.CreateDirectory(folder);
                }

                if (Directory.Exists(source))
                {
                    Directory.Move(source, target);
                }
                else if (File.Exists(source))
                {
                    File.Move(source, target);
                }
            }
            catch
            {
                // Jedna pozycja moze sie nie udac - reszta listy ma isc dalej.
            }
        }
    }

    /// <summary>Przenosi plik do magazynu fence'a. Zwraca nowa sciezke albo stara, gdy sie nie udalo.</summary>
    public string MoveIn(string path, string fenceId)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }

            var folder = Path.Combine(_root, fenceId);
            Directory.CreateDirectory(folder);

            var target = UniquePath(folder, Path.GetFileName(path));

            if (Directory.Exists(path))
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            return target;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>Odklada plik z magazynu z powrotem na pulpit uzytkownika.</summary>
    public string MoveOutToDesktop(string path)
    {
        try
        {
            if (!IsInStorage(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                return path;
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop))
            {
                return path;
            }

            var target = UniquePath(desktop, Path.GetFileName(path));

            if (Directory.Exists(path))
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            return target;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// Przenosi plik z magazynu do Kosza - usuniecie, ktore da sie jeszcze cofnac.
    /// Pliki spoza magazynu nie sa nasze, wiec ich nie ruszamy. Zwraca true, gdy pliku
    /// pod ta sciezka juz nie ma.
    /// </summary>
    public bool MoveToRecycleBin(string path)
    {
        if (!IsInStorage(path))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(path))
            {
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
            }
            else if (File.Exists(path))
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
            }
        }
        catch
        {
            // Plik zajety albo brak uprawnien - pozycja zostaje w fence'ie.
        }

        return !File.Exists(path) && !Directory.Exists(path);
    }

    /// <summary>
    /// Wklada kopie pliku spoza pulpitu do magazynu - uzywane przy naprawie pozycji,
    /// ktorych skrot zostal tylko w menu Start. Tam oryginal ma zostac na miejscu.
    /// </summary>
    public string? CopyIn(string sourcePath, string fenceId)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return null;
            }

            var folder = Path.Combine(_root, fenceId);
            Directory.CreateDirectory(folder);

            var target = UniquePath(folder, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, target);

            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sprzata katalog usunietego fence'a, gdy nic w nim nie zostalo.</summary>
    public void TryRemoveFolder(string fenceId)
    {
        try
        {
            var folder = Path.Combine(_root, fenceId);

            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() == false)
            {
                Directory.Delete(folder);
            }
        }
        catch
        {
            // Pusty katalog wiecej nie zaszkodzi.
        }
    }

    private static IEnumerable<string> DesktopFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static string? TryGetDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Dokleja licznik do nazwy, gdy cel juz istnieje - zeby nic nie nadpisac.</summary>
    private static string UniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){extension}");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }
}
