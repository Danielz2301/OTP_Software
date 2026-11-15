using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace OTPSoftware.App.Converters;

/// <summary>
/// Converts hex color strings (such as <c>#26EF4444</c> or <c>#74B9FF</c>) into cached,
/// frozen <see cref="SolidColorBrush"/> instances to eliminate per-cell Brush allocations on scroll.
/// </summary>
public sealed class HexToFrozenBrushConverter : IValueConverter
{
    private const int MaxCachedBrushes = 512;
    private static readonly ConcurrentDictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves <paramref name="hex"/> to a cached frozen <see cref="Brush"/>, returning
    /// <paramref name="fallback"/> (or <see cref="Brushes.Transparent"/>) if invalid or empty.
    /// </summary>
    public static Brush ConvertHexToBrush(string? hex, Brush? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback ?? Brushes.Transparent;
        }

        string key = hex.Trim();
        if (Cache.TryGetValue(key, out Brush? cached))
        {
            return cached;
        }

        try
        {
            if (ColorConverter.ConvertFromString(key) is Color color)
            {
                var brush = new SolidColorBrush(color);
                if (brush.CanFreeze)
                {
                    brush.Freeze();
                }

                if (Cache.Count >= MaxCachedBrushes)
                {
                    Cache.Clear();
                }

                Cache.TryAdd(key, brush);
                return brush;
            }
        }
        catch
        {
            // Fall through to fallback brush when an invalid color literal is supplied.
        }

        return fallback ?? Brushes.Transparent;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        Brush? fallback = parameter as Brush;
        return ConvertHexToBrush(value as string, fallback);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

