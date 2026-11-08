using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Atlas.App.Converters;

/// <summary>字符串非空时显示元素（用于标题栏的可选副标题 / 版本 / 状态胶囊）。</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
