using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace GotifyDesktop;

public sealed class SmallIconConverter : IValueConverter
{
    private const string DefaultIcon = "pack://application:,,,/Assets/logo.png";
    private const int Capacity = 32;
    private readonly Dictionary<string, BitmapSource> _images = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string path = value as string ?? DefaultIcon;
        try { return Load(path); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException)
        { return path == DefaultIcon ? null : Load(DefaultIcon); }
    }
    private BitmapSource Load(string path)
    {
        if (_images.TryGetValue(path, out var image)) return image;
        // Only application resources and the local, already authenticated icon cache.
        // Never cause a network request from a list-row binding.
        var uri = new Uri(path, UriKind.Absolute);
        if (uri.Scheme != "pack" && !uri.IsFile) throw new ArgumentException("Only local icons are supported.");
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache; bitmap.DecodePixelWidth = 64; bitmap.UriSource = uri; bitmap.EndInit(); bitmap.Freeze();
        if (_images.Count == Capacity) _images.Remove(_order.Dequeue());
        _images.Add(path, bitmap); _order.Enqueue(path); return bitmap;
    }
    internal void Clear() { _images.Clear(); _order.Clear(); }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
