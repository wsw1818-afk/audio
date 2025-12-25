using System.Globalization;
using System.Windows.Data;
using AudioRecorder.Models;

namespace AudioRecorder.Converters;

/// <summary>
/// RecordingFormat을 표시 이름으로 변환
/// </summary>
public class FormatToDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is RecordingFormat format)
        {
            return format.GetDisplayName();
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
