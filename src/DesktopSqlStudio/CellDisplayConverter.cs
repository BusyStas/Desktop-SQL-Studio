using System.Globalization;
using System.Windows.Data;

namespace DesktopSqlStudio;

/// <summary>Shows SQL NULL as NULL and binary values as hex, like SSMS.</summary>
public sealed class CellDisplayConverter : IValueConverter
{
    public static readonly CellDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => "0x" + System.Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 64))) + (bytes.Length > 64 ? "…" : ""),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
