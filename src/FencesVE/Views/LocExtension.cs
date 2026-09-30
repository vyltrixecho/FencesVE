using System.Windows.Markup;
using FencesVE.Services;

namespace FencesVE.Views;

/// <summary>
/// Napis z zasobow prosto w XAML: <c>Text="{v:Loc Settings_Theme}"</c>.
/// <para>
/// Rozwiazuje sie przy wczytywaniu okna, a nie na biezaco - zmiana jezyka w ustawieniach
/// przeladowuje okno, wiec dynamiczne wiazanie nic by tu nie dalo, a kosztowalo obiekt
/// nasluchu przy kazdym napisie.
/// </para>
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
