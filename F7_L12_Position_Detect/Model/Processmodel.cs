using F7_L12_Position_Detect.Model.Tool;
using GalaSoft.MvvmLight;
using HalconDotNet;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;

namespace F7_L12_Position_Detect.Model
{
    public class Processmodel : ObservableObject
    {
        [XmlIgnore]
        [XmlElement("二维码累积内容")]
        public string Barcodes { set; get; } = string.Empty;

        //[XmlElement("容器编号")]
        //public string RQSN { set; get; }=string.Empty;


        [XmlElement]
        [XmlIgnore]
        public string BarcodeStr { set; get; }

        [XmlElement]
        [XmlIgnore]
        public string RCMD { get; set; } = string.Empty;


        [XmlIgnore]
        public int ToolIndex { set; get; } = -1;

        [XmlElement("工具模型")]
        public ObservableCollection<ToolModel> Tools { get; set; } = new ObservableCollection<ToolModel>();

        [XmlElement("当前工具")]
        public ToolModel tool { set; get; } = new ToolModel();

        [XmlElement("选择工具序号")]
        public int selectedindex { set; get; } = -1;

        //public HWindowControlWPF HWindow = null;
        [XmlIgnore]
        public HWindow HWindow = null;

        [XmlIgnore]
        public int ImageNum { set; get; } = 0;


        [XmlIgnore]
        [XmlElement("胶路高度检测触发")]
        public IO Trig_IO { get; set; } = new IO();




        [XmlElement("状态颜色")]
        ///透明,等待
        ///黄色，运行中
        ///绿色，OK
        ///红色, NG
        [XmlIgnore]
        public SolidColorBrush StatusColor
        {

            get
            {
                switch (_Status)
                {
                    case ToolStatus.识别中:
                        {
                            return Brushes.Orange;
                        }
                    case ToolStatus.OK:
                        {
                            return Brushes.GreenYellow;
                        }
                    case ToolStatus.NG:
                    case ToolStatus.NG2:
                        {
                            return Brushes.OrangeRed;
                        }
                    default:
                        {
                            // return new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
                            return Brushes.Gray;
                        }
                }
            }

        }


        private ToolStatus _Status = ToolStatus.等待中;
        [XmlElement("测试状态")]


        public ToolStatus Status
        {
            set
            {
                _Status = value;
                RaisePropertyChanged(() => Status);
                RaisePropertyChanged(() => StatusColor);

            }
            get { return _Status; }
        }

        [XmlElement("扫码枪接收信息")]
        public string Scannerstr { set; get; } = string.Empty;

        [XmlElement("SN列表")]
        public ObservableCollection<string> SNList { set; get; } = new ObservableCollection<string>();

        [XmlIgnore]
        [XmlElement("显示照片")]
        public BitmapSource ShowBitmapSource { set; get; }

        // 报工相关属性
        private string _currentProductSN = string.Empty;
        [XmlElement("当前产品SN")]
        public string CurrentProductSN
        {
            get { return _currentProductSN; }
            set
            {
                _currentProductSN = value;
                RaisePropertyChanged(() => CurrentProductSN);
                RaisePropertyChanged(() => CanStartDetection);
            }
        }

        private string _currentWorkerName = string.Empty;
        [XmlIgnore]
        public string CurrentWorkerName
        {
            get { return _currentWorkerName; }
            set
            {
                _currentWorkerName = value;
                RaisePropertyChanged(() => CurrentWorkerName);
            }
        }

        private string _currentWorkerID = string.Empty;
        [XmlIgnore]
        public string CurrentWorkerID
        {
            get { return _currentWorkerID; }
            set
            {
                _currentWorkerID = value;
                RaisePropertyChanged(() => CurrentWorkerID);
            }
        }

        private bool _isReportingWork = false;
        [XmlIgnore]
        public bool IsReportingWork
        {
            get { return _isReportingWork; }
            set
            {
                _isReportingWork = value;
                RaisePropertyChanged(() => IsReportingWork);
            }
        }

        [XmlIgnore]
        public bool CanStartDetection
        {
            get { return !string.IsNullOrEmpty(CurrentProductSN) && !IsReportingWork; }
        }

        // 扫码状态相关属性
        private string _scanCodeStatus = "等待扫码";
        [XmlIgnore]
        public string ScanCodeStatus
        {
            get { return _scanCodeStatus; }
            set
            {
                _scanCodeStatus = value;
                RaisePropertyChanged(() => ScanCodeStatus);
                RaisePropertyChanged(() => ScanCodeStatusColor);
            }
        }

        [XmlIgnore]
        public SolidColorBrush ScanCodeStatusColor
        {
            get
            {
                switch (_scanCodeStatus)
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
        }

        [XmlElement("报工成功记录")]
        public ObservableCollection<string> ReportWorkSuccessLog { set; get; } = new ObservableCollection<string>();

        // 刷新上岗人员状态
        private string _refreshWorkerStatus = "";
        [XmlIgnore]
        public string RefreshWorkerStatus
        {
            get { return _refreshWorkerStatus; }
            set
            {
                _refreshWorkerStatus = value;
                RaisePropertyChanged(() => RefreshWorkerStatus);
                RaisePropertyChanged(() => RefreshWorkerStatusColor);
            }
        }

        [XmlIgnore]
        public SolidColorBrush RefreshWorkerStatusColor
        {
            get
            {
                switch (_refreshWorkerStatus)
                {
                    case "刷新成功":
                        return Brushes.Green;
                    case "刷新失败":
                    case "查询失败":
                        return Brushes.Red;
                    case "正在刷新...":
                        return Brushes.Orange;
                    default:
                        return Brushes.Transparent;
                }
            }
        }

    }
}
