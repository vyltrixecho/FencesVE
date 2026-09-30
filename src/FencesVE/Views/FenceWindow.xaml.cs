using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FencesVE.Interop;
using FencesVE.Models;
using FencesVE.Services;
using FencesVE.ViewModels;

namespace FencesVE.Views;

public partial class FenceWindow : Window
{
    /// <summary>
    /// Fence, z ktorego wlasnie trwa przeciaganie.
    /// <para>
    /// Kiedys szlo to wlasnym formatem schowka. Obiekt danych z wlasnym formatem musi jednak
    /// dac sie przelozyc na pamiec COM, zeby przeczytal go ktos spoza procesu - a powloka
    /// czyta wszystko, co przeciagamy. Przeciaganie miedzy fence'ami i tak nigdy nie wychodzi
    /// poza ten proces, wiec zwykle pole jest tu i prostsze, i pewniejsze: w obiekcie danych
    /// zostaje sama lista plikow, ktora Windows rozumie od zawsze.
    /// </para>
    /// </summary>
    private static FenceWindow? _dragSource;

    private readonly FenceManager _manager;
    private readonly DispatcherTimer _portalDebounce;
    private readonly DispatcherTimer _autoHideDelay;

    private FileSystemWatcher? _portalWatcher;
    private IntPtr _hwnd;
    private Point _dragStart;
    private bool _mouseDownOnItem;

    /// <summary>Czy przyklejony fence jest teraz wysuniety na ekran.</summary>
    private bool _isPeeking;

    /// <summary>Otwarte menu kontekstowe generuje MouseLeave - bez tego fence chowalby sie pod menu.</summary>
    private bool _menuOpen;

    /// <summary>Krawedzie obszaru roboczego zapamietane przy przyklejaniu (jednostki WPF).</summary>
    private double _dockAnchorRight;
    private double _dockAnchorBottom;

    /// <summary>
    /// SizeChanged potrafi odpalic juz przy pierwszym ukladzie okna, zanim policzymy krawedzie.
    /// Bez tej flagi fence przyklejony do prawej/dolnej skakal wtedy na sasiedni monitor.
    /// </summary>
    private bool _dockAnchorsReady;

    /// <summary>Rozmiar ikon, dla ktorego ostatnio wyciagnelismy bitmapy z powloki.</summary>
    private int _appliedIconSize = -1;

    /// <summary>
    /// Absolutna granica rozmiaru wzdluz osi, ktorej belka nie zajmuje. Nizej fence przestaje
    /// byc chwytliwy mysza. Samo okno ma MinWidth/MinHeight ustawione na 2, zeby dalo sie je
    /// zwinac do paska przy krawedzi - limit pilnujemy tutaj.
    /// </summary>
    private const double MinFenceSize = 48;

    /// <summary>Granice wysokosci belki tytulu.</summary>
    public const double MinHeaderHeight = 18;
    public const double MaxHeaderHeight = 72;

    /// <summary>
    /// Najmniejszy rozmiar tego fence'a: tyle, zeby obok belki zmiescil sie caly jeden kafelek
    /// z ikona.
    /// <para>
    /// Kiedys byla tu stala 160x56 px - fence z belka z boku, zwezony wlasnie do 160, nie dawal
    /// sie zwezic ani o piksel. Sama belka jako granica tez nie wystarczyla: ponizej szerokosci
    /// kafelka ikony nie znikaja, tylko sa przycinane, wiec fence konczyl jako pasek z szescioma
    /// pionowymi kreskami po ikonach. Granica liczona z kafelka zatrzymuje zwezanie dokladnie
    /// tam, gdzie ikony jeszcze widac w calosci - czyli "na rozmiarze ikon".
    /// </para>
    /// <para>Zeby zejsc nizej, jest zwijanie: dwuklik w belke zostawia sama belke.</para>
    /// </summary>
    private double MinFenceWidth => Math.Max(
        (IsHeaderVertical ? CollapsedWidth() : 0) + Vm.CellWidth + 6,
        MinFenceSize);

    /// <summary>Najmniejsza wysokosc tego fence'a - ta sama zasada w drugiej osi.</summary>
    private double MinFenceHeight => Math.Max(
        (IsHeaderVertical ? 0 : CollapsedHeight()) + Vm.CellHeight + 6,
        MinFenceSize);

    public FenceWindow(FenceModel model, FenceManager manager)
    {
        _manager = manager;
        Model = model;
        Vm = new FenceViewModel(model);

        InitializeComponent();

        ShowActivated = false;
        DataContext = Vm;

        Left = model.X;
        Top = model.Y;

        _portalDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _portalDebounce.Tick += (_, _) =>
        {
            _portalDebounce.Stop();
            ReloadItems();
        };

        _autoHideDelay = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _autoHideDelay.Tick += (_, _) =>
        {
            _autoHideDelay.Stop();

            // Kursor mogl wrocic albo otworzyc menu, zanim minelo opoznienie.
            if (IsMouseOver || _menuOpen)
            {
                return;
            }

            CollapseForHover();
        };

        DragOver += OnDragOver;
        Drop += OnDrop;
        MouseMove += OnMouseMove;
        KeyDown += OnKeyDown;
        MouseLeftButtonUp += (_, _) => _mouseDownOnItem = false;
        MouseEnter += OnMouseEnterWindow;
        MouseLeave += OnMouseLeaveWindow;
        SizeChanged += OnSizeChangedForDock;

        // Obrocony tytul przycina sie do wysokosci belki, wiec musi nadazac za jej zmiana.
        HeaderBorder.SizeChanged += (_, _) => UpdateVerticalTitleLength();

        ApplySettings(manager.Settings);

        // Rozmiar dopiero teraz: dolna granica liczy sie z kafelka ikony, a jego rozmiar
        // znamy dopiero po zastosowaniu ustawien. Wczesniej fence zapisany waziej niz kafelek
        // przy domyslnym rozmiarze ikon zostalby przy starcie rozepchniety.
        Width = Math.Max(model.Width, MinFenceWidth);
        Height = Math.Max(model.Height, MinFenceHeight);

        ReloadItems();
    }

    public FenceModel Model { get; }

    public FenceViewModel Vm { get; }

    private AppSettings Settings => _manager.Settings;

    private int EffectiveIconSize => Model.IconSizeOverride > 0 ? Model.IconSizeOverride : Settings.IconSize;

    // ---- cykl zycia okna ---------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        // Okno narzedziowe: nie pokazuje sie w Alt+Tab ani na pasku zadan.
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        PinToDesktopLevel();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Stan zwiniecia odtwarzamy dopiero po zmierzeniu belki tytulu.
            // Fence z auto-zwijaniem zawsze startuje zwiniety - rozwinie go najechanie kursorem.
            if (Model.RolledUp || (Model.AutoRollUp && Model.Dock == EdgeDock.None))
            {
                SetRollUp(true, animate: false, persist: false);
            }

            // Przyklejony fence startuje schowany przy krawedzi.
            if (Model.Dock != EdgeDock.None)
            {
                SetPeek(false, animate: false);
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _portalWatcher?.Dispose();
        _portalWatcher = null;
        _portalDebounce.Stop();
        _autoHideDelay.Stop();
        base.OnClosed(e);
    }

    /// <summary>
    /// Przy kazdej zmianie pozycji wymuszamy wstawienie okna na dno z-order.
    /// Dzieki temu fence zyje na poziomie pulpitu i nigdy nie zaslania aplikacji.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
            pos.hwndInsertAfter = DesktopService.GetDesktopAnchor();
            pos.flags &= ~NativeMethods.SWP_NOZORDER;
            Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
        }
        else if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            // Zmiana rozdzielczosci przesuwa krawedzie - przyklejony fence trzeba dosunac na nowo.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (Model.Dock != EdgeDock.None)
                {
                    SetPeek(_isPeeking, animate: false);
                }
            });
        }

        return IntPtr.Zero;
    }

    /// <summary>Wpina okno z powrotem tuz nad tapete - pod wszystkimi aplikacjami.</summary>
    public void PinToDesktopLevel()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            DesktopService.GetDesktopAnchor(),
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // ---- chowanie do krawedzi (szuflada) -----------------------------------

    /// <summary>Obszar roboczy monitora, na ktorym stoi fence, przeliczony na jednostki WPF.</summary>
    private Rect WorkingAreaDip()
    {
        var handle = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;

        // MONITORINFO zwraca piksele fizyczne, a Left/Top okna WPF sa w jednostkach niezaleznych od DPI.
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;

        var topLeft = transform.Transform(new Point(area.Left, area.Top));
        var bottomRight = transform.Transform(new Point(area.Right, area.Bottom));

        return new Rect(topLeft, bottomRight);
    }

    /// <summary>
    /// Ile fence'a zostaje widoczne po schowaniu do krawedzi.
    /// <para>
    /// Przy wlaczonej opcji zostaje cala belka z tytulem - ale tylko wtedy, gdy stoi po tej
    /// samej stronie, do ktorej fence sie chowa. Chowanie przycina fence od strony ekranu:
    /// przy krawedzi gornej zostaje gorny pasek, przy lewej - lewy. Belka z innej strony
    /// zostalaby wiec i tak poza ekranem, a uzytkownik dostalby gruby pasek bez nazwy.
    /// </para>
    /// </summary>
    private double PeekThickness()
    {
        var strip = Math.Clamp(Settings.PeekWidth, 2, 60);

        if (!Settings.PeekShowsHeader)
        {
            return strip;
        }

        // Chowanie skraca fence wzdluz jednej osi. Belka zmiesci sie w pasku tylko wtedy,
        // gdy lezy w poprzek tej osi - belka pionowa przy chowaniu w pionie zostalaby
        // przycieta wzdluz, a nie odslonieta.
        var shrinksVertically = Model.Dock is EdgeDock.Top or EdgeDock.Bottom;

        if (shrinksVertically == IsHeaderVertical)
        {
            return strip;
        }

        return IsHeaderVertical ? CollapsedWidth() : CollapsedHeight();
    }

    /// <summary>
    /// Wysuwa (shown) albo chowa przyklejony fence do paska przy krawedzi.
    /// <para>
    /// Okno nie zmienia przy tym ani rozmiaru, ani polozenia: stoi przy krawedzi w pelnym
    /// rozmiarze rozwinietego fence'a, a chowanie skraca tylko RootBorder w jego wnetrzu - patrz
    /// <see cref="AnimateRootSize"/>. Reszta okna jest calkiem przezroczysta, wiec klikniecia
    /// i przeciaganie przechodza przez nia na pulpit jak przez puste miejsce.
    /// </para>
    /// <para>
    /// Wypychanie okna poza ekran odpadlo wczesniej: przy kilku monitorach fence wjezdzal na
    /// sasiedni ekran (i lapal jego skalowanie DPI). Okno stojace przy krawedzi zostaje na swoim.
    /// </para>
    /// </summary>
    private void SetPeek(bool shown, bool animate)
    {
        if (Model.Dock == EdgeDock.None)
        {
            _isPeeking = false;
            return;
        }

        _isPeeking = shown;

        var area = WorkingAreaDip();
        var peek = PeekThickness();

        _dockAnchorRight = area.Right;
        _dockAnchorBottom = area.Bottom;
        _dockAnchorsReady = true;

        // ContentRoot dostaje sztywny rozmiar pelnego fence'a i jest przycinany przez RootBorder.
        // Przy wlaczonym podgladzie belki dosuwamy go tak, zeby w pasku zostala wlasnie belka -
        // a wiec do jej strony, nie do krawedzi ekranu. Bez tej opcji zostaje kawalek od strony
        // ekranu, bo to on realnie wystaje zza krawedzi.
        //
        // Stretch na osi, ktorej nie przycinamy: bez tego DockPanel skurczylby sie do zawartosci
        // i belka przestalaby siegac krawedzi fence'a.
        var anchorToHeader = Settings.PeekShowsHeader;

        ContentRoot.VerticalAlignment =
            (anchorToHeader ? EffectiveHeaderSide == HeaderSide.Bottom : Model.Dock == EdgeDock.Bottom)
                ? VerticalAlignment.Bottom
                : VerticalAlignment.Stretch;

        ContentRoot.HorizontalAlignment =
            (anchorToHeader ? EffectiveHeaderSide == HeaderSide.Right : Model.Dock == EdgeDock.Right)
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Stretch;

        // Zwijanie skraca tylko te os, wzdluz ktorej stoi belka.
        var fullWidth = Model.RolledUp && IsHeaderVertical
            ? CollapsedWidth()
            : Math.Max(Model.Width, MinFenceWidth);

        var fullHeight = Model.RolledUp && !IsHeaderVertical
            ? CollapsedHeight()
            : Math.Max(Model.Height, MinFenceHeight);

        var horizontal = Model.Dock is EdgeDock.Left or EdgeDock.Right;

        if (horizontal)
        {
            ContentRoot.Width = fullWidth - RootBorder.BorderThickness.Left - RootBorder.BorderThickness.Right;
            ContentRoot.Height = double.NaN;
        }
        else
        {
            ContentRoot.Height = fullHeight - RootBorder.BorderThickness.Top - RootBorder.BorderThickness.Bottom;
            ContentRoot.Width = double.NaN;
        }

        // W glab ekranu okno ma rozmiar rozwinietego fence'a, nawet gdy fence jest zwiniety:
        // zwiniecie przy prawej/dolnej krawedzi przesuwaloby inaczej okno, a z nim cala szuflade.
        var windowWidth = horizontal ? Math.Max(Model.Width, MinFenceWidth) : fullWidth;
        var windowHeight = horizontal ? fullHeight : Math.Max(Model.Height, MinFenceHeight);

        var left = Model.Dock switch
        {
            EdgeDock.Left => area.Left,
            EdgeDock.Right => area.Right - windowWidth,
            _ => Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - windowWidth)),
        };

        var top = Model.Dock switch
        {
            EdgeDock.Top => area.Top,
            EdgeDock.Bottom => area.Bottom - windowHeight,
            _ => Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - windowHeight)),
        };

        SetBoundsAtOnce(left, top, windowWidth, windowHeight);

        // RootBorder przylega do krawedzi ekranu, a w poprzek niej wypelnia okno.
        RootBorder.HorizontalAlignment = Model.Dock switch
        {
            EdgeDock.Left => HorizontalAlignment.Left,
            EdgeDock.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Stretch,
        };

        RootBorder.VerticalAlignment = Model.Dock switch
        {
            EdgeDock.Top => VerticalAlignment.Top,
            EdgeDock.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Stretch,
        };

        var along = horizontal ? WidthProperty : HeightProperty;
        var across = horizontal ? HeightProperty : WidthProperty;
        RootBorder.BeginAnimation(across, null);
        RootBorder.SetValue(across, double.NaN);

        var windowSize = horizontal ? windowWidth : windowHeight;
        var target = shown ? (horizontal ? fullWidth : fullHeight) : peek;

        // Podpowiedz pustego fence'a stoi na srodku okna. Przy wysuwaniu pokazujemy ja dopiero
        // na koncu - inaczej wisialaby nad szuflada, ktora jeszcze nie dojechala.
        if (shown && animate)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateEmptyHint();
        }

        AnimateRootSize(along, CurrentRootSize(along, windowSize), target, animate, () =>
        {
            // Wysuniety, rozwiniety fence zajmuje cale okno i wtedy RootBorder idzie za oknem -
            // przy zmianie rozmiaru uchwytami sztywny rozmiar zostawalby w tyle.
            if (shown && Math.Abs(target - windowSize) < 0.5)
            {
                RootBorder.SetValue(along, double.NaN);
            }

            UpdateEmptyHint();
        });
    }

    /// <summary>
    /// Fence przy prawej/dolnej krawedzi musi przesuwac sie przy kazdej zmianie rozmiaru okna
    /// (np. uchwytami), zeby jego krawedz zostawala przyklejona do brzegu ekranu.
    /// </summary>
    private void OnSizeChangedForDock(object sender, SizeChangedEventArgs e)
    {
        if (_dockAnchorsReady)
        {
            switch (Model.Dock)
            {
                case EdgeDock.Right:
                    Left = _dockAnchorRight - ActualWidth;
                    break;

                case EdgeDock.Bottom:
                    Top = _dockAnchorBottom - ActualHeight;
                    break;
            }
        }
    }

    // ---- animacja w oknie, ktore stoi w miejscu -----------------------------

    /// <summary>Numer biezacej animacji RootBorder - zakonczenie starszej nie moze juz niczego ruszac.</summary>
    private int _rootAnimation;

    /// <summary>
    /// Zmienia rozmiar RootBorder wzdluz jednej osi, a okno zostawia w spokoju.
    /// <para>
    /// Wczesniej animowane bylo samo okno i przy prawej/dolnej krawedzi (szuflada, belka na dole)
    /// krawedz, ktora miala stac, skakala. Przezroczyste okno WPF rysuje osobny watek i po kazdym
    /// przesunieciu okna przez klatke widac w nowym miejscu jeszcze stara zawartosc - a przy tych
    /// krawedziach z rozmiarem musi jechac tez pozycja. Zmierzone: szuflada przy dolnej krawedzi
    /// odrywala sie przy wysuwaniu o kilkadziesiat pikseli, a przy chowaniu wchodzila pod pasek
    /// zadan. Kazda zmiana rozmiaru okna czeka tez na watek renderujacy, wiec przy wielu ikonach
    /// animacja gubila klatki.
    /// </para>
    /// <para>Tu okno stoi, a klatka animacji to tylko inne przyciecie zawartosci.</para>
    /// </summary>
    private void AnimateRootSize(
        DependencyProperty property, double from, double target, bool animate, Action? completed = null)
    {
        var version = ++_rootAnimation;

        // Wartosc koncowa od razu jako wlasna: animacja ja przykrywa, a gdy sie skonczy, nie ma
        // czego doganiac.
        RootBorder.BeginAnimation(property, null);
        RootBorder.SetValue(property, target);

        if (!animate || Math.Abs(from - target) < 0.5)
        {
            ContentRoot.CacheMode = null;
            completed?.Invoke();
            return;
        }

        CacheContentDuringAnimation();

        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };

        animation.Completed += (_, _) =>
        {
            if (version != _rootAnimation)
            {
                return;
            }

            ContentRoot.CacheMode = null;
            completed?.Invoke();
        };

        RootBorder.BeginAnimation(property, animation);
    }

    /// <summary>Biezacy rozmiar RootBorder wzdluz osi - takze w polowie animacji.</summary>
    private double CurrentRootSize(DependencyProperty property, double whenStretched)
    {
        var value = (double)RootBorder.GetValue(property);
        return double.IsNaN(value) ? whenStretched : value;
    }

    /// <summary>RootBorder znow wypelnia cale okno: bez szuflady i bez trwajacej animacji zwijania.</summary>
    private void ResetRootBorder()
    {
        _rootAnimation++;

        RootBorder.BeginAnimation(WidthProperty, null);
        RootBorder.BeginAnimation(HeightProperty, null);
        RootBorder.Width = double.NaN;
        RootBorder.Height = double.NaN;
        RootBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
        RootBorder.VerticalAlignment = VerticalAlignment.Stretch;
        ContentRoot.CacheMode = null;
    }

    /// <summary>
    /// Na czas animacji rysuje zawartosc fence'a raz do bitmapy, zamiast od nowa w kazdej klatce.
    /// <para>
    /// Kazdy podpis ma wlasny DropShadowEffect (osobny przebieg renderowania), a kazda ikona
    /// skaluje sie z 256 px. Przy wielu ikonach przerysowywanie wszystkiego co klatke
    /// potrafilo nie nadazac za animacja.
    /// </para>
    /// <para>
    /// Tylko przy krawedzi: tam ContentRoot ma sztywny rozmiar pelnego fence'a, wiec bitmapa
    /// zostaje wazna przez cala animacje - zmienia sie tylko przyciecie. Przy zwyklym zwijaniu
    /// zawartosc zmienia rozmiar co klatke i cache tylko dokladalby pracy.
    /// </para>
    /// </summary>
    private void CacheContentDuringAnimation()
    {
        if (Model.Dock == EdgeDock.None)
        {
            return;
        }

        // Cache nie uwzglednia skalowania DPI przodkow - bez RenderAtScale tekst bylby rozmyty.
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        ContentRoot.CacheMode = new BitmapCache
        {
            RenderAtScale = scale,
            SnapsToDevicePixels = true,
            EnableClearType = false,
        };
    }

    /// <summary>Polozenie i rozmiar okna w DIP wprost z Win32 - wlasciwosci WPF bywaja o krok do tylu.</summary>
    private Rect CurrentBoundsDip()
    {
        if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                             ?? Matrix.Identity;

            return new Rect(
                fromDevice.Transform(new Point(rect.Left, rect.Top)),
                fromDevice.Transform(new Point(rect.Right, rect.Bottom)));
        }

        return new Rect(Left, Top, ActualWidth, ActualHeight);
    }

    /// <summary>
    /// Czy fence chowa sie sam: albo jako szuflada przy krawedzi, albo zwijajac sie do belki.
    /// Przyklejenie do krawedzi ma pierwszenstwo - dwa mechanizmy naraz bilyby sie o wysokosc okna.
    /// </summary>
    private bool HasAutoHide => Model.Dock != EdgeDock.None || Model.AutoRollUp;

    /// <summary>Pokazuje zawartosc fence'a po najechaniu kursorem.</summary>
    private void ExpandForHover()
    {
        if (Model.Dock != EdgeDock.None)
        {
            if (!_isPeeking)
            {
                SetPeek(true, animate: true);
            }
        }
        else if (Model.AutoRollUp && Model.RolledUp)
        {
            // persist: false - to stan chwilowy, a nie wybor uzytkownika.
            SetRollUp(false, animate: true, persist: false);
        }
    }

    /// <summary>Chowa zawartosc fence'a po zjechaniu kursorem.</summary>
    private void CollapseForHover()
    {
        if (Model.Dock != EdgeDock.None)
        {
            if (_isPeeking)
            {
                SetPeek(false, animate: true);
            }
        }
        else if (Model.AutoRollUp && !Model.RolledUp)
        {
            SetRollUp(true, animate: true, persist: false);
        }
    }

    private void OnMouseEnterWindow(object sender, MouseEventArgs e)
    {
        if (!HasAutoHide)
        {
            return;
        }

        _autoHideDelay.Stop();
        ExpandForHover();
    }

    private void OnMouseLeaveWindow(object sender, MouseEventArgs e)
    {
        if (!HasAutoHide)
        {
            return;
        }

        _autoHideDelay.Stop();
        _autoHideDelay.Start();
    }

    public void SetAutoRollUp(bool enabled)
    {
        Model.AutoRollUp = enabled;

        if (enabled)
        {
            if (Model.Dock == EdgeDock.None && !IsMouseOver)
            {
                SetRollUp(true, animate: true, persist: false);
            }
        }
        else
        {
            _autoHideDelay.Stop();

            if (Model.Dock == EdgeDock.None && Model.RolledUp)
            {
                SetRollUp(false, animate: true, persist: false);
            }
        }

        _manager.RequestSave();
    }

    public void SetDock(EdgeDock dock)
    {
        if (Model.Dock == dock)
        {
            return;
        }

        // Ramka i zaokraglenie zaleza od krawedzi przyklejenia, wiec musza sie przeliczyc
        // razem z nia - inaczej fence zostaje z plaskim bokiem po poprzedniej stronie.
        Model.Dock = dock;
        RootBorder.CornerRadius = FenceCorners();
        RootBorder.BorderThickness = FenceBorderThickness();
        UpdateHeaderCorners();

        if (dock == EdgeDock.None)
        {
            _isPeeking = false;
            _dockAnchorsReady = false;
            _autoHideDelay.Stop();

            // Zdejmujemy szuflade i sztywny rozmiar zawartosci i wracamy do pelnych wymiarow
            // fence'a. Wyrownanie tez: zostawione od krawedzi (do dolu/prawej) sciskaloby
            // DockPanel do rozmiaru zawartosci i belka ladowala w srodku fence'a.
            ResetRootBorder();
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            ContentRoot.Width = double.NaN;
            ContentRoot.Height = double.NaN;
            ContentRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
            ContentRoot.VerticalAlignment = VerticalAlignment.Stretch;
            UpdateEmptyHint();

            Width = Model.RolledUp && IsHeaderVertical
                ? CollapsedWidth()
                : Math.Max(Model.Width, MinFenceWidth);

            Height = Model.RolledUp && !IsHeaderVertical
                ? CollapsedHeight()
                : Math.Max(Model.Height, MinFenceHeight);

            var area = WorkingAreaDip();
            Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        }
        else
        {
            SetPeek(true, animate: false);
            SetPeek(false, animate: true);
        }

        SyncBoundsToModel();
        _manager.RequestSave();
    }

    // ---- wyglad ------------------------------------------------------------

    public void ApplySettings(AppSettings settings)
    {
        RootBorder.CornerRadius = FenceCorners();
        RootBorder.BorderThickness = FenceBorderThickness();
        UpdateHeaderCorners();

        _autoHideDelay.Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.AutoHideDelayMs, 100, 5000));

        Vm.IconSize = EffectiveIconSize;
        Vm.ShowLabels = settings.ShowLabels;
        Vm.LabelFontSize = Model.FontSizeOverride > 0 ? Model.FontSizeOverride : settings.FontSize;

        var textColor = Model.TextColorOverride is { Length: > 0 }
            ? ThemeService.ParseColor(Model.TextColorOverride, ThemeService.DefaultTextColor(settings.Theme))
            : ThemeService.ResolveTextColor(settings);

        var textBrush = new SolidColorBrush(textColor);
        textBrush.Freeze();
        Vm.TextBrush = textBrush;

        // Kolor bazowy: wlasny dla tego fence'a albo globalny.
        // Wczesniej nadpisanie dotykalo tylko tla, a belka tytulu zostawala przy kolorze
        // globalnym - i to ona czyta sie jako "kolor fence'a", wiec zmiana wygladala na nieskuteczna.
        var accentSource = Model.AccentOverride is { Length: > 0 } ? Model.AccentOverride : settings.Accent;
        var accent = ThemeService.ParseColor(accentSource, Color.FromRgb(0x10, 0x14, 0x18));

        RootBorder.Background = ThemeService.BuildBackgroundBrush(accent, settings.Opacity);
        HeaderBorder.Background = ThemeService.BuildHeaderBrush(accent, settings.Opacity, settings.Theme == AppTheme.Dark);

        // Blokada musi byc widoczna: klodka w belce, kursor bez strzalek przesuwania
        // i wylaczone uchwyty zmiany rozmiaru (inaczej kursor obiecuje cos, czego nie da sie zrobic).
        LockIndicator.Visibility = settings.LockFences ? Visibility.Visible : Visibility.Collapsed;
        HeaderBorder.Cursor = settings.LockFences ? Cursors.Arrow : Cursors.SizeAll;
        ResizeGrid.IsHitTestVisible = !settings.LockFences;

        // Kolejnosc ma znaczenie: ApplyHeaderHeight ustawia grubosc belki wzdluz osi,
        // ktora zalezy od tego, po ktorej stronie belka wlasnie stanela.
        ApplyHeaderSide();
        ApplyHeaderHeight(settings);

        // Zmiana szerokosci uchwytu przesuwa schowany fence - trzeba go dosunac na nowo.
        if (Model.Dock != EdgeDock.None && IsLoaded)
        {
            SetPeek(_isPeeking, animate: false);
        }

        // Ikony przeladowujemy tylko przy realnej zmianie rozmiaru. Wczesniej robil to
        // kazdy ruch dowolnego suwaka w ustawieniach - przeciagniecie krycia potrafilo
        // odpalic kilkadziesiat przebiegow wyciagania ikon z powloki.
        if (Vm.IconSize != _appliedIconSize)
        {
            _appliedIconSize = Vm.IconSize;
            RefreshIcons();
        }
    }

    /// <summary>Wysokosc belki tytulu: wlasna dla tego fence'a albo globalna.</summary>
    private double EffectiveHeaderHeight
    {
        get
        {
            var raw = Model.HeaderHeightOverride > 0 ? Model.HeaderHeightOverride : Settings.HeaderHeight;
            return Math.Clamp(raw, MinHeaderHeight, MaxHeaderHeight);
        }
    }

    /// <summary>Strona belki: wlasna dla tego fence'a albo globalna.</summary>
    private HeaderSide EffectiveHeaderSide => Model.HeaderSideOverride ?? Settings.HeaderSide;

    /// <summary>Belka z boku jest pionowa - zwijanie i zmiana rozmiaru dzialaja wtedy w poziomie.</summary>
    private bool IsHeaderVertical => EffectiveHeaderSide is HeaderSide.Left or HeaderSide.Right;

    /// <summary>Wyrownanie nazwy: wlasne dla tego fence'a albo globalne.</summary>
    private TitleAlignment EffectiveTitleAlignment =>
        Model.TitleAlignmentOverride ?? Settings.TitleAlignment;

    /// <summary>
    /// Przestawia belke na wybrana strone. Pionowa belka nie miesci tytulu, wiec zostaje
    /// z niej sam uchwyt z przyciskiem menu - tytul czyta sie wtedy z menu albo z podpowiedzi.
    /// </summary>
    private void ApplyHeaderSide()
    {
        var side = EffectiveHeaderSide;
        var vertical = IsHeaderVertical;

        // Zmiana ustawien moze przyjsc w trakcie zmiany nazwy - wtedy nie wolno ruszac
        // widocznoscia tytulu ani edytora, bo przepadlby wpisywany wlasnie tekst.
        var renaming = TitleEditor.Visibility == Visibility.Visible;

        DockPanel.SetDock(HeaderBorder, side switch
        {
            HeaderSide.Bottom => Dock.Bottom,
            HeaderSide.Left => Dock.Left,
            HeaderSide.Right => Dock.Right,
            _ => Dock.Top,
        });

        if (vertical)
        {
            // Tytul obrocony o 270 stopni czyta sie z dolu do gory, jak na grzbiecie ksiazki.
            TitleText.LayoutTransform = new RotateTransform(270);
            TitleText.HorizontalAlignment = HorizontalAlignment.Center;
            TitleText.VerticalAlignment = VerticalAlignment.Center;

            // Edytor nazwy nie ma sie gdzie zmiescic - zmiana nazwy zostaje w menu.
            if (!renaming)
            {
                TitleEditor.Visibility = Visibility.Collapsed;
            }

            HeaderBorder.Padding = new Thickness(0, 6, 0, 6);
            HeaderButtons.Orientation = Orientation.Vertical;
            HeaderButtons.HorizontalAlignment = HorizontalAlignment.Center;
            HeaderButtons.VerticalAlignment = VerticalAlignment.Top;
            LockIndicator.Margin = new Thickness(0, 0, 0, 6);

            // Przyciety tytul da sie wtedy doczytac z podpowiedzi.
            HeaderBorder.ToolTip = Model.Title;
        }
        else
        {
            TitleText.LayoutTransform = Transform.Identity;
            TitleText.HorizontalAlignment = HorizontalAlignment.Stretch;
            TitleText.VerticalAlignment = VerticalAlignment.Center;
            TitleText.MaxWidth = double.PositiveInfinity;

            // Z prawej strony belki stoja klodka i przycisk menu - tyle miejsca trzeba im
            // zostawic. Przy wyrownaniu do srodka to samo idzie z lewej, inaczej nazwa
            // siedzialaby na srodku tego, co zostalo, a nie na srodku belki.
            const double buttonsReserve = 46;
            TitleText.Margin = EffectiveTitleAlignment == TitleAlignment.Center
                ? new Thickness(buttonsReserve, 0, buttonsReserve, 0)
                : new Thickness(0, 0, buttonsReserve, 0);

            HeaderBorder.Padding = new Thickness(10, 0, 6, 0);
            HeaderButtons.Orientation = Orientation.Horizontal;
            HeaderButtons.HorizontalAlignment = HorizontalAlignment.Right;
            HeaderButtons.VerticalAlignment = VerticalAlignment.Center;
            LockIndicator.Margin = new Thickness(0, 0, 6, 0);
            HeaderBorder.ToolTip = null;
        }

        if (!renaming)
        {
            TitleText.Visibility = Visibility.Visible;
        }

        // Wyrownanie liczy sie w tekscie, wiec przy belce pionowej jedzie razem z obrotem
        // i wypada wzdluz niej - "z lewej" to poczatek nazwy, czyli dol belki.
        TitleText.TextAlignment = EffectiveTitleAlignment switch
        {
            TitleAlignment.Center => TextAlignment.Center,
            TitleAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };

        // Podpowiedz dla pustego fence'a stoi na srodku okna - odsuwamy ja od belki,
        // zeby nie wchodzila pod nia niezaleznie od tego, z ktorej strony teraz stoi.
        EmptyHint.Margin = side switch
        {
            HeaderSide.Bottom => new Thickness(0, 0, 0, 18),
            HeaderSide.Left => new Thickness(18, 0, 0, 0),
            HeaderSide.Right => new Thickness(0, 0, 18, 0),
            _ => new Thickness(0, 18, 0, 0),
        };

        UpdateVerticalTitleLength();
    }

    /// <summary>
    /// Ile miejsca w pionowej belce zajmuje przycisk menu z klodka. Liczymy po Visibility,
    /// a nie po IsVisible: to drugie jest falszywe takze wtedy, gdy okno jest chwilowo
    /// schowane (zwiniete, wsuniete za krawedz), i rezerwa wychodzilaby wtedy za mala.
    /// </summary>
    private double VerticalButtonsSpace =>
        MenuButton.Height + (LockIndicator.Visibility == Visibility.Visible ? 20 : 6) + 6;

    /// <summary>
    /// Obrocony tytul rosnie wzdluz belki, a nie w poprzek, wiec przycinanie tekstu
    /// trzeba oprzec o wysokosc belki - inaczej dluga nazwa wyjezdzalaby poza fence.
    /// </summary>
    private void UpdateVerticalTitleLength()
    {
        if (!IsHeaderVertical)
        {
            return;
        }

        // Margines liczy sie w ukladzie rodzica i nie obraca sie razem z tekstem -
        // od gory rezerwuje miejsce na przycisk menu, ktory stoi tam pionowo.
        var reserved = VerticalButtonsSpace;
        TitleText.Margin = new Thickness(0, reserved, 0, 6);
        TitleText.MaxWidth = Math.Max(HeaderBorder.ActualHeight - reserved - 12, 0);
    }

    private void ApplyHeaderHeight(AppSettings settings)
    {
        var height = EffectiveHeaderHeight;

        // Ta sama liczba jest grubascia belki niezaleznie od strony - przy pionowej
        // staje sie szerokoscia, wiec drugi wymiar trzeba wyzerowac, inaczej zostalby
        // narzucony z poprzedniego ustawienia.
        if (IsHeaderVertical)
        {
            HeaderBorder.MinWidth = height;
            HeaderBorder.MinHeight = 0;
        }
        else
        {
            HeaderBorder.MinHeight = height;
            HeaderBorder.MinWidth = 0;
        }

        // Przycisk menu i klodka musza zmiescic sie w belce, gdy jest niska.
        var button = Math.Clamp(height - 8, 12, 24);
        MenuButton.Height = button;
        MenuButton.Width = button + 2;
        MenuButton.FontSize = Math.Clamp(button - 6, 8, 16);
        LockIndicator.FontSize = Math.Clamp(button - 8, 7, 14);

        // Wysokosc przycisku wlasnie sie zmienila, a od niej zalezy miejsce na obrocony tytul.
        UpdateVerticalTitleLength();

        // Zwiniety fence ma wysokosc belki - po jej zmianie trzeba przeliczyc okno.
        // Czekamy na przeliczenie ukladu, bo ActualHeight jest jeszcze stara.
        if (!IsLoaded)
        {
            return;
        }

        if (Model.Dock != EdgeDock.None)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SetPeek(_isPeeking, animate: false));
        }
        else if (Model.RolledUp)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SetRollUp(true, animate: false, persist: false));
        }
    }

    /// <summary>
    /// Zaokraglenie fence'a. Bok przyklejony do krawedzi ekranu jest plaski: zaokraglony rog
    /// przy pasku zadan przepuszcza tlo i czyta sie jako szpara miedzy fence'em a paskiem.
    /// </summary>
    private CornerRadius FenceCorners()
    {
        var r = Math.Clamp(Settings.CornerRadius, 0, 24);

        return Model.Dock switch
        {
            EdgeDock.Top => new CornerRadius(0, 0, r, r),
            EdgeDock.Bottom => new CornerRadius(r, r, 0, 0),
            EdgeDock.Left => new CornerRadius(0, r, r, 0),
            EdgeDock.Right => new CornerRadius(r, 0, 0, r),
            _ => new CornerRadius(r),
        };
    }

    /// <summary>
    /// Ramka fence'a. Po stronie przyklejenia znika - ta jedna linia to wlasnie "odstep"
    /// widoczny miedzy fence'em a paskiem zadan.
    /// </summary>
    private Thickness FenceBorderThickness() => Model.Dock switch
    {
        EdgeDock.Top => new Thickness(1, 0, 1, 1),
        EdgeDock.Bottom => new Thickness(1, 1, 1, 0),
        EdgeDock.Left => new Thickness(0, 1, 1, 1),
        EdgeDock.Right => new Thickness(1, 1, 0, 1),
        _ => new Thickness(1),
    };

    private void UpdateHeaderCorners()
    {
        var fence = FenceCorners();

        // Belka lezy tuz przy krawedzi fence'a, wiec jej rogi musza byc o grubosc ramki
        // mniejsze - inaczej wystaja zza zaokraglenia tla.
        static double Inner(double radius) => Math.Max(radius - 1, 0);

        if (Model.RolledUp)
        {
            // Zwiniety fence to sama belka - bierze zaokraglenie calego fence'a.
            HeaderBorder.CornerRadius = new CornerRadius(
                Inner(fence.TopLeft), Inner(fence.TopRight), Inner(fence.BottomRight), Inner(fence.BottomLeft));
            return;
        }

        // Zaokraglone zostaja tylko te rogi, ktore stykaja sie z krawedzia fence'a;
        // te przy zawartosci musza byc ostre, inaczej pokazywalo by przez nie tlo.
        HeaderBorder.CornerRadius = EffectiveHeaderSide switch
        {
            HeaderSide.Bottom => new CornerRadius(0, 0, Inner(fence.BottomRight), Inner(fence.BottomLeft)),
            HeaderSide.Left => new CornerRadius(Inner(fence.TopLeft), 0, 0, Inner(fence.BottomLeft)),
            HeaderSide.Right => new CornerRadius(0, Inner(fence.TopRight), Inner(fence.BottomRight), 0),
            _ => new CornerRadius(Inner(fence.TopLeft), Inner(fence.TopRight), 0, 0),
        };
    }

    // ---- zawartosc ---------------------------------------------------------

    public void ReloadItems()
    {
        Vm.Items.Clear();

        if (Model.PortalFolder is { Length: > 0 } folder)
        {
            LoadPortalItems(folder);
        }
        else
        {
            foreach (var item in Model.Items)
            {
                Vm.Items.Add(new FenceItemViewModel(item));
            }
        }

        UpdateEmptyHint();
        RefreshIcons();
        SetupPortalWatcher();
    }

    private void LoadPortalItems(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            var entries = Directory.EnumerateFileSystemEntries(folder)
                .Where(p => !IsHidden(p))
                .OrderBy(p => Directory.Exists(p) ? 0 : 1)
                .ThenBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase);

            foreach (var path in entries)
            {
                Vm.Items.Add(new FenceItemViewModel(new FenceItem { Path = path }));
            }
        }
        catch (Exception)
        {
            // Brak uprawnien albo znikniety folder - fence po prostu zostaje pusty.
        }
    }

    private static bool IsHidden(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);
        }
        catch
        {
            return true;
        }
    }

    private void SetupPortalWatcher()
    {
        _portalWatcher?.Dispose();
        _portalWatcher = null;

        if (Model.PortalFolder is not { Length: > 0 } folder || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            _portalWatcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                EnableRaisingEvents = true,
            };

            void Schedule(object? _, FileSystemEventArgs __) =>
                Dispatcher.BeginInvoke(() => { _portalDebounce.Stop(); _portalDebounce.Start(); });

            _portalWatcher.Created += Schedule;
            _portalWatcher.Deleted += Schedule;
            _portalWatcher.Changed += Schedule;
            _portalWatcher.Renamed += (_, _) =>
                Dispatcher.BeginInvoke(() => { _portalDebounce.Stop(); _portalDebounce.Start(); });
        }
        catch
        {
            _portalWatcher = null;
        }
    }

    public void RefreshIcons()
    {
        var size = EffectiveIconSize;
        var snapshot = Vm.Items.ToList();

        foreach (var item in snapshot)
        {
            var target = item;

            // Ikony ida przez wlasny watek STA serwisu - powloka nie oddaje ich rzetelnie
            // z watku puli. Wywolanie zwrotne przychodzi z tamtego watku, chyba ze ikona
            // byla juz w pamieci - wtedy leci tu i teraz i nie ma po co czekac na kolejke.
            _manager.Icons.RequestIcon(target.Path, size, icon =>
            {
                if (Dispatcher.CheckAccess())
                {
                    target.Icon = icon;
                }
                else
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () => target.Icon = icon);
                }
            });
        }
    }

    /// <summary>
    /// Podpowiedz stoi na srodku okna. Przyklejony fence ma okno pelnego rozmiaru takze wtedy,
    /// gdy jest schowany - podpowiedz wisialaby wtedy w powietrzu nad paskiem przy krawedzi.
    /// </summary>
    private void UpdateEmptyHint() =>
        EmptyHint.Visibility = Vm.Items.Count == 0 && !Model.RolledUp && (Model.Dock == EdgeDock.None || _isPeeking)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Dodaje sciezki do fence'a, pomijajac duplikaty. Zwraca liczbe faktycznie dodanych.</summary>
    public int AddPaths(IEnumerable<string> paths)
    {
        if (Model.PortalFolder is { Length: > 0 })
        {
            return 0; // Fence w trybie portalu odzwierciedla folder i nie ma wlasnej listy.
        }

        var added = 0;
        var deferred = new List<PendingMove>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Model.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var stored = StoreIfNeeded(path, deferred);

            if (Model.Items.Any(i => string.Equals(i.Path, stored, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var item = new FenceItem { Path = stored };
            Model.Items.Add(item);
            Vm.Items.Add(new FenceItemViewModel(item));
            added++;
        }

        if (added > 0)
        {
            UpdateEmptyHint();
            RefreshIcons();
            _manager.RequestSave();
            ScrollNewItemsIntoView();
        }

        if (deferred.Count > 0)
        {
            // Nie tutaj: jestesmy w srodku obslugi upuszczenia, a Eksplorator czeka z
            // zakonczeniem przeciagania, az oddamy sterowanie. Okno z pytaniem i czekanie
            // na proces pomocniczy zawiesiloby go na ten caly czas.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => MoveWithElevation(deferred));
        }

        return added;
    }

    /// <summary>
    /// Nowa pozycja laduje na koncu listy, wiec przy pelnym fence'ie chowa sie pod dolna
    /// krawedzia - ScrollViewer ja ma, tylko bez wskazowki, ze trzeba przewinac. Czekamy
    /// na uklad WrapPanela po dodaniu (DispatcherPriority.Loaded), dopiero potem przewijamy,
    /// inaczej ScrollableHeight jeszcze nie uwzglednia nowej pozycji.
    /// </summary>
    private void ScrollNewItemsIntoView() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ContentScroll.ScrollToBottom());

    /// <summary>
    /// Dokancza przenosiny, na ktore zwykle konto nie ma prawa - skroty z pulpitu wszystkich
    /// uzytkownikow. Bez tego ikona zostawalaby i na pulpicie, i w fence'ie.
    /// </summary>
    private void MoveWithElevation(IReadOnlyList<PendingMove> moves)
    {
        var names = string.Join(", ", moves.Select(m => Path.GetFileNameWithoutExtension(m.Source)));

        var answer = MessageBox.Show(
            Loc.Get("Msg_CommonDesktopMove", names),
            "FencesVE",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return; // Pozycja zostaje w fence'ie, ale wskazuje na skrot lezacy dalej na pulpicie.
        }

        if (ItemStorageService.RunElevatedMove(moves) == ElevatedMoveResult.Cancelled)
        {
            return;
        }

        var movedAny = false;

        foreach (var move in moves)
        {
            if (!ShellService.Exists(move.Target))
            {
                continue;
            }

            // Widok trzyma te same obiekty pozycji, co model - wystarczy przestawic sciezke
            // i powiadomic wiazania. Kolejnosc ma znaczenie: po zmianie sciezki pozycji juz
            // nie znajdziemy po starej nazwie.
            foreach (var vm in Vm.Items.Where(i => string.Equals(i.Path, move.Source, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                vm.Model.Path = move.Target;
                vm.PathChanged();
            }

            foreach (var item in Model.Items.Where(i => string.Equals(i.Path, move.Source, StringComparison.OrdinalIgnoreCase)))
            {
                item.Path = move.Target;
            }

            movedAny = true;
        }

        if (movedAny)
        {
            RefreshIcons();
            RefreshExistence();
            _manager.RequestSave();
        }
        else
        {
            MessageBox.Show(
                Loc.Get("Msg_CommonDesktopFailed"),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Wciaga plik do magazynu fence'a, zeby ta sama rzecz nie lezala jednoczesnie
    /// na pulpicie i w fence'ie. Ruszamy tylko to, co lezy wprost na pulpicie albo juz
    /// jest w magazynie (przeciagniecie miedzy fence'ami) - plik z dowolnego innego
    /// miejsca na dysku zostaje tam, gdzie byl.
    /// </summary>
    private string StoreIfNeeded(string path, List<PendingMove> deferred)
    {
        if (!Settings.MoveItemsIntoFence)
        {
            return path;
        }

        var storage = _manager.Storage;

        if (!ItemStorageService.IsOnDesktop(path) && !storage.IsInStorage(path))
        {
            return path;
        }

        var moved = storage.MoveIn(path, Model.Id);

        if (!string.Equals(moved, path, StringComparison.OrdinalIgnoreCase))
        {
            return moved;
        }

        // Przeniesienie odbilo sie od uprawnien. Skroty instalatorow leza na pulpicie
        // wszystkich uzytkownikow - te zbieramy i ruszamy potem jednym zapytaniem o zgode.
        if (ItemStorageService.IsOnCommonDesktop(path))
        {
            deferred.Add(new PendingMove(path, storage.ReserveTarget(path, Model.Id)));
        }

        return path;
    }

    public void RemovePaths(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0)
        {
            return;
        }

        // Co przyszlo z pulpitu, na pulpit wraca. Plik juz przeniesiony gdzie indziej
        // (przeciagniecie do innego fence'a) nie istnieje pod ta sciezka i zostaje pominiety.
        foreach (var item in Model.Items.Where(i => set.Contains(i.Path)))
        {
            _manager.Storage.MoveOutToDesktop(item.Path);
        }

        Model.Items.RemoveAll(i => set.Contains(i.Path));

        foreach (var vm in Vm.Items.Where(i => set.Contains(i.Path)).ToList())
        {
            Vm.Items.Remove(vm);
        }

        UpdateEmptyHint();
        _manager.RequestSave();
    }

    /// <summary>
    /// Przestawia pozycje na nowa sciezke - po zmianie nazwy pliku na pulpicie.
    /// Zwraca true, gdy ten fence faktycznie trzymal stara sciezke.
    /// </summary>
    public bool RenamePath(string oldPath, string newPath)
    {
        var matched = false;

        foreach (var vm in Vm.Items.Where(i => string.Equals(i.Path, oldPath, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            vm.Model.Path = newPath;
            vm.PathChanged();
            vm.RefreshExistence();

            // Wlasna nazwa nadana przez uzytkownika zostaje; domyslna idzie za plikiem.
            if (vm.Model.DisplayName is not { Length: > 0 })
            {
                vm.DisplayName = ShellService.GetDisplayName(newPath);
            }

            matched = true;
        }

        foreach (var item in Model.Items.Where(i => string.Equals(i.Path, oldPath, StringComparison.OrdinalIgnoreCase)))
        {
            item.Path = newPath;
            matched = true;
        }

        if (matched)
        {
            RefreshIcons();
            _manager.RequestSave();
        }

        return matched;
    }

    /// <summary>Sprawdza, czy pliki nadal istnieja, i odswieza wygaszenie brakujacych.</summary>
    public void RefreshExistence()
    {
        foreach (var item in Vm.Items)
        {
            item.RefreshExistence();
        }
    }

    // ---- przeciaganie i upuszczanie ---------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var canDrop = e.Data.GetDataPresent(DataFormats.FileDrop) && Model.PortalFolder is not { Length: > 0 };
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        AddPaths(paths);

        // Przeciagniecie z innego fence'a to przeniesienie, nie kopia. Zrodlo jest wciaz
        // ustawione - to wywolanie dzieje sie w srodku petli przeciagania tamtego okna.
        if (_dragSource is { } source && !ReferenceEquals(source, this))
        {
            _manager.RemoveFromFence(source.Model.Id, paths);
        }

        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDownOnItem || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _mouseDownOnItem = false;

        var selected = Vm.Items.Where(i => i.IsSelected).Select(i => i.Path).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, selected);
        _dragSource = this;

        try
        {
            var effect = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move);
            FinishDrag(selected, effect);
        }
        catch (COMException)
        {
            // Powloka potrafi odrzucic operacje przeciagania - ignorujemy.
        }
        finally
        {
            _dragSource = null;
        }
    }

    /// <summary>
    /// Konczy przeciagniecie ikony na pulpit.
    /// <para>
    /// Powloka nie zrobi tego za nas. Magazyn fence'ow siedzi w <c>%APPDATA%</c>, a Windows
    /// odmawia przenoszenia i kopiowania plikow <b>z</b> katalogow AppData: upuszczenie na
    /// pulpicie konczy sie wtedy efektem <see cref="DragDropEffects.Copy"/> i... niczym wiecej.
    /// Zmierzone: ten sam plik z <c>C:\Temp</c>, z Dokumentow albo z katalogu profilu ladnie
    /// laduje na pulpicie (efekt <see cref="DragDropEffects.Move"/>), ten sam plik z
    /// <c>AppData\Roaming</c> i <c>AppData\Local</c> - nie laduje nigdzie.
    /// </para>
    /// <para>
    /// Dlatego nie wierzymy deklarowanemu efektowi, tylko patrzymy, gdzie skonczyl kursor.
    /// Nad pulpitem odkladamy plik sami, dokladnie tak jak pozycja menu "Przenies z powrotem
    /// na pulpit" - a ta droga jest w pelni nasza i dziala.
    /// </para>
    /// </summary>
    private void FinishDrag(IReadOnlyList<string> paths, DragDropEffects effect)
    {
        // Jedyny przypadek, w ktorym powloka naprawde zabrala pliki - zostaje uprzatnac liste.
        if (effect == DragDropEffects.Move)
        {
            DropMovedAway(paths);
            return;
        }

        // Przerwanie klawiszem Escape konczy sie tak samo jak upuszczenie, ale wtedy przycisk
        // myszy jest wciaz wcisniety - i tylko po tym da sie je odroznic. Pytamy Windows
        // o stan fizyczny: przez caly czas trwania petli OLE do WPF nie doszedl zaden
        // komunikat myszy, wiec jego wlasny stan przycisku jest tu bezuzyteczny.
        if (NativeMethods.IsLeftButtonDown())
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor) || !DesktopService.IsDesktopAt(cursor))
        {
            return;
        }

        // Tylko to, co ten fence realnie trzyma u siebie. Pozycja bedaca odnosnikiem do pliku
        // lezacego gdzies na dysku nie ma czego "wracac" na pulpit.
        var stored = paths.Where(p => _manager.Storage.IsInStorage(p) && ShellService.Exists(p)).ToList();

        if (stored.Count > 0)
        {
            RemovePaths(stored);
        }
    }

    /// <summary>
    /// Po przeciagnieciu ikony poza fence - najczesciej z powrotem na pulpit - plik zabiera
    /// powloka i pod stara sciezka juz go nie ma. Bez tego pozycja zostawalaby w fence'ie
    /// jako wygaszona "brakujaca", a przeciez uzytkownik wlasnie ja stad zabral.
    /// </summary>
    private void DropMovedAway(IEnumerable<string> paths)
    {
        var gone = paths.Where(p => !ShellService.Exists(p)).ToList();
        if (gone.Count == 0)
        {
            return;
        }

        // Pliki juz sa gdzie indziej - sama lista, bez odkladania czegokolwiek na pulpit.
        var set = new HashSet<string>(gone, StringComparer.OrdinalIgnoreCase);
        Model.Items.RemoveAll(i => set.Contains(i.Path));

        foreach (var vm in Vm.Items.Where(i => set.Contains(i.Path)).ToList())
        {
            Vm.Items.Remove(vm);
        }

        UpdateEmptyHint();
        _manager.RequestSave();
    }

    // ---- belka tytulu ------------------------------------------------------

    /// <summary>
    /// Dwuklik w sama nazwe zmienia nazwe. Przy belce pionowej edytor nie ma sie gdzie zmiescic,
    /// wiec zdarzenie leci dalej do belki i konczy sie zwyklym zwinieciem.
    /// </summary>
    private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || IsHeaderVertical)
        {
            return;
        }

        BeginRenameTitle();
        e.Handled = true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SetRollUp(!Model.RolledUp, animate: true, persist: true);
            e.Handled = true;
            return;
        }

        if (Settings.LockFences)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove rzuca, gdy przycisk zostal zwolniony zanim ruszylismy oknem.
            return;
        }

        // Przyklejony fence wraca na swoja krawedz - przeciaganiem zmieniamy tylko pozycje wzdluz niej.
        if (Model.Dock != EdgeDock.None)
        {
            SetPeek(true, animate: false);
        }

        SyncBoundsToModel();
        _manager.RequestSave();
    }

    private void Header_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowFenceMenu();
        e.Handled = true;
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e) => ShowFenceMenu();

    private void BeginRenameTitle()
    {
        TitleEditor.Text = Vm.Title;
        TitleEditor.Visibility = Visibility.Visible;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditor.Focus();
        TitleEditor.SelectAll();
    }

    private void CommitRenameTitle(bool save)
    {
        if (TitleEditor.Visibility != Visibility.Visible)
        {
            return;
        }

        if (save && !string.IsNullOrWhiteSpace(TitleEditor.Text))
        {
            Vm.Title = TitleEditor.Text.Trim();

            // Pionowa belka pokazuje nazwe takze w podpowiedzi - inaczej zostalaby stara.
            if (IsHeaderVertical)
            {
                HeaderBorder.ToolTip = Vm.Title;
            }

            _manager.RequestSave();
        }

        TitleEditor.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    private void TitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRenameTitle(save: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitRenameTitle(save: false);
            e.Handled = true;
        }
    }

    private void TitleEditor_LostFocus(object sender, RoutedEventArgs e) => CommitRenameTitle(save: true);

    // ---- zwijanie ----------------------------------------------------------

    private double CollapsedHeight()
    {
        var header = HeaderBorder.ActualHeight > 0 ? HeaderBorder.ActualHeight : EffectiveHeaderHeight;
        return header + RootBorder.BorderThickness.Top + RootBorder.BorderThickness.Bottom;
    }

    private double CollapsedWidth()
    {
        var header = HeaderBorder.ActualWidth > 0 ? HeaderBorder.ActualWidth : EffectiveHeaderHeight;
        return header + RootBorder.BorderThickness.Left + RootBorder.BorderThickness.Right;
    }

    public void SetRollUp(bool rolled, bool animate, bool persist)
    {
        if (rolled)
        {
            if (!Model.RolledUp)
            {
                Model.RestoreHeight = ActualHeight;
                Model.RestoreWidth = ActualWidth;
            }

            Model.RolledUp = true;
            ContentScroll.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            Model.RolledUp = false;
            ContentScroll.Visibility = Visibility.Visible;
            UpdateEmptyHint();
        }

        if (Model.Dock != EdgeDock.None)
        {
            // Przy krawedzi rozmiarem steruje szuflada - inaczej obie animacje bilyby sie o RootBorder.
            SetPeek(_isPeeking, animate);
        }
        else
        {
            AnimateRollUp(rolled, animate);
        }

        UpdateHeaderCorners();

        if (persist)
        {
            _manager.RequestSave();
        }
    }

    /// <summary>
    /// Zwija fence do samej belki albo go rozwija. Belka stoi w miejscu, a fence skraca sie
    /// albo wydluza od przeciwnej strony.
    /// <para>
    /// Przez cala animacje okno ma rozmiar pelnego fence'a, a skraca sie RootBorder - patrz
    /// <see cref="AnimateRootSize"/>. Samo okno zmienia rozmiar tylko raz: przy rozwijaniu na
    /// poczatku, przy zwijaniu na koncu. Wczesniej belka na dole przy rozwijaniu podskakiwala
    /// o kilkadziesiat pikseli, a po kazdym zwinieciu fence zjezdzal o piksel nizej, bo dolna
    /// krawedz liczyla sie za kazdym razem od nowa z zaokraglonych Top i ActualHeight.
    /// </para>
    /// </summary>
    private void AnimateRollUp(bool rolled, bool animate)
    {
        var vertical = IsHeaderVertical;
        var along = vertical ? WidthProperty : HeightProperty;

        // Belka po prawej albo na dole: w miejscu stoi dalsza krawedz okna.
        var farEdge = EffectiveHeaderSide is HeaderSide.Right or HeaderSide.Bottom;

        var collapsed = vertical ? CollapsedWidth() : CollapsedHeight();
        var target = rolled
            ? collapsed
            : vertical
                ? Math.Max(Model.RestoreWidth, MinFenceWidth)
                : Math.Max(Model.RestoreHeight, MinFenceHeight);

        var bounds = CurrentBoundsDip();
        var windowSize = vertical ? bounds.Width : bounds.Height;

        RootBorder.HorizontalAlignment = !vertical
            ? HorizontalAlignment.Stretch
            : farEdge ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        RootBorder.VerticalAlignment = vertical
            ? VerticalAlignment.Stretch
            : farEdge ? VerticalAlignment.Bottom : VerticalAlignment.Top;

        // Zanim okno urosnie, RootBorder musi trzymac to, co widac teraz - inaczej w pierwszej
        // klatce po powiekszeniu okna mignalby caly fence.
        var from = CurrentRootSize(along, windowSize);
        RootBorder.BeginAnimation(along, null);
        RootBorder.SetValue(along, from);

        if (windowSize < target - 0.5)
        {
            ResizeAlongHeader(vertical, farEdge, target);
        }

        // Podpowiedz pustego fence'a stoi na srodku okna, ktore juz ma pelny rozmiar -
        // pokazujemy ja dopiero, gdy fence dojedzie.
        if (!rolled && animate)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }

        AnimateRootSize(along, from, target, animate, () =>
        {
            ResizeAlongHeader(vertical, farEdge, target);

            RootBorder.SetValue(along, double.NaN);
            RootBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            RootBorder.VerticalAlignment = VerticalAlignment.Stretch;
            UpdateEmptyHint();
        });
    }

    /// <summary>Zmienia rozmiar okna wzdluz osi belki tak, ze krawedz z belka stoi w miejscu.</summary>
    private void ResizeAlongHeader(bool vertical, bool farEdge, double size)
    {
        var bounds = CurrentBoundsDip();

        if (vertical)
        {
            SetBoundsAtOnce(farEdge ? bounds.Right - size : bounds.Left, bounds.Top, size, bounds.Height);
        }
        else
        {
            SetBoundsAtOnce(bounds.Left, farEdge ? bounds.Bottom - size : bounds.Top, bounds.Width, size);
        }
    }

    // ---- zmiana rozmiaru ---------------------------------------------------

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Settings.LockFences || sender is not Thumb { Tag: string tag })
        {
            return;
        }

        // Zwiniety fence ma po jednej osi rozmiar samej belki - tej osi nie skalujemy.
        if (Model.RolledUp && IsHeaderVertical)
        {
            ResizeVertically(tag, e);
            return;
        }

        if (tag.Contains('W'))
        {
            var width = Width - e.HorizontalChange;
            if (width >= MinFenceWidth)
            {
                // Pozycja i rozmiar naraz - inaczej prawa krawedz drga przy ciagnieciu lewej.
                SetBoundsAtOnce(Left + e.HorizontalChange, Top, width, Height);
            }
        }
        else if (tag.Contains('E'))
        {
            var width = Width + e.HorizontalChange;
            if (width >= MinFenceWidth)
            {
                Width = width;
            }
        }

        if (Model.RolledUp)
        {
            return; // Zwiniety fence ma wysokosc belki - pionowo nie skalujemy.
        }

        ResizeVertically(tag, e);
    }

    private void ResizeVertically(string tag, DragDeltaEventArgs e)
    {
        if (tag.Contains('N'))
        {
            var height = Height - e.VerticalChange;
            if (height >= MinFenceHeight)
            {
                // Pozycja i rozmiar naraz - inaczej dolna krawedz drga przy ciagnieciu gornej.
                SetBoundsAtOnce(Left, Top + e.VerticalChange, Width, height);
            }
        }
        else if (tag.Contains('S'))
        {
            var height = Height + e.VerticalChange;
            if (height >= MinFenceHeight)
            {
                Height = height;
            }
        }
    }

    /// <summary>
    /// Ustawia polozenie i rozmiar okna jednym SetWindowPos, a potem wyrownuje do nich
    /// wlasciwosci WPF. Osobne przypisania Left/Top i Width/Height to dwa ruchy okna -
    /// miedzy nimi widac stan posredni i przeciwlegla krawedz drga.
    /// </summary>
    private void SetBoundsAtOnce(double left, double top, double width, double height)
    {
        if (_hwnd != IntPtr.Zero)
        {
            var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                           ?? Matrix.Identity;

            // Liczymy od krawedzi, a nie od rozmiaru, zeby zaokraglenia nie przesuwaly
            // krawedzi, ktora ma stac w miejscu.
            var x = (int)Math.Round(left * toDevice.M11);
            var y = (int)Math.Round(top * toDevice.M22);
            var w = Math.Max(1, (int)Math.Round((left + width) * toDevice.M11) - x);
            var h = Math.Max(1, (int)Math.Round((top + height) * toDevice.M22) - y);

            // Okno, ktore juz tam stoi, zostawiamy w spokoju - kazde SetWindowPos przestawia
            // je tez w z-order (patrz WndProc), a szuflada wola to przy kazdym wysunieciu.
            if (!NativeMethods.GetWindowRect(_hwnd, out var current) ||
                current.Left != x || current.Top != y ||
                current.Right - current.Left != w || current.Bottom - current.Top != h)
            {
                NativeMethods.SetWindowPos(
                    _hwnd, IntPtr.Zero, x, y, w, h,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }

            if (SyncPropertiesFromHwnd())
            {
                return;
            }
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Przepisuje do Left/Top/Width/Height to, co faktycznie stoi w oknie Win32.
    /// Wartosci liczone z pikseli urzadzenia zaokraglaja sie z powrotem dokladnie do tych
    /// samych pikseli, wiec WPF nie przesunie okna o piksel przy ich przypisaniu.
    /// </summary>
    private bool SyncPropertiesFromHwnd()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return false;
        }

        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                         ?? Matrix.Identity;

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);

        Left = rect.Left * fromDevice.M11;
        Top = rect.Top * fromDevice.M22;
        Width = (rect.Right - rect.Left) * fromDevice.M11;
        Height = (rect.Bottom - rect.Top) * fromDevice.M22;
        return true;
    }

    private void Resize_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (Model.Dock != EdgeDock.None)
        {
            // Przy krawedzi nowy rozmiar staje sie rozmiarem wysunietej szuflady.
            Model.Width = Width;

            if (!Model.RolledUp)
            {
                Model.Height = Height;
                Model.RestoreHeight = Height;
            }

            SetPeek(_isPeeking, animate: false);
        }

        SyncBoundsToModel();
        _manager.RequestSave();
    }

    /// <summary>
    /// Ustawia fence w podanym punkcie ekranu (piksele fizyczne, np. pozycja kursora),
    /// przycinajac go do obszaru roboczego monitora. Wywolywac po Show().
    /// </summary>
    public void PlaceAtDevicePoint(int deviceX, int deviceY)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;

        var point = transform.Transform(new Point(deviceX, deviceY));

        Left = point.X;
        Top = point.Y;

        // Dopiero teraz okno stoi na docelowym monitorze, wiec obszar roboczy bedzie ten wlasciwy.
        var area = WorkingAreaDip();
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));

        SyncBoundsToModel();
    }

    /// <summary>Przepisuje aktualne polozenie i rozmiar okna do modelu.</summary>
    public void SyncBoundsToModel()
    {
        Model.X = Left;
        Model.Y = Top;

        // Przyklejony fence bywa zwiniety do paska - jego rozmiar trzyma model, nie okno.
        if (Model.Dock != EdgeDock.None)
        {
            return;
        }

        // Zwiniety fence ma rozmiar samej belki - w modelu musi zostac ten sprzed zwiniecia,
        // inaczej rozwiniecie oddaloby pasek zamiast fence'a. Zwija sie tylko jedna os,
        // zalezna od strony belki, wiec ta druga zapisuje sie normalnie.
        if (!Model.RolledUp || IsHeaderVertical)
        {
            Model.Height = Height;
            Model.RestoreHeight = Height;
        }

        if (!Model.RolledUp || !IsHeaderVertical)
        {
            Model.Width = Width;
            Model.RestoreWidth = Width;
        }
    }

    // ---- zmiana monitorow --------------------------------------------------

    /// <summary>Biezace polozenie fence'a w pikselach fizycznych, razem z monitorem, na ktorym stoi.</summary>
    public FencePlacement? CapturePlacement()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return null;
        }

        var handle = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);

        if (DisplayService.Describe(handle) is not { } monitor)
        {
            return null;
        }

        return new FencePlacement
        {
            Left = rect.Left,
            Top = rect.Top,
            PixelWidth = rect.Right - rect.Left,
            PixelHeight = rect.Bottom - rect.Top,
            MonitorLeft = monitor.Bounds.Left,
            MonitorTop = monitor.Bounds.Top,
            MonitorWidth = monitor.Bounds.Right - monitor.Bounds.Left,
            MonitorHeight = monitor.Bounds.Bottom - monitor.Bounds.Top,
            AreaLeft = monitor.Work.Left,
            AreaTop = monitor.Work.Top,
            AreaWidth = monitor.WorkWidth,
            AreaHeight = monitor.WorkHeight,
            Dpi = monitor.Dpi,
            Width = Model.Width,
            Height = Model.Height,
            RestoreWidth = Model.RestoreWidth,
            RestoreHeight = Model.RestoreHeight,
        };
    }

    /// <summary>
    /// Stawia fence w zapamietanym polozeniu. Pozycje ustawiamy przez SetWindowPos
    /// w pikselach fizycznych - Left/Top okna WPF przelicza sie przez DPI monitora,
    /// na ktorym okno stoi teraz, a nie tego, na ktory ma trafic.
    /// Po ukladzie trzeba jeszcze wywolac <see cref="SettleAfterDisplayChange"/>.
    /// </summary>
    public void ApplyPlacement(FencePlacement placement)
    {
        Model.Width = placement.Width;
        Model.Height = placement.Height;
        Model.RestoreWidth = placement.RestoreWidth;
        Model.RestoreHeight = placement.RestoreHeight;

        // Niedokonczona animacja zwijania/szuflady nadpisalaby to, co tu ustawimy.
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);

        // Przyklejonemu fence'owi rozmiar ustawia szuflada - patrz SettleAfterDisplayChange.
        if (Model.Dock == EdgeDock.None)
        {
            // Zakonczenie przerwanego zwijania zmienialoby jeszcze rozmiar okna po swojemu.
            ResetRootBorder();

            Width = Model.RolledUp && IsHeaderVertical
                ? CollapsedWidth()
                : Math.Max(Model.Width, MinFenceWidth);

            Height = Model.RolledUp && !IsHeaderVertical
                ? CollapsedHeight()
                : Math.Max(Model.Height, MinFenceHeight);
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, placement.Left, placement.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// Dopina fence do obszaru roboczego monitora, na ktorym stoi po zmianie ukladu ekranow:
    /// przyklejony wraca do krawedzi, zwykly nie moze wystawac poza ekran.
    /// </summary>
    public void SettleAfterDisplayChange()
    {
        if (Model.Dock != EdgeDock.None)
        {
            SetPeek(_isPeeking, animate: false);
        }
        else
        {
            var area = WorkingAreaDip();
            Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
            Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        }

        SyncBoundsToModel();
    }

    // ---- interakcja z pozycjami -------------------------------------------

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FenceItemViewModel item })
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _mouseDownOnItem = false;
            Launch(item);
            e.Handled = true;
            return;
        }

        var toggle = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (toggle)
        {
            item.IsSelected = !item.IsSelected;
        }
        else
        {
            if (!item.IsSelected)
            {
                ClearSelection();
                item.IsSelected = true;
            }
        }

        _mouseDownOnItem = item.IsSelected;
        _dragStart = e.GetPosition(this);
        e.Handled = true;
    }

    private void Item_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FenceItemViewModel item })
        {
            return;
        }

        if (!item.IsSelected)
        {
            ClearSelection();
            item.IsSelected = true;
        }

        ShowItemMenu(item);
        e.Handled = true;
    }

    private void Content_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ClearSelection();

    private void Content_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowFenceMenu();
        e.Handled = true;
    }

    private void ClearSelection()
    {
        foreach (var item in Vm.Items)
        {
            item.IsSelected = false;
        }
    }

    private void Launch(FenceItemViewModel item)
    {
        try
        {
            ShellService.Open(item.Path);
        }
        catch (Exception ex)
        {
            item.RefreshExistence();
            MessageBox.Show(
                Loc.Get("Msg_OpenFailed", item.Path, ex.Message),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ---- menu kontekstowe --------------------------------------------------

    private void ShowFenceMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MenuItemFor(Loc.Get("Menu_Rename"), BeginRenameTitle));
        menu.Items.Add(MenuItemFor(
            Loc.Get(Model.RolledUp ? "Menu_Unroll" : "Menu_Roll"),
            () => SetRollUp(!Model.RolledUp, animate: true, persist: true)));

        var autoRoll = new MenuItem
        {
            Header = Loc.Get("Menu_AutoRoll"),
            IsCheckable = true,
            IsChecked = Model.AutoRollUp,
            // Przy krawedzi chowaniem steruje szuflada, wiec ta opcja nic by nie wniosla.
            IsEnabled = Model.Dock == EdgeDock.None,
            ToolTip = Model.Dock == EdgeDock.None
                ? Loc.Get("Menu_AutoRollTip")
                : Loc.Get("Menu_AutoRollDisabledTip"),
        };
        autoRoll.Click += (_, _) => SetAutoRollUp(autoRoll.IsChecked);
        menu.Items.Add(autoRoll);

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuItemFor(Loc.Get("Menu_SortByName"), SortByName));
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_Refresh"), () =>
        {
            _manager.Icons.ClearCache();
            ReloadItems();
            RefreshExistence();
        }));
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_RepairMissing"), RepairMissing));
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_RemoveMissing"), RemoveMissing));

        var stored = Model.Items.Count(i => _manager.Storage.IsInStorage(i.Path));
        var emptyToDesktop = MenuItemFor(Loc.Get("Menu_EmptyToDesktop"), EmptyToDesktop);
        emptyToDesktop.IsEnabled = stored > 0;

        if (stored == 0)
        {
            emptyToDesktop.ToolTip = Loc.Get("Menu_EmptyToDesktopEmptyTip");
        }

        menu.Items.Add(emptyToDesktop);

        menu.Items.Add(new Separator());

        menu.Items.Add(BuildHeaderSideMenu());
        menu.Items.Add(BuildTitleAlignmentMenu());
        menu.Items.Add(BuildDockMenu());

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuItemFor(Loc.Get("Menu_FenceColor"), PickFenceColor));
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_FenceColorEyedrop"), () => PickWithEyedropper(isText: false)));
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_FenceTextColor"), PickFenceTextColor));
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_FenceTextColorEyedrop"), () => PickWithEyedropper(isText: true)));
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_FenceFontSize"), PickFenceFontSize));
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_FenceHeaderHeight"), PickFenceHeaderHeight));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_SaveTheme"), SaveThemeFromThisFence));

        var applyTheme = MenuItemFor(Loc.Get("Menu_ApplyTheme"), ApplySavedTheme);
        applyTheme.IsEnabled = Settings.SavedFenceTheme is not null;
        applyTheme.ToolTip = Settings.SavedFenceTheme is null ? Loc.Get("Menu_ApplyThemeEmptyTip") : null;
        menu.Items.Add(applyTheme);

        if (Model.AccentOverride is { Length: > 0 } ||
            Model.TextColorOverride is { Length: > 0 } ||
            Model.FontSizeOverride > 0 ||
            Model.HeaderHeightOverride > 0 ||
            Model.HeaderSideOverride is not null ||
            Model.TitleAlignmentOverride is not null)
        {
            menu.Items.Add(MenuItemFor(Loc.Get("Menu_ResetToGlobal"), () =>
            {
                Model.AccentOverride = null;
                Model.TextColorOverride = null;
                Model.FontSizeOverride = 0;
                Model.HeaderHeightOverride = 0;
                Model.TitleAlignmentOverride = null;
                SetHeaderSide(null);
            }));
        }

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuItemFor(Loc.Get("Menu_NewFence"), () => _manager.CreateFence()));
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_Settings"), () => _manager.ShowSettings()));

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuItemFor(Loc.Get("Menu_DeleteFence"), DeleteSelf));

        OpenMenu(menu);
    }

    private MenuItem BuildHeaderSideMenu()
    {
        var root = new MenuItem { Header = Loc.Get("Menu_HeaderSide") };

        void AddOption(string header, HeaderSide side)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = Model.HeaderSideOverride == side,
            };

            item.Click += (_, _) => SetHeaderSide(side);
            root.Items.Add(item);
        }

        AddOption(Loc.Get("Side_Top"), HeaderSide.Top);
        AddOption(Loc.Get("Side_Bottom"), HeaderSide.Bottom);
        AddOption(Loc.Get("Side_Left"), HeaderSide.Left);
        AddOption(Loc.Get("Side_Right"), HeaderSide.Right);

        root.Items.Add(new Separator());

        var global = new MenuItem
        {
            Header = Loc.Get("Menu_HeaderSideGlobal", SideLabel(Settings.HeaderSide)),
            IsCheckable = true,
            IsChecked = Model.HeaderSideOverride is null,
        };
        global.Click += (_, _) => SetHeaderSide(null);
        root.Items.Add(global);

        return root;
    }

    /// <summary>
    /// Zapisuje wyglad tego fence'a jako wzorzec dla nastepnych. Bierzemy same nadpisania,
    /// wiec to, co ten fence dziedziczy z ustawien globalnych, zostaje dziedziczone takze
    /// przez nowe fence'y - a nie zamraza sie w motywie jako wartosc.
    /// </summary>
    private void SaveThemeFromThisFence()
    {
        Settings.SavedFenceTheme = FenceTheme.FromFence(Model);
        _manager.RequestSave();

        MessageBox.Show(
            Loc.Get("Msg_ThemeSaved", Vm.Title), "FencesVE",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ApplySavedTheme()
    {
        if (Settings.SavedFenceTheme is not { } theme)
        {
            return;
        }

        theme.ApplyTo(Model);
        ApplySettings(Settings);
        _manager.RequestSave();
    }

    private MenuItem BuildTitleAlignmentMenu()
    {
        var root = new MenuItem { Header = Loc.Get("Menu_TitleAlignment") };

        void AddOption(string header, TitleAlignment alignment)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = Model.TitleAlignmentOverride == alignment,
            };

            item.Click += (_, _) => SetTitleAlignment(alignment);
            root.Items.Add(item);
        }

        AddOption(Loc.Get("Align_Left"), TitleAlignment.Left);
        AddOption(Loc.Get("Align_Center"), TitleAlignment.Center);
        AddOption(Loc.Get("Align_Right"), TitleAlignment.Right);

        root.Items.Add(new Separator());

        var global = new MenuItem
        {
            Header = Loc.Get("Menu_TitleAlignmentGlobal", AlignmentLabel(Settings.TitleAlignment)),
            IsCheckable = true,
            IsChecked = Model.TitleAlignmentOverride is null,
        };
        global.Click += (_, _) => SetTitleAlignment(null);
        root.Items.Add(global);

        return root;
    }

    /// <summary>Przestawia wyrownanie nazwy. null oznacza powrot do ustawienia globalnego.</summary>
    public void SetTitleAlignment(TitleAlignment? alignment)
    {
        Model.TitleAlignmentOverride = alignment;
        ApplySettings(Settings);
        _manager.RequestSave();
    }

    private static string AlignmentLabel(TitleAlignment alignment) => alignment switch
    {
        TitleAlignment.Center => Loc.Get("AlignLower_Center"),
        TitleAlignment.Right => Loc.Get("AlignLower_Right"),
        _ => Loc.Get("AlignLower_Left"),
    };

    private static string SideLabel(HeaderSide side) => side switch
    {
        HeaderSide.Bottom => Loc.Get("SideLower_Bottom"),
        HeaderSide.Left => Loc.Get("SideLower_Left"),
        HeaderSide.Right => Loc.Get("SideLower_Right"),
        _ => Loc.Get("SideLower_Top"),
    };

    /// <summary>
    /// Przestawia belke tego fence'a. null oznacza powrot do ustawienia globalnego.
    /// </summary>
    public void SetHeaderSide(HeaderSide? side)
    {
        var wasVertical = IsHeaderVertical;

        Model.HeaderSideOverride = side;
        ApplySettings(Settings);

        // Przy zmianie miedzy belka pozioma a pionowa zwija sie inna os, wiec zwiniety
        // fence trzeba przeliczyc - inaczej zostalby pasek wzdluz nieaktualnej krawedzi.
        if (Model.RolledUp && wasVertical != IsHeaderVertical)
        {
            if (wasVertical)
            {
                Width = Math.Max(Model.RestoreWidth, MinFenceWidth);
            }
            else
            {
                Height = Math.Max(Model.RestoreHeight, MinFenceHeight);
            }

            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                () => SetRollUp(true, animate: false, persist: false));
        }

        _manager.RequestSave();
    }

    private MenuItem BuildDockMenu()
    {
        var root = new MenuItem { Header = Loc.Get("Menu_Dock") };

        void AddOption(string header, EdgeDock dock)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = Model.Dock == dock,
            };

            item.Click += (_, _) => SetDock(dock);
            root.Items.Add(item);
        }

        AddOption(Loc.Get("Dock_Left"), EdgeDock.Left);
        AddOption(Loc.Get("Dock_Right"), EdgeDock.Right);
        AddOption(Loc.Get("Dock_Top"), EdgeDock.Top);
        AddOption(Loc.Get("Dock_Bottom"), EdgeDock.Bottom);

        root.Items.Add(new Separator());
        AddOption(Loc.Get("Dock_None"), EdgeDock.None);

        return root;
    }

    /// <summary>Otwiera menu i wstrzymuje chowanie szuflady, dopoki menu jest widoczne.</summary>
    private void OpenMenu(ContextMenu menu)
    {
        _menuOpen = true;

        menu.Closed += (_, _) =>
        {
            _menuOpen = false;

            if (HasAutoHide && !IsMouseOver)
            {
                _autoHideDelay.Stop();
                _autoHideDelay.Start();
            }
        };

        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    private void ShowItemMenu(FenceItemViewModel item)
    {
        var selectedCount = Vm.Items.Count(i => i.IsSelected);
        var menu = new ContextMenu();

        menu.Items.Add(MenuItemFor(Loc.Get("Menu_Open"), () => Launch(item)));
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_RevealInExplorer"), () =>
        {
            try { ShellService.RevealInExplorer(item.Path); } catch { /* plik mogl zniknac */ }
        }));
        menu.Items.Add(MenuItemFor(Loc.Get("Menu_Properties"), () =>
        {
            try { ShellService.ShowProperties(item.Path); } catch { /* brak obslugi verbu */ }
        }));

        if (Model.PortalFolder is not { Length: > 0 })
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor(Loc.Get("Menu_RenameItem"), () => RenameItem(item)));

            // Pozycja wciagnieta do magazynu wraca przy usunieciu na pulpit, wiec tak sie tez
            // nazywa. Pozycja bedaca tylko odnosnikiem do pliku gdzies na dysku niczego nie
            // przenosi - znika sam wpis, i o tym mowi druga nazwa.
            var stored = Vm.Items.Any(i => i.IsSelected && _manager.Storage.IsInStorage(i.Path));

            var header = (stored, selectedCount > 1) switch
            {
                (true, true) => Loc.Get("Menu_MoveToDesktopCount", selectedCount),
                (true, false) => Loc.Get("Menu_MoveToDesktop"),
                (false, true) => Loc.Get("Menu_RemoveFromFenceCount", selectedCount),
                (false, false) => Loc.Get("Menu_RemoveFromFence"),
            };

            menu.Items.Add(MenuItemFor(
                header,
                () => RemovePaths(Vm.Items.Where(i => i.IsSelected).Select(i => i.Path).ToList())));

            // Odnosnik do pliku gdzies na dysku usuwa sie pozycja wyzej - kasowanie cudzego
            // pliku z fence'a byloby niespodzianka. Do Kosza idzie tylko to, co lezy w magazynie.
            if (stored)
            {
                menu.Items.Add(MenuItemFor(
                    selectedCount > 1 ? Loc.Get("Menu_DeleteItemCount", selectedCount) : Loc.Get("Menu_DeleteItem"),
                    DeleteSelected));
            }
        }

        OpenMenu(menu);
    }

    private static MenuItem MenuItemFor(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void SortByName()
    {
        var sorted = Model.Items
            .OrderBy(i => i.DisplayName ?? ShellService.GetDisplayName(i.Path), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Model.Items.Clear();
        Model.Items.AddRange(sorted);

        ReloadItems();
        _manager.RequestSave();
    }

    /// <summary>
    /// Prostuje sciezki pozycji, ktorych pliki zniknely z pierwotnego miejsca.
    /// <para>
    /// Typowy przypadek: skrot skasowany z pulpitu, ale wciaz obecny w menu Start. Szukamy
    /// pliku o tej samej nazwie w menu Start i na pulpicie, i przestawiamy pozycje na niego -
    /// zamiast kasowac ja razem z nazwa wlasna i miejscem w fence'ie.
    /// </para>
    /// </summary>
    private void RepairMissing()
    {
        var missing = Model.Items
            .Where(i => !ShellService.Exists(i.Path))
            .ToList();

        if (missing.Count == 0)
        {
            MessageBox.Show(
                Loc.Get("Msg_NothingToRepair"), "FencesVE",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var index = BuildShortcutIndex();
        var repaired = 0;

        foreach (var item in missing)
        {
            var name = Path.GetFileName(item.Path);

            if (!index.TryGetValue(name, out var found))
            {
                continue;
            }

            // Oryginal zostaje tam, gdzie jest (zwykle w menu Start) - do fence'a trafia kopia
            // w jego magazynie. Inaczej naprawa wyrwalaby skrot z menu Start.
            var stored = Settings.MoveItemsIntoFence
                ? _manager.Storage.CopyIn(found, Model.Id)
                : found;

            if (stored is null || _manager.IsTracked(stored))
            {
                continue;
            }

            item.Path = stored;
            repaired++;
        }

        if (repaired > 0)
        {
            _manager.Icons.Invalidate(string.Empty);
            ReloadItems();
            _manager.RequestSave();
        }

        MessageBox.Show(
            Loc.Get("Msg_RepairResult", repaired, missing.Count), "FencesVE",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Skroty z menu Start i pulpitu, po nazwie pliku. Pierwszy trafiony wygrywa.</summary>
    private static Dictionary<string, string> BuildShortcutIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var extension = Path.GetExtension(file);

                    if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    index.TryAdd(Path.GetFileName(file), file);
                }
            }
            catch
            {
                // Brak dostepu do jednego katalogu nie moze przerwac szukania w pozostalych.
            }
        }

        return index;
    }

    /// <summary>
    /// Usuwa zaznaczone pozycje: pliki z magazynu trafiaja do Kosza, a odnosniki do plikow
    /// lezacych gdzie indziej znikaja z samej listy - tamtych plikow nie kasujemy.
    /// </summary>
    private void DeleteSelected()
    {
        if (Model.PortalFolder is { Length: > 0 })
        {
            return;
        }

        var selected = Vm.Items.Where(i => i.IsSelected).Select(i => i.Path).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var stored = selected.Where(p => _manager.Storage.IsInStorage(p)).ToList();

        // Same odnosniki - nic nie idzie do Kosza, wiec nie ma o co pytac.
        if (stored.Count == 0)
        {
            RemovePaths(selected);
            return;
        }

        var answer = MessageBox.Show(
            selected.Count > 1
                ? Loc.Get("Msg_DeleteItemsConfirm", selected.Count)
                : Loc.Get("Msg_DeleteItemConfirm", Vm.Items.First(i => i.IsSelected).DisplayName),
            "FencesVE",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var path in stored)
        {
            _manager.Storage.MoveToRecycleBin(path);
        }

        // Z listy znika to, czego juz nie ma, plus odnosniki. Plik, ktory nie dal sie
        // usunac (zajety, brak uprawnien), zostaje w fence'ie tam, gdzie byl.
        var gone = new HashSet<string>(
            selected.Where(p => !stored.Contains(p, StringComparer.OrdinalIgnoreCase) || !ShellService.Exists(p)),
            StringComparer.OrdinalIgnoreCase);

        Model.Items.RemoveAll(i => gone.Contains(i.Path));

        foreach (var vm in Vm.Items.Where(i => gone.Contains(i.Path)).ToList())
        {
            Vm.Items.Remove(vm);
        }

        UpdateEmptyHint();
        _manager.RequestSave();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Edytor nazwy fence'a sam obsluguje Delete - kasuje wtedy znaki, nie ikony.
        if (e.Key != Key.Delete || e.OriginalSource is TextBox)
        {
            return;
        }

        DeleteSelected();
        e.Handled = true;
    }

    private void RemoveMissing()
    {
        var missing = Vm.Items.Where(i => !ShellService.Exists(i.Path)).Select(i => i.Path).ToList();
        RemovePaths(missing);
    }

    /// <summary>
    /// Odklada na pulpit wszystko, co ten fence wciagnal do magazynu. Droga powrotna dla
    /// calego fence'a - bez usuwania go i bez klikania pozycja po pozycji.
    /// </summary>
    private void EmptyToDesktop()
    {
        var stored = Model.Items.Where(i => _manager.Storage.IsInStorage(i.Path)).Select(i => i.Path).ToList();
        if (stored.Count == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            Loc.Get("Msg_EmptyToDesktop", Vm.Title, stored.Count),
            "FencesVE",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            RemovePaths(stored);
        }
    }

    private void RenameItem(FenceItemViewModel item)
    {
        var input = InputDialog.Ask(Loc.Get("Prompt_NewDisplayName"), Loc.Get("Menu_Rename"), item.DisplayName);
        if (input is null)
        {
            return;
        }

        var trimmed = input.Trim();
        item.DisplayName = string.IsNullOrEmpty(trimmed) ? ShellService.GetDisplayName(item.Path) : trimmed;
        item.Model.DisplayName = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        _manager.RequestSave();
    }

    private void PickFenceHeaderHeight()
    {
        var input = InputDialog.Ask(
            Loc.Get("Prompt_HeaderHeight"),
            Loc.Get("Prompt_HeaderHeightTitle"),
            EffectiveHeaderHeight.ToString("0"));

        if (input is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            Model.HeaderHeightOverride = 0;
        }
        else if (double.TryParse(input.Trim(), out var parsed))
        {
            Model.HeaderHeightOverride = Math.Clamp(parsed, MinHeaderHeight, MaxHeaderHeight);
        }
        else
        {
            MessageBox.Show(
                Loc.Get("Prompt_NumberRange", MinHeaderHeight.ToString("0"), MaxHeaderHeight.ToString("0")),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ApplySettings(Settings);
        _manager.RequestSave();
    }

    private void PickFenceColor()
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };

        var current = ThemeService.ParseColor(Model.AccentOverride ?? Settings.Accent, Colors.Black);
        dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        Model.AccentOverride = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        ApplySettings(Settings);
        _manager.RequestSave();
    }

    private void PickFenceTextColor()
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };

        var current = Model.TextColorOverride is { Length: > 0 }
            ? ThemeService.ParseColor(Model.TextColorOverride, Colors.White)
            : ThemeService.ResolveTextColor(Settings);

        dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        Model.TextColorOverride = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        ApplySettings(Settings);
        _manager.RequestSave();
    }

    /// <summary>Pobiera kolor z dowolnego piksela ekranu - np. wprost z tapety.</summary>
    private void PickWithEyedropper(bool isText)
    {
        var picked = ScreenColorPicker.Pick();
        if (picked is not { } color)
        {
            return;
        }

        var hex = ThemeService.ToHex(color);

        if (isText)
        {
            Model.TextColorOverride = hex;
        }
        else
        {
            Model.AccentOverride = hex;
        }

        ApplySettings(Settings);
        _manager.RequestSave();
    }

    private void PickFenceFontSize()
    {
        var current = Model.FontSizeOverride > 0 ? Model.FontSizeOverride : Settings.FontSize;

        var input = InputDialog.Ask(
            Loc.Get("Prompt_FontSize"),
            Loc.Get("Prompt_FontSizeTitle"),
            current.ToString("0"));

        if (input is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            Model.FontSizeOverride = 0;
        }
        else if (double.TryParse(input.Trim(), out var parsed))
        {
            Model.FontSizeOverride = Math.Clamp(parsed, 8, 24);
        }
        else
        {
            MessageBox.Show(Loc.Get("Prompt_NumberRange", 8, 24), "FencesVE", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ApplySettings(Settings);
        _manager.RequestSave();
    }

    private void DeleteSelf()
    {
        var stored = Model.Items.Count(i => _manager.Storage.IsInStorage(i.Path));

        var result = MessageBox.Show(
            Loc.Get("Msg_DeleteFenceConfirm", Vm.Title, stored),
            "FencesVE",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _manager.DeleteFence(Model.Id);
        }
    }
}
