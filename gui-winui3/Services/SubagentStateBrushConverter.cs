using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace RuApp.Services;

public sealed class SubagentStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Application.Current.Resources[(value as string) switch
        {
            "working" => "RuInfoBrush",
            "attention" => "RuWarningBrush",
            "done" => "RuSuccessBrush",
            _ => "RuMutedBrush",
        }];
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
