using System.Globalization;
using System.Windows.Data;
using AudioRecorder.Models;

namespace AudioRecorder.Converters;

/// <summary>
/// RecordingState가 Stopped일 때만 true 반환
/// </summary>
public class StateToEnabledConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is RecordingState state)
        {
            return state == RecordingState.Stopped;
        }
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
