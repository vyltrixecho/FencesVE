using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FencesVE.Services;
using Forms = System.Windows.Forms;
using Gdi = System.Drawing;
using GdiDraw = System.Drawing.Drawing2D;

namespace FencesVE.Views;

/// <summary>
/// Pipeta: pobiera kolor dowolnego piksela na ekranie.
/// <para>
/// Ekran jest najpierw zrzucany do bitmapy, a nakladka pokazuje ten zrzut zamiast
/// zywego obrazu. Dzieki temu kolor czytamy z bitmapy, a nie z ekranu - gdybysmy
/// czytali z ekranu, samo okno pipety mieszaloby sie w odczyt. Przy okazji obraz
/// pod kursorem stoi w miejscu, wiec da sie spokojnie wycelowac.
/// </para>
/// </summary>
internal sealed class ScreenColorPicker : Window
{
    private const int LoupeSize = 132;
    private const int LoupeZoom = 12;

    private readonly Gdi.Bitmap _screen;
    private readonly Image _canvas;
    private readonly Border _loupe;
    private readonly Image _loupeImage;
    private readonly TextBlock _readout;
    private readonly Border _readoutBox;

    private Color? _picked;

    private ScreenColorPicker(Gdi.Bitmap screen, BitmapSource source)
    {
        _screen = screen;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Nakladka przykrywa caly pulpit wirtualny - takze drugi monitor.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _canvas = new Image
        {
            Source = source,
            Stretch = Stretch.Fill,
        };

        _loupeImage = new Image
        {
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        RenderOptions.SetBitmapScalingMode(_loupeImage, BitmapScalingMode.NearestNeighbor);

        var crosshair = new Rectangle
        {
            Width = LoupeZoom,
            Height = LoupeZoom,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        _loupe = new Border
        {
            Width = LoupeSize,
            Height = LoupeSize,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = new Grid { Children = { _loupeImage, crosshair } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.6,
                Color = Colors.Black,
            },
        };

        _readout = new TextBlock
        {
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas, Segoe UI"),
            FontSize = 13,
            Margin = new Thickness(8, 4, 8, 4),
            Text = "#000000",
        };

        _readoutBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)),
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = _readout,
        };

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 20, 20, 20)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 48),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Text = Loc.Get("Picker_Hint"),
            },
        };

        Content = new Grid { Children = { _canvas, _loupe, _readoutBox, hint } };

        MouseMove += OnMouseMoved;
        MouseLeftButtonUp += OnPick;
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            UpdateForPosition(Mouse.GetPosition(_canvas));
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _screen.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Otwiera pipete i zwraca wskazany kolor albo null, gdy uzytkownik anulowal.</summary>
    public static Color? Pick()
    {
        Gdi.Bitmap? screen = null;

        try
        {
            var bounds = Forms.SystemInformation.VirtualScreen;

            screen = new Gdi.Bitmap(bounds.Width, bounds.Height, Gdi.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Gdi.Graphics.FromImage(screen))
            {
                graphics.CopyFromScreen(bounds.Location, Gdi.Point.Empty, bounds.Size);
            }

            var source = ToBitmapSource(screen);

            var picker = new ScreenColorPicker(screen, source);
            screen = null; // od tego miejsca bitmape zwalnia okno

            picker.ShowDialog();
            return picker._picked;
        }
        catch (Exception)
        {
            // Zrzut ekranu potrafi sie nie udac (np. chroniona zawartosc) - lepiej
            // cicho zrezygnowac z pipety niz wywrocic okno ustawien.
            return null;
        }
        finally
        {
            screen?.Dispose();
        }
    }

    private void OnMouseMoved(object sender, MouseEventArgs e) => UpdateForPosition(e.GetPosition(_canvas));

    private void OnPick(object sender, MouseButtonEventArgs e)
    {
        var pixel = ToPixel(e.GetPosition(_canvas));
        if (pixel is null)
        {
            return;
        }

        var color = _screen.GetPixel(pixel.Value.X, pixel.Value.Y);
        _picked = Color.FromRgb(color.R, color.G, color.B);

        Close();
    }

    /// <summary>
    /// Przelicza pozycje kursora w jednostkach WPF na piksel bitmapy.
    /// Liczymy z proporcji wzgledem rozmiaru kontrolki, wiec skalowanie DPI
    /// nie ma tu znaczenia - dziala tak samo na monitorze 100% i 150%.
    /// </summary>
    private Gdi.Point? ToPixel(Point position)
    {
        if (_canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
        {
            return null;
        }

        var x = (int)(position.X / _canvas.ActualWidth * _screen.Width);
        var y = (int)(position.Y / _canvas.ActualHeight * _screen.Height);

        if (x < 0 || y < 0 || x >= _screen.Width || y >= _screen.Height)
        {
            return null;
        }

        return new Gdi.Point(x, y);
    }

    private void UpdateForPosition(Point position)
    {
        var pixel = ToPixel(position);
        if (pixel is null)
        {
            return;
        }

        var color = _screen.GetPixel(pixel.Value.X, pixel.Value.Y);

        _readout.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}   ({color.R}, {color.G}, {color.B})";
        _readout.Foreground = color.GetBrightness() > 0.55 ? Brushes.Black : Brushes.White;
        _readoutBox.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));

        _loupeImage.Source = BuildLoupe(pixel.Value);

        // Lupa i odczyt chodza za kursorem, ale nie moga wyjechac poza nakladke.
        const double offset = 22;
        var loupeX = Math.Max(0, Math.Min(position.X + offset, ActualWidth - LoupeSize - 8));
        var loupeY = Math.Max(0, Math.Min(position.Y + offset, ActualHeight - LoupeSize - 42));

        _loupe.Margin = new Thickness(loupeX, loupeY, 0, 0);
        _readoutBox.Margin = new Thickness(loupeX, loupeY + LoupeSize + 6, 0, 0);
    }

    /// <summary>Wycina kwadrat wokol wskazanego piksela i powieksza go do rozmiaru lupy.</summary>
    private BitmapSource ToLoupeSource(Gdi.Bitmap zoomed) => ToBitmapSource(zoomed);

    private BitmapSource BuildLoupe(Gdi.Point center)
    {
        var side = LoupeSize / LoupeZoom;
        var half = side / 2;

        using var zoomed = new Gdi.Bitmap(side * LoupeZoom, side * LoupeZoom, Gdi.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Gdi.Graphics.FromImage(zoomed))
        {
            graphics.InterpolationMode = GdiDraw.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = GdiDraw.PixelOffsetMode.Half;

            var sourceRect = new Gdi.Rectangle(center.X - half, center.Y - half, side, side);

            graphics.DrawImage(
                _screen,
                new Gdi.Rectangle(0, 0, zoomed.Width, zoomed.Height),
                sourceRect,
                Gdi.GraphicsUnit.Pixel);
        }

        return ToLoupeSource(zoomed);
    }

    private static BitmapSource ToBitmapSource(Gdi.Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();

        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
