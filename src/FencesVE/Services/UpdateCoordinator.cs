using System.IO;
using System.Windows;

namespace FencesVE.Services;

/// <summary>
/// Spina <see cref="UpdateService"/> z interfejsem: pyta uzytkownika, pobiera i instaluje.
/// Samo podmienienie pliku i restart zostaje po stronie <see cref="ApplyHandler"/>,
/// bo wymaga zwolnienia muteksu jednej instancji - a ten trzyma klasa App.
/// </summary>
public sealed class UpdateCoordinator
{
    /// <summary>Ustawiane przez App: podmienia .exe, uruchamia nowa wersje i zamyka biezaca.</summary>
    public Action<string>? ApplyHandler { get; set; }

    /// <summary>
    /// Sprawdza kanal i - gdy jest nowsza wersja - proponuje instalacje.
    /// Zwraca krotki komunikat do pokazania w ustawieniach.
    /// </summary>
    public async Task<string> CheckAndOfferAsync(string? feedUrl, Window? owner, bool quietWhenUpToDate = false)
    {
        var result = await UpdateService.CheckAsync(feedUrl).ConfigureAwait(true);

        switch (result.Status)
        {
            case UpdateCheckStatus.UpToDate:
                return Loc.Get("Update_UpToDate", UpdateService.CurrentVersionText);

            case UpdateCheckStatus.Failed:
                if (!quietWhenUpToDate)
                {
                    return Loc.Get("Update_CheckFailed", result.Error);
                }

                return result.Error ?? Loc.Get("Update_Error");

            case UpdateCheckStatus.UpdateAvailable:
                return await OfferAsync(result.Manifest!, owner).ConfigureAwait(true);

            default:
                return "";
        }
    }

    private async Task<string> OfferAsync(UpdateManifest manifest, Window? owner)
    {
        var notes = string.IsNullOrWhiteSpace(manifest.Notes) ? "" : Loc.Get("Update_Notes", manifest.Notes);

        var question = Loc.Get("Update_Question", manifest.Version, UpdateService.CurrentVersionText, notes);

        var answer = owner is not null
            ? MessageBox.Show(owner, question, "FencesVE", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(question, "FencesVE", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return Loc.Get("Update_Postponed", manifest.Version);
        }

        try
        {
            var downloaded = await UpdateService.DownloadAsync(manifest).ConfigureAwait(true);

            if (ApplyHandler is null)
            {
                File.Delete(downloaded);
                return Loc.Get("Update_NoInstaller");
            }

            ApplyHandler(downloaded);
            return Loc.Get("Update_Installing");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.Get("Update_Failed", ex.Message),
                "FencesVE",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return Loc.Get("Update_FailedShort");
        }
    }

    /// <summary>
    /// Pelny przebieg bez zadnych okien - do uruchomienia z harmonogramu (parametr --update).
    /// Zwraca opis tego, co sie stalo.
    /// </summary>
    public async Task<string> RunSilentAsync(string? feedUrl)
    {
        var result = await UpdateService.CheckAsync(feedUrl).ConfigureAwait(true);

        switch (result.Status)
        {
            case UpdateCheckStatus.UpToDate:
                return $"UP-TO-DATE {UpdateService.CurrentVersionText}";

            case UpdateCheckStatus.Failed:
                return $"FAILED {result.Error}";
        }

        var manifest = result.Manifest!;

        try
        {
            var downloaded = await UpdateService.DownloadAsync(manifest).ConfigureAwait(true);

            if (ApplyHandler is null)
            {
                File.Delete(downloaded);
                return "FAILED brak obslugi instalacji";
            }

            ApplyHandler(downloaded);
            return $"UPDATED {UpdateService.CurrentVersionText} -> {manifest.Version}";
        }
        catch (Exception ex)
        {
            return $"FAILED {ex.Message}";
        }
    }
}
