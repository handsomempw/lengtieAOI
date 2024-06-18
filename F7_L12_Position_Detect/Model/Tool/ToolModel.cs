using GalaSoft.MvvmLight;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;

namespace F7_L12_Position_Detect.Model.Tool
{
    public class ToolModel : ObservableObject
    {
        [XmlIgnore]
        public QRCode.Reg Reg = new QRCode.Reg();

        [XmlIgnore]
        public PositionDetect.ShapeMatch ShapeMatch = new PositionDetect.ShapeMatch();


        [XmlElement("工具序号")]
        public int Index { set; get; } = 0;

        [XmlElement("触发指令")]
        public string Command { set; get; } = "T1-1";

        [XmlElement("工具名称")]
        public string Name { set; get; } = string.Empty;

        //[XmlElement("曝光时间")]
        //public double ExposureTime { set; get; } = 10000;


        #region 二维码识别

        [XmlElement("是否识别二维码")]
        public bool DecodeBarcode { set; get; } = false;

        [XmlElement("二维码内容")]
        public string BarcodeStr { set; get; } = string.Empty;
        [XmlElement("二维码ROI")]
        public ROI BarCodeROI { set; get; } = new ROI();
        #endregion

        #region 位置检测

        [XmlElement("位置检测")]
        public bool PositionDetect { set; get; } = true;


        [XmlElement("位置检测模型文件名称")]
        public string ModelFileName { set; get; } = string.Empty;

        [XmlElement("模板匹配分值下限")]

        public double MinScore { set; get; } = 0.8;

        public double ActualScore { set; get; } = 0;


        [XmlElement("比例um/pixel")]
        public double K { set; get; } = 1;

        [XmlElement("初始X轴坐标")]
        public double InitX { set; get; } = 0;
        [XmlElement("初始Y轴坐标")]
        public double InitY { set; get; } = 0;


        [XmlElement("检测X轴坐标")]
        public double ActualX { set; get; } = 0;
        [XmlElement("检测Y轴坐标")]
        public double ActualY { set; get; } = 0;
        [XmlElement("X轴偏差")]
        public double DeltaX { set; get; } = 0;
        [XmlElement("Y轴偏差")]
        public double DeltaY { set; get; } = 0;


        [XmlElement("X轴取反")]
        public bool DirectX { set; get; } = false;
        [XmlElement("Y轴取反")]
        public bool DirectY { set; get; } = false;
        [XmlElement("XY轴对换")]
        public bool InvertXY { set; get; } = false;

        [XmlElement("发送结果状态")]
        public bool SendStatus { set; get; } = false;

        [XmlElement("状态颜色")]
        ///透明,等待
        ///黄色，运行中
        ///绿色，OK
        ///红色, NG
        [XmlIgnore]
        public SolidColorBrush StatusColor { set; get; } = Brushes.Transparent;


        [XmlElement("位置检测ROI")]
        public ROI PositionROI { set; get; } = new ROI();

        #endregion
        [XmlIgnore]
        public BitmapSource BitmapSource { get; set; } = null;

    }

    public class ROI : ObservableObject
    {
        public int Row1 { set; get; } = 0;
        public int Row2 { set; get; } = 0;
        public int Col1 { set; get; } = 0;
        public int Col2 { set; get; } = 0;
    }
}
