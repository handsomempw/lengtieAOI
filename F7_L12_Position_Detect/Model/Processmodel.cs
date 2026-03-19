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

        // 相机状态（用于现场快速判断）
        private string _cameraStatus = "未知";
        [XmlIgnore]
        public string CameraStatus
        {
            get { return _cameraStatus; }
            set
            {
                _cameraStatus = value;
                RaisePropertyChanged(() => CameraStatus);
                RaisePropertyChanged(() => CameraStatusColor);
            }
        }

        [XmlIgnore]
        public SolidColorBrush CameraStatusColor
        {
            get
            {
                if (string.IsNullOrEmpty(_cameraStatus)) return Brushes.Gray;
                if (_cameraStatus == "正常") return Brushes.Green;
                if (_cameraStatus.Contains("超时") || _cameraStatus.Contains("异常") || _cameraStatus.Contains("断开"))
                {
                    return Brushes.OrangeRed;
                }
                return Brushes.Gray;
            }
        }

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

        /// <summary>
        /// 阶段检测状态管理（动态阶段列表）。
        /// </summary>
        /// <remarks>
        /// 业务背景：
        /// - 工具通过 <see cref="ToolModel.Command"/> 绑定到某个阶段指令（如 A1/A2/A3...）。同一阶段可包含多个工具。
        /// - PLC 侧只提供“单地址触发”，阶段推进由视觉端内部状态机（CurrentStageIndex/CompletedStages）控制。
        /// - 任一阶段出现 NG/NG2/超时，将 <see cref="NeedRestart"/> 置为 true；下一次触发必须清空工具运行态并从第 0 阶段重跑。
        /// </remarks>

        [XmlIgnore]
        public List<string> ConfiguredStages { get; set; } = new List<string>();

        [XmlIgnore]
        public HashSet<string> CompletedStages { get; set; } = new HashSet<string>();

        private int _currentStageIndex = 0;
        [XmlIgnore]
        public int CurrentStageIndex
        {
            get { return _currentStageIndex; }
            set
            {
                _currentStageIndex = value;
                RaisePropertyChanged(() => CurrentStageIndex);
                RaisePropertyChanged(() => CanExecuteReportWork);
            }
        }

        [XmlIgnore]
        public bool NeedRestart { get; set; } = false;

        [XmlIgnore]
        public bool CanExecuteReportWork
        {
            get
            {
                // 业务意图：报工条件应当随“已配置阶段”变化
                // - 只配置 A1：A1 完成即可报工
                // - 配置 A1/A2/A3：必须全部完成才可报工
                // 该判断不关心每阶段工具数量，工具 OK/NG 的聚合由阶段结算逻辑负责。
                return ConfiguredStages.Count > 0 &&
                       CompletedStages.Count >= ConfiguredStages.Count &&
                       ConfiguredStages.All(s => CompletedStages.Contains(s));
            }
        }

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

        // 报工状态显示
        private string _reportWorkStatus = "";
        [XmlIgnore]
        public string ReportWorkStatus
        {
            get { return _reportWorkStatus; }
            set
            {
                _reportWorkStatus = value;
                RaisePropertyChanged(() => ReportWorkStatus);
                RaisePropertyChanged(() => ReportWorkStatusColor);
            }
        }

        [XmlIgnore]
        public SolidColorBrush ReportWorkStatusColor
        {
            get
            {
                if (_reportWorkStatus.Contains("✅") || _reportWorkStatus.Contains("成功"))
                    return Brushes.Green;
                else if (_reportWorkStatus.Contains("❌") || _reportWorkStatus.Contains("失败"))
                    return Brushes.Red;
                else if (_reportWorkStatus.Contains("🔄") || _reportWorkStatus.Contains("正在"))
                    return Brushes.Orange;
                else if (_reportWorkStatus.Contains("⚠️") || _reportWorkStatus.Contains("未报工"))
                    return Brushes.DarkOrange;
                else
                    return Brushes.Transparent;
            }
        }

    }
}
