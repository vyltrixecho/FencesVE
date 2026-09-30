using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using FencesVE.Models;
using FencesVE.Services;
using FencesVE.Views;
using Forms = System.Windows.Forms;

namespace FencesVE;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private FenceManager? _manager;
    private IpcService? _ipc;
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;

    /// <summary>Jezyk, w ktorym zlozone jest menu zasobnika - patrz <see cref="RefreshTrayMenuLanguage"/>.</summary>
    private string _trayMenuLanguage = "";

    /// <summary>
    /// Aplikacja siedzi w zasobniku i nie ma konsoli ani glownego okna, wiec bez tego
    /// kazda awaria przy starcie jest niema - proces po prostu znika.
    /// </summary>
    private static void LogCrash(string origin, object? error)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FencesVE",
                "crash.log");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{origin}] ==={Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nie mamy gdzie tego zglosic - trudno.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Zanim wczytamy ustawienia, idziemy za jezykiem Windows - inaczej komunikat
        // o juz dzialajacej instancji wyszedlby w jezyku neutralnym, nie w systemowym.
        LocalizationService.Apply(AppLanguage.System);

        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash("AppDomain", args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("Task", args.Exception);
            args.SetObserved();
        };

        try
        {
            RunStartup(e);
        }
        catch (Exception ex)
        {
            LogCrash("OnStartup", ex);
            throw;
        }
    }

    private void RunStartup(StartupEventArgs e)
    {
        // Tryb pomocniczy z podniesionymi uprawnieniami: przenosimy wskazane pliki i wychodzimy.
        // Musi byc przed muteksem - inaczej ta instancja zobaczylaby dzialajaca aplikacje
        // i zamiast pracy pokazala komunikat "juz uruchomiona".
        var moveList = ParseOption(e.Args, ItemStorageService.MoveItemsArgument);
        if (moveList is not null)
        {
            ItemStorageService.RunMoveList(moveList);
            Shutdown();
            return;
        }

        var command = ParseCommand(e.Args);

        // Po aktualizacji startujemy, gdy stara instancja zdazy oddac muteks.
        WaitForPreviousInstance(e.Args);

        UpdateService.CleanupBackup();

        // Druga instancja rozsypalaby uklad - kazda zapisywalaby swoja wersje pliku.
        _singleInstance = new Mutex(initiallyOwned: true, "FencesVE.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            // Wywolanie z menu pulpitu: przekazujemy polecenie dzialajacej instancji i znikamy.
            // Zwykle ponowne uruchomienie otwiera ustawienia - przy schowanej ikonie zasobnika
            // to jedyna droga do nich, jesli menu pulpitu jest wylaczone.
            if (IpcService.TrySend(command ?? IpcService.CommandSettings))
            {
                Shutdown();
                return;
            }

            MessageBox.Show(
                Loc.Get("Msg_AlreadyRunning"),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);

            MessageBox.Show(
                Loc.Get("Msg_UnexpectedError", args.Exception),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            args.Handled = true;
        };

        // Przed pierwszym oknem: styl musi byc w zasobach, zanim cokolwiek sie narysuje.
        SystemThemeService.ApplyToApplication();

        // Dzialajaca poprzednia wersja trzyma swoj katalog i dalej zapisuje do niego
        // uklad - przenosiny danych w tym czasie rozjechalyby obie kopie.
        if (LegacyMigration.IsLegacyRunning())
        {
            var answer = MessageBox.Show(
                Loc.Get("Msg_LegacyRunning"),
                "FencesVE",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                Shutdown();
                return;
            }

            LegacyMigration.StopLegacy();
        }

        var config = new ConfigService();
        LegacyMigration.Run(config.ConfigDirectory);

        _manager = new FenceManager(config, new IconService());

        // Jezyk tez przed pierwszym oknem - napisy rozwiazuja sie przy wczytywaniu XAML.
        LocalizationService.Apply(_manager.Settings.Language);
        _manager.Start();

        // Przy wylogowaniu, restarcie i zamknieciu Windows ubija proces, nie czekajac
        // na uprzatniecie - OnExit sie wtedy nie wykonuje i przepadalo wszystko, czego
        // nie zdazyl zapisac timer dlawiacy (700 ms) oraz pozycje fence'ow, ktore
        // przepisuje dopiero SaveNow. Stad osobny zapis na koniec sesji.
        SystemEvents.SessionEnding += OnSessionEnding;

        SetupTray();

        _ipc = new IpcService();
        _ipc.CommandReceived += HandleCommand;
        _ipc.Start();

        // Sciezka do .exe jest zapisana w rejestrze, wiec po przeniesieniu pliku
        // wpisy trzeba przepisac. Robimy to przy kazdym starcie.
        ShellMenuService.Sync(_manager.Settings.DesktopMenuIntegration);

        _manager.Updates.ApplyHandler = InstallUpdateAndRestart;

        if (command is not null)
        {
            HandleCommand(command);
        }

        if (e.Args.Any(a => string.Equals(a, "--update", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunSilentUpdateAsync();
        }
        else if (_manager.Settings.CheckUpdatesOnStartup &&
                 !string.IsNullOrWhiteSpace(_manager.Settings.UpdateFeedUrl))
        {
            _ = CheckUpdatesInBackgroundAsync();
        }
    }

    /// <summary>
    /// Podmienia plik .exe i restartuje aplikacje. Muteks trzeba oddac przed startem
    /// nowego procesu, inaczej zobaczy zajete miejsce i zamknie sie.
    /// </summary>
    private void InstallUpdateAndRestart(string downloadedPath)
    {
        try
        {
            _manager?.SaveNow();

            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
            _singleInstance = null;

            UpdateService.ApplyAndRestart(downloadedPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.Get("Msg_UpdateInstallFailed", ex.Message),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        Shutdown();
    }

    private static void LogUpdate(string line)
    {
        try
        {
            var log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FencesVE",
                "update.log");

            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch
        {
            // Brak logu nie moze przerwac aktualizacji.
        }
    }

    private async Task RunSilentUpdateAsync()
    {
        var feed = _manager!.Settings.UpdateFeedUrl;
        LogUpdate($"START feed='{feed}'");

        string result;
        try
        {
            result = await _manager.Updates.RunSilentAsync(feed);
        }
        catch (Exception ex)
        {
            LogUpdate($"FAILED wyjatek: {ex}");
            Shutdown();
            return;
        }

        LogUpdate(result);

        // Gdy nic nie bylo do zrobienia, tryb --update nie ma po co trzymac aplikacji.
        if (!result.StartsWith("UPDATED", StringComparison.Ordinal))
        {
            Shutdown();
        }
    }

    private async Task CheckUpdatesInBackgroundAsync()
    {
        // Chwila zwloki, zeby sprawdzanie nie konkurowalo ze startem fence'ow.
        await Task.Delay(TimeSpan.FromSeconds(8));

        var result = await UpdateService.CheckAsync(_manager!.Settings.UpdateFeedUrl);
        if (result.Status != UpdateCheckStatus.UpdateAvailable)
        {
            return;
        }

        _tray?.ShowBalloonTip(
            8000,
            "FencesVE",
            Loc.Get("Update_Available", result.Manifest!.Version),
            Forms.ToolTipIcon.Info);
    }

    /// <summary>Po aktualizacji nowy proces czeka, az poprzedni zakonczy prace.</summary>
    private static void WaitForPreviousInstance(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i], "--updated", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(args[i + 1], out var pid))
            {
                return;
            }

            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(10_000);
            }
            catch
            {
                // Proces juz zniknal - nie ma na co czekac.
            }

            return;
        }
    }

    /// <summary>Zwraca wartosc stojaca za przelacznikiem, np. <c>--move-items lista.txt</c>.</summary>
    private static string? ParseOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Zamienia argumenty wiersza polecen na polecenie zrozumiale dla IPC.</summary>
    private static string? ParseCommand(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase))
            {
                return IpcService.CommandSettings;
            }

            if (string.Equals(arg, "--new-fence", StringComparison.OrdinalIgnoreCase))
            {
                return IpcService.CommandNewFence;
            }
        }

        return null;
    }

    private void HandleCommand(string command)
    {
        if (_manager is null)
        {
            return;
        }

        switch (command)
        {
            case IpcService.CommandSettings:
                _manager.ShowSettings();
                break;

            case IpcService.CommandNewFence:
                _manager.CreateFenceAtCursor();
                break;
        }
    }

    /// <summary>
    /// Ostatnia szansa na zapis przed koncem sesji Windows.
    /// <para>
    /// SystemEvents ma wlasne okno komunikatow, wiec zdarzenie przychodzi takze wtedy,
    /// gdy nie ma otwartego zadnego fence'a - w odroznieniu od <c>Application.SessionEnding</c>,
    /// ktore wisi na WM_QUERYENDSESSION okna glownego, a tego ta aplikacja nie ma.
    /// Wola nas przy tym z wlasnego watku, a SaveNow czyta polozenie okien WPF,
    /// wiec robota musi wrocic na watek UI.
    /// </para>
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        try
        {
            Dispatcher.Invoke(() => _manager?.SaveNow(), DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            // Windows i tak zaraz zamknie proces - zostaje slad w logu.
            LogCrash("SessionEnding", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.SessionEnding -= OnSessionEnding;

        _ipc?.Dispose();
        _manager?.Shutdown();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _trayIcon?.Dispose();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }

    // ---- zasobnik systemowy ------------------------------------------------

    private void SetupTray()
    {
        if (_manager is null)
        {
            return;
        }

        _trayIcon = LoadAppIcon(Forms.SystemInformation.SmallIconSize);

        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "FencesVE",
            Visible = _manager.Settings.ShowTrayIcon,
        };

        _tray.DoubleClick += (_, _) => _manager.ShowSettings();
        _tray.ContextMenuStrip = BuildTrayMenu();
        _trayMenuLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        // Napisy menu rozwiazuja sie raz, przy skladaniu. Okno ustawien po zmianie jezyka
        // powstaje od nowa, menu fence'ow tez - ale to jedno zostawalo w starym jezyku.
        _manager.StateChanged += RefreshTrayMenuLanguage;
        _manager.StateChanged += () => _tray.Visible = _manager.Settings.ShowTrayIcon;
    }

    private void RefreshTrayMenuLanguage()
    {
        var current = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        if (_tray is null || string.Equals(current, _trayMenuLanguage, StringComparison.Ordinal))
        {
            return;
        }

        _trayMenuLanguage = current;

        // Przez kolejke: zdarzenie potrafi przyjsc z klikniecia w samym menu, a wtedy
        // podmiana strip'a spod otwartego menu konczy sie wyjatkiem.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_tray is null)
            {
                return;
            }

            var old = _tray.ContextMenuStrip;
            _tray.ContextMenuStrip = BuildTrayMenu();
            old?.Dispose();
        });
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var manager = _manager!;
        var menu = new Forms.ContextMenuStrip();

        menu.Items.Add(Loc.Get("Tray_NewFence"), null, (_, _) => manager.CreateFence());
        menu.Items.Add(Loc.Get("Tray_NewPortalFence"), null, (_, _) => CreatePortalFence());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var hideIcons = new Forms.ToolStripMenuItem(Loc.Get("Tray_HideDesktopIcons"))
        {
            CheckOnClick = true,
            Checked = manager.Settings.HideDesktopIcons,
        };
        hideIcons.Click += (_, _) =>
        {
            manager.Settings.HideDesktopIcons = hideIcons.Checked;
            manager.ApplySettingsToAll();
        };
        menu.Items.Add(hideIcons);

        var lockFences = new Forms.ToolStripMenuItem(Loc.Get("Tray_LockFences"))
        {
            CheckOnClick = true,
            Checked = manager.Settings.LockFences,
        };
        lockFences.Click += (_, _) =>
        {
            manager.Settings.LockFences = lockFences.Checked;
            manager.ApplySettingsToAll();
        };
        menu.Items.Add(lockFences);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.Get("Tray_Import"), null, (_, _) => manager.ImportDesktopItems(silent: false));
        menu.Items.Add(Loc.Get("Tray_RollAll"), null, (_, _) => SetAllRolled(true));
        menu.Items.Add(Loc.Get("Tray_UnrollAll"), null, (_, _) => SetAllRolled(false));

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.Get("Tray_CheckUpdates"), null, async (_, _) =>
            await manager.Updates.CheckAndOfferAsync(manager.Settings.UpdateFeedUrl, null));
        menu.Items.Add(Loc.Get("Tray_Settings"), null, (_, _) => manager.ShowSettings());
        menu.Items.Add(Loc.Get("Tray_Exit"), null, (_, _) => Shutdown());

        // Stan checkboxow moze zmienic sie spoza menu (np. z okna ustawien).
        menu.Opening += (_, _) =>
        {
            hideIcons.Checked = manager.Settings.HideDesktopIcons;
            lockFences.Checked = manager.Settings.LockFences;
        };

        // WinForms nie idzie za motywem Windows - bez tego przy ciemnej reszcie aplikacji
        // z zasobnika wyskakiwalby bialy prostokat.
        if (SystemThemeService.IsDarkMode())
        {
            DarkTrayMenu.Apply(menu);
        }

        return menu;
    }

    private void SetAllRolled(bool rolled)
    {
        if (_manager is null)
        {
            return;
        }

        foreach (var window in _manager.Windows)
        {
            window.SetRollUp(rolled, animate: true, persist: false);
        }

        _manager.RequestSave();
    }

    private void CreatePortalFence()
    {
        if (_manager is null)
        {
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = Loc.Get("Tray_PickPortalFolder"),
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var name = new DirectoryInfo(dialog.SelectedPath.TrimEnd(Path.DirectorySeparatorChar)).Name;
        _manager.CreateFence(name, dialog.SelectedPath);
    }

    /// <summary>
    /// Wczytuje ikone aplikacji w rozmiarze pasujacym do zasobnika.
    /// Plik .ico jest osadzony w zestawie i zawiera osiem rozmiarow, wiec Windows
    /// dostaje gotowy wariant zamiast przeskalowanego jednego obrazka.
    /// </summary>
    private static Icon LoadAppIcon(System.Drawing.Size size)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("FencesVE.Assets.FencesVE.ico");

            if (stream is not null)
            {
                return new Icon(stream, size);
            }
        }
        catch
        {
            // Spadamy do wariantow ponizej.
        }

        try
        {
            if (Environment.ProcessPath is { Length: > 0 } exe)
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // Ostatecznie lepsza jakakolwiek ikona niz brak zasobnika.
        }

        return SystemIcons.Application;
    }
}
