using System.Globalization;
using Avalonia.Data.Converters;

namespace HeyMelodyWindows.App;

public sealed class OptionSelectedConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is null || values[1] is null) return false;
        return (values[0], values[1]) switch
        {
            (UiOption selected, UiOption item) => selected.Key == item.Key,
            (UiAncMode selected, UiAncMode item) => selected.Key == item.Key,
            (UiEqEntry selected, UiEqEntry item) => selected.Id == item.Id,
            _ => false
        };
    }
}
