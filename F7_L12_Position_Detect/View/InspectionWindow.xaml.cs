using F7_L12_Position_Detect.ViewModel;
using Panuon.WPF.UI;
using System;
using System.Windows;
using System.Windows.Media;
using System.Globalization;
using System.Windows.Data;

namespace F7_L12_Position_Detect.View
{
    /// <summary>
    /// InspectionWindow.xaml 的交互逻辑
    /// </summary>
    public partial class InspectionWindow : WindowX
    {
        private ViewModelLocator _vml;

        public InspectionWindow()
        {
            InitializeComponent();
            _vml = Application.Current.FindResource("Locator") as ViewModelLocator;
            this.DataContext = _vml.Main;
        }

        private void BtnOKInspection_Click(object sender, RoutedEventArgs e)
        {
            if (_vml?.Main != null && !_vml.Main.DataModel.Processmodel.OKInspectionCompleted)
            {
                _vml.Main.StartOKInspection();
            }
        }

        private void BtnNGInspection_Click(object sender, RoutedEventArgs e)
        {
            if (_vml?.Main != null && !_vml.Main.DataModel.Processmodel.NGInspectionCompleted)
            {
                _vml.Main.StartNGInspection();
            }
        }
    }

    // 转换器类
    public class BoolToOKButtonColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool completed && completed)
                return new SolidColorBrush(Color.FromRgb(129, 199, 132)); // 浅绿色
            return new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToNGButtonColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool completed && completed)
                return new SolidColorBrush(Color.FromRgb(255, 183, 77)); // 浅橙色
            return new SolidColorBrush(Color.FromRgb(255, 152, 0)); // 橙色
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToOKButtonTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool completed && completed)
                return "✓ OK点检完成";
            return "开始OK点检";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToNGButtonTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool completed && completed)
                return "✓ NG点检完成";
            return "开始NG点检";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

