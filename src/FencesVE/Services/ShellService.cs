using System.Diagnostics;
using System.IO;
using FencesVE.Models;

namespace FencesVE.Services;

/// <summary>Uruchamianie plikow i klasyfikacja ich do kategorii uzywanych przez reguly.</summary>
public static class ShellService
{
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".rtf", ".md", ".pdf", ".doc", ".docx", ".odt", ".xls", ".xlsx", ".ods",
        ".ppt", ".pptx", ".odp", ".csv", ".epub", ".mobi", ".tex", ".log", ".json", ".xml",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico",
        ".svg", ".heic", ".avif", ".psd", ".raw", ".cr2", ".nef",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma", ".opus", ".mid", ".midi",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab",
    };

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".com", ".appref-ms",
    };

    private static readonly HashSet<string> ShortcutExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url",
    };

    public static FileCategory Categorize(string path)
    {
        if (Directory.Exists(path))
        {
            return FileCategory.Folder;
        }

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return FileCategory.Unknown;
        }

        if (ShortcutExtensions.Contains(ext)) return FileCategory.Shortcut;
        if (ProgramExtensions.Contains(ext)) return FileCategory.Program;
        if (ImageExtensions.Contains(ext)) return FileCategory.Image;
        if (VideoExtensions.Contains(ext)) return FileCategory.Video;
        if (AudioExtensions.Contains(ext)) return FileCategory.Audio;
        if (ArchiveExtensions.Contains(ext)) return FileCategory.Archive;
        if (DocumentExtensions.Contains(ext)) return FileCategory.Document;

        return FileCategory.Unknown;
    }

    /// <summary>Nazwa pokazywana pod ikona: nazwa pliku bez rozszerzenia dla skrotow i programow.</summary>
    public static string GetDisplayName(string path)
    {
        if (Directory.Exists(path))
        {
            return new DirectoryInfo(path.TrimEnd(Path.DirectorySeparatorChar)).Name;
        }

        var category = Categorize(path);
        return category is FileCategory.Shortcut or FileCategory.Program
            ? Path.GetFileNameWithoutExtension(path)
            : Path.GetFileName(path);
    }

    /// <summary>Otwiera plik/folder domyslnym programem.</summary>
    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    /// <summary>Otwiera Eksplorator z zaznaczonym plikiem.</summary>
    public static void RevealInExplorer(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true,
        });
    }

    /// <summary>Pokazuje systemowe okno wlasciwosci pliku.</summary>
    public static void ShowProperties(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            Verb = "properties",
        });
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
