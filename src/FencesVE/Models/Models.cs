using System.Text.Json.Serialization;

namespace FencesVE.Models;

/// <summary>Typ kryterium, po ktorym regula dopasowuje pliki z pulpitu.</summary>
public enum RuleKind
{
    /// <summary>Dopasowanie po rozszerzeniu, np. "pdf" albo ".pdf".</summary>
    Extension,

    /// <summary>Dopasowanie po kategorii pliku (dokument, obraz, program...).</summary>
    Category,

    /// <summary>Dopasowanie po fragmencie nazwy (bez rozroznienia wielkosci liter).</summary>
    NameContains,

    /// <summary>Dopasowanie po wyrazeniu regularnym na nazwie pliku.</summary>
    NameRegex,

    /// <summary>Dopasowanie wszystkiego, co nie trafilo do zadnej innej reguly.</summary>
    Everything,
}

/// <summary>Zgrubna kategoria pliku uzywana przez reguly typu <see cref="RuleKind.Category"/>.</summary>
public enum FileCategory
{
    Unknown,
    Folder,
    Shortcut,
    Program,
    Document,
    Image,
    Video,
    Audio,
    Archive,
}

public sealed class SortRule
{
    public RuleKind Kind { get; set; } = RuleKind.Extension;

    /// <summary>Wzorzec zalezny od <see cref="Kind"/>: rozszerzenie, nazwa kategorii, fragment nazwy lub regex.</summary>
    public string Pattern { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

/// <summary>Krawedz ekranu, do ktorej fence sie chowa (wysuwana szuflada).</summary>
public enum EdgeDock
{
    None,
    Left,
    Right,
    Top,
    Bottom,
}

public sealed class FenceItem
{
    public string Path { get; set; } = "";

    /// <summary>Wlasna nazwa nadana przez uzytkownika. Gdy null, uzywana jest nazwa pliku.</summary>
    public string? DisplayName { get; set; }
}

public sealed class FenceModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Nowy fence";

    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 260;

    /// <summary>Czy fence jest zwiniety do samej belki tytulu.</summary>
    public bool RolledUp { get; set; }

    /// <summary>Wysokosc sprzed zwiniecia, zeby dalo sie wrocic do poprzedniego rozmiaru.</summary>
    public double RestoreHeight { get; set; } = 260;

    /// <summary>
    /// Szerokosc sprzed zwiniecia. Osobna od <see cref="RestoreHeight"/>, bo fence z belka
    /// z boku zwija sie w poziomie i to szerokosc trzeba potem odtworzyc.
    /// </summary>
    public double RestoreWidth { get; set; } = 320;

    /// <summary>Kolor tla w formacie #AARRGGBB. Gdy null, uzywany jest kolor globalny.</summary>
    public string? AccentOverride { get; set; }

    /// <summary>Rozmiar ikon. 0 oznacza "uzyj globalnego".</summary>
    public int IconSizeOverride { get; set; }

    /// <summary>Kolor czcionki w formacie #RRGGBB. Gdy null, uzywany jest kolor globalny.</summary>
    public string? TextColorOverride { get; set; }

    /// <summary>Rozmiar czcionki etykiet. 0 oznacza "uzyj globalnego".</summary>
    public double FontSizeOverride { get; set; }

    /// <summary>Wysokosc belki tytulu w pikselach. 0 oznacza "uzyj globalnej".</summary>
    public double HeaderHeightOverride { get; set; }

    /// <summary>Strona, po ktorej stoi belka tytulu. Gdy null, uzywana jest globalna.</summary>
    public HeaderSide? HeaderSideOverride { get; set; }

    /// <summary>Wyrownanie nazwy na belce. Gdy null, uzywane jest globalne.</summary>
    public TitleAlignment? TitleAlignmentOverride { get; set; }

    /// <summary>Krawedz, do ktorej fence sie chowa. None = zwykly fence na pulpicie.</summary>
    public EdgeDock Dock { get; set; } = EdgeDock.None;

    /// <summary>
    /// Fence zwija sie do belki sam, gdy kursor z niego zjedzie, i rozwija po najechaniu.
    /// Dla fence'a przyklejonego do krawedzi nie ma znaczenia - tam chowaniem steruje szuflada.
    /// </summary>
    public bool AutoRollUp { get; set; }

    /// <summary>
    /// Tryb "portal": fence pokazuje na zywo zawartosc wskazanego folderu zamiast wlasnej listy.
    /// Gdy null, fence trzyma wlasna liste pozycji.
    /// </summary>
    public string? PortalFolder { get; set; }

    public List<FenceItem> Items { get; set; } = new();
    public List<SortRule> Rules { get; set; } = new();

    /// <summary>
    /// Polozenie fence'a osobno dla kazdego ukladu monitorow (klucz: podpis z
    /// <see cref="FencesVE.Services.DisplayService.Signature"/>). Jedno X/Y na wszystkie
    /// uklady nie wystarczalo: po podpieciu innego monitora Windows sam przestawial okna,
    /// a najblizszy zapis nadpisywal nimi uklad uzytkownika.
    /// </summary>
    public Dictionary<string, FencePlacement> Placements { get; set; } = new();

    /// <summary>
    /// Czytniki ekranu odczytuja pozycje list po ToString() elementu, a nie po tym,
    /// co widac w szablonie - bez tego lista fence'ow w ustawieniach byla ogloszana
    /// jako "FencesVE.Models.FenceModel".
    /// </summary>
    public override string ToString() => Title;
}

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// Wyrownanie nazwy na belce. Przy belce pionowej dziala wzdluz niej: nazwa czyta sie
/// z dolu do gory, wiec <see cref="Left"/> to poczatek tekstu, czyli dol belki.
/// </summary>
public enum TitleAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>Jezyk interfejsu. <see cref="System"/> idzie za jezykiem Windows.</summary>
public enum AppLanguage
{
    System,
    Polish,
    English,
}

/// <summary>
/// Strona fence'a, po ktorej stoi belka tytulu. Przy belce pionowej (Left/Right)
/// nie ma na niej tytulu - zostaje sam waski uchwyt z przyciskiem menu.
/// </summary>
public enum HeaderSide
{
    Top,
    Bottom,
    Left,
    Right,
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>Kolor bazowy tla fence'a w formacie #RRGGBB.</summary>
    public string Accent { get; set; } = "#101418";

    /// <summary>Krycie tla fence'a, 0.05 - 1.0.</summary>
    public double Opacity { get; set; } = 0.55;

    public double CornerRadius { get; set; } = 2;

    public int IconSize { get; set; } = 48;

    /// <summary>Kolor czcionki w formacie #RRGGBB. Gdy null, wynika z motywu.</summary>
    public string? TextColor { get; set; }

    /// <summary>Rozmiar czcionki etykiet pod ikonami.</summary>
    public double FontSize { get; set; } = 11;

    /// <summary>Wysokosc belki tytulu fence'a w pikselach.</summary>
    public double HeaderHeight { get; set; } = 28;

    /// <summary>Domyslna strona belki tytulu dla fence'ow bez wlasnego ustawienia.</summary>
    public HeaderSide HeaderSide { get; set; } = HeaderSide.Top;

    /// <summary>Domyslne wyrownanie nazwy na belce.</summary>
    public TitleAlignment TitleAlignment { get; set; } = TitleAlignment.Left;

    /// <summary>
    /// Dwuklik w pusty pulpit chowa i pokazuje wszystkie fence'y. Domyslnie wlaczone -
    /// to skrot, ktory nic nie psuje: drugi dwuklik przywraca stan.
    /// </summary>
    public bool DesktopDoubleClickHidesFences { get; set; } = true;

    /// <summary>
    /// Czy pozycja wciagnieta do fence'a ma znikac z pulpitu. Windows nie pozwala ukryc
    /// pojedynczej ikony pulpitu, wiec plik jest przenoszony do magazynu fence'a,
    /// a przy usunieciu z fence'a wraca na pulpit.
    /// </summary>
    public bool MoveItemsIntoFence { get; set; } = true;

    /// <summary>
    /// Zapisany wyglad fence'a, ktory dostaja nowe fence'y. null = nic nie zapisano
    /// i nowy fence idzie za samymi ustawieniami globalnymi.
    /// </summary>
    public FenceTheme? SavedFenceTheme { get; set; }

    /// <summary>Jezyk interfejsu. Domyslnie za Windows.</summary>
    public AppLanguage Language { get; set; } = AppLanguage.System;

    /// <summary>Ile pikseli schowanego fence'a zostaje przy krawedzi, zeby dalo sie go zlapac.</summary>
    public double PeekWidth { get; set; } = 8;

    /// <summary>
    /// Czy schowany do krawedzi fence zostawia widoczna cala belke z tytulem zamiast
    /// cienkiego paska. Dziala, gdy belka stoi po tej stronie, do ktorej fence sie chowa.
    /// </summary>
    public bool PeekShowsHeader { get; set; }

    /// <summary>Po ilu milisekundach od zjechania kursorem fence chowa sie sam.</summary>
    public double AutoHideDelayMs { get; set; } = 600;

    /// <summary>
    /// Czy natywne ikony pulpitu maja byc ukryte. Domyslnie wylaczone - ukrycie cudzych
    /// ikon bez pytania przy pierwszym uruchomieniu byloby zbyt inwazyjne.
    /// Uzytkownik wlacza to z zasobnika albo z ustawien.
    /// </summary>
    public bool HideDesktopIcons { get; set; }

    /// <summary>Gdy wlaczone, fence'ow nie da sie przesuwac ani skalowac mysza.</summary>
    public bool LockFences { get; set; }

    /// <summary>Czy pokazywac etykiety (nazwy) pod ikonami.</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>Czy reguly maja automatycznie wciagac nowe pliki z pulpitu.</summary>
    public bool AutoSortEnabled { get; set; } = true;

    public bool RunAtStartup { get; set; }

    /// <summary>
    /// Czy pokazywac ikone w zasobniku systemowym. Bez niej do ustawien prowadzi menu pulpitu
    /// albo ponowne uruchomienie FencesVE - druga instancja otwiera wtedy okno ustawien.
    /// </summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Czy dodac wpisy FencesVE do menu kontekstowego pulpitu.</summary>
    public bool DesktopMenuIntegration { get; set; } = true;

    /// <summary>
    /// Adres pliku JSON z opisem najnowszego wydania. Musi byc HTTPS
    /// (http tylko dla localhost). Puste = aktualizacje wylaczone.
    /// </summary>
    public string UpdateFeedUrl { get; set; } = "";

    /// <summary>Czy sprawdzac aktualizacje przy starcie aplikacji.</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;
}

/// <summary>
/// Zapisany wyglad fence'a - to, co w modelu fence'a jest nadpisaniem ustawien globalnych.
/// Sluzy za wzorzec dla nowych fence'ow i do przeniesienia wygladu na istniejacy.
/// <para>
/// Puste pole (null albo 0) znaczy "wedlug ustawien globalnych" - dokladnie tak samo,
/// jak w samym fence'ie, wiec motyw zapisuje sie i odtwarza bez tlumaczenia wartosci.
/// </para>
/// </summary>
public sealed class FenceTheme
{
    public string? Accent { get; set; }
    public string? TextColor { get; set; }
    public double FontSize { get; set; }
    public int IconSize { get; set; }
    public double HeaderHeight { get; set; }
    public HeaderSide? HeaderSide { get; set; }
    public TitleAlignment? TitleAlignment { get; set; }

    public static FenceTheme FromFence(FenceModel fence) => new()
    {
        Accent = fence.AccentOverride,
        TextColor = fence.TextColorOverride,
        FontSize = fence.FontSizeOverride,
        IconSize = fence.IconSizeOverride,
        HeaderHeight = fence.HeaderHeightOverride,
        HeaderSide = fence.HeaderSideOverride,
        TitleAlignment = fence.TitleAlignmentOverride,
    };

    public void ApplyTo(FenceModel fence)
    {
        fence.AccentOverride = Accent;
        fence.TextColorOverride = TextColor;
        fence.FontSizeOverride = FontSize;
        fence.IconSizeOverride = IconSize;
        fence.HeaderHeightOverride = HeaderHeight;
        fence.HeaderSideOverride = HeaderSide;
        fence.TitleAlignmentOverride = TitleAlignment;
    }
}

/// <summary>
/// Zapamietane polozenie fence'a w jednym ukladzie monitorow. Pozycja okna i monitora
/// sa w pikselach fizycznych - przy kilku monitorach o roznym DPI tylko one sa jednoznaczne.
/// Rozmiary fence'a zostaja w jednostkach WPF, tak jak w <see cref="FenceModel"/>.
/// </summary>
public sealed class FencePlacement
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    /// <summary>Granice monitora, na ktorym stal fence - po nich rozpoznajemy ten sam ekran.</summary>
    public int MonitorLeft { get; set; }
    public int MonitorTop { get; set; }
    public int MonitorWidth { get; set; }
    public int MonitorHeight { get; set; }

    /// <summary>Obszar roboczy tego monitora (bez paska zadan).</summary>
    public int AreaLeft { get; set; }
    public int AreaTop { get; set; }
    public int AreaWidth { get; set; }
    public int AreaHeight { get; set; }

    public uint Dpi { get; set; } = 96;

    public double Width { get; set; }
    public double Height { get; set; }
    public double RestoreWidth { get; set; }
    public double RestoreHeight { get; set; }

    /// <summary>
    /// Polozenie wyliczone przez nas po zmianie monitorow, a nie ulozone przez uzytkownika.
    /// Takie przy nastepnej wizycie w tym ukladzie liczymy od nowa, z najswiezszego ukladu.
    /// </summary>
    public bool Auto { get; set; }
}

public sealed class LayoutFile
{
    public int Version { get; set; } = 1;

    /// <summary>Uklad monitorow z ostatniego zapisu - pozwala wykryc zmiane miedzy uruchomieniami.</summary>
    public string? DisplaySignature { get; set; }

    public AppSettings Settings { get; set; } = new();
    public List<FenceModel> Fences { get; set; } = new();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(LayoutFile))]
public partial class LayoutJsonContext : JsonSerializerContext
{
}
