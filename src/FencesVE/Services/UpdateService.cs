using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FencesVE.Services;

/// <summary>Opis wydania pobierany z kanalu aktualizacji.</summary>
public sealed class UpdateManifest
{
    /// <summary>Wersja w formacie "1.2.3" albo "1.2.3.4".</summary>
    public string Version { get; set; } = "";

    /// <summary>Bezposredni link do nowego FencesVE.exe.</summary>
    public string Url { get; set; } = "";

    /// <summary>Suma SHA-256 pliku spod <see cref="Url"/>, szesnastkowo. Wymagana.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Opis zmian pokazywany uzytkownikowi.</summary>
    public string? Notes { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UpdateManifest))]
public partial class UpdateJsonContext : JsonSerializerContext
{
}

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateManifest? Manifest, string? Error)
{
    public static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate, null, null);

    public static UpdateCheckResult Available(UpdateManifest manifest) =>
        new(UpdateCheckStatus.UpdateAvailable, manifest, null);

    public static UpdateCheckResult Failed(string error) => new(UpdateCheckStatus.Failed, null, error);
}

/// <summary>
/// Sprawdza kanal aktualizacji, pobiera nowy plik i podmienia dzialajacy .exe.
/// <para>
/// Bezpieczenstwo: kanal i plik musza isc po HTTPS (wyjatek: localhost, do testow),
/// a pobrany plik jest przyjmowany dopiero po zgodnosci sumy SHA-256 z manifestu.
/// Bez tego aktualizator bylby wygodna droga na wstrzykniecie dowolnego kodu.
/// </para>
/// </summary>
public sealed class UpdateService
{
    private const string BackupSuffix = ".old";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static string CurrentVersionText
    {
        get
        {
            var v = CurrentVersion;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"FencesVE/{CurrentVersionText}");
        return client;
    }

    /// <summary>Pobiera manifest i porownuje wersje z biezaca.</summary>
    public static async Task<UpdateCheckResult> CheckAsync(string? feedUrl, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return UpdateCheckResult.Failed(Loc.Get("Update_NoFeed"));
        }

        if (!TryValidateUrl(feedUrl, out var feed, out var urlError))
        {
            return UpdateCheckResult.Failed(urlError);
        }

        try
        {
            var json = await Http.GetStringAsync(feed, token).ConfigureAwait(false);

            var manifest = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.UpdateManifest);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                return UpdateCheckResult.Failed(Loc.Get("Update_ErrNoVersion"));
            }

            if (!Version.TryParse(manifest.Version, out var remote))
            {
                return UpdateCheckResult.Failed(Loc.Get("Update_ErrBadVersion", manifest.Version));
            }

            if (remote <= CurrentVersion)
            {
                return UpdateCheckResult.UpToDate();
            }

            if (string.IsNullOrWhiteSpace(manifest.Url))
            {
                return UpdateCheckResult.Failed(Loc.Get("Update_ErrNoUrl"));
            }

            if (!TryValidateUrl(manifest.Url, out _, out var downloadError))
            {
                return UpdateCheckResult.Failed(downloadError);
            }

            if (string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                return UpdateCheckResult.Failed(
                    Loc.Get("Update_NoSha"));
            }

            return UpdateCheckResult.Available(manifest);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>Pobiera plik do katalogu tymczasowego i sprawdza jego sume kontrolna.</summary>
    public static async Task<string> DownloadAsync(UpdateManifest manifest, CancellationToken token = default)
    {
        var target = Path.Combine(Path.GetTempPath(), $"FencesVE-{manifest.Version}-{Guid.NewGuid():N}.exe");

        await using (var response = await Http.GetStreamAsync(manifest.Url, token).ConfigureAwait(false))
        await using (var file = File.Create(target))
        {
            await response.CopyToAsync(file, token).ConfigureAwait(false);
        }

        var actual = await ComputeSha256Async(target, token).ConfigureAwait(false);
        var expected = manifest.Sha256.Replace("-", "").Trim();

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(target);
            throw new InvalidOperationException(Loc.Get("Update_ErrChecksum", expected, actual));
        }

        return target;
    }

    /// <summary>
    /// Podmienia dzialajacy plik .exe i uruchamia nowa wersje.
    /// Windows pozwala zmienic nazwe uruchomionego pliku (skasowac juz nie),
    /// wiec stara wersja idzie na bok jako .old i znika przy nastepnym starcie.
    /// </summary>
    public static void ApplyAndRestart(string downloadedPath)
    {
        var current = Environment.ProcessPath
                      ?? throw new InvalidOperationException(Loc.Get("Update_ErrNoExePath"));

        var backup = current + BackupSuffix;
        TryDelete(backup);

        File.Move(current, backup);

        try
        {
            File.Move(downloadedPath, current);
        }
        catch
        {
            // Nie zostawiamy uzytkownika bez pliku wykonywalnego.
            File.Move(backup, current);
            throw;
        }

        // Nowa instancja czeka, az ta zwolni muteks jednej instancji - inaczej
        // zobaczylaby "FencesVE juz dziala" i od razu by sie zamknela.
        Process.Start(new ProcessStartInfo
        {
            FileName = current,
            Arguments = $"--updated {Environment.ProcessId}",
            UseShellExecute = true,
        });
    }

    /// <summary>Sprzata plik poprzedniej wersji. Wywolywane przy starcie.</summary>
    public static void CleanupBackup()
    {
        try
        {
            var current = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(current))
            {
                TryDelete(current + BackupSuffix);
            }
        }
        catch
        {
            // Zostawiony plik .old nikomu nie przeszkadza.
        }
    }

    private static bool TryValidateUrl(string value, out Uri uri, out string error)
    {
        uri = null!;
        error = "";

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            error = Loc.Get("Update_BadUrl", value);
            return false;
        }

        var isLocal = parsed.IsLoopback;

        if (parsed.Scheme != Uri.UriSchemeHttps && !(isLocal && parsed.Scheme == Uri.UriSchemeHttp))
        {
            error = Loc.Get("Update_HttpsRequired");
            return false;
        }

        uri = parsed;
        return true;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Plik moze byc zablokowany - sprobujemy nastepnym razem.
        }
    }
}
