using System.Runtime.InteropServices;
using System.Text;
using FencesVE.Interop;

namespace FencesVE.Services;

/// <summary>
/// Operacje na pulpicie Windows: znalezienie listy ikon i jej ukrycie/pokazanie.
/// Nic tu nie jest trwale - ukrycie to zwykle ShowWindow(SW_HIDE), wiec restart
/// Explorera albo zamkniecie aplikacji przywraca stan wyjsciowy.
/// </summary>
public sealed class DesktopService
{
    private static IntPtr _cachedAnchor = NativeMethods.HWND_BOTTOM;
    private static long _cachedAnchorAt;

    /// <summary>Klasy okien, z ktorych sklada sie pulpit.</summary>
    private static readonly string[] DesktopClasses =
        ["SysListView32", "SHELLDLL_DefView", "WorkerW", "Progman"];

    /// <summary>Czy pod podanym punktem ekranu jest pulpit, a nie jakiekolwiek okno na nim.</summary>
    internal static bool IsDesktopAt(NativeMethods.POINT point)
    {
        var hwnd = NativeMethods.WindowFromPoint(point);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        return IsDesktopClass(hwnd) || IsDesktopClass(NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT));
    }

    /// <summary>Czy uchwyt nalezy do jednego z okien pulpitu.</summary>
    private static bool IsDesktopClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var buffer = new StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        var name = buffer.ToString();

        return DesktopClasses.Any(c => string.Equals(c, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Zwraca uchwyt, po ktorym nalezy wstawic fence, zeby wyladowal dokladnie nad oknem pulpitu.
    /// <para>
    /// HWND_BOTTOM sie tu nie nadaje: wpycha okno *pod* Progmana, a ten maluje tapete.
    /// W normalnym stanie tego nie widac (Progman i tak lezy na samym dole), ale po "Pokaz pulpit"
    /// powloka podnosi Progmana i fence'y znikaja. Dlatego szukamy okna lezacego bezposrednio
    /// nad pulpitem i wstawiamy sie pod nie - czyli tuz nad tapeta, a wciaz pod wszystkimi aplikacjami.
    /// </para>
    /// </summary>
    public static IntPtr GetDesktopAnchor()
    {
        // EnumWindows przy kazdej zmianie pozycji byloby drogie podczas przeciagania okna.
        var now = Environment.TickCount64;
        if (now - _cachedAnchorAt < 500)
        {
            return _cachedAnchor;
        }

        var ownPid = (uint)Environment.ProcessId;
        var previous = IntPtr.Zero;
        var anchor = NativeMethods.HWND_BOTTOM;
        var found = false;
        var buffer = new StringBuilder(64);

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            buffer.Clear();
            NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);

            if (string.Equals(buffer.ToString(), "Progman", StringComparison.Ordinal))
            {
                // Nic nad pulpitem - idziemy na sam gorny koniec, zeby byc nad tapeta.
                anchor = previous == IntPtr.Zero ? NativeMethods.HWND_TOP : previous;
                found = true;
                return false;
            }

            // Wlasne fence'y pomijamy, inaczej ustawialyby sie wzgledem siebie nawzajem.
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != ownPid)
            {
                previous = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        _cachedAnchor = found ? anchor : NativeMethods.HWND_BOTTOM;
        _cachedAnchorAt = now;

        return _cachedAnchor;
    }

    /// <summary>
    /// Znajduje uchwyt SysListView32 z ikonami pulpitu.
    /// Zaleznie od konfiguracji (np. wlaczonej pokazywanej tapety slideshow) lista siedzi
    /// albo pod Progman, albo pod jednym z okien WorkerW.
    /// </summary>
    public static IntPtr FindDesktopListView()
    {
        var progman = NativeMethods.FindWindowW("Progman", null);
        var defView = NativeMethods.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

        if (defView == IntPtr.Zero)
        {
            defView = FindDefViewUnderWorkerW();
        }

        if (defView == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return NativeMethods.FindWindowExW(defView, IntPtr.Zero, "SysListView32", "FolderView");
    }

    private static IntPtr FindDefViewUnderWorkerW()
    {
        var found = IntPtr.Zero;
        var buffer = new StringBuilder(256);

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            buffer.Clear();
            NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);

            if (!string.Equals(buffer.ToString(), "WorkerW", StringComparison.Ordinal))
            {
                return true; // szukaj dalej
            }

            var child = NativeMethods.FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child != IntPtr.Zero)
            {
                found = child;
                return false; // znalezione, przerwij enumeracje
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>Czy natywne ikony pulpitu sa teraz widoczne.</summary>
    public static bool AreDesktopIconsVisible()
    {
        var list = FindDesktopListView();
        return list != IntPtr.Zero && NativeMethods.IsWindowVisible(list);
    }

    /// <summary>Ukrywa lub pokazuje natywne ikony pulpitu. Zwraca true, gdy sie udalo.</summary>
    public static bool SetDesktopIconsVisible(bool visible)
    {
        var list = FindDesktopListView();
        if (list == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.ShowWindow(list, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        return true;
    }

    /// <summary>Zwraca sciezki do obu folderow pulpitu: uzytkownika i wspolnego (Public).</summary>
    public static IReadOnlyList<string> GetDesktopFolders()
    {
        var folders = new List<string>();

        var user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(user))
        {
            folders.Add(user);
        }

        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(common) && !folders.Contains(common, StringComparer.OrdinalIgnoreCase))
        {
            folders.Add(common);
        }

        return folders;
    }
}
