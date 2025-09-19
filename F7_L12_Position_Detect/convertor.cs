using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows;
using F7_L12_Position_Detect.Model.Tool;

namespace F7_L12_Position_Detect
{
  


    [ValueConversion(typeof(bool), typeof(string))]
    public class permission2lockorunlock_Converter : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return Encoding.Unicode.GetString(BitConverter.GetBytes((UInt16)0xe968), 0, 2);
            }
            else
            {
                return Encoding.Unicode.GetString(BitConverter.GetBytes((UInt16)0xe966), 0, 2);
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }



    [ValueConversion(typeof(bool), typeof(string))]
    public class Bool2OKNG_Converter : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return "OK";
            }
            else
            {
                return "NG";
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }


    [ValueConversion(typeof(bool), typeof(string))]
    public class Bool2OKNG_Converter_INV : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return "NG";
            }
            else
            {
                return "OK";
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }


    [ValueConversion(typeof(bool), typeof(SolidColorBrush))]
    public class Bool2SolidColor_Converter : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return Brushes.YellowGreen;
            }
            else
            {
                return Brushes.OrangeRed;
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }


    [ValueConversion(typeof(bool), typeof(SolidColorBrush))]
    public class Bool2SolidColor_Converter_INV : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return Brushes.OrangeRed;
            }
            else
            {
                return Brushes.YellowGreen;
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }


    [ValueConversion(typeof(KStatus), typeof(string))]
    public class Status2ZH_Converter : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((KStatus)value == KStatus.Wait)
            {
                return "等待测试";
            }
            else if ((KStatus)value == KStatus.Processing)
            {
                return "测试中";
            }
            else if ((KStatus)value == KStatus.Processing_OK)
            {
                return "测试过程合格";
            }
            else if ((KStatus)value == KStatus.Processing_NG)
            {
                return "测试过程不合格";
            }
            else if ((KStatus)value == KStatus.OK)
            {
                return "合格";
            }
            else
            {
                return "不合格";
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    //[ValueConversion(typeof(ToolType), typeof(string))]
    //public class Tooltype2ZH_Converter : IValueConverter
    //{
    //    //源属性传给目标属性时，调用此方法ConvertBack
    //    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    //    {
    //        if (value == null)
    //        { throw new ArgumentNullException("value can not be null"); }
    //        if ((ToolType)value == ToolType.Template)
    //        {
    //            return "模板";
    //        }
    //        else
    //        {
    //            return "面积";
    //        }

    //    }

    //    //目标属性传给源属性时，调用此方法ConvertBack
    //    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    //    {
    //        return null;
    //    }
    //}





    [ValueConversion(typeof(KStatus), typeof(SolidColorBrush))]
    public class Status2BackGround_Converter : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((KStatus)value == KStatus.Wait)
            {
                return Brushes.Orange;
            }
            else if ((KStatus)value == KStatus.Processing)
            {
                return Brushes.BlueViolet;
            }
            else if ((KStatus)value == KStatus.OK)
            {
                return Brushes.YellowGreen;
            }
            else
            {
                return new SolidColorBrush(Color.FromArgb(0xff, 0xd7, 0x00, 0x0F));
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    [ValueConversion(typeof(KStatus), typeof(SolidColorBrush))]
    public class Status2ForeGround_Converter : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((KStatus)value == KStatus.Wait)
            {
                return Brushes.White;
            }
            else if ((KStatus)value == KStatus.Processing)
            {
                return Brushes.White;
            }
            else if ((KStatus)value == KStatus.OK)
            {
                return Brushes.White;
            }
            else
            {
                return Brushes.White;
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    [ValueConversion(typeof(Visibility), typeof(string))]
    public class Bool2Visible_Converter : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return Visibility.Visible;
            }
            else
            {
                return Visibility.Collapsed;
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    [ValueConversion(typeof(Visibility), typeof(string))]
    public class Bool2Visible_Converter_INV : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return Visibility.Collapsed;
            }
            else
            {
                return Visibility.Visible;
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }


    [ValueConversion(typeof(TestModes), typeof(Visibility))]
    public class TestMode2Visibility_Dimension : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((TestModes)value!=TestModes.面积)
            {
                return Visibility.Collapsed;
            }
            else
            {
                return Visibility.Visible;
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }


    [ValueConversion(typeof(TestModes), typeof(Visibility))]
    public class TestMode2Visibility_Barcode : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((TestModes)value != TestModes.二维码)
            {
                return Visibility.Collapsed;
            }
            else
            {
                return Visibility.Visible;
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }



    [ValueConversion(typeof(TestModes), typeof(Visibility))]
    public class TestMode2Visibility_ShapeMatch : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((TestModes)value != TestModes.模板匹配)
            {
                return Visibility.Collapsed;
            }
            else
            {
                return Visibility.Visible;
            }
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }


    [ValueConversion(typeof(bool), typeof(bool))]
    public class BoolINV_Converter : IValueConverter
    {
        //源属性传给目标属性时，调用此方法ConvertBack
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            return !((bool)value);
        }

        //目标属性传给源属性时，调用此方法ConvertBack
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }
    public enum KStatus
    {
        Wait,
        Processing,
        Processing_OK,
        Processing_NG,
        OK,
        NG
    }

    public enum TestModes
    {
        面积,
        二维码,
        模板匹配
    }

    /// <summary>
    /// 布尔值转检测状态文本转换器
    /// </summary>
    [ValueConversion(typeof(bool), typeof(string))]
    public class Bool2DetectionStatus : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return "可以检测";
            }
            else
            {
                return "请输入SN";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    /// <summary>
    /// 扫码状态转颜色转换器
    /// </summary>
    [ValueConversion(typeof(string), typeof(SolidColorBrush))]
    public class ScanCodeStatus2Color : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return Brushes.Gray;

            string status = value.ToString();
            switch (status)
            {
                case "扫码成功":
                case "SN获取成功":
                case "工序校验通过":
                    return Brushes.Green;
                case "扫码失败":
                case "SN获取失败":
                case "工序校验失败":
                    return Brushes.Red;
                case "正在处理...":
                    return Brushes.Orange;
                default:
                    return Brushes.Gray;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    /// <summary>
    /// 布尔值转状态颜色转换器
    /// </summary>
    [ValueConversion(typeof(bool), typeof(SolidColorBrush))]
    public class Bool2StatusColor : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return Brushes.Green;
            }
            else
            {
                return Brushes.Red;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    /// <summary>
    /// 布尔值转配置背景色转换器
    /// </summary>
    [ValueConversion(typeof(bool), typeof(SolidColorBrush))]
    public class Bool2ConfigBackground : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return new SolidColorBrush(Color.FromRgb(255, 255, 224)); // 淡黄色，表示可编辑
            }
            else
            {
                return Brushes.LightGray; // 灰色，表示只读
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    /// <summary>
    /// 布尔值转配置边框色转换器
    /// </summary>
    [ValueConversion(typeof(bool), typeof(SolidColorBrush))]
    public class Bool2ConfigBorder : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return Brushes.Orange; // 橙色边框，表示可编辑
            }
            else
            {
                return Brushes.Gray; // 灰色边框，表示只读
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    /// <summary>
    /// 布尔值转配置前景色转换器
    /// </summary>
    [ValueConversion(typeof(bool), typeof(SolidColorBrush))]
    public class Bool2ConfigForeground : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            { throw new ArgumentNullException("value can not be null"); }
            if ((bool)value)
            {
                return Brushes.DarkBlue; // 深蓝色，表示可编辑
            }
            else
            {
                return Brushes.Gray; // 灰色，表示只读
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
