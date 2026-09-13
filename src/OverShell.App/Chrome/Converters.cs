using System.Globalization;
using System.Windows.Data;

namespace OverShell.App.Chrome;

/// <summary>True when the bound value is not null; lets a trigger show a control only when a nullable has a value.</summary>
public sealed class IsNotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
