using FencesVE.Interop;
using FencesVE.Models;

namespace FencesVE.Services;

/// <summary>Jeden monitor w pikselach fizycznych.</summary>
internal readonly record struct MonitorInfo(
    IntPtr Handle,
    NativeMethods.RECT Bounds,
    NativeMethods.RECT Work,
    bool Primary,
    uint Dpi)
{
    public int WorkWidth => Work.Right - Work.Left;
    public int WorkHeight => Work.Bottom - Work.Top;

    public bool SameBounds(FencePlacement p) =>
        Bounds.Left == p.MonitorLeft && Bounds.Top == p.MonitorTop &&
        Bounds.Right - Bounds.Left == p.MonitorWidth && Bounds.Bottom - Bounds.Top == p.MonitorHeight;
}

/// <summary>
/// Uklad monitorow i przenoszenie polozenia fence'a miedzy ukladami.
/// Monitory czytamy wprost z Win32, a nie z WinForms Screen.AllScreens -
/// tamta lista bywa zapamietana i tuz po zmianie monitorow pokazuje jeszcze stary uklad.
/// </summary>
internal static class DisplayService
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            if (Describe(handle) is { } monitor)
            {
                monitors.Add(monitor);
            }

            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    public static MonitorInfo? Describe(IntPtr handle)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };

        if (!NativeMethods.GetMonitorInfoW(handle, ref info))
        {
            return null;
        }

        var dpi = NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
            ? dpiX
            : 96u;

        return new MonitorInfo(
            handle, info.rcMonitor, info.rcWork, (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0, dpi);
    }

    /// <summary>
    /// Podpis ukladu: granice wszystkich monitorow plus znacznik glownego. Obszar roboczy
    /// celowo pomijamy - przesuniecie paska zadan to nie jest inny uklad monitorow.
    /// </summary>
    public static string Signature(IEnumerable<MonitorInfo> monitors) =>
        string.Join("|", monitors
            .OrderBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .Select(m => $"{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Right - m.Bounds.Left}x{m.Bounds.Bottom - m.Bounds.Top}"
                         + (m.Primary ? "*" : "")));

    /// <summary>
    /// Przenosi polozenie z innego ukladu monitorow na biezacy. Jesli tamten monitor
    /// nadal jest podpiety, polozenie zostaje bez zmian. Inaczej fence trafia na monitor
    /// glowny, a jego pozycja skaluje sie z wolnym miejscem na ekranie - fence przy prawej
    /// krawedzi zostaje przy prawej, fence na srodku na srodku, niezaleznie od rozdzielczosci.
    /// </summary>
    public static FencePlacement Map(FencePlacement source, IReadOnlyList<MonitorInfo> monitors)
    {
        var target = monitors.FirstOrDefault(m => m.SameBounds(source));
        var sameMonitor = target.Handle != IntPtr.Zero;

        if (!sameMonitor)
        {
            target = monitors.FirstOrDefault(m => m.Primary);

            if (target.Handle == IntPtr.Zero)
            {
                target = monitors[0];
            }
        }

        var scale = source.Dpi > 0 ? (double)target.Dpi / source.Dpi : 1;

        // Fence nie moze byc wiekszy niz obszar roboczy nowego ekranu (jednostki WPF).
        var maxWidth = target.WorkWidth * 96.0 / target.Dpi;
        var maxHeight = target.WorkHeight * 96.0 / target.Dpi;

        var pixelWidth = Math.Min((int)Math.Round(source.PixelWidth * scale), target.WorkWidth);
        var pixelHeight = Math.Min((int)Math.Round(source.PixelHeight * scale), target.WorkHeight);

        int left, top;

        if (sameMonitor)
        {
            left = source.Left;
            top = source.Top;
        }
        else
        {
            left = target.Work.Left + (int)Math.Round(
                Fraction(source.Left - source.AreaLeft, source.AreaWidth - source.PixelWidth)
                * Math.Max(0, target.WorkWidth - pixelWidth));

            top = target.Work.Top + (int)Math.Round(
                Fraction(source.Top - source.AreaTop, source.AreaHeight - source.PixelHeight)
                * Math.Max(0, target.WorkHeight - pixelHeight));
        }

        return new FencePlacement
        {
            Left = left,
            Top = top,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            MonitorLeft = target.Bounds.Left,
            MonitorTop = target.Bounds.Top,
            MonitorWidth = target.Bounds.Right - target.Bounds.Left,
            MonitorHeight = target.Bounds.Bottom - target.Bounds.Top,
            AreaLeft = target.Work.Left,
            AreaTop = target.Work.Top,
            AreaWidth = target.WorkWidth,
            AreaHeight = target.WorkHeight,
            Dpi = target.Dpi,
            Width = Math.Min(source.Width, maxWidth),
            Height = Math.Min(source.Height, maxHeight),
            RestoreWidth = Math.Min(source.RestoreWidth, maxWidth),
            RestoreHeight = Math.Min(source.RestoreHeight, maxHeight),
            Auto = true,
        };
    }

    /// <summary>Gdzie w wolnym miejscu stal fence: 0 = przy lewej/gornej krawedzi, 1 = przy prawej/dolnej.</summary>
    private static double Fraction(int offset, int free) =>
        free > 0 ? Math.Clamp((double)offset / free, 0, 1) : 0;

    /// <summary>Czy dwa polozenia to w praktyce to samo - uzytkownik niczego nie przestawil.</summary>
    public static bool SamePosition(FencePlacement a, FencePlacement b) =>
        Math.Abs(a.Left - b.Left) <= 2 && Math.Abs(a.Top - b.Top) <= 2 &&
        Math.Abs(a.PixelWidth - b.PixelWidth) <= 2 && Math.Abs(a.PixelHeight - b.PixelHeight) <= 2 &&
        Math.Abs(a.Width - b.Width) < 1 && Math.Abs(a.Height - b.Height) < 1;
}
