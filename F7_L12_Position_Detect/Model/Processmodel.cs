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
                RaisePropertyChanged(() => CanStartDetection);
            }
        }

        /// <summary>
        /// 判断当前检测按钮是否可以启动工具检测。该属性只提供界面和工具启动层保护，普通产品的SN、工序和点检到期门禁仍由对应业务流程分别校验。
        /// </summary>
        /// <remarks>
        /// 点检样件在MES确认身份并进入OK或NG模式后允许检测；未完成点检身份确认的等待扫码状态不会被该属性放行。
        /// </remarks>
        [XmlIgnore]
        public bool CanStartDetection
        {
            get
            {
                bool isInspectionReady = IsInspectionMode
                    && string.IsNullOrEmpty(PendingInspectionType)
                    && (InspectionType == "OK" || InspectionType == "NG");

                return !string.IsNullOrEmpty(CurrentProductSN)
                    && !IsReportingWork
                    && (!IsInspectionOverdue || isInspectionReady);
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
                    case "工序校验通过":
                        return Brushes.Green;
                    case "扫码失败":
                    case "SN获取失败":
                    case "工序校验失败":
                        return Brushes.Red;
                    case "正在读取扫码器":
                    case "条码已接收，正在解析SN":
                    case "SN已确认，正在校验工序":
                        return Brushes.Orange;
                    case "扫码器超时":
                    case "MES超时":
                    case "工序不通过":
                        return Brushes.Red;
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
        /// - 普通生产阶段出现 NG/NG2/超时后，<see cref="NeedRestart"/> 配合 XML 重试策略决定下一次触发位置。
        /// - 点检模式使用独立规则：当前阶段失败时保留 <see cref="CurrentStageIndex"/>，下一次相同PLC触发重复当前阶段，XML全局策略不改变点检重试方式。
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

        #region 点检状态（可配置周期强制点检：待扫码 / 执行中 / 完成状态）

        private string _pendingInspectionType = null; // "OK" / "NG" / null
        /// <summary>
        /// 已选择点检类型，但尚未扫描匹配的点检SN；等待状态不可触发检测。
        /// </summary>
        [XmlIgnore]
        public string PendingInspectionType
        {
            get { return _pendingInspectionType; }
            set
            {
                _pendingInspectionType = value;
                RaisePropertyChanged(() => PendingInspectionType);
                RaisePropertyChanged(() => InspectionStatusText);
                RaisePropertyChanged(() => IsInspectionActive);
                RaisePropertyChanged(() => CanStartDetection);
            }
        }

        private bool _isInspectionMode = false;
        /// <summary>
        /// 当前已通过MES确认点检最终产品SN并允许检测。该状态只属于本次运行中的点检样件，不代表普通产品已完成工序校验或报工。
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
                RaisePropertyChanged(() => IsInspectionActive);
                RaisePropertyChanged(() => CanStartDetection);
            }
        }

        private string _inspectionType = null; // "OK" / "NG" / null
        /// <summary>
        /// 当前已确认样件的点检类型，仅接受OK或NG；窗口选择后的等待扫码状态由 PendingInspectionType 表示。
        /// </summary>
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
        /// <summary>
        /// 当前点检周期的OK样件是否已完成。该标记属于运行态，软件重启后按未完成处理，直到OK和NG都完成才更新周期时间。
        /// </summary>
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
        /// <summary>
        /// 当前点检周期的NG样件是否已完成。该标记属于运行态，软件重启后按未完成处理，直到OK和NG都完成才更新周期时间。
        /// </summary>
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

        /// <summary>
        /// 面向界面的点检状态文案，区分已进入样件检测和已选择类型等待扫码两种操作状态。
        /// </summary>
        [XmlIgnore]
        public string InspectionStatusText
        {
            get
            {
                if (IsInspectionMode)
                    return $"点检模式: {InspectionType}点检";
                if (!string.IsNullOrEmpty(PendingInspectionType))
                    return $"等待{PendingInspectionType}点检扫码";
                return string.Empty;
            }
        }

        /// <summary>
        /// 判断点检流程是否占用当前检测入口，包括等待扫码和已进入样件模式两个阶段。
        /// </summary>
        [XmlIgnore]
        public bool IsInspectionActive
        {
            get { return IsInspectionMode || !string.IsNullOrEmpty(PendingInspectionType); }
        }

        private TimeSpan _timeToNextInspection = TimeSpan.Zero;
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
                if (_timeToNextInspection <= TimeSpan.Zero)
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
                    return Brushes.OrangeRed;
                else if (_timeToNextInspection.TotalHours < 1)
                    return Brushes.Orange;
                else
                    return Brushes.LightGreen;
            }
        }

        [XmlIgnore]
        public bool IsInspectionOverdue
        {
            get { return _timeToNextInspection <= TimeSpan.Zero; }
        }

        #endregion

    }
}
