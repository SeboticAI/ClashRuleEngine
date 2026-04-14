using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ClashRuleEngine.UI
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => value is Visibility v && v == Visibility.Visible;
    }

    public class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value as string ?? "#2563EB")); }
            catch { return new SolidColorBrush(Colors.DodgerBlue); }
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    public class EnabledToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => (value is bool b && b) ? 1.0 : 0.4;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    public class PriorityToDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value is int i ? $"#{i + 1}" : "#?";
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }
}
