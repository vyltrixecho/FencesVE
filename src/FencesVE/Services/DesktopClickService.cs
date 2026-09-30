using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using FencesVE.Interop;

namespace FencesVE.Services;

/// <summary>
/// Wylapuje dwuklik w pusty pulpit.
/// <para>
/// Powloka nie wysyla takiego zdarzenia nikomu z zewnatrz, wiec jedyna droga jest
/// globalny podglad myszy (WH_MOUSE_LL). Sam dwuklik skladamy z dwoch klikniec tak
/// samo, jak robi to Windows: po czasie z <c>GetDoubleClickTime</c> i po odleglosci
/// z <c>SM_CXDOUBLECLK</c>, zeby uszanowac ustawienia uzytkownika.
/// </para>
/// </summary>
public sealed class DesktopClickService : IDisposable
{
    /// <summary>
    /// Delegat musi zyc tak dlugo, jak podglad - inaczej zbierze go GC, a Windows
    /// zawola nieistniejacy juz kod. Stad pole, a nie lambda w miejscu instalacji.
    /// </summary>
    private readonly NativeMethods.HookProc _callback;

    private readonly Dispatcher _dispatcher;

    private IntPtr _hook;
    private uint _lastClickTime;
    private NativeMethods.POINT _lastClickPoint;

    /// <summary>Lista ikon pulpitu - szukanie jej przy kazdym kliknieciu byloby zbyt drogie.</summary>
    private IntPtr _desktopList;

    public DesktopClickService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = OnMouseEvent;
    }

    /// <summary>Dwuklik w pusty pulpit. Wolane na watku UI.</summary>
    public event Action? DesktopDoubleClicked;

    public bool IsEnabled => _hook != IntPtr.Zero;

    public void SetEnabled(bool enabled)
    {
        if (enabled == IsEnabled)
        {
            return;
        }

        if (enabled)
        {
            // hmod = 0 i dwThreadId = 0: podglad globalny z tego procesu.
            _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _callback, IntPtr.Zero, 0);
        }
        else
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Dispose() => SetEnabled(false);

    private IntPtr OnMouseEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        // Kazda milisekunda tutaj opoznia mysz calego systemu, wiec robimy absolutne minimum
        // i nie dotykamy niczego, co moze sie zablokowac.
        if (code >= 0 && wParam == NativeMethods.WM_LBUTTONDOWN)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

            if (IsSecondClickOfPair(data) && IsEmptyDesktopAt(data.pt) && !IsIconSelected())
            {
                // Zerujemy, zeby trzecie klikniecie nie odpalilo tego drugi raz.
                _lastClickTime = 0;
                _dispatcher.BeginInvoke(DispatcherPriority.Input, () => DesktopDoubleClicked?.Invoke());
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool IsSecondClickOfPair(NativeMethods.MSLLHOOKSTRUCT data)
    {
        var previousTime = _lastClickTime;
        var previousPoint = _lastClickPoint;

        _lastClickTime = data.time;
        _lastClickPoint = data.pt;

        if (previousTime == 0 || data.time - previousTime > NativeMethods.GetDoubleClickTime())
        {
            return false;
        }

        return Math.Abs(data.pt.X - previousPoint.X) <= NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK)
            && Math.Abs(data.pt.Y - previousPoint.Y) <= NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK);
    }

    /// <summary>
    /// Czy pod kursorem jest goly pulpit. Fence lezy na poziomie pulpitu, ale jest zwyklym
    /// oknem, wiec klikniecie w niego trafi w jego klase, a nie w klasy powloki - i slusznie
    /// nie uruchomi skrotu.
    /// </summary>
    private static bool IsEmptyDesktopAt(NativeMethods.POINT point) => DesktopService.IsDesktopAt(point);

    /// <summary>
    /// Czy dwuklik trafil w ikone, a nie w wolne miejsce.
    /// <para>
    /// <c>WindowFromPoint</c> tego nie rozroznia: cala lista ikon to jedno okno, wiec nad ikona
    /// i nad pustym miejscem zwraca ten sam uchwyt - i dwuklik w skrot chowal fence'y przy okazji
    /// jego uruchamiania. Pierwsze klikniecie pary zdazylo juz jednak zmienic zaznaczenie:
    /// klikniecie w ikone ja zaznacza, klikniecie w wolne miejsce czysci cala liste. Stad
    /// pytanie o licznik zaznaczonych.
    /// </para>
    /// <para>
    /// Pytamy z limitem czasu. Zwykly <c>SendMessage</c> z podgladu myszy zawiesilby kursor
    /// calego systemu na tak dlugo, jak dlugo Explorer bylby zajety. Brak odpowiedzi liczymy
    /// jako "ikona" - lepiej nie zrobic nic, niz schowac fence'y wbrew uzytkownikowi.
    /// </para>
    /// </summary>
    private bool IsIconSelected()
    {
        if (_desktopList == IntPtr.Zero || !NativeMethods.IsWindow(_desktopList))
        {
            _desktopList = DesktopService.FindDesktopListView();
        }

        if (_desktopList == IntPtr.Zero)
        {
            return false; // Bez listy ikon nie ma w co trafic - pulpit jest pusty.
        }

        var answered = NativeMethods.SendMessageTimeoutW(
            _desktopList,
            NativeMethods.LVM_GETSELECTEDCOUNT,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG,
            40,
            out var selected);

        return answered == IntPtr.Zero || selected != IntPtr.Zero;
    }

}
