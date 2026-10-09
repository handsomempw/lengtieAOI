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

        /// <summary>
        /// AOI 工具编辑前的运行时快照，用于保存成功后的参数变更审查，不写入工程 XML。
        /// </summary>
        [XmlIgnore]
        public int EditingToolIndex { get; set; } = -1;

        [XmlIgnore]
        public ToolModel EditingToolSnapshot { get; set; }

        [XmlIgnore]
        public DateTime EditingToolSnapshotTime { get; set; } = DateTime.MinValue;

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
                RaisePropertyChanged(() => CanStartDetection);
            }
        }

        /// <summary>
        /// 检测启动保护状态，用于主界面按钮显示和工具执行前的最后保险。正式生产作业资格由PLC触发门禁校验SN、工序和点检类型；这里保留SN、报工中和点检周期检查，避免异常入口在空SN、报工清理中或点检到期时直接启动视觉工具。
        /// 点检到期后只认可已经扫描对应点检SN形成的点检模式状态，防止只点击点检按钮或残留模式标志放行普通产品。
        /// </summary>
        [XmlIgnore]
        public bool CanStartDetection
        {
            get
            {
                bool isInspectionScan = IsInspectionMode && ScanCodeStatus == $"点检模式-{InspectionType}点检";
                return !string.IsNullOrEmpty(CurrentProductSN) && !IsReportingWork && (!IsInspectionOverdue || isInspectionScan);
            }
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
                RaisePropertyChanged(() => CanStartDetection);
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
                    case "母排绑定成功":
                    case "路径B组装绑定及报工成功":
                    case "工序校验通过":
                        return Brushes.Green;
                    case "扫码失败":
                    case "SN获取失败":
                    case "工序校验失败":
                    case "工单校验失败":
                    case "路径B组装绑定失败":
                    case "路径B组装绑定/报工失败":
                    case "AOI工序校验失败":
                    case "点检到期":
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

        // 阶段检测状态管理
        private bool _a1StageCompleted = false;
        [XmlIgnore]
        public bool A1StageCompleted
        {
            get { return _a1StageCompleted; }
            set
            {
                _a1StageCompleted = value;
                RaisePropertyChanged(() => A1StageCompleted);
                RaisePropertyChanged(() => CanExecuteReportWork);
            }
        }

        private bool _a2StageCompleted = false;
        [XmlIgnore]
        public bool A2StageCompleted
        {
            get { return _a2StageCompleted; }
            set
            {
                _a2StageCompleted = value;
                RaisePropertyChanged(() => A2StageCompleted);
                RaisePropertyChanged(() => CanExecuteReportWork);
            }
        }

        [XmlIgnore]
        public bool CanExecuteReportWork
        {
            get 
            { 
                // 根据工程配置动态判断：统计工程中配置了哪些阶段
                bool hasA1 = Tools.Any(t => t.Command == "A1" || t.Command == "ALL");
                bool hasA2 = Tools.Any(t => t.Command == "A2" || t.Command == "ALL");
                
                // 根据配置的阶段判断是否都已完成
                bool allCompleted = true;
                if (hasA1 && !A1StageCompleted)
                {
                    allCompleted = false;
                }
                if (hasA2 && !A2StageCompleted)
                {
                    allCompleted = false;
                }
                
                return allCompleted;
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

        // 点检相关状态
        private bool _isInspectionMode = false;
        /// <summary>
        /// 点检流程标志。该标志由点检SN或点检窗口启动，表示当前PLC触发服务OK/NG点检流程。
        /// </summary>
        [XmlIgnore]
        public bool IsInspectionMode
        {
            get { return _isInspectionMode; }
            set
            {
                _isInspectionMode = value;
                RaisePropertyChanged(() => IsInspectionMode);
                RaisePropertyChanged(() => InspectionStatusText);
                RaisePropertyChanged(() => CanStartDetection);
            }
        }

        private string _inspectionType = null; // "OK" 或 "NG" 或 null
        [XmlIgnore]
        public string InspectionType
        {
            get { return _inspectionType; }
            set
            {
                _inspectionType = value;
                RaisePropertyChanged(() => InspectionType);
                RaisePropertyChanged(() => InspectionStatusText);
                RaisePropertyChanged(() => CanStartDetection);
            }
        }

        private bool _okInspectionCompleted = false;
        [XmlIgnore]
        public bool OKInspectionCompleted
        {
            get { return _okInspectionCompleted; }
            set
            {
                _okInspectionCompleted = value;
                RaisePropertyChanged(() => OKInspectionCompleted);
                RaisePropertyChanged(() => InspectionStatusText);
            }
        }

        private bool _ngInspectionCompleted = false;
        [XmlIgnore]
        public bool NGInspectionCompleted
        {
            get { return _ngInspectionCompleted; }
            set
            {
                _ngInspectionCompleted = value;
                RaisePropertyChanged(() => NGInspectionCompleted);
                RaisePropertyChanged(() => InspectionStatusText);
            }
        }

        [XmlIgnore]
        public string InspectionStatusText
        {
            get
            {
                if (IsInspectionMode)
                {
                    return $"点检模式: {InspectionType}点检";
                }
                return string.Empty;
            }
        }

        private TimeSpan _timeToNextInspection = TimeSpan.Zero;
        /// <summary>
        /// 距离下次点检的剩余时间。小于等于零时表示点检到期，并同步驱动倒计时颜色和主界面可检测状态。
        /// </summary>
        [XmlIgnore]
        public TimeSpan TimeToNextInspection
        {
            get { return _timeToNextInspection; }
            set
            {
                _timeToNextInspection = value;
                RaisePropertyChanged(() => TimeToNextInspection);
                RaisePropertyChanged(() => TimeToNextInspectionText);
                RaisePropertyChanged(() => InspectionCountdownColor);
                RaisePropertyChanged(() => IsInspectionOverdue);
                RaisePropertyChanged(() => CanStartDetection);
            }
        }

        [XmlIgnore]
        public string TimeToNextInspectionText
        {
            get
            {
                if (IsInspectionOverdue)
                    return "已到期";
                int hours = (int)_timeToNextInspection.TotalHours;
                int minutes = _timeToNextInspection.Minutes;
                return $"{hours:00}:{minutes:00}";
            }
        }

        [XmlIgnore]
        public SolidColorBrush InspectionCountdownColor
        {
            get
            {
                if (IsInspectionOverdue)
                    return Brushes.Red;
                else if (_timeToNextInspection.TotalHours < 1)
                    return Brushes.Orange;
                else
                    return Brushes.Gray;
            }
        }

        [XmlIgnore]
        public bool IsInspectionOverdue
        {
            get { return _timeToNextInspection <= TimeSpan.Zero; }
        }

    }
}
