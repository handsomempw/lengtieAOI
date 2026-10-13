using F7_L12_Position_Detect.Model.Setting;
using GalaSoft.MvvmLight;
using HalconDotNet;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Xml.Serialization;

namespace F7_L12_Position_Detect.Model
{
    public class SettingModel : ObservableObject
    {
        [XmlIgnore]
        public HWindow HWindow { get; set; } = null;

        [XmlIgnore]
        [XmlElement("相机配置")]
        public Camera.DATA camedata { set; get; } = new Camera.DATA();




        [XmlElement("照片保存设置")]
        public ImageSaveSetting ImageSaveSetting { get; set; }=new ImageSaveSetting();

        [XmlElement("扫码器")]
        public Scanner.ScannerModel ScannerModel { set; get; } = new Scanner.ScannerModel();

        [XmlElement("霍尼韦尔扫码器")]

        public Honeywell.HF800 HF800 { set; get; } = new Honeywell.HF800();


        public string ScannerMode { set; get; } = "HF800";

        /// <summary>
        /// 扫码枪连接状态（用于界面显示）
        /// </summary>
        private bool _scannerIsConnected = false;
        [XmlIgnore]
        public bool ScannerIsConnected
        {
            get { return _scannerIsConnected; }
            set
            {
                _scannerIsConnected = value;
                RaisePropertyChanged(() => ScannerIsConnected);
            }
        }

        #region 工程配置
        [XmlElement("工程路径")]
        public string Prjdir { set; get; } = $"{Environment.CurrentDirectory}\\工程文件";
        [XmlIgnore]
        [XmlElement("工程名称清单")]
        public ObservableCollection<string> Prjs { set; get; } = new ObservableCollection<string>();

        [XmlElement("选中工程序号")]
        public int prjselected { set; get; } = -1;

        [XmlElement("工程名称")]
        public string Name { set; get; } = "999";
        [XmlIgnore]
        [XmlElement("权限")]
        public bool permission { get; set; } = false;

        /// <summary>
        /// Debug 程序的本地开发调试授权开关。打开后使用本地测试授权，关闭后使用动态密码认证。
        /// 默认关闭，现场配置不会因为升级自动获得本地编辑权限。
        /// </summary>
        [XmlElement("开发调试授权")]
        public bool DeveloperLocalAuthEnabled { get; set; } = false;

        /// <summary>
        /// 当前编辑权限的授权时间和审查上下文。运行时字段不写入配置文件，权限会话结束时清空。
        /// </summary>
        [XmlIgnore]
        public DateTime PermissionGrantedAt { get; set; } = DateTime.MinValue;

        [XmlIgnore]
        public string PermissionAuthorizerName { get; set; } = string.Empty;

        [XmlIgnore]
        public string PermissionAuthorizerNo { get; set; } = string.Empty;

        [XmlIgnore]
        public string PermissionRequestedReceivers { get; set; } = string.Empty;

        [XmlIgnore]
        public int PermissionPrivilegeLevel { get; set; }

        [XmlIgnore]
        public int PermissionPeriodMinutes { get; set; }

        [XmlIgnore]
        public string PermissionReason { get; set; } = string.Empty;

        /// <summary>
        /// 当前动态密码授权会话的审计关联编号。运行时字段不写入工程 XML，权限关闭后清空。
        /// </summary>
        [XmlIgnore]
        public string PermissionAuditId { get; set; } = string.Empty;
        #endregion

        #region 通讯配置

        [XmlElement("机器人")]
        public TcpSetting RobotConnect { set; get; } = new TcpSetting();
        [XmlElement("上位机")]
        public TcpSetting SoftConnect { set; get; } = new TcpSetting();

        [XmlElement("扫码报工服务器")]
        public TcpSetting BarcodeReporter { set; get; }=new TcpSetting();



        #region PLC配置
        [XmlElement("PLC通讯IP")]
        public string PLC_IP { set; get; } = "10.23.8.20";
        [XmlElement("PLC通讯端口")]
        public int PLC_Port { set; get; } = 502;
        [XmlElement("信号交互地址")]
        public int AddressStart { set; get; } = 7080;

        /// <summary>
        /// PLC连接状态（用于界面显示）
        /// </summary>
        private bool _plcIsConnected = false;
        [XmlIgnore]
        public bool PLCIsConnected
        {
            get { return _plcIsConnected; }
            set
            {
                _plcIsConnected = value;
                RaisePropertyChanged(() => PLCIsConnected);
            }
        }

        #endregion





        [XmlIgnore]
        public TcpClientHelper.TcpClientH TcpClientH { set; get; } = new TcpClientHelper.TcpClientH();

        [XmlIgnore]
        public TcpServerHelper.TCPServerH TcpServerRobot { set; get; } = new TcpServerHelper.TCPServerH();
        [XmlIgnore]
        public TcpServerHelper.TCPServerH TcpServerSoft { set; get; } = new TcpServerHelper.TCPServerH();

        #endregion


        [XmlElement("测试延时")]

        public int delaytime { set; get; } = 1000;


        [XmlElement("缩放照片尺寸")]
        public int ImageSize { set; get; } = 140;


        [XmlElement("自动记录识别过程日志")]
        public bool SaveProcessData { set; get; } = true;

        [XmlElement("扫码检索服务器地址")]
        public string SNlistServerIP { set; get; } = "10.23.8.28";

        /// <summary>
        /// 冷贴 AOI 工具编辑的动态密码认证配置。设备号由报工配置中的设备编号统一提供。
        /// </summary>
        [XmlElement("动态密码认证")]
        public DynamicPasswordAuthenticationSetting DynamicPasswordAuth { set; get; } = new DynamicPasswordAuthenticationSetting();

        [XmlElement("报工配置")]
        public ReportWorkConfig ReportWorkConfig { set; get; } = new ReportWorkConfig();

        /// <summary>
        /// 母排组装单母排组装配置。该能力默认关闭，维护人员解锁后开启并保存，保存后立即按新快照执行。
        /// </summary>
        [XmlElement("母排绑定配置")]
        public BusbarBindingConfig BusbarBinding { get; set; } = new BusbarBindingConfig();

    }

    /// <summary>
    /// 报工配置类
    /// </summary>
    public class ReportWorkConfig : ObservableObject
    {
        private string _stationCode = "ST001";
        [XmlElement("工位号")]
        public string StationCode
        {
            get { return _stationCode; }
            set
            {
                _stationCode = value;
                RaisePropertyChanged(() => StationCode);
            }
        }

        private string _procedureCode = "";
        [XmlElement("工序码")]
        public string ProcedureCode
        {
            get { return _procedureCode; }
            set
            {
                _procedureCode = value;
                RaisePropertyChanged(() => ProcedureCode);
            }
        }

        private string _procedureName = "视觉检测";
        [XmlElement("工序名称")]
        public string ProcedureName
        {
            get { return _procedureName; }
            set
            {
                _procedureName = value;
                RaisePropertyChanged(() => ProcedureName);
            }
        }

        private string _currentWorkOrderCode = string.Empty;
        [XmlElement("当前工单号")]
        public string CurrentWorkOrderCode
        {
            get { return _currentWorkOrderCode; }
            set
            {
                _currentWorkOrderCode = value;
                RaisePropertyChanged(() => CurrentWorkOrderCode);
            }
        }

        private string _equipmentNumber = "VIS001";
        [XmlElement("设备编号")]
        public string EquipmentNumber
        {
            get { return _equipmentNumber; }
            set
            {
                _equipmentNumber = value;
                RaisePropertyChanged(() => EquipmentNumber);
            }
        }

        private bool _enableAutoReportWork = true;
        [XmlElement("启用自动报工")]
        public bool EnableAutoReportWork
        {
            get { return _enableAutoReportWork; }
            set
            {
                _enableAutoReportWork = value;
                RaisePropertyChanged(() => EnableAutoReportWork);
            }
        }

        // 点检相关配置
        private string _okInspectionSN = "OK_INSPECTION";
        /// <summary>
        /// 旧版本全局OK点检SN，仅在工程缺少“点检配置.xml”时作为首次迁移来源。配方文件生成后，扫码流程读取当前工程维护的OK点检SN。
        /// </summary>
        [XmlElement("OK点检SN码")]
        public string OKInspectionSN
        {
            get { return _okInspectionSN; }
            set
            {
                _okInspectionSN = value;
                RaisePropertyChanged(() => OKInspectionSN);
            }
        }

        private string _ngInspectionSN = "NG_INSPECTION";
        /// <summary>
        /// 旧版本全局NG点检SN，仅在工程缺少“点检配置.xml”时作为首次迁移来源。配方文件生成后，扫码流程读取当前工程维护的NG点检SN。
        /// </summary>
        [XmlElement("NG点检SN码")]
        public string NGInspectionSN
        {
            get { return _ngInspectionSN; }
            set
            {
                _ngInspectionSN = value;
                RaisePropertyChanged(() => NGInspectionSN);
            }
        }

        private DateTime _lastInspectionTime = DateTime.MinValue;
        /// <summary>
        /// 旧版本全局点检完成时间，仅在工程缺少“点检配置.xml”时作为首次迁移来源。配方文件生成后，倒计时、提醒和生产门禁读取当前工程的完成时间。
        /// </summary>
        [XmlElement("上次点检完成时间")]
        public DateTime LastInspectionTime
        {
            get { return _lastInspectionTime; }
            set
            {
                _lastInspectionTime = value;
                RaisePropertyChanged(() => LastInspectionTime);
            }
        }
    }

    public enum IOstatus
    {
        连接失败,
        高电平,
        低电平
    }

    public class IO : ObservableObject
    {

        private int _IOstatus = -1;
        [XmlIgnore]
        public int IOstatus
        {
            get
            {
                return _IOstatus;
            }
            set
            {
                _IOstatus = value;
                RaisePropertyChanged(() => IOstatusBackGround);
                RaisePropertyChanged(() => IOStatusForeGround);
            }
        }
        [XmlIgnore]
        public SolidColorBrush IOstatusBackGround
        {
            get
            {
                if (_IOstatus == -1)
                {
                    return Brushes.WhiteSmoke;
                }

                else if (IOstatus == 1)
                {
                    return Brushes.GreenYellow;
                }
                else
                {
                    return Brushes.Black;
                }
            }
        }
        public SolidColorBrush IOStatusForeGround
        {
            get
            {
                if (_IOstatus == -1)
                {
                    return Brushes.Black;
                }
                else if (_IOstatus == 1)
                {
                    return Brushes.OrangeRed;
                }
                else
                {
                    return Brushes.White;
                }
            }
        }
    }
}
