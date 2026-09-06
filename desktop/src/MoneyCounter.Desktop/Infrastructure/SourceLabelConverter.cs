using System.Globalization;
using System.Windows.Data;
namespace MoneyCounter.Desktop.Infrastructure;
public sealed class SourceLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString() switch
    {
        "MANUAL" => "人工录入", "CSV" => "CSV 导入", "SIMULATED" => "模拟数据", _ => "来源未知"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
