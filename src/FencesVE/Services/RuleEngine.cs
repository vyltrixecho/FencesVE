using System.IO;
using System.Text.RegularExpressions;
using FencesVE.Models;

namespace FencesVE.Services;

/// <summary>Dopasowuje sciezki plikow do regul auto-sortowania zdefiniowanych na fence'ach.</summary>
public static class RuleEngine
{
    public static bool Matches(SortRule rule, string path)
    {
        if (!rule.Enabled)
        {
            return false;
        }

        switch (rule.Kind)
        {
            case RuleKind.Everything:
                return true;

            case RuleKind.Extension:
            {
                var wanted = rule.Pattern.TrimStart('.', '*');
                if (string.IsNullOrWhiteSpace(wanted))
                {
                    return false;
                }

                var actual = Path.GetExtension(path).TrimStart('.');
                return string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase);
            }

            case RuleKind.Category:
            {
                if (!Enum.TryParse<FileCategory>(rule.Pattern, ignoreCase: true, out var wanted))
                {
                    return false;
                }

                return ShellService.Categorize(path) == wanted;
            }

            case RuleKind.NameContains:
            {
                if (string.IsNullOrWhiteSpace(rule.Pattern))
                {
                    return false;
                }

                return Path.GetFileName(path).Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
            }

            case RuleKind.NameRegex:
            {
                if (string.IsNullOrWhiteSpace(rule.Pattern))
                {
                    return false;
                }

                try
                {
                    return Regex.IsMatch(
                        Path.GetFileName(path),
                        rule.Pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException)
                {
                    // Niepoprawny regex wpisany przez uzytkownika - traktujemy jak brak dopasowania.
                    return false;
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Wybiera pierwszy fence, ktorego reguly pasuja do pliku.
    /// Reguly <see cref="RuleKind.Everything"/> maja najnizszy priorytet, zeby fence typu
    /// "reszta" nie przechwycil plikow nalezacych do bardziej konkretnych regul.
    /// </summary>
    public static FenceModel? FindTarget(IEnumerable<FenceModel> fences, string path)
    {
        FenceModel? catchAll = null;

        foreach (var fence in fences)
        {
            // Fence w trybie portalu odzwierciedla folder i nie przyjmuje wlasnych pozycji -
            // wskazanie go jako celu konczylo sie cichym zgubieniem pliku.
            if (fence.PortalFolder is { Length: > 0 })
            {
                continue;
            }

            foreach (var rule in fence.Rules)
            {
                if (!Matches(rule, path))
                {
                    continue;
                }

                if (rule.Kind == RuleKind.Everything)
                {
                    catchAll ??= fence;
                }
                else
                {
                    return fence;
                }
            }
        }

        return catchAll;
    }
}
