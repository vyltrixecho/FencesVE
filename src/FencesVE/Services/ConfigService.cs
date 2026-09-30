using System.IO;
using System.Text.Json;
using FencesVE.Models;

namespace FencesVE.Services;

/// <summary>Wczytuje i zapisuje uklad fence'ow do %APPDATA%\FencesVE\layout.json.</summary>
public sealed class ConfigService
{
    private readonly object _ioLock = new();

    /// <summary>Pomija utworzenie kopii przy najblizszym zapisie - patrz <see cref="Load"/>.</summary>
    private bool _skipBackupOnNextSave;

    public string ConfigDirectory { get; }

    public string LayoutPath { get; }

    /// <summary>True, gdy przy starcie nie bylo jeszcze zapisanego ukladu.</summary>
    public bool IsFirstRun { get; private set; }

    public ConfigService()
    {
        ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FencesVE");
        LayoutPath = Path.Combine(ConfigDirectory, "layout.json");
    }

    /// <summary>Poprzedni dobry uklad. Sluzy do odtworzenia po awarii albo uszkodzeniu pliku.</summary>
    public string BackupPath => LayoutPath + ".bak";

    /// <summary>True, gdy uklad zostal odtworzony z kopii zapasowej.</summary>
    public bool RestoredFromBackup { get; private set; }

    public LayoutFile Load()
    {
        if (!File.Exists(LayoutPath) && !File.Exists(BackupPath))
        {
            IsFirstRun = true;
            return CreateDefault();
        }

        if (TryRead(LayoutPath, out var layout))
        {
            return layout;
        }

        // Glowny plik nie da sie wczytac - najpierw probujemy kopii z poprzedniego zapisu.
        // Dopiero gdy i ona zawiedzie, schodzimy do ukladu domyslnego.
        if (TryRead(BackupPath, out var fromBackup))
        {
            RestoredFromBackup = true;

            // Najblizszy zapis nie moze zrobic kopii z pliku, ktory wlasnie okazal sie
            // nieczytelny - wepchnalby smieci na miejsce jedynej dobrej kopii.
            _skipBackupOnNextSave = true;

            return fromBackup;
        }

        // IsFirstRun celowo zostaje false: uszkodzony plik to nie pierwsze uruchomienie,
        // a automatyczny import calego pulpitu do domyslnych fence'ow tylko pogorszylby sprawe.
        return CreateDefault();
    }

    private bool TryRead(string path, out LayoutFile layout)
    {
        layout = new LayoutFile();

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize(json, LayoutJsonContext.Default.LayoutFile);

            if (parsed is null)
            {
                return false;
            }

            layout = parsed;
            return true;
        }
        catch (Exception ex)
        {
            TryBackupBrokenFile(path, ex);
            return false;
        }
    }

    public void Save(LayoutFile layout)
    {
        lock (_ioLock)
        {
            Directory.CreateDirectory(ConfigDirectory);

            var json = JsonSerializer.Serialize(layout, LayoutJsonContext.Default.LayoutFile);

            // Zapis przez plik tymczasowy, zeby przerwany zapis nie zostawil polowy JSON-a.
            var tmp = LayoutPath + ".tmp";

            try
            {
                File.WriteAllText(tmp, json);

                if (File.Exists(LayoutPath) && !_skipBackupOnNextSave)
                {
                    // File.Replace robi podmiane i kopie zapasowa w jednej operacji systemu plikow,
                    // wiec nie ma chwili, w ktorej zaden z plikow nie jest kompletny.
                    File.Replace(tmp, LayoutPath, BackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    // Move z nadpisaniem, a nie Delete + Move: to drugie zostawialo chwile,
                    // w ktorej ukladu nie ma w ogole, i przerwanie wlasnie wtedy kosztowaloby
                    // caly plik.
                    File.Move(tmp, LayoutPath, overwrite: true);
                }

                _skipBackupOnNextSave = false;
            }
            catch
            {
                // Niedokonczony plik tymczasowy nie moze zostac - przy nastepnym zapisie
                // File.Replace potknalby sie o niego.
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    // Trudno - i tak zglaszamy blad wyzej.
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Odklada nieczytelny plik na bok razem z opisem bledu. Kopiujemy, a nie przenosimy:
    /// przeniesienie glownego pliku zabieraloby ostatnia szanse na reczne odzyskanie czegos
    /// z jego wnetrza, gdyby kopia zapasowa tez byla do niczego.
    /// </summary>
    private static void TryBackupBrokenFile(string path, Exception ex)
    {
        try
        {
            if (File.Exists(path))
            {
                var broken = path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(path, broken, overwrite: true);
                File.WriteAllText(broken + ".txt", ex.ToString());
            }
        }
        catch
        {
            // Nic wiecej nie zrobimy - lecimy z domyslnym ukladem.
        }
    }

    /// <summary>Uklad startowy dla pierwszego uruchomienia: trzy fence'y z sensownymi regulami.</summary>
    private static LayoutFile CreateDefault()
    {
        var layout = new LayoutFile();

        layout.Fences.Add(new FenceModel
        {
            Title = "Programy",
            X = 60,
            Y = 60,
            Width = 360,
            Height = 280,
            RestoreHeight = 280,
            Rules =
            {
                new SortRule { Kind = RuleKind.Extension, Pattern = "lnk" },
                new SortRule { Kind = RuleKind.Extension, Pattern = "exe" },
                new SortRule { Kind = RuleKind.Extension, Pattern = "url" },
            },
        });

        layout.Fences.Add(new FenceModel
        {
            Title = "Dokumenty",
            X = 60,
            Y = 370,
            Width = 360,
            Height = 260,
            RestoreHeight = 260,
            Rules =
            {
                new SortRule { Kind = RuleKind.Category, Pattern = nameof(FileCategory.Document) },
            },
        });

        layout.Fences.Add(new FenceModel
        {
            Title = "Media",
            X = 450,
            Y = 60,
            Width = 360,
            Height = 260,
            RestoreHeight = 260,
            Rules =
            {
                new SortRule { Kind = RuleKind.Category, Pattern = nameof(FileCategory.Image) },
                new SortRule { Kind = RuleKind.Category, Pattern = nameof(FileCategory.Video) },
                new SortRule { Kind = RuleKind.Category, Pattern = nameof(FileCategory.Audio) },
            },
        });

        return layout;
    }
}
