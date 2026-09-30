using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FencesVE.Models;
using FencesVE.Services;

namespace FencesVE.Views;

public partial class SettingsWindow : Window
{
    private readonly FenceManager _manager;
    private readonly DispatcherTimer _iconRefreshDebounce;

    /// <summary>Blokuje reakcje na zdarzenia kontrolek w trakcie wstepnego wypelniania formularza.</summary>
    private bool _loading = true;

    public SettingsWindow(FenceManager manager)
    {
        _manager = manager;
        InitializeComponent();

        // Domyslny motyw WPF jest jasny niezaleznie od Windows - w trybie ciemnym
        // okno swiecilo bielą na tle reszty systemu.
        SystemThemeService.ApplyIfDark(this);

        _iconRefreshDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _iconRefreshDebounce.Tick += (_, _) =>
        {
            _iconRefreshDebounce.Stop();
            _manager.RefreshAllIcons();
        };

        // Fence moze powstac poza tym oknem (np. z menu pulpitu) - lista musi nadazyc.
        Activated += (_, _) => ReloadFenceCombo();
        Closed += (_, _) => _iconRefreshDebounce.Stop();

        RuleKindCombo.ItemsSource = new[]
        {
            new RuleKindOption(RuleKind.Extension, Loc.Get("Rule_Extension")),
            new RuleKindOption(RuleKind.Category, Loc.Get("Rule_Category")),
            new RuleKindOption(RuleKind.NameContains, Loc.Get("Rule_NameContains")),
            new RuleKindOption(RuleKind.NameRegex, Loc.Get("Rule_NameRegex")),
            new RuleKindOption(RuleKind.Everything, Loc.Get("Rule_Everything")),
        };
        RuleKindCombo.SelectedIndex = 0;

        LoadFromSettings();
        _loading = false;
    }

    private AppSettings Settings => _manager.Settings;

    /// <summary>
    /// Adres kanalu aktualizacji zapisuje sie przy utracie fokusu. Zamkniecie okna krzyzykiem
    /// albo przyciskiem nie zawsze go wywoluje, wiec wpisany wlasnie adres przepadal.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (_loading)
        {
            return;
        }

        var typed = FeedUrlBox.Text.Trim();

        if (!string.Equals(typed, Settings.UpdateFeedUrl, StringComparison.Ordinal))
        {
            Settings.UpdateFeedUrl = typed;
            _manager.ApplySettingsToAll();
        }
    }

    private void LoadFromSettings()
    {
        ThemeCombo.SelectedIndex = Settings.Theme == AppTheme.Dark ? 0 : 1;
        LanguageCombo.SelectedIndex = Settings.Language switch
        {
            AppLanguage.Polish => 1,
            AppLanguage.English => 2,
            _ => 0,
        };
        HeaderSideCombo.SelectedIndex = Settings.HeaderSide switch
        {
            HeaderSide.Bottom => 1,
            HeaderSide.Left => 2,
            HeaderSide.Right => 3,
            _ => 0,
        };
        TitleAlignmentCombo.SelectedIndex = Settings.TitleAlignment switch
        {
            TitleAlignment.Center => 1,
            TitleAlignment.Right => 2,
            _ => 0,
        };
        OpacitySlider.Value = Settings.Opacity;
        RadiusSlider.Value = Settings.CornerRadius;
        IconSizeSlider.Value = Settings.IconSize;
        FontSizeSlider.Value = Settings.FontSize;
        HeaderHeightSlider.Value = Math.Clamp(
            Settings.HeaderHeight, HeaderHeightSlider.Minimum, HeaderHeightSlider.Maximum);
        PeekSlider.Value = Settings.PeekWidth;
        AutoHideSlider.Value = Math.Clamp(Settings.AutoHideDelayMs, AutoHideSlider.Minimum, AutoHideSlider.Maximum);

        ShowLabelsCheck.IsChecked = Settings.ShowLabels;
        PeekShowsHeaderCheck.IsChecked = Settings.PeekShowsHeader;
        HideDesktopIconsCheck.IsChecked = Settings.HideDesktopIcons;
        LockFencesCheck.IsChecked = Settings.LockFences;
        AutoSortCheck.IsChecked = Settings.AutoSortEnabled;
        DesktopDoubleClickCheck.IsChecked = Settings.DesktopDoubleClickHidesFences;
        MoveIntoFenceCheck.IsChecked = Settings.MoveItemsIntoFence;
        TrayIconCheck.IsChecked = Settings.ShowTrayIcon;
        StartupCheck.IsChecked = StartupService.IsEnabled();
        DesktopMenuCheck.IsChecked = ShellMenuService.IsInstalled();

        LayoutPathBox.Text = _manager.LayoutPathForDisplay;

        VersionLabel.Text = Loc.Get("Settings_VersionInstalled", UpdateService.CurrentVersionText);
        FeedUrlBox.Text = Settings.UpdateFeedUrl;
        CheckOnStartupCheck.IsChecked = Settings.CheckUpdatesOnStartup;

        UpdateColorPreview();
        UpdateSliderLabels();
        ReloadFenceCombo();
    }

    private void UpdateColorPreview()
    {
        ColorPreview.Background = new SolidColorBrush(ThemeService.ParseColor(Settings.Accent, Colors.Black));
        TextColorPreview.Background = new SolidColorBrush(ThemeService.ResolveTextColor(Settings));
    }

    private void UpdateSliderLabels()
    {
        OpacityLabel.Text = Loc.Get("Settings_OpacityValue", Settings.Opacity.ToString("P0"));
        RadiusLabel.Text = Loc.Get("Settings_CornerRadiusValue", Settings.CornerRadius.ToString("F0"));
        IconSizeLabel.Text = Loc.Get("Settings_IconSizeValue", Settings.IconSize);
        FontSizeLabel.Text = Loc.Get("Settings_FontSizeValue", Settings.FontSize.ToString("F0"));
        HeaderHeightLabel.Text = Loc.Get("Settings_HeaderHeightValue", Settings.HeaderHeight.ToString("F0"));
        PeekLabel.Text = Loc.Get("Settings_PeekValue", Settings.PeekWidth.ToString("F0"));
        AutoHideLabel.Text = Loc.Get("Settings_AutoHideValue", Settings.AutoHideDelayMs.ToString("F0"));
    }

    private void Commit()
    {
        if (_loading)
        {
            return;
        }

        _manager.ApplySettingsToAll();
    }

    // ---- zakladka Wyglad ---------------------------------------------------

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.Theme = ThemeCombo.SelectedIndex == 1 ? AppTheme.Light : AppTheme.Dark;
        Commit();
    }

    private void HeaderSideCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.HeaderSide = HeaderSideCombo.SelectedIndex switch
        {
            1 => HeaderSide.Bottom,
            2 => HeaderSide.Left,
            3 => HeaderSide.Right,
            _ => HeaderSide.Top,
        };

        Commit();
    }

    /// <summary>
    /// Napisy rozwiazuja sie przy wczytywaniu okna, wiec po zmianie jezyka okno trzeba
    /// zlozyc od nowa. Menu fence''ow powstaja przy kazdym otwarciu, wiec tam wystarczy
    /// sama zmiana kultury.
    /// </summary>
    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.Language = LanguageCombo.SelectedIndex switch
        {
            1 => AppLanguage.Polish,
            2 => AppLanguage.English,
            _ => AppLanguage.System,
        };

        LocalizationService.Apply(Settings.Language);
        Commit();
        _manager.ReopenSettings();
    }

    private void TitleAlignmentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.TitleAlignment = TitleAlignmentCombo.SelectedIndex switch
        {
            1 => TitleAlignment.Center,
            2 => TitleAlignment.Right,
            _ => TitleAlignment.Left,
        };

        Commit();
    }

    private void PickColorButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };

        var current = ThemeService.ParseColor(Settings.Accent, Colors.Black);
        dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        Settings.Accent = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        UpdateColorPreview();
        Commit();
    }

    private void PickTextColorButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };

        var current = ThemeService.ResolveTextColor(Settings);
        dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        Settings.TextColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        UpdateColorPreview();
        Commit();
    }

    private void EyedropAccentButton_Click(object sender, RoutedEventArgs e)
    {
        if (PickFromScreen() is not { } color)
        {
            return;
        }

        Settings.Accent = ThemeService.ToHex(color);
        UpdateColorPreview();
        Commit();
    }

    private void EyedropTextButton_Click(object sender, RoutedEventArgs e)
    {
        if (PickFromScreen() is not { } color)
        {
            return;
        }

        Settings.TextColor = ThemeService.ToHex(color);
        UpdateColorPreview();
        Commit();
    }

    /// <summary>
    /// Pobiera kolor z ekranu, chowajac na ten czas okno ustawien - zwykle bierze sie kolor
    /// wlasnie spod niego. Przywrocenie okna idzie przez finally: bez tego jakikolwiek blad
    /// w trakcie pobierania zostawialby ustawienia zminimalizowane i wygladalo to na zawieszenie.
    /// </summary>
    private Color? PickFromScreen()
    {
        var restore = WindowState;
        WindowState = WindowState.Minimized;

        try
        {
            return ScreenColorPicker.Pick();
        }
        finally
        {
            WindowState = restore;
            Activate();
        }
    }

    private void ResetTextColorButton_Click(object sender, RoutedEventArgs e)
    {
        Settings.TextColor = null;
        UpdateColorPreview();
        Commit();
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        Settings.FontSize = FontSizeSlider.Value;
        UpdateSliderLabels();
        Commit();
    }

    private void HeaderHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        Settings.HeaderHeight = HeaderHeightSlider.Value;
        UpdateSliderLabels();
        Commit();
    }

    private void PeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        Settings.PeekWidth = PeekSlider.Value;
        UpdateSliderLabels();
        Commit();
    }

    private void AutoHideSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        Settings.AutoHideDelayMs = AutoHideSlider.Value;
        UpdateSliderLabels();
        Commit();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        Settings.Opacity = OpacitySlider.Value;
        UpdateSliderLabels();
        Commit();
    }

    private void RadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        Settings.CornerRadius = RadiusSlider.Value;
        UpdateSliderLabels();
        Commit();
    }

    private void IconSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        Settings.IconSize = (int)IconSizeSlider.Value;
        UpdateSliderLabels();
        Commit();

        // Wyciaganie ikon z powloki jest drogie, a suwak sypie zdarzeniami przy kazdym pikselu.
        // Uklad zmienia sie od razu, same bitmapy dociagamy, gdy uzytkownik skonczy przeciagac.
        _iconRefreshDebounce.Stop();
        _iconRefreshDebounce.Start();
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.ShowLabels = ShowLabelsCheck.IsChecked == true;
        Settings.PeekShowsHeader = PeekShowsHeaderCheck.IsChecked == true;
        Settings.HideDesktopIcons = HideDesktopIconsCheck.IsChecked == true;
        Settings.LockFences = LockFencesCheck.IsChecked == true;
        Settings.AutoSortEnabled = AutoSortCheck.IsChecked == true;
        Settings.DesktopDoubleClickHidesFences = DesktopDoubleClickCheck.IsChecked == true;
        Settings.MoveItemsIntoFence = MoveIntoFenceCheck.IsChecked == true;
        Settings.ShowTrayIcon = TrayIconCheck.IsChecked == true;
        Commit();
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var wanted = StartupCheck.IsChecked == true;

        if (!StartupService.SetEnabled(wanted))
        {
            MessageBox.Show(
                Loc.Get("Msg_StartupFailed"),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            _loading = true;
            StartupCheck.IsChecked = StartupService.IsEnabled();
            _loading = false;
            return;
        }

        Settings.RunAtStartup = wanted;
        Commit();
    }

    private void DesktopMenuCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var wanted = DesktopMenuCheck.IsChecked == true;
        var succeeded = wanted ? ShellMenuService.Install() : ShellMenuService.Uninstall();

        if (!succeeded)
        {
            MessageBox.Show(
                Loc.Get("Msg_DesktopMenuFailed"),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            _loading = true;
            DesktopMenuCheck.IsChecked = ShellMenuService.IsInstalled();
            _loading = false;
            return;
        }

        Settings.DesktopMenuIntegration = wanted;
        Commit();
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e) => _manager.ImportDesktopItems(silent: false);

    private void NewFenceButton_Click(object sender, RoutedEventArgs e)
    {
        _manager.CreateFence();
        ReloadFenceCombo();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void VyltrixBanner_Click(object sender, MouseButtonEventArgs e) =>
        OpenLink("https://vyltrixecho.pl", Loc.Get("Link_Website"));

    private void BuyCoffeeButton_Click(object sender, MouseButtonEventArgs e) =>
        OpenLink("https://buycoffee.to/vyltrixecho", Loc.Get("Link_Coffee"));

    private void OpenLink(string url, string description)
    {
        try
        {
            ShellService.Open(url);
        }
        catch (Exception ex)
        {
            // Brak domyslnej przegladarki albo zablokowany shell - nie ma po co ubijac okna ustawien.
            MessageBox.Show(
                Loc.Get("Msg_LinkFailed", description, ex.Message),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ---- zakladka Aktualizacje ---------------------------------------------

    private void FeedUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.UpdateFeedUrl = FeedUrlBox.Text.Trim();
        Commit();
    }

    private void CheckOnStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.CheckUpdatesOnStartup = CheckOnStartupCheck.IsChecked == true;
        Commit();
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        Settings.UpdateFeedUrl = FeedUrlBox.Text.Trim();
        Commit();

        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = Loc.Get("Settings_Checking");

        try
        {
            var result = await _manager.Updates.CheckAndOfferAsync(Settings.UpdateFeedUrl, this);
            UpdateStatus.Text = result;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    // ---- zakladka Reguly ---------------------------------------------------

    private void ReloadFenceCombo()
    {
        var previous = FenceCombo.SelectedItem as FenceModel;

        FenceCombo.ItemsSource = null;
        FenceCombo.ItemsSource = _manager.Layout.Fences;

        if (previous is not null && _manager.Layout.Fences.Contains(previous))
        {
            FenceCombo.SelectedItem = previous;
        }
        else if (_manager.Layout.Fences.Count > 0)
        {
            FenceCombo.SelectedIndex = 0;
        }
    }

    private void FenceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReloadRulesList();

    private void ReloadRulesList()
    {
        RulesList.ItemsSource = null;

        if (FenceCombo.SelectedItem is not FenceModel fence)
        {
            return;
        }

        RulesList.ItemsSource = fence.Rules
            .Select(r => new RuleRow(r, Describe(r)))
            .ToList();

        RulesList.DisplayMemberPath = nameof(RuleRow.Label);
    }

    private static string Describe(SortRule rule) => rule.Kind switch
    {
        RuleKind.Extension => Loc.Get("RuleDesc_Extension", rule.Pattern.TrimStart('.')),
        RuleKind.Category => Loc.Get("RuleDesc_Category", rule.Pattern),
        RuleKind.NameContains => Loc.Get("RuleDesc_NameContains", rule.Pattern),
        RuleKind.NameRegex => Loc.Get("RuleDesc_NameRegex", rule.Pattern),
        RuleKind.Everything => Loc.Get("RuleDesc_Everything"),
        _ => rule.Pattern,
    };

    private void AddRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (FenceCombo.SelectedItem is not FenceModel fence ||
            RuleKindCombo.SelectedItem is not RuleKindOption option)
        {
            return;
        }

        var pattern = RulePatternBox.Text.Trim();

        if (option.Kind != RuleKind.Everything && string.IsNullOrWhiteSpace(pattern))
        {
            MessageBox.Show(Loc.Get("Msg_RulePatternRequired"), "FencesVE", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (option.Kind == RuleKind.Category &&
            !Enum.TryParse<FileCategory>(pattern, ignoreCase: true, out _))
        {
            var allowed = string.Join(", ", Enum.GetNames<FileCategory>());
            MessageBox.Show(
                Loc.Get("Msg_UnknownCategory", allowed),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        fence.Rules.Add(new SortRule { Kind = option.Kind, Pattern = pattern });
        RulePatternBox.Clear();

        ReloadRulesList();
        _manager.RequestSave();
    }

    private void RemoveRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (FenceCombo.SelectedItem is not FenceModel fence || RulesList.SelectedItem is not RuleRow row)
        {
            return;
        }

        fence.Rules.Remove(row.Rule);
        ReloadRulesList();
        _manager.RequestSave();
    }

    private void ApplyRulesButton_Click(object sender, RoutedEventArgs e) =>
        _manager.ImportDesktopItems(silent: false);

    // ToString() jest nadpisane, bo po nim czytniki ekranu ogloszaja pozycje list -
    // domyslne ToString() rekordu wypisuje cala zawartosc razem z nazwami pol.

    private sealed record RuleKindOption(RuleKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record RuleRow(SortRule Rule, string Label)
    {
        public override string ToString() => Label;
    }
}
