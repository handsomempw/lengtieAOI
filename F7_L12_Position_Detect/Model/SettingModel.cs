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
    public enum StageFailRetryStrategy
    {
        /// <summary>
        /// 普通生产阶段失败（NG/NG2/超时）后，下次触发从第一个阶段（通常为A1）重跑。点检样件始终保留当前阶段，供调参后重复触发。
        /// </summary>
        从头开始重跑 = 0,

        /// <summary>
        /// 普通生产阶段失败后，不清理已完成阶段与其它阶段工具结果，仅在下一次触发时重试当前失败阶段。点检样件使用相同的当前阶段重试口径。
        /// </summary>
        仅重试失败阶段_保留已有结果 = 1,
    }

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

        /// <summary>
        /// 扫码数据进入本软件的方式（配置里可改字符串）：<br/>
        /// <b>HF800</b>：网口指令取码（Honeywell HF800），占用 TCP 连接，不占 COM。<br/>
        /// <b>其它值</b>（如 Serial、空、自定义）：走 <see cref="ScannerModel"/> 串口 SDK，<b>串口被本进程独占</b>，勿与其它软件或第二处同时打开同一 COM。<br/>
        /// <b>Keyboard</b> / <b>键盘口</b> / <b>HID</b>：键盘楔入，不占串口；请把焦点放在主界面「产品 SN」框用枪扫，「扫码测试」不会打开串口。
        /// </summary>
        public string ScannerMode { set; get; } = "HF800";

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

        [XmlElement("报工配置")]
        public ReportWorkConfig ReportWorkConfig { set; get; } = new ReportWorkConfig();

        /// <summary>
        /// 普通生产阶段失败后的重试策略，保存于主配置XML。点检模式使用独立规则：当前失败阶段重复触发，XML全局策略不改变点检的重试方式。
        /// </summary>
        [XmlElement("阶段失败重试策略")]
        public StageFailRetryStrategy StageFailRetryStrategy { get; set; } = StageFailRetryStrategy.从头开始重跑;

        [XmlElement("执行超时ms")]
        public int ExecutionTimeoutMs { get; set; } = 5000;

        [XmlElement("扫码触发自动执行阶段")]
        public bool EnableScanAutoTriggerStages { get; set; } = false;

    }

    /// <summary>
    /// 报工配置类
    /// </summary>
    public class ReportWorkConfig : ObservableObject
    {
        /// <summary>
        /// 点检周期的默认值，单位为小时。配置缺失或超出允许范围时沿用该值，保证升级后仍按既有12小时门禁运行。
        /// </summary>
        public const int DefaultInspectionIntervalHours = 12;

        /// <summary>
        /// 点检周期允许的最小值，单位为小时。1小时是普通产品到期门禁和倒计时的最小业务粒度。
        /// </summary>
        public const int MinInspectionIntervalHours = 1;

        /// <summary>
        /// 点检周期允许的最大值，单位为小时。48小时限制现场最长免点检窗口，避免异常配置造成长期放行。
        /// </summary>
        public const int MaxInspectionIntervalHours = 48;

        /// <summary>
        /// NG点检的“任一NG”条件标识，供XML配置和运行时判定共用。
        /// </summary>
        public const string InspectionNgConditionAny = "任一NG";

        /// <summary>
        /// NG点检的“全部NG”条件标识，供XML配置和运行时判定共用。
        /// </summary>
        public const string InspectionNgConditionAll = "全部NG";

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

        // MES报工接口最后一个入参：工序码/工序编号（PROCEDURE_NUMBER）
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

        private int _inspectionIntervalHours = DefaultInspectionIntervalHours;
        /// <summary>
        /// 两轮强制点检之间的有效时长，单位为小时。有效范围为1至48小时；配置节点缺失、填写小于1或大于48的值时，运行时按12小时处理。
        /// </summary>
        [XmlElement("点检周期小时")]
        public int InspectionIntervalHours
        {
            get { return _inspectionIntervalHours; }
            set
            {
                _inspectionIntervalHours = value;
                RaisePropertyChanged(() => InspectionIntervalHours);
            }
        }

        private string _inspectionNgPassCondition = InspectionNgConditionAny;
        /// <summary>
        /// NG点检的判定口径。“任一NG”接受至少一个判定框NG，“全部NG”要求本次参与检测的判定框全部NG；NG2始终表示点检异常。
        /// </summary>
        [XmlElement("NG点检合格条件")]
        public string InspectionNgPassCondition
        {
            get { return _inspectionNgPassCondition; }
            set
            {
                _inspectionNgPassCondition = value;
                RaisePropertyChanged(() => InspectionNgPassCondition);
            }
        }

        private string _okInspectionSN = "OK_INSPECTION";
        /// <summary>
        /// OK点检样件在MES中的最终绑定产品SN。扫码经MES实时解析并确认该SN后进入OK点检，点检链路跳过工序校验和正常报工。
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
        /// NG点检样件在MES中的最终绑定产品SN。扫码经MES实时解析并确认该SN后进入NG点检，点检链路跳过工序校验和正常报工。
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
        /// 最近一轮OK和NG点检全部完成的时间。倒计时和普通产品到期门禁共同使用该时间，首次部署或无效时间按已经到期处理。
        /// </summary>
        [XmlElement("上次点检时间")]
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
                RaisePropertyChanged(() => IOStatusDisplayText);
            }
        }

        [XmlIgnore]
        public string IOStatusDisplayText
        {
            get
            {
                if (_IOstatus == -1) return "PLC断连";
                return $"触发:{_IOstatus}";
            }
        }

        [XmlIgnore]
        public SolidColorBrush IOstatusBackGround
        {
            get
            {
                if (_IOstatus == -1)
                {
                    return Brushes.OrangeRed;
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
