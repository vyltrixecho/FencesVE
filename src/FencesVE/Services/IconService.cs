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

        try
        {
            _queue.Add(() =>
            {
                var icon = GetIcon(path, requestedSize);
                if (icon is not null)
                {
                    onLoaded(icon);
                }
            });
        }
        catch (InvalidOperationException)
        {
            // Kolejka zamknieta w trakcie zamykania aplikacji.
        }
    }

    private void ProcessQueue()
    {
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
    public ImageSource? GetIcon(string path, int requestedSize)
    {
        var listIndex = requestedSize > 48 ? NativeMethods.SHIL_JUMBO : NativeMethods.SHIL_EXTRALARGE;
        var key = CacheKey(path, requestedSize);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var icon = LoadIcon(path, listIndex);
        if (icon is not null)
        {
            _cache[key] = icon;
        }

        return icon;
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
