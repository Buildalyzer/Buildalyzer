
using System.ComponentModel;
using System.Globalization;
using Buildalyzer.IO;

namespace Buildalyzer.Conversion;

/// <summary>Implements a <see cref="TypeConverter"/> for <see cref="IOPath"/>.</summary>
internal sealed class IOPathTypeConverter : TypeConverter
{
    /// <inheritdoc />
    [Pure]
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    /// <inheritdoc />
    [Pure]
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object? value)
        => value is null || value is string
            ? IOPath.Parse(value as string)
            : base.ConvertFrom(context, culture, value);
}
