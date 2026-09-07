using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SpaceTestPC.App.Converters;

public sealed class StatusTextBrushConverter : IValueConverter
{
    private static readonly Brush NormalBrush = new SolidColorBrush(Color.FromRgb(21, 128, 61));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(180, 35, 24));
    private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(71, 84, 103));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? string.Empty;
        if (ContainsAny(text, "异常", "断开", "失败", "占用")) return ErrorBrush;
        if (ContainsAny(text, "正常", "已连接", "就绪") || text.StartsWith("状态：", StringComparison.Ordinal) && !text.Contains("等待扫码", StringComparison.Ordinal))
            return NormalBrush;
        return NeutralBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));
}
