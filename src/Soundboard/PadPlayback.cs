using System;
using System.Globalization;
using System.Windows.Data;

namespace Soundboard;

public sealed record PadPlayback(Guid Id, string Label);

public sealed class PadPlaybackLabel : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [Sound sound, PadPlayback playing] && sound.Id == playing.Id ? playing.Label : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
