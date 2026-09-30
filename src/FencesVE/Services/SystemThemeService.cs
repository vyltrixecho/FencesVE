using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using FencesVE.Interop;

namespace FencesVE.Services;

/// <summary>
/// Motyw aplikacji Windows (Ustawienia -> Personalizacja -> Kolory -> "Tryb aplikacji").
/// Okna narzedziowe aplikacji ida za systemem, a nie za ustawieniem motywu fence'ow -
/// to sa dwie rozne rzeczy: tamto maluje kontenery na pulpicie, to jest chrom okien.
/// </summary>
public static class SystemThemeService
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);

            // Brak wartosci = domyslny jasny motyw (tak jest na swiezej instalacji).
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wciaga okno w ciemny motyw: slownik stylow na zawartosc i ciemna belke tytulu z DWM.
    /// Belka musi byc ustawiona po utworzeniu uchwytu, wiec wchodzimy na SourceInitialized.
    /// </summary>
    /// <summary>
    /// Scala slownik ciemnego motywu w zasoby aplikacji. Musi byc na tym poziomie,
    /// a nie w pojedynczym oknie: menu kontekstowe fence'ow i podpowiedzi mieszkaja
    /// we wlasnych oknach (Popup), wiec stylu z zasobow jednego okna by nie zobaczyly.
    /// </summary>
    public static void ApplyToApplication()
    {
        if (!IsDarkMode() || Application.Current is not { } app)
        {
            return;
        }

        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Views/DarkTheme.xaml", UriKind.Absolute),
        });
    }

    public static void ApplyIfDark(Window window)
    {
        if (!IsDarkMode())
        {
            return;
        }

        // Styl okna jest keyed, wiec nie zlapie sie sam - slownik siedzi juz w zasobach aplikacji.
        window.SetResourceReference(FrameworkElement.StyleProperty, "DarkWindowStyle");

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyDarkTitleBar(window);
        }
        else
        {
            window.SourceInitialized += (_, _) => ApplyDarkTitleBar(window);
        }
    }

    private static void ApplyDarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;

        // Atrybut dostal numer 20 dopiero w Windows 10 1903; wczesniejsze kompilacje
        // uzywaly 19. Starszy system zwroci blad na obu i po prostu zostanie przy jasnej belce.
        if (NativeMethods.DwmSetWindowAttribute(
                handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
        {
            NativeMethods.DwmSetWindowAttribute(
                handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref enabled, sizeof(int));
        }
    }
}
