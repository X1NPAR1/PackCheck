using System.Globalization;
using System.Windows.Data;
using PackCheck.App.ViewModels;

namespace PackCheck.App.Converters;

/// <summary>Converts a game rule's current on/off state into a short display text.</summary>
public sealed class GameRuleStateConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var rule = values[0] as string;
        var viewModel = values[1] as ServerItemViewModel;
        if (rule is null || viewModel is null)
        {
            return "—";
        }

        return viewModel[rule] switch
        {
            true => "Açık",
            false => "Kapalı",
            _ => "Bilinmiyor"
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
