using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using FencesVE.Models;
using FencesVE.Services;

namespace FencesVE.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Pojedyncza ikona w fence'ie.</summary>
public sealed class FenceItemViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _isSelected;
    private bool _isMissing;
    private string _displayName = "";

    public FenceItemViewModel(FenceItem model)
    {
        Model = model;
        _displayName = model.DisplayName ?? ShellService.GetDisplayName(model.Path);
        _isMissing = !ShellService.Exists(model.Path);
    }

    public FenceItem Model { get; }

    public string Path => Model.Path;

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public ImageSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Plik zniknal z dysku - pokazujemy pozycje przygaszona.</summary>
    public bool IsMissing
    {
        get => _isMissing;
        set => Set(ref _isMissing, value);
    }

    public void RefreshExistence() => IsMissing = !ShellService.Exists(Path);

    /// <summary>Plik zmienil miejsce (wciagniecie do magazynu) - sciezka jest inna, pozycja ta sama.</summary>
    public void PathChanged() => Raise(nameof(Path));

    // Po ToString() czytniki ekranu ogloszaja pozycje listy - bez tego kazda ikona
    // przedstawiala sie jako "FencesVE.ViewModels.FenceItemViewModel".
    public override string ToString() => DisplayName;
}

/// <summary>Stan jednego fence'a widziany przez warstwe UI.</summary>
public sealed class FenceViewModel : ObservableObject
{
    private int _iconSize = 48;
    private bool _showLabels = true;
    private string _title = "";
    private Brush _textBrush = Brushes.White;
    private double _labelFontSize = 11;

    public FenceViewModel(FenceModel model)
    {
        Model = model;
        _title = model.Title;
    }

    public FenceModel Model { get; }

    public ObservableCollection<FenceItemViewModel> Items { get; } = new();

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, value))
            {
                Model.Title = value;
            }
        }
    }

    public int IconSize
    {
        get => _iconSize;
        set
        {
            if (Set(ref _iconSize, value))
            {
                Raise(nameof(CellWidth));
                Raise(nameof(CellHeight));
            }
        }
    }

    public bool ShowLabels
    {
        get => _showLabels;
        set
        {
            if (Set(ref _showLabels, value))
            {
                Raise(nameof(CellWidth));
                Raise(nameof(CellHeight));
                Raise(nameof(LabelVisibility));
            }
        }
    }

    public Visibility LabelVisibility => ShowLabels ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Kolor tekstu etykiet i tytulu - globalny albo nadpisany na tym fence'ie.</summary>
    public Brush TextBrush
    {
        get => _textBrush;
        set => Set(ref _textBrush, value);
    }

    public double LabelFontSize
    {
        get => _labelFontSize;
        set
        {
            if (Set(ref _labelFontSize, value))
            {
                Raise(nameof(TitleFontSize));
                Raise(nameof(LabelMaxHeight));
                Raise(nameof(CellHeight));
            }
        }
    }

    /// <summary>Tytul jest odrobine wiekszy od etykiet, zeby belka byla czytelna.</summary>
    public double TitleFontSize => LabelFontSize + 1;

    /// <summary>
    /// Szerokosc kafelka. Zapas 36 px jest po to, zeby zmiescila sie etykieta szersza od ikony -
    /// bez etykiet to czysta strata miejsca, a przy waskim fence'ie z belka z boku strata
    /// wiekszosci szerokosci.
    /// </summary>
    public double CellWidth => IconSize + (ShowLabels ? 36 : 12);

    public double CellHeight => IconSize + (ShowLabels ? LabelMaxHeight + 12 : 12);

    /// <summary>Miejsce na dwie linie etykiety przy biezacym rozmiarze czcionki.</summary>
    public double LabelMaxHeight => Math.Round(LabelFontSize * 2.55);
}
