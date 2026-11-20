using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FencesVE.Interop;

namespace FencesVE.Services;

/// <summary>
/// Wyciaga ikony plikow z powloki Windows. Uzywa systemowych list obrazow
/// (jumbo 256 px / extra large 48 px), zeby ikony nie byly rozmyte przy duzych rozmiarach.
/// </summary>
public sealed class IconService : IDisposable
{
    private readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Kolejka zadan dla watku wyciagajacego ikony.
    /// <para>
    /// Musi to byc jeden watek w apartamencie STA. Powloka daje tu wspoldzielona liste
    /// obrazow przez COM, a ta z watku puli (MTA) potrafi po prostu nie odpowiedziec -
    /// i wlasnie tak wygladalo "czasem niektore ikony sie nie wczytuja". Jeden watek
    /// przy okazji szereguje dostep, wiec kilka fence'ow odswiezanych naraz nie wchodzi
    /// sobie w droge.
    /// </para>
    /// </summary>
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;

    public IconService()
    {
        _worker = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "FencesVE.Icons",
        };

        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>
    /// Wyciaga ikone poza watkiem UI i oddaje ja przez <paramref name="onLoaded"/>.
    /// Wywolanie zwrotne leci z watku ikon - odbiorca musi sam wrocic na swoj watek.
    /// </summary>
    public void RequestIcon(string path, int requestedSize, Action<ImageSource> onLoaded)
    {
        // Ikona juz wyciagnieta idzie prosto do odbiorcy. Przepuszczanie jej przez kolejke
        // oznaczalo, ze po kazdym odswiezeniu fence przez chwile stal bez ikon, czekajac
        // na watek, ktory i tak tylko zagladal do slownika.
        if (TryGetCached(path, requestedSize, out var cached))
        {
            onLoaded(cached);
            return;
        }

        if (_queue.IsAddingCompleted)
        {
            return;
        }

        Enqueue(path, requestedSize, onLoaded, attempt: 0);
    }

    /// <summary>
    /// Ile razy ponawiamy ikone, ktora wyszla pusta albo czarna. Zaraz po starcie Windows
    /// powloka wypelnia liste obrazow w tle i przez kilka, kilkanascie sekund oddaje
    /// puste sloty. Wczesniej taki czarny kwadrat trafial do cache i zostawal do restartu.
    /// </summary>
    private const int MaxAttempts = 8;

    private void Enqueue(string path, int requestedSize, Action<ImageSource> onLoaded, int attempt)
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            _queue.Add(() =>
            {
                var lastAttempt = attempt >= MaxAttempts - 1;
                var icon = GetIcon(path, requestedSize, acceptBlank: lastAttempt);
                if (icon is not null)
                {
                    onLoaded(icon);
                    return;
                }

                if (!lastAttempt)
                {
                    ScheduleRetry(path, requestedSize, onLoaded, attempt + 1);
                }
            });
        }
        catch (InvalidOperationException)
        {
            // Kolejka zamknieta w trakcie zamykania aplikacji.
        }
    }

    private void ScheduleRetry(string path, int requestedSize, Action<ImageSource> onLoaded, int attempt)
    {
        // 1 s, 2 s, 3 s... razem okolo pol minuty, z zapasem na wolny start systemu.
        var delay = TimeSpan.FromSeconds(attempt);
        System.Threading.Timer? timer = null;
        timer = new System.Threading.Timer(_ =>
        {
            timer?.Dispose();
            Enqueue(path, requestedSize, onLoaded, attempt);
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    private void ProcessQueue()
    {
        try
        {
            NativeMethods.FileIconInit(true);
        }
        catch
        {
            // Brak eksportu nie blokuje ikon, najwyzej zostaje stare zachowanie.
        }

        foreach (var job in _queue.GetConsumingEnumerable())
        {
            try
            {
                job();
            }
            catch
            {
                // Jedna ikona nie moze zatrzymac calej kolejki.
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();

        var finished = _worker.Join(TimeSpan.FromSeconds(2));

        // Watek moze jeszcze siedziec w wywolaniu powloki - zwolnienie kolejki spod niego
        // rzucaloby wyjatkiem przy zamykaniu aplikacji. Zostawiamy ja wtedy GC.
        if (finished)
        {
            _queue.Dispose();
        }
    }

    private static string CacheKey(string path, int requestedSize) =>
        $"{(requestedSize > 48 ? NativeMethods.SHIL_JUMBO : NativeMethods.SHIL_EXTRALARGE)}|{path}";

    private bool TryGetCached(string path, int requestedSize, out ImageSource icon) =>
        _cache.TryGetValue(CacheKey(path, requestedSize), out icon!);

    /// <summary>Zwraca ikone dla sciezki, korzystajac z cache. Wynik jest zamrozony (thread-safe).</summary>
    public ImageSource? GetIcon(string path, int requestedSize, bool acceptBlank = true)
    {
        var listIndex = requestedSize > 48 ? NativeMethods.SHIL_JUMBO : NativeMethods.SHIL_EXTRALARGE;
        var key = CacheKey(path, requestedSize);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var icon = LoadIcon(path, listIndex);
        if (icon is null)
        {
            return null;
        }

        if (icon is BitmapSource bitmapIcon && LooksBlank(bitmapIcon))
        {
            // Nie zapisujemy w cache - przy nastepnej probie powloka zwykle ma juz ikone.
            return acceptBlank ? icon : null;
        }

        _cache[key] = icon;
        return icon;
    }

    /// <summary>
    /// Rozpoznaje niewypelniony slot listy obrazow: calkiem przezroczysty albo
    /// jednolicie czarny kwadrat. Prawdziwa ikona ma przezroczyste brzegi albo kolor.
    /// </summary>
    private static bool LooksBlank(BitmapSource source)
    {
        try
        {
            var bitmap = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            if (width == 0 || height == 0)
            {
                return true;
            }

            var stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            var visible = 0;
            var opaque = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                if (alpha == 0)
                {
                    continue;
                }

                visible++;
                if (alpha > 240)
                {
                    opaque++;
                }

                if (pixels[i] > 12 || pixels[i + 1] > 12 || pixels[i + 2] > 12)
                {
                    return false; // Jest jakis kolor - to prawdziwa ikona.
                }
            }

            // Nic nie widac albo prawie caly kwadrat jest kryjaco czarny.
            var total = width * height;
            return visible == 0 || opaque > total * 0.9;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Czysci cache, np. po zmianie rozmiaru ikon albo odswiezeniu zawartosci.</summary>
    public void ClearCache() => _cache.Clear();

    public void Invalidate(string path)
    {
        foreach (var key in _cache.Keys)
        {
            if (key.EndsWith("|" + path, StringComparison.OrdinalIgnoreCase))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private static ImageSource? LoadIcon(string path, int listIndex)
    {
        try
        {
            var exists = File.Exists(path) || Directory.Exists(path);
            var isDirectory = Directory.Exists(path);

            var info = new NativeMethods.SHFILEINFOW();
            uint flags = NativeMethods.SHGFI_SYSICONINDEX;
            uint attributes = NativeMethods.FILE_ATTRIBUTE_NORMAL;

            if (!exists)
            {
                // Plik zniknal - i tak chcemy pokazac ikone wg rozszerzenia.
                flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
            }
            else if (isDirectory)
            {
                attributes = NativeMethods.FILE_ATTRIBUTE_DIRECTORY;
            }

            var result = NativeMethods.SHGetFileInfoW(
                path, attributes, ref info, Marshal.SizeOf<NativeMethods.SHFILEINFOW>(), flags);

            if (result == IntPtr.Zero)
            {
                return null;
            }

            var iid = NativeMethods.IID_IImageList;
            if (NativeMethods.SHGetImageList(listIndex, ref iid, out var imageList) != 0 || imageList is null)
            {
                return null;
            }

            var hIcon = IntPtr.Zero;
            try
            {
                if (imageList.GetIcon(info.iIcon, NativeMethods.ILD_TRANSPARENT, out hIcon) != 0 || hIcon == IntPtr.Zero)
                {
                    return null;
                }

                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                source.Freeze();
                return source;
            }
            finally
            {
                if (hIcon != IntPtr.Zero)
                {
                    NativeMethods.DestroyIcon(hIcon);
                }

                Marshal.ReleaseComObject(imageList);
            }
        }
        catch
        {
            // Brak ikony nie jest bledem krytycznym - pozycja pokaze sie bez obrazka.
            return null;
        }
    }
}
