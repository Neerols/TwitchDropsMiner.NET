using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TwitchDropsMiner.UI;

/// <summary>StatusKind → кисть (цвета подобраны так, чтобы читались и в светлой, и в тёмной теме).</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush Good = Frozen(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly Brush Warn = Frozen(Color.FromRgb(0xC8, 0x8F, 0x00));
    private static readonly Brush Bad = Frozen(Color.FromRgb(0xD9, 0x3A, 0x3A));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusKind.Good => Good,
        StatusKind.Warn => Warn,
        StatusKind.Bad => Bad,
        // обычный статус — основной цвет текста текущей темы (иначе WPF подставит чёрный)
        _ => Application.Current?.TryFindResource("TextFillColorPrimaryBrush") ?? DependencyProperty.UnsetValue,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Перевод в XAML: Text="{ui:Tr gui.tabs.main, Default=Main}".</summary>
public sealed class TrExtension : System.Windows.Markup.MarkupExtension
{
    public string Key { get; set; } = "";
    public string? Default { get; set; }
    public TrExtension() { }
    public TrExtension(string key) { Key = key; }
    public override object ProvideValue(IServiceProvider serviceProvider) => Core.L.T(Key, Default).TrimEnd();
}
