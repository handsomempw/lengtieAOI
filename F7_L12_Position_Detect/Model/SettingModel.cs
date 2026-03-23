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
        /// 阶段失败（NG/NG2/超时）后，下次触发从第一个阶段（通常为A1）重跑。
        /// </summary>
        从头开始重跑 = 0,

        /// <summary>
        /// 阶段失败后，不清理已完成阶段与其它阶段工具结果，仅在下一次触发时重试当前失败阶段。
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
