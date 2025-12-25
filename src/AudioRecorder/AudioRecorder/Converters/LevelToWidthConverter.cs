using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AudioRecorder.Converters;

/// <summary>
/// 오디오 레벨(0.0~1.0)을 Width로 변환
/// </summary>
public class LevelToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 ||
            values[0] is not double level ||
            values[1] is not double maxWidth)
        {
            return 0.0;
        }

        return Math.Max(0, Math.Min(maxWidth, level * maxWidth));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 단일 값 레벨 컨버터 (최대 폭 기본값 사용)
/// </summary>
public class LevelToPercentWidthConverter : IValueConverter
{
    public double MaxWidth { get; set; } = 200;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is float level)
        {
            return Math.Max(0, Math.Min(MaxWidth, level * MaxWidth));
        }
        if (value is double levelD)
        {
            return Math.Max(0, Math.Min(MaxWidth, levelD * MaxWidth));
        }
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
