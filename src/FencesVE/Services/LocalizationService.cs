using System.Globalization;
using System.Resources;
using FencesVE.Models;

namespace FencesVE.Services;

/// <summary>
/// Napisy interfejsu. Polski jest jezykiem neutralnym (siedzi wprost w zestawie),
/// angielski dochodzi jako zestaw satelicki - stad w csproj SatelliteResourceLanguages=en.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Manager =
        new("FencesVE.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>Napis spod klucza. Brakujacy klucz zwraca sam klucz - widac go wtedy w UI zamiast pustki.</summary>
    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Napis z podstawieniami, jak <c>string.Format</c>.</summary>
    public static string Get(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}

/// <summary>Wybor jezyka interfejsu i ustawienie go dla calej aplikacji.</summary>
public static class LocalizationService
{
    /// <summary>Jezyki, ktore aplikacja ma przetlumaczone.</summary>
    private static readonly string[] Supported = ["pl", "en"];

    /// <summary>
    /// Ustawia jezyk interfejsu. Dla <see cref="AppLanguage.System"/> bierze jezyk Windows,
    /// a gdy nie jest obslugiwany - angielski, bo to jezyk szerzej zrozumialy niz polski.
    /// </summary>
    public static void Apply(AppLanguage language)
    {
        var culture = Resolve(language);

        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static CultureInfo Resolve(AppLanguage language)
    {
        if (language == AppLanguage.Polish)
        {
            return CultureInfo.GetCultureInfo("pl");
        }

        if (language == AppLanguage.English)
        {
            return CultureInfo.GetCultureInfo("en");
        }

        var system = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        return CultureInfo.GetCultureInfo(Supported.Contains(system) ? system : "en");
    }
}
