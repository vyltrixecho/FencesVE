using System.Windows;
using System.Windows.Media;
using FencesVE.Models;

namespace FencesVE.Services;

/// <summary>
/// Wylicza pedzle motywu z ustawien i wsadza je do slownika zasobow aplikacji.
/// Okna fence'ow siegaja po nie przez DynamicResource, wiec zmiana ustawien
/// odswieza wszystkie fence'y bez ich przeladowywania.
/// </summary>
public static class ThemeService
{
    public const string BackgroundKey = "FenceBackgroundBrush";
    public const string HeaderKey = "FenceHeaderBrush";
    public const string TextKey = "FenceTextBrush";
    public const string HoverKey = "FenceHoverBrush";
    public const string SelectionKey = "FenceSelectionBrush";
    public const string BorderKey = "FenceBorderBrush";

    public static void Apply(AppSettings settings)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var accent = ParseColor(settings.Accent, Color.FromRgb(0x10, 0x14, 0x18));
        var dark = settings.Theme == AppTheme.Dark;

        resources[BackgroundKey] = BuildBackgroundBrush(accent, settings.Opacity);
        resources[HeaderKey] = BuildHeaderBrush(accent, settings.Opacity, dark);

        resources[TextKey] = Freeze(new SolidColorBrush(ResolveTextColor(settings)));

        var overlay = dark ? Colors.White : Colors.Black;
        resources[HoverKey] = Freeze(new SolidColorBrush(WithAlpha(overlay, dark ? 0.12 : 0.08)));
        resources[SelectionKey] = Freeze(new SolidColorBrush(WithAlpha(overlay, dark ? 0.24 : 0.16)));
        resources[BorderKey] = Freeze(new SolidColorBrush(WithAlpha(overlay, dark ? 0.16 : 0.14)));
    }

    /// <summary>Pedzel tla fence'a dla podanego koloru bazowego.</summary>
    public static SolidColorBrush BuildBackgroundBrush(Color accent, double opacity) =>
        Freeze(new SolidColorBrush(WithAlpha(accent, Math.Clamp(opacity, 0.05, 1.0))));

    /// <summary>
    /// Pedzel belki tytulu: ten sam kolor bazowy, lekko przyciemniony (albo rozjasniony
    /// w motywie jasnym) i bardziej kryjacy, zeby tytul byl czytelny na kazdej tapecie.
    /// </summary>
    public static SolidColorBrush BuildHeaderBrush(Color accent, double opacity, bool dark)
    {
        var headerOpacity = Math.Clamp(Math.Clamp(opacity, 0.05, 1.0) + 0.18, 0.15, 1.0);
        return Freeze(new SolidColorBrush(WithAlpha(Shade(accent, dark ? -0.15 : 0.10), headerOpacity)));
    }

    /// <summary>Kolor czcionki wybrany przez uzytkownika, a gdy go nie ma - domyslny dla motywu.</summary>
    public static Color ResolveTextColor(AppSettings settings) =>
        ParseColor(settings.TextColor, DefaultTextColor(settings.Theme));

    public static Color DefaultTextColor(AppTheme theme) =>
        theme == AppTheme.Dark ? Color.FromRgb(0xF2, 0xF4, 0xF7) : Color.FromRgb(0x14, 0x17, 0x1A);

    public static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            var converted = ColorConverter.ConvertFromString(value);
            return converted is Color color ? color : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    /// <summary>Przyciemnia (ujemny amount) lub rozjasnia (dodatni) kolor.</summary>
    private static Color Shade(Color color, double amount)
    {
        static byte Mix(byte channel, double amount)
        {
            var target = amount < 0 ? 0.0 : 255.0;
            var factor = Math.Abs(amount);
            return (byte)Math.Round(channel + (target - channel) * factor);
        }

        return Color.FromRgb(Mix(color.R, amount), Mix(color.G, amount), Mix(color.B, amount));
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
