using System.IO;
using System.Windows;
using System.Windows.Threading;
using FencesVE.Models;
using FencesVE.Views;

namespace FencesVE.Services;

/// <summary>
/// Serce aplikacji: trzyma uklad, okna fence'ow, obserwuje pulpit
/// i pilnuje, zeby stan na ekranie zgadzal sie z ustawieniami.
/// </summary>
public sealed class FenceManager
{
    private readonly ConfigService _config;
    private readonly Dictionary<string, FenceWindow> _windows = new(StringComparer.Ordinal);
    private readonly List<FileSystemWatcher> _desktopWatchers = new();
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _maintenance;
    private readonly DispatcherTimer _displayDebounce;

    private readonly DesktopClickService _desktopClicks;

    private SettingsWindow? _settingsWindow;
    private int _maintenanceTicks;
    private bool _desktopIconsWereVisibleAtStart;

    /// <summary>
    /// Uklad monitorow wlasnie sie zmienia. Windows przestawia wtedy okna po swojemu -
    /// do czasu odtworzenia ukladu nie wolno tych pozycji zapisac, bo nadpisalyby uklad uzytkownika.
    /// </summary>
    private bool _displayChangePending;

    /// <summary>Nieudany zapis zglaszamy raz, a nie przy kazdej probie - patrz <see cref="SaveNow"/>.</summary>
    private bool _saveErrorReported;

    public FenceManager(ConfigService config, IconService icons)
    {
        _config = config;
        Icons = icons;
        Layout = config.Load();

        _saveDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SaveNow();
        };

        _maintenance = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Krotki interwal, zeby fence'y wracaly szybko po "Pokaz pulpit" i po restarcie Explorera.
            Interval = TimeSpan.FromMilliseconds(1200),
        };
        _maintenance.Tick += OnMaintenanceTick;

        // Podpiecie stacji dokujacej czy monitora to seria zdarzen w krotkim odstepie,
        // a Windows przestawia okna jeszcze chwile po nich. Ukladamy fence'y po uspokojeniu.
        _displayDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000),
        };
        _displayDebounce.Tick += (_, _) =>
        {
            _displayDebounce.Stop();
            _displayChangePending = false;
            RestoreLayoutForDisplays();
        };

        Storage = new ItemStorageService(config.ConfigDirectory);

        _desktopClicks = new DesktopClickService(Dispatcher.CurrentDispatcher);
        _desktopClicks.DesktopDoubleClicked += ToggleFencesHidden;

        // Uklad przeniesiony ze starej wersji wskazuje jeszcze na jej magazyn.
        if (LegacyMigration.FixStoredPaths(Layout, config.ConfigDirectory))
        {
            RequestSave();
        }
    }

    public IconService Icons { get; }

    /// <summary>Magazyn plikow wciagnietych do fence'ow - patrz <see cref="ItemStorageService"/>.</summary>
    public ItemStorageService Storage { get; }

    public UpdateCoordinator Updates { get; } = new();

    public LayoutFile Layout { get; }

    public AppSettings Settings => Layout.Settings;

    public IReadOnlyCollection<FenceWindow> Windows => _windows.Values;

    /// <summary>Sciezka pliku ukladu - pokazywana w ustawieniach.</summary>
    public string LayoutPathForDisplay => _config.LayoutPath;

    /// <summary>Wywolywane, gdy zmieni sie cos, co tray musi odzwierciedlic (np. blokada).</summary>
    public event Action? StateChanged;

    // ---- start i stop ------------------------------------------------------

    public void Start()
    {
        ThemeService.Apply(Settings);

        _desktopIconsWereVisibleAtStart = DesktopService.AreDesktopIconsVisible();

        if (_config.IsFirstRun)
        {
            ImportDesktopItems(silent: true);
        }

        foreach (var model in Layout.Fences)
        {
            ShowFence(model);
        }

        ApplyDesktopIconVisibility();
        SetupDesktopWatchers();
        ApplyDesktopClickShortcut();

        // Po odtworzeniu z kopii glowny plik jest wciaz ten uszkodzony. Przepisujemy go od razu,
        // zeby stan na dysku znow byl spojny, a nie dopiero przy pierwszej zmianie ukladu.
        if (_config.RestoredFromBackup)
        {
            SaveNow();
        }

        _maintenance.Start();

        // Aplikacja mogla wystartowac przy innym ukladzie monitorow niz ten z ostatniego zapisu.
        // ContextIdle: po odtworzeniu zwiniecia i szuflad, ktore okna robia przy Loaded.
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, RestoreLayoutForDisplays);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void Shutdown()
    {
        _maintenance.Stop();
        _saveDebounce.Stop();
        _displayDebounce.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _desktopClicks.Dispose();
        Icons.Dispose();

        foreach (var watcher in _desktopWatchers)
        {
            watcher.Dispose();
        }

        _desktopWatchers.Clear();

        // Nie zostawiamy uzytkownika z pulpitem bez ikon.
        if (_desktopIconsWereVisibleAtStart)
        {
            DesktopService.SetDesktopIconsVisible(true);
        }

        // Zapis MUSI isc przed zamknieciem okien: SaveNow przepisuje pozycje
        // z zywych okien do modelu, a z pusta lista nie mialby czego przepisac.
        SaveNow();

        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
        }

        _windows.Clear();
    }

    // ---- fence'y -----------------------------------------------------------

    private void ShowFence(FenceModel model)
    {
        var window = new FenceWindow(model, this);
        _windows[model.Id] = window;
        window.Show();
    }

    public FenceWindow CreateFence(string? title = null, string? portalFolder = null)
    {
        var model = new FenceModel
        {
            Title = title ?? Loc.Get("Fence_DefaultTitle"),
            X = 160 + Layout.Fences.Count * 28,
            Y = 160 + Layout.Fences.Count * 28,
            PortalFolder = portalFolder,
        };

        // Nowy fence dziedziczy zapisany wyglad - o to chodzi w "motywie belki".
        Settings.SavedFenceTheme?.ApplyTo(model);

        // Nowy fence ma byc widoczny - gdyby wpadl w ukryty zestaw, wygladaloby to
        // na brak reakcji na polecenie.
        SetFencesHidden(false);

        Layout.Fences.Add(model);
        ShowFence(model);
        RequestSave();

        return _windows[model.Id];
    }

    /// <summary>Tworzy fence w miejscu, gdzie stoi kursor - uzywane przez menu pulpitu.</summary>
    public FenceWindow CreateFenceAtCursor(string? title = null)
    {
        var cursor = System.Windows.Forms.Control.MousePosition;

        var window = CreateFence(title);
        window.PlaceAtDevicePoint(cursor.X, cursor.Y);

        RequestSave();
        return window;
    }

    public void DeleteFence(string id)
    {
        // Pliki wciagniete do tego fence'a wracaja na pulpit - inaczej zostalyby
        // zamkniete w magazynie, do ktorego nikt juz nie zaglada.
        var model = Layout.Fences.FirstOrDefault(f => f.Id == id);
        if (model is not null)
        {
            foreach (var item in model.Items.Where(i => Storage.IsInStorage(i.Path)))
            {
                Storage.MoveOutToDesktop(item.Path);
            }
        }

        if (_windows.Remove(id, out var window))
        {
            window.Close();
        }

        Layout.Fences.RemoveAll(f => f.Id == id);
        Storage.TryRemoveFolder(id);
        RequestSave();
    }

    public void RemoveFromFence(string fenceId, IEnumerable<string> paths)
    {
        if (_windows.TryGetValue(fenceId, out var window))
        {
            window.RemovePaths(paths);
        }
    }

    /// <summary>Czy sciezka siedzi juz w ktorymkolwiek fence'ie.</summary>
    public bool IsTracked(string path) =>
        Layout.Fences.Any(f => f.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)));

    // ---- ustawienia --------------------------------------------------------

    public void ApplySettingsToAll()
    {
        ThemeService.Apply(Settings);

        foreach (var window in _windows.Values)
        {
            window.ApplySettings(Settings);
        }

        ApplyDesktopIconVisibility();
        ApplyDesktopClickShortcut();
        RequestSave();
        StateChanged?.Invoke();
    }

    private void ApplyDesktopClickShortcut() =>
        _desktopClicks.SetEnabled(Settings.DesktopDoubleClickHidesFences);

    /// <summary>Czy fence'y sa teraz schowane skrotem. Stan chwilowy - nie zapisuje sie do pliku.</summary>
    public bool FencesHidden { get; private set; }

    public void ToggleFencesHidden() => SetFencesHidden(!FencesHidden);

    /// <summary>
    /// Chowa albo pokazuje wszystkie fence'y naraz. Ukrycie idzie przez Hide(), wiec okna
    /// zyja dalej z cala zawartoscia i wracaja natychmiast - odtwarzanie ich od nowa
    /// kosztowaloby ponowne wyciaganie ikon z powloki.
    /// </summary>
    public void SetFencesHidden(bool hidden)
    {
        if (hidden == FencesHidden)
        {
            return;
        }

        FencesHidden = hidden;

        foreach (var window in _windows.Values)
        {
            if (hidden)
            {
                window.Hide();
            }
            else
            {
                window.Show();
                window.PinToDesktopLevel();
            }
        }

        StateChanged?.Invoke();
    }

    /// <summary>Wyrzuca cache ikon i wyciaga je z powloki na nowo - po zmianie rozmiaru ikon.</summary>
    public void RefreshAllIcons()
    {
        Icons.ClearCache();

        foreach (var window in _windows.Values)
        {
            window.RefreshIcons();
        }
    }

    public void ApplyDesktopIconVisibility() =>
        DesktopService.SetDesktopIconsVisible(!Settings.HideDesktopIcons);

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>
    /// Sklada okno ustawien od nowa - po zmianie jezyka, bo napisy rozwiazuja sie
    /// przy wczytywaniu XAML i inaczej zostalyby w poprzednim jezyku.
    /// Zamkniecie i otwarcie idzie przez kolejke, zeby nie ubic okna w trakcie
    /// obslugi jego wlasnego zdarzenia.
    /// </summary>
    public void ReopenSettings()
    {
        var window = _settingsWindow;
        if (window is null)
        {
            return;
        }

        var dispatcher = window.Dispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            window.Close();
            ShowSettings();
        });
    }

    // ---- zapis -------------------------------------------------------------

    /// <summary>Odklada zapis o chwile, zeby przeciaganie okna nie zapisywalo pliku setki razy.</summary>
    public void RequestSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    public void SaveNow()
    {
        if (!_displayChangePending)
        {
            foreach (var window in _windows.Values)
            {
                // Rozmiar/pozycja moga zmienic sie bez naszego udzialu (np. zmiana DPI).
                window.SyncBoundsToModel();
            }

            CapturePlacements(autoIds: null);
        }

        try
        {
            _config.Save(Layout);
            _saveErrorReported = false;
        }
        catch (Exception ex)
        {
            // Zapis leci co 700 ms i przy kazdym zamknieciu - trwala przyczyna (pelny dysk,
            // zabrany profil) zasypalaby uzytkownika oknami. Mowimy raz, do skutku.
            if (_saveErrorReported)
            {
                return;
            }

            _saveErrorReported = true;

            MessageBox.Show(
                Loc.Get("Msg_SaveFailed", _config.LayoutPath, ex.Message),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ---- zmiana monitorow --------------------------------------------------

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            _displayChangePending = true;
            _displayDebounce.Stop();
            _displayDebounce.Start();
        });

    /// <summary>
    /// Uklada fence'y dla biezacego ukladu monitorow. Uklad juz znany wraca dokladnie tak,
    /// jak go uzytkownik zostawil. Nowy dostaje polozenie przeniesione z poprzedniego ukladu
    /// (patrz <see cref="DisplayService.Map"/>).
    /// </summary>
    private void RestoreLayoutForDisplays()
    {
        var monitors = DisplayService.GetMonitors();

        if (monitors.Count == 0)
        {
            return;
        }

        var signature = DisplayService.Signature(monitors);
        var previous = Layout.DisplaySignature;
        Layout.DisplaySignature = signature;

        // Plik z wersji bez zapamietywania ukladow - biezace polozenie staje sie punktem wyjscia.
        if (previous is null)
        {
            CapturePlacements(autoIds: null);
            RequestSave();
            return;
        }

        var autoIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var window in _windows.Values)
        {
            var placements = window.Model.Placements;
            placements.TryGetValue(signature, out var saved);
            placements.TryGetValue(previous, out var before);

            FencePlacement? target;

            if (saved is not null && (!saved.Auto || before is null || previous == signature))
            {
                target = saved;

                if (saved.Auto)
                {
                    autoIds.Add(window.Model.Id);
                }
            }
            else if (before is not null)
            {
                target = DisplayService.Map(before, monitors);
                autoIds.Add(window.Model.Id);
            }
            else
            {
                // Nie ma skad wziac polozenia - wystarczy, zeby fence byl widoczny.
                target = null;
            }

            if (target is not null)
            {
                window.ApplyPlacement(target);
            }
        }

        // Po przeniesieniu okno lapie DPI nowego monitora i przelicza uklad - dopiero wtedy
        // obszar roboczy i rozmiar sa te wlasciwe.
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            foreach (var window in _windows.Values)
            {
                window.SettleAfterDisplayChange();
            }

            CapturePlacements(autoIds);
            RequestSave();
        });
    }

    /// <summary>
    /// Zapamietuje polozenie kazdego fence'a pod biezacym ukladem monitorow. Polozenie
    /// wyliczone przez nas zostaje oznaczone jako automatyczne, dopoki uzytkownik go nie zmieni.
    /// </summary>
    private void CapturePlacements(ISet<string>? autoIds)
    {
        if (Layout.DisplaySignature is not { } signature)
        {
            return;
        }

        foreach (var window in _windows.Values)
        {
            if (window.CapturePlacement() is not { } placement)
            {
                continue;
            }

            var placements = window.Model.Placements;

            placement.Auto = autoIds?.Contains(window.Model.Id)
                             ?? (placements.TryGetValue(signature, out var old)
                                 && old.Auto
                                 && DisplayService.SamePosition(old, placement));

            placements[signature] = placement;
        }
    }

    // ---- pulpit i auto-sortowanie -----------------------------------------

    private void SetupDesktopWatchers()
    {
        foreach (var folder in DesktopService.GetDesktopFolders())
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };

                watcher.Created += OnDesktopItemCreated;
                watcher.Renamed += OnDesktopItemRenamed;

                _desktopWatchers.Add(watcher);
            }
            catch
            {
                // Brak dostepu do folderu pulpitu nie moze wywrocic aplikacji.
            }
        }
    }

    private void OnDesktopItemCreated(object sender, FileSystemEventArgs e) => HandleNewDesktopItem(e.FullPath);

    private void OnDesktopItemRenamed(object sender, RenamedEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Zmiana nazwy to ten sam plik pod nowa sciezka. Wczesniej pozycja po prostu
            // znikala z fence'a - uzytkownik zmienial nazwe skrotu i tracil go z fence'a.
            var followed = false;

            foreach (var window in _windows.Values.ToList())
            {
                followed |= window.RenamePath(e.OldFullPath, e.FullPath);
            }

            // Plik, ktorego zaden fence nie znal, przechodzi przez reguly jak nowy.
            if (!followed)
            {
                HandleNewDesktopItem(e.FullPath);
            }
        });
    }

    private void HandleNewDesktopItem(string path)
    {
        if (!Settings.AutoSortEnabled)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (IsTracked(path) || !ShellService.Exists(path))
            {
                return;
            }

            var target = RuleEngine.FindTarget(Layout.Fences, path);
            if (target is null || !_windows.TryGetValue(target.Id, out var window))
            {
                return;
            }

            window.AddPaths(new[] { path });
        });
    }

    /// <summary>
    /// Przepuszcza wszystko, co lezy teraz na pulpicie, przez reguly fence'ow.
    /// Uzywane przy pierwszym uruchomieniu i z menu w zasobniku.
    /// </summary>
    public int ImportDesktopItems(bool silent)
    {
        var imported = 0;

        foreach (var folder in DesktopService.GetDesktopFolders())
        {
            string[] entries;
            try
            {
                // Lista musi byc gotowa przed petla: wciaganie do magazynu zabiera pliki
                // z tego samego katalogu, a leniwa enumeracja zmienianego katalogu potrafi
                // rzucic wyjatkiem albo po cichu pominac czesc pozycji.
                entries = Directory.GetFileSystemEntries(folder);
            }
            catch
            {
                continue;
            }

            foreach (var path in entries)
            {
                if (IsTracked(path))
                {
                    continue;
                }

                try
                {
                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                var target = RuleEngine.FindTarget(Layout.Fences, path);
                if (target is null)
                {
                    continue;
                }

                if (_windows.TryGetValue(target.Id, out var window))
                {
                    imported += window.AddPaths(new[] { path });
                }
                else
                {
                    // Fence jeszcze nie ma okna (import przy starcie) - piszemy prosto do modelu.
                    // Plik i tak musi zjechac do magazynu, inaczej ikona zostanie na pulpicie
                    // i jednoczesnie pojawi sie w fence'ie.
                    var stored = Settings.MoveItemsIntoFence && ItemStorageService.IsOnDesktop(path)
                        ? Storage.MoveIn(path, target.Id)
                        : path;

                    target.Items.Add(new FenceItem { Path = stored });
                    imported++;
                }
            }
        }

        if (imported > 0)
        {
            RequestSave();
        }

        if (!silent)
        {
            MessageBox.Show(
                imported > 0
                    ? Loc.Get("Msg_Imported", imported)
                    : Loc.Get("Msg_NoNewItems"),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        return imported;
    }

    private void OnMaintenanceTick(object? sender, EventArgs e)
    {
        _maintenanceTicks++;

        // Explorer potrafi zrestartowac sie sam i przywrocic ikony pulpitu.
        if (Settings.HideDesktopIcons && DesktopService.AreDesktopIconsVisible())
        {
            DesktopService.SetDesktopIconsVisible(false);
        }

        foreach (var window in _windows.Values)
        {
            window.PinToDesktopLevel();
        }

        // Sprawdzanie istnienia plikow jest drozsze, wiec rzadziej.
        if (_maintenanceTicks % 5 == 0)
        {
            foreach (var window in _windows.Values)
            {
                window.RefreshExistence();
            }
        }
    }
}
