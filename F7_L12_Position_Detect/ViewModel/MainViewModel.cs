using F7_L12_Position_Detect.Model;
using F7_L12_Position_Detect.View;
using GalaSoft.MvvmLight;
using Panuon.WPF.UI;
using System.IO;
using System.Windows.Markup;
using System;
using System.Xml.Serialization;
using Microsoft.Win32;
using System.Windows;
using GalaSoft.MvvmLight.Command;
using System.Windows.Controls;
using F7_L12_Position_Detect.Model.Tool;
using System.Windows.Media;
using System.Text;
using System.Threading;
using TcpServerHelper;
using System.Drawing;
using System.Reflection;
using System.Windows.Interop;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using HalconDotNet;
using QRCode;
using PositionDetect;
using Camera;
using System.Collections.Generic;
using static System.Net.Mime.MediaTypeNames;
using System.Runtime;
using System.Linq;
using System.Security.Cryptography;
using HslCommunication.ModBus;
using System.Windows.Input;
using System.Data;
using Color = System.Drawing.Color;
using System.Diagnostics;
using System.Threading.Tasks;
using Honeywell;
using F7_L12_Position_Detect.Service;

namespace F7_L12_Position_Detect.ViewModel
{
    /// <summary>
    /// This class contains properties that the main View can data bind to.
    /// <para>
    /// Use the <strong>mvvminpc</strong> snippet to add bindable properties to this ViewModel.
    /// </para>
    /// <para>
    /// You can also use Blend to data bind with the tool's support.
    /// </para>
    /// <para>
    /// See http://www.galasoft.ch/mvvm
    /// </para>
    /// </summary>
    public partial class MainViewModel : ViewModelBase
    {
        /// <summary>
        /// Initializes a new instance of the MainViewModel class.
        /// </summary>
        public MainViewModel()
        {

            DeleteToolCMD = new RelayCommand(DeleteTool);
            AddToolCMD = new RelayCommand(AddTool);
            ClearToolCMD = new RelayCommand(ClearTool);
            CopyToolCMD = new RelayCommand(CopyTool);
            InsertToolCMD = new RelayCommand(InsertTool);


            NEW_PRJCMD = new RelayCommand(NEW_PRJCMD_process);
            SAVE_PRJCMD = new RelayCommand(SAVE_PRJ_process);
            //REGISTER_MAIN_CMD = new RelayCommand(REGISTER_MAIN_process);
            //ADD_MAIN_CMD = new RelayCommand(ADD_MAIN_process);
        }

        [XmlElement("数据模型")]
        public DataModel DataModel { get; set; } = new DataModel();

        // MES服务实例
        private readonly MESService _mesService = new MESService();

        // 最近一次确认的产品SN：用于识别新产品切换，避免出现“A1清了但A2还在”的残留。
        private string _lastProductSN = string.Empty;

        /// <summary>
        /// 新产品切换时统一复位检测相关状态：
        /// 1) 清空工具结果（A1/A2/ALL）
        /// 2) 重置阶段完成标志
        /// 3) 清空报工状态提示
        /// </summary>
        private void ResetForNewProduct(string newProductSN)
        {
            if (string.IsNullOrEmpty(newProductSN))
            {
                return;
            }

            if (string.Equals(_lastProductSN, newProductSN, StringComparison.Ordinal))
            {
                return;
            }

            _lastProductSN = newProductSN;

            try
            {
                App.Current.Dispatcher.Invoke(new Action(() =>
                {
                    ClearTools();
                    DataModel.Processmodel.A1StageCompleted = false;
                    DataModel.Processmodel.A2StageCompleted = false;
                    DataModel.Processmodel.ReportWorkStatus = string.Empty;
                }));
            }
            catch
            {
            }
        }

        /*
         * 图片命名策略（业务口径）：
         * - 目标：文件名前缀优先使用 MES 解析后的“最外层SN”（CurrentProductSN），避免出现空前缀导致 `-01-A1-1-OK-....jpg`。
         * - 回退：当 PLC触发抓拍/保存发生在SN链路尚未更新、或点检/异常流程导致SN为空时，按优先级回退到 SNList[工位] / Scannerstr / BarcodeStr。
         * - 兜底：仍取不到任何可用标识时使用 "NOSN"（避免空文件名片段与排查困难）。
         * - 约束：前缀会做文件名非法字符清理，避免因为SN含特殊字符导致保存失败。
         */

        private static string SanitizeFileNamePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value.Trim())
            {
                sb.Append(invalid.Contains(ch) ? '_' : ch);
            }
            return sb.ToString();
        }

        private string GetImageFilePrefix(int toolIndex)
        {
            string rawCurrentSn = DataModel?.Processmodel?.CurrentProductSN;
            string prefix = SanitizeFileNamePart(rawCurrentSn);

            if (!string.IsNullOrEmpty(prefix))
            {
                return prefix;
            }

            try
            {
                var snList = DataModel?.Processmodel?.SNList;
                var tools = DataModel?.Processmodel?.Tools;
                if (snList != null && tools != null && toolIndex >= 0 && toolIndex < tools.Count)
                {
                    int pos = tools[toolIndex].ProductPositionNO;
                    if (pos >= 0 && pos < snList.Count)
                    {
                        var sn = SanitizeFileNamePart(snList[pos]);
                        if (!string.IsNullOrEmpty(sn))
                        {
                            WriteDetailLog($"[图片命名] 回退前缀来源=SNList[{pos}]（CurrentProductSN为空）");
                            return sn;
                        }
                    }
                }
            }
            catch
            {
            }

            prefix = SanitizeFileNamePart(DataModel?.Processmodel?.Scannerstr);
            if (!string.IsNullOrEmpty(prefix))
            {
                WriteDetailLog($"[图片命名] 回退前缀来源=Scannerstr（CurrentProductSN为空）");
                return prefix;
            }

            prefix = SanitizeFileNamePart(DataModel?.Processmodel?.BarcodeStr);
            if (!string.IsNullOrEmpty(prefix))
            {
                WriteDetailLog($"[图片命名] 回退前缀来源=BarcodeStr（CurrentProductSN为空）");
                return prefix;
            }

            WriteDetailLog($"[图片命名] 使用NOSN兜底（CurrentProductSN/SNList/Scannerstr/BarcodeStr均为空）；toolIndex={toolIndex}, RCMD={DataModel?.Processmodel?.RCMD ?? "空"}");
            return "NOSN";
        }

        // 点检定时器
        private System.Windows.Threading.DispatcherTimer _inspectionTimer;
        
        // 点检弹窗引用
        private Window _inspectionWindow = null;

        /// <summary>
        /// 点检有效周期，单位为小时。该周期用于主界面倒计时、点检弹窗和普通产品作业门禁，保持三处口径一致。
        /// </summary>
        private const double InspectionIntervalHours = 24;

        private void UIInvoke(Action action)
        {
            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }

        private void UIBeginInvoke(Action action)
        {
            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action);
        }


        public void InitHwindow(HWindow _HWindow)
        {
            DataModel.Settingmodel.HWindow = _HWindow;
            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                DataModel.Processmodel.Tools[i].Reg.Hwindow = _HWindow;
                DataModel.Processmodel.Tools[i].ShapeMatch.HWindow = _HWindow;
            }
        }
        public void InitShm()
        {
            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                if (DataModel.Processmodel.Tools[i].TestMode == TestModes.模板匹配)
                {
                    try
                    {
                        string shmfilename = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{DataModel.Processmodel.Tools[i].Index}.shm";
                        if (File.Exists(shmfilename))
                        {
                            DataModel.Processmodel.Tools[i].ShapeMatch.init(shmfilename);
                        }
                        else
                        {
                            writeLog($"模型文件不存在:{DataModel.Settingmodel.Name}\\Tool{DataModel.Processmodel.Tools[i].Index}.shm");
                        }
                    }
                    catch (Exception ex)
                    {
                        writeLog($"模型文件加载失败:{DataModel.Settingmodel.Name}\\Tool{DataModel.Processmodel.Tools[i].Index}.shm;{ex.Message}");
                        continue;
                    }
                }
            }
        }

        #region 通讯
        #region 机器人
        public void SendMsgRobot(string cmd)
        {
            // 机器人通讯已禁用，当前业务只使用PLC通信
            // DataModel.Settingmodel.TcpServerRobot.SendMessage(cmd);
            // writeLog($"视觉->机器人:{cmd}");
        }

        public void InitRobotServer()
        {
            // 机器人服务器已禁用，当前业务只使用PLC通信
            /*
            DataModel.Settingmodel.TcpServerRobot = new TCPServerH();

            DataModel.Settingmodel.TcpServerRobot.ClientConnected += RobotTcpServer_ClientConnected;
            DataModel.Settingmodel.TcpServerRobot.ClientDisconnected += RobotTcpServer_ClientDisconnected;
            DataModel.Settingmodel.TcpServerRobot.MessageReceived += RobotTcpServer_MessageReceived;
            new Thread(() =>
            {
                try
                {
                    DataModel.Settingmodel.TcpServerRobot.StartListener(DataModel.Settingmodel.RobotConnect.LocalIP, DataModel.Settingmodel.RobotConnect.LocalPort);
                    writeLog("TCP服务端启动侦听[等待机器人连线]");
                }
                catch (Exception ex2)
                {
                    writeError(ex2.Message + Environment.NewLine + ex2.StackTrace);
                }
            }).Start(); ;
            */
        }
        // 机器人事件处理方法已禁用
        /*
        private void RobotTcpServer_ClientConnected(TCPServerH sender, object e)
        {
            try
            {
                TCPevent TCPevent = (TCPevent)e;
                if (TCPevent.Msg == "客户端连接")
                {
                    writeLog("机器人已经连接");
                    DataModel.Settingmodel.RobotConnect.IsConnected = true;
                }
                else if (TCPevent.Msg == "客户端重新连接")
                {
                    writeLog("机器人重新连接");
                    DataModel.Settingmodel.RobotConnect.IsConnected = true;
                }
            }
            catch (Exception ex) {; }
        }

        private void RobotTcpServer_ClientDisconnected(TCPServerH sender, object e)
        {
            try
            {
                TCPevent TCPevent = (TCPevent)e;
                if (TCPevent.Msg == "客户端掉线")
                {
                    writeLog("机器人已经离线");
                    DataModel.Settingmodel.RobotConnect.IsConnected = false;
                }
                else if (TCPevent.Msg == "发送失败，客户端掉线")
                {
                    writeLog("发送失败，客户端掉线");
                    DataModel.Settingmodel.RobotConnect.IsConnected = false;
                }
                else
                {
                    writeLog(TCPevent.Msg);
                    DataModel.Settingmodel.RobotConnect.IsConnected = false;
                }
            }
            catch (Exception ex) {; }
        }
        */

        // 机器人消息接收处理方法已禁用
        /*
        private void RobotTcpServer_MessageReceived(TCPServerH sender, object e)
        {
            try
            {
                TCPevent TCPevent = (TCPevent)e;
                string cmd = (string)TCPevent.Msg;
                writeLog($"机器人->视觉:{cmd}");
                //   NoticeBox.Show((string)TCPevent.Msg, "接收机器人信息", MessageBoxIcon.Info, true, 3000);
                DataModel.Processmodel.ToolIndex = -1;

                DataModel.Processmodel.RCMD = cmd;

                if (cmd.StartsWith("SA_"))
                {
                    #region 切换配方或配方检测
                    #endregion
                }
                else if (cmd == "ZERO")
                {

                }
                else
                {

                    for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
                    {
                        if (DataModel.Processmodel.Tools[i].Command == cmd)
                        {
                            if (i == 0)
                            {
                                ClearTools();
                            }
                            writeLog($"机器人->视觉:{cmd}开始设置参数", false);
                            DataModel.Processmodel.ToolIndex = i + 1;

                            // 检查工具是否指定了特定相机，如果是则切换相机
                            SwitchCameraForTool(DataModel.Processmodel.Tools[i]);

                            DataModel.Settingmodel.camedata.CameraModel.exposuretime = DataModel.Processmodel.Tools[i].ExposureTime;
                            DataModel.Settingmodel.camedata.CameraModel.camera.Exposure = DataModel.Processmodel.Tools[i].ExposureTime;
                            DataModel.Settingmodel.camedata.CameraModel.camera.bnSetParam_Click();
                            Thread.Sleep(DataModel.Settingmodel.delaytime);
                            writeLog($"机器人->视觉:{cmd}开始触发", false);
                            DataModel.Settingmodel.camedata.CameraModel.camera.bnTriggerExec_Click();
                            writeLog($"机器人->视觉:{cmd}触发完成", false);

                            break;
                        }
                    }
                }
            }
            catch (Exception ex) {; }
        }
        */


        public void ClearTools()
        {
            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                ClearTool(DataModel.Processmodel.Tools[i]);
            }
        }

        /// <summary>
        /// 根据指令清除对应工具的状态（用于A1/A2多阶段检测，避免相互影响）
        /// </summary>
        /// <param name="command">指令名称，如"A1"、"A2"或"ALL"</param>
        public void ClearToolsByCommand(string command)
        {
            WriteDetailLog($"[工具清理] 开始清理指令{command}对应的工具状态");
            
            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                var tool = DataModel.Processmodel.Tools[i];
                
                // 判断工具是否属于当前指令
                bool shouldClear = false;
                if (command == "ALL")
                {
                    // ALL指令清除所有工具
                    shouldClear = true;
                }
                else
                {
                    // 只清除匹配当前指令的工具
                    shouldClear = (tool.Command == command || tool.Command == "ALL");
                }
                
                if (shouldClear)
                {
                    WriteDetailLog($"[工具清理] 清理工具{i}({tool.Name}), Command={tool.Command}");
                    ClearTool(tool);
                }
                else
                {
                    WriteDetailLog($"[工具清理] 保留工具{i}({tool.Name}), Command={tool.Command}, Status={tool.ToolStatus}");
                }
            }
        }

        public void ClearTool(ToolModel tool)
        {
            tool.BarcodeStr = string.Empty;
            tool.DeltaX = 0;
            tool.DeltaY = 0;
            tool.ActualScore = 0;
            tool.ActualX = 0;
            tool.ActualY = 0;
            tool.ActualAngle = 0;
            tool.ActualDimension = 0;
            tool.ToolStatus = ToolStatus.等待中;

        }

        /// <summary>
        /// 根据工具配置切换相机
        /// </summary>
        /// <param name="tool">工具对象</param>
        private void SwitchCameraForTool(ToolModel tool)
        {
            try
            {
                // 检查工具是否配置了相机序列号
                if (string.IsNullOrEmpty(tool.CameraSerialNumber))
                {
                    writeLog($"工具{tool.Index}未配置相机序列号，请在工具设置中选择相机", false);
                    return;
                }

                var cameraData = DataModel.Settingmodel.camedata;
                var camera = cameraData.CameraModel.camera;

                // 查找匹配的相机索引
                int targetIndex = -1;
                for (int j = 0; j < camera.cbDeviceList.Count; j++)
                {
                    if (camera.cbDeviceList[j].Contains(tool.CameraSerialNumber))
                    {
                        targetIndex = j;
                        break;
                    }
                }

                // 如果找到匹配的相机且与当前选择不同，则切换相机
                if (targetIndex >= 0 && camera.SelectedIndex != targetIndex)
                {
                    writeLog($"工具{tool.Index}切换到相机: {camera.cbDeviceList[targetIndex]}", false);

                    // 关闭当前相机 - 使用状态轮询替代固定延时
                    camera.bnClose_Click();
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    while (stopwatch.ElapsedMilliseconds < 3000 && camera.bnCloseEnabled)
                    {
                        Thread.Sleep(50); // 50ms轮询间隔
                    }

                    // 切换到目标相机
                    camera.SelectedIndex = targetIndex;

                    // 打开新相机 - 使用状态轮询替代固定延时
                    camera.bnOpen_Click();
                    stopwatch.Restart();
                    while (stopwatch.ElapsedMilliseconds < 3000 && !camera.bnCloseEnabled)
                    {
                        Thread.Sleep(50); // 50ms轮询间隔
                    }

                    // 启动取流
                    camera.bnStartGrab_Click();

                    writeLog($"工具{tool.Index}相机切换完成", false);
                }
                else if (targetIndex >= 0)
                {
                    writeLog($"工具{tool.Index}相机已是目标相机，无需切换", false);
                }
                else
                {
                    writeLog($"工具{tool.Index}指定的相机序列号{tool.CameraSerialNumber}未找到，请检查相机连接", false);
                }
            }
            catch (Exception ex)
            {
                writeLog($"工具{tool.Index}相机切换失败: {ex.Message}", false);
            }
        }



        #endregion
        #region Soft
        public void SendMsgSoft(string cmd)
        {
            DataModel.Settingmodel.TcpServerSoft.SendMessage(cmd);
            writeLog($"视觉->上位机:{cmd}");
        }

        public void InitSoftServer()
        {

            DataModel.Settingmodel.TcpServerSoft = new TCPServerH();


            DataModel.Settingmodel.TcpServerSoft.ClientConnected += SoftTcpServer_ClientConnected;
            DataModel.Settingmodel.TcpServerSoft.ClientDisconnected += SoftTcpServer_ClientDisconnected;
            DataModel.Settingmodel.TcpServerSoft.MessageReceived += SoftTcpServer_MessageReceived;
            new Thread(() =>
            {
                try
                {
                    DataModel.Settingmodel.TcpServerSoft.StartListener(DataModel.Settingmodel.SoftConnect.LocalIP, DataModel.Settingmodel.SoftConnect.LocalPort);
                    writeLog("TCP服务端启动侦听[等待上位机连线]");
                }
                catch (Exception ex2)
                {
                    writeError(ex2.Message + Environment.NewLine + ex2.StackTrace);
                }
            }).Start(); ;

        }
        private void SoftTcpServer_ClientConnected(TCPServerH sender, object e)
        {
            try
            {
                TCPevent TCPevent = (TCPevent)e;
                if (TCPevent.Msg == "客户端连接")
                {
                    writeLog("上位机已经连接");
                    DataModel.Settingmodel.SoftConnect.IsConnected = true;
                }
                else if (TCPevent.Msg == "客户端重新连接")
                {
                    writeLog("上位机重新连接");
                    DataModel.Settingmodel.SoftConnect.IsConnected = true;
                }



            }
            catch (Exception ex) {; }
        }

        private void SoftTcpServer_ClientDisconnected(TCPServerH sender, object e)
        {
            try
            {
                TCPevent TCPevent = (TCPevent)e;
                if (TCPevent.Msg == "客户端掉线")
                {
                    writeLog("上位机已经离线");
                    DataModel.Settingmodel.SoftConnect.IsConnected = false;
                }
                else if (TCPevent.Msg == "发送失败，客户端掉线")
                {
                    writeLog("发送失败，客户端掉线");
                    DataModel.Settingmodel.SoftConnect.IsConnected = false;
                }
                else
                {
                    writeLog(TCPevent.Msg);
                    DataModel.Settingmodel.SoftConnect.IsConnected = false;
                }
            }
            catch (Exception ex) {; }
        }

        private void SoftTcpServer_MessageReceived(TCPServerH sender, object e)
        {
            try
            {
                TCPevent TCPevent = (TCPevent)e;
                string cmd = (string)TCPevent.Msg;
                writeLog($"上位机->视觉:{cmd}");
                //  NoticeBox.Show((string)TCPevent.Msg, "接收上位机信息", MessageBoxIcon.Info, true, 3000);
                //DataModel.Processmodel.ToolIndex = -1;
                //for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
                //{
                //    if (DataModel.Processmodel.Tools[i].Command == cmd)
                //    {
                //        DataModel.Processmodel.ToolIndex = i;

                //        break;
                //    }
                //}
            }
            catch (Exception ex) {; }
        }

        //public void ClearTool(ToolModel tool)
        //{
        //    tool.BarcodeStr = string.Empty;
        //    tool.DeltaX = 0;
        //    tool.DeltaY = 0;
        //    tool.ActualScore = 0;
        //    tool.ActualX = 0;
        //    tool.ActualY = 0;

        //}



        #endregion
        #region PLC通讯
        // 添加执行状态锁，防止竞态条件
        private volatile bool _isExecuting = false;
        private readonly object _executionLock = new object();

        // PLC连接复用：维护一个长连接，避免频繁创建/销毁连接
        private ModbusTcpNet _plcConnection = null;
        private readonly object _plcConnectionLock = new object();
        private DateTime _lastConnectionAttempt = DateTime.MinValue;
        private const int CONNECTION_RETRY_INTERVAL_MS = 5000; // 连接失败后重试间隔5秒
        private const int CONNECTION_TIMEOUT_MS = 3000; // 连接超时3秒（从1ms改为3秒）
        private const int RECEIVE_TIMEOUT_MS = 2000; // 接收超时2秒（从1ms改为2秒）

        /// <summary>
        /// 获取或创建PLC连接（连接复用）
        /// </summary>
        /// <returns>ModbusTcpNet连接对象，如果连接失败返回null</returns>
        private ModbusTcpNet GetOrCreatePLCConnection()
        {
            lock (_plcConnectionLock)
            {
                try
                {
                    // 如果连接已存在且可用，直接返回
                    if (_plcConnection != null)
                    {
                        // 尝试进行一次简单操作来检测连接是否有效
                        try
                        {
                            var testResult = _plcConnection.ReadUInt16("0", 1);
                            if (testResult.IsSuccess)
                            {
                                // 连接有效，更新连接状态
                                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    DataModel.Settingmodel.PLCIsConnected = true;
                                }));
                                return _plcConnection;
                            }
                        }
                        catch
                        {
                            // 连接可能已断开，需要重新连接
                            WriteDetailLog("[PLC连接] 检测到连接已断开，准备重新连接");
                        }

                        // 连接无效，关闭并释放
                        try
                        {
                            _plcConnection.ConnectClose();
                        }
                        catch { }
                        _plcConnection = null;
                    }

                    // 检查是否需要重试连接（避免频繁重试）
                    if (_lastConnectionAttempt != DateTime.MinValue)
                    {
                        var elapsed = (DateTime.Now - _lastConnectionAttempt).TotalMilliseconds;
                        if (elapsed < CONNECTION_RETRY_INTERVAL_MS)
                        {
                            // 距离上次尝试时间太短，跳过本次连接尝试
                            App.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DataModel.Settingmodel.PLCIsConnected = false;
                            }));
                            return null;
                        }
                    }

                    // 创建新连接
                    _lastConnectionAttempt = DateTime.Now;
                    WriteDetailLog($"[PLC连接] 尝试连接PLC {DataModel.Settingmodel.PLC_IP}:{DataModel.Settingmodel.PLC_Port}");

                    ModbusTcpNet modbusTcp = new ModbusTcpNet();
                    modbusTcp.ConnectTimeOut = CONNECTION_TIMEOUT_MS;
                    modbusTcp.ReceiveTimeOut = RECEIVE_TIMEOUT_MS;
                    modbusTcp.IpAddress = DataModel.Settingmodel.PLC_IP;
                    modbusTcp.Port = DataModel.Settingmodel.PLC_Port;
                    modbusTcp.DataFormat = HslCommunication.Core.DataFormat.CDAB;

                    var connectResult = modbusTcp.ConnectServer();
                    if (connectResult.IsSuccess)
                    {
                        _plcConnection = modbusTcp;
                        WriteDetailLog("[PLC连接] 连接成功，已建立长连接");
                        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DataModel.Settingmodel.PLCIsConnected = true;
                        }));
                        return _plcConnection;
                    }
                    else
                    {
                        WriteDetailLog($"[PLC连接] 连接失败: {connectResult.Message}");
                        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DataModel.Settingmodel.PLCIsConnected = false;
                            // 通讯失败数据置为-1
                            DataModel.Processmodel.Trig_IO.IOstatus = -1;
                        }));
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    WriteDetailLog($"[PLC连接] 连接异常: {ex.Message}");
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DataModel.Settingmodel.PLCIsConnected = false;
                        DataModel.Processmodel.Trig_IO.IOstatus = -1;
                    }));
                    return null;
                }
            }
        }

        /// <summary>
        /// 关闭PLC连接（在程序退出或需要重新连接时调用）
        /// </summary>
        public void ClosePLCConnection()
        {
            lock (_plcConnectionLock)
            {
                if (_plcConnection != null)
                {
                    try
                    {
                        _plcConnection.ConnectClose();
                        WriteDetailLog("[PLC连接] 已关闭长连接");
                    }
                    catch (Exception ex)
                    {
                        WriteDetailLog($"[PLC连接] 关闭连接异常: {ex.Message}");
                    }
                    finally
                    {
                        _plcConnection = null;
                        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DataModel.Settingmodel.PLCIsConnected = false;
                        }));
                    }
                }
            }
        }

        public void PLC_Start()
        {
            Thread t = new Thread(PLC_Process);
            t.IsBackground = true; // 设置为后台线程
            t.Start();

            //Thread t2 = new Thread(shakehand2);
            //t2.Start();
        }
        private void PLC_Process()
        {
            int lastTriggerValue_Base = 0;
            int lastTriggerValue_A1 = 0;
            int lastTriggerValue_A2 = 0;

            while (true)
            {
                // 逻辑：启动时立即连接PLC，PLC触发后再进行扫码、转换、校验等流
                // 使用连接复用：获取或创建PLC连接
                var modbusTcp = GetOrCreatePLCConnection();
                if (modbusTcp == null)
                {
                    // 连接失败，等待后继续
                    Thread.Sleep(100);
                    continue;
                }

                // 连接成功，开始轮询三个地址
                try
                {
                    WriteDetailLog($"[PLC轮询] 开始轮询三个地址");

                        #region 读取数据 - 多地址轮询模式
                        // 检查基础地址 - 全部检测触发
                        var resultBase = modbusTcp.ReadUInt16(DataModel.Settingmodel.AddressStart.ToString(), 1);
                        if (resultBase.IsSuccess)
                        {
                            int currentValue = resultBase.Content[0];
                            WriteDetailLog($"[PLC轮询] 基础地址{DataModel.Settingmodel.AddressStart}: {currentValue}");

                            if (currentValue > 0 && currentValue != lastTriggerValue_Base)
                            {
                                writeLog($"PLC触发: ALL指令 (地址{DataModel.Settingmodel.AddressStart}={currentValue})");
                                WriteDetailLog($"[PLC触发] 基础地址触发值变化: {lastTriggerValue_Base} -> {currentValue}");
                                // 先更新触发值，防止重复触发
                                lastTriggerValue_Base = currentValue;
                                ProcessPLCTrigger("ALL", DataModel.Settingmodel.AddressStart, currentValue, modbusTcp);
                            }
                            else
                            {
                                lastTriggerValue_Base = currentValue;
                            }
                        }
                        else
                        {
                            // 读取失败，可能连接已断开，标记连接无效以便下次重新连接
                            WriteDetailLog($"[PLC轮询] 基础地址读取失败: {resultBase.Message}");
                            lock (_plcConnectionLock)
                            {
                                if (_plcConnection != null)
                                {
                                    try { _plcConnection.ConnectClose(); } catch { }
                                    _plcConnection = null;
                                }
                            }
                            App.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DataModel.Settingmodel.PLCIsConnected = false;
                            }));
                        }

                        // 检查基础地址+1 - A1一阶段检测触发
                        var resultA1 = modbusTcp.ReadUInt16((DataModel.Settingmodel.AddressStart + 1).ToString(), 1);
                        if (resultA1.IsSuccess)
                        {
                            int currentValue = resultA1.Content[0];
                            WriteDetailLog($"[PLC轮询] A1地址{DataModel.Settingmodel.AddressStart + 1}: {currentValue}");

                            if (currentValue > 0 && currentValue != lastTriggerValue_A1)
                            {
                                writeLog($"PLC触发: A1指令 (地址{DataModel.Settingmodel.AddressStart + 1}={currentValue})");
                                WriteDetailLog($"[PLC触发] A1地址触发值变化: {lastTriggerValue_A1} -> {currentValue}");
                                // 先更新触发值，防止重复触发
                                lastTriggerValue_A1 = currentValue;
                                ProcessPLCTrigger("A1", DataModel.Settingmodel.AddressStart + 1, currentValue, modbusTcp);
                            }
                            else
                            {
                                lastTriggerValue_A1 = currentValue;
                            }
                        }

                        // 检查基础地址+2 - A2二阶段检测触发
                        var resultA2 = modbusTcp.ReadUInt16((DataModel.Settingmodel.AddressStart + 2).ToString(), 1);
                        if (resultA2.IsSuccess)
                        {
                            int currentValue = resultA2.Content[0];
                            WriteDetailLog($"[PLC轮询] A2地址{DataModel.Settingmodel.AddressStart + 2}: {currentValue}");

                            if (currentValue > 0 && currentValue != lastTriggerValue_A2)
                            {
                                writeLog($"PLC触发: A2指令 (地址{DataModel.Settingmodel.AddressStart + 2}={currentValue})");
                                WriteDetailLog($"[PLC触发] A2地址触发值变化: {lastTriggerValue_A2} -> {currentValue}");
                                // 先更新触发值，防止重复触发
                                lastTriggerValue_A2 = currentValue;
                                ProcessPLCTrigger("A2", DataModel.Settingmodel.AddressStart + 2, currentValue, modbusTcp);
                            }
                            else
                            {
                                lastTriggerValue_A2 = currentValue;
                            }
                        }
                    #endregion

                    // 不再关闭连接，保持长连接复用
                    // WriteDetailLog($"[PLC连接] 关闭连接"); // 已移除
                    // modbusTcp.ConnectClose(); // 已移除
                }
                catch (Exception ex)
                {
                    writeError($"PLC通讯异常: {ex.Message}");
                    WriteDetailLog($"[PLC异常] {ex.Message}\n{ex.StackTrace}");
                    
                    // 异常情况下关闭连接，下次重新建立
                    lock (_plcConnectionLock)
                    {
                        if (_plcConnection != null)
                        {
                            try { _plcConnection.ConnectClose(); } catch { }
                            _plcConnection = null;
                        }
                    }
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DataModel.Settingmodel.PLCIsConnected = false;
                    }));
                    
                    // 确保在通讯异常情况下也释放执行锁
                    lock (_executionLock)
                    {
                        _isExecuting = false;
                    }
                }
                Thread.Sleep(100);
            }

        }

        public bool GetSNList(string barcode)
        {
            try
            {
                string sql = $"SELECT [成品码] FROM [绑定完表格] WHERE  [物料名称]='容器码' and [条形码]= '{barcode}' ORDER BY [编号]";
                DataTable dt = Sqlserver.MicrosoftSql_DATABASE.Read(sql);
                //DataModel.Processmodel.SNList.Clear();
                var snList = new List<string>(dt.Rows.Count);
                for (int i = 0; i < dt.Rows.Count; i++)
                {
                    snList.Add(dt.Rows[i][0].ToString());
                }

                UIInvoke(() =>
                {
                    foreach (var sn in snList)
                    {
                        DataModel.Processmodel.SNList.Add(sn);
                    }
                });
                return true;

            }
            catch { return false; }
        }

        /// <summary>
        /// 处理扫码并集成MES服务
        /// 包括SN解析和工序校验
        /// </summary>
        /// <param name="scanCode">扫码内容</param>
        private async void ProcessScanCodeWithMES(string scanCode)
        {
            await ProcessScanCodeWithMESAsync(scanCode);
        }

        /// <summary>
        /// 处理扫码并集成MES服务（异步公共方法）
        /// 包括SN解析和工序校验
        /// </summary>
        /// <param name="scanCode">扫码内容</param>
        public async Task ProcessScanCodeWithMESAsync(string scanCode)
        {
            try
            {
                // 更新扫码状态为处理中
                UIInvoke(() => DataModel.Processmodel.ScanCodeStatus = "正在处理...");

                // 获取工序名称
                string procedureName = DataModel.Settingmodel.ReportWorkConfig.ProcedureName;

                // 关键链路：先获取最外层（最终绑定）SN，点检旁路和工单校验通过后再执行工序校验。
                var scanResult = await _mesService.ProcessScanCodeAsync(scanCode);

                // 处理扫码结果
                if (scanResult.Success)
                {
                    // SN获取成功，直接更新状态（不通过Dispatcher，确保同步）
                    // 若切换到新产品SN，则统一清理上一只产品的检测状态（根治A1清了但A2残留）
                    ResetForNewProduct(scanResult.SN);
                    UIInvoke(() =>
                    {
                        DataModel.Processmodel.CurrentProductSN = scanResult.SN;
                        DataModel.Processmodel.ScanCodeStatus = scanResult.StatusMessage;
                    });
                    // 日志输出使用BeginInvoke避免阻塞
                    UIBeginInvoke(new Action(() =>
                    {
                        writeLog($"扫码处理成功: SN={scanResult.SN}");
                    }));

                    // 检查是否是点检SN码（转换后的SN码）
                    string sn = scanResult.SN;
                    string okSN = DataModel.Settingmodel.ReportWorkConfig.OKInspectionSN;
                    string ngSN = DataModel.Settingmodel.ReportWorkConfig.NGInspectionSN;

                    if (sn == okSN || sn == ngSN)
                    {
                        // 进入点检模式
                        string type = sn == okSN ? "OK" : "NG";
                        UIInvoke(() =>
                        {
                            DataModel.Processmodel.IsInspectionMode = true;
                            DataModel.Processmodel.InspectionType = type;
                        });
                        
                        // 跳过工序校验（设置为点检模式状态）
                        UIInvoke(() => DataModel.Processmodel.ScanCodeStatus = $"点检模式-{type}点检");
                        UIBeginInvoke(new Action(() =>
                        {
                            writeLog($"⚠️ 检测到点检SN码，进入{type}点检模式", false);
                        }));
                        
                        // 点检模式下不需要进行工序校验，直接返回
                        return;
                    }

                    // 点检SN已经在上方进入点检流程；这里处理普通产品，点检到期时停在工单、工序和视觉检测之前。
                    string inspectionError = ValidateInspectionGateForProduction(scanResult.SN);
                    if (!string.IsNullOrEmpty(inspectionError))
                    {
                        UIInvoke(() => DataModel.Processmodel.ScanCodeStatus = "点检到期");
                        UIBeginInvoke(new Action(() =>
                        {
                            writeLog(inspectionError, true);
                            ShowInspectionWindow();
                        }));
                        return;
                    }

                    string workOrderError = ValidateScanWorkOrder(scanResult.SN);
                    if (!string.IsNullOrEmpty(workOrderError))
                    {
                        UIInvoke(() => DataModel.Processmodel.ScanCodeStatus = "工单校验失败");
                        UIBeginInvoke(new Action(() =>
                        {
                            writeLog(workOrderError, true);
                        }));
                        return;
                    }

                    var validationResult = await _mesService.ValidateProcessAsync(scanResult.SN, procedureName);

                    // 处理工序校验结果（正常流程）
                    if (validationResult != null)
                    {
                        // 直接更新状态（不通过Dispatcher，确保同步）
                        if (validationResult.Success)
                        {
                            UIInvoke(() => DataModel.Processmodel.ScanCodeStatus = validationResult.StatusMessage);
                            // 日志输出使用BeginInvoke避免阻塞
                            UIBeginInvoke(new Action(() =>
                            {
                                writeLog($"工序校验通过: SN={scanResult.SN}, 工序={procedureName}");
                            }));
                        }
                        else
                        {
                            UIInvoke(() => DataModel.Processmodel.ScanCodeStatus = validationResult.StatusMessage);
                            // 日志输出使用BeginInvoke避免阻塞
                            UIBeginInvoke(new Action(() =>
                            {
                                writeLog($"工序校验失败: {validationResult.ErrorMessage}");
                            }));
                        }
                    }
                }
                else
                {
                    // SN获取失败，直接更新状态
                    UIInvoke(() => DataModel.Processmodel.ScanCodeStatus = scanResult.StatusMessage);
                    // 日志输出使用BeginInvoke避免阻塞
                    UIBeginInvoke(new Action(() =>
                    {
                        writeLog($"扫码处理失败: {scanResult.ErrorMessage}");
                    }));
                }
            }
            catch (Exception ex)
            {
                // 直接更新状态
                UIInvoke(() => DataModel.Processmodel.ScanCodeStatus = "处理异常");
                // 日志输出使用BeginInvoke避免阻塞
                UIBeginInvoke(new Action(() =>
                {
                    writeLog($"扫码处理异常: {ex.Message}");
                }));
            }
        }

        /// <summary>
        /// 校验普通生产扫码的工单号与已保存工单号/批号是否一致。
        /// 点检SN在调用前已经进入点检旁路，该校验只影响普通产品进入工序校验和后续报工的资格。
        /// </summary>
        /// <param name="sn">MES解析后的产品SN，用于日志追溯。</param>
        /// <returns>校验通过返回空字符串；返回非空文本时停止普通扫码流程。</returns>
        private string ValidateScanWorkOrder(string sn)
        {
            string currentWorkOrder = (DataModel.Settingmodel.ReportWorkConfig.CurrentWorkOrderCode ?? string.Empty).Trim();
            string actualWorkOrder = string.Empty;

            if (string.IsNullOrEmpty(currentWorkOrder))
            {
                return $"[扫码工单校验失败] 当前工单号/批号为空，扫码工单={actualWorkOrder}, SN={sn}；已拦截扫码，未进入工序校验和报工流程。";
            }

            try
            {
                actualWorkOrder = (MES_ORACLE_DATABASE.MES_ORACLE_DATABASE.get_WO_CODE(sn) ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                return $"[扫码工单校验失败] 当前工单号/批号={currentWorkOrder}, 扫码工单查询异常={ex.Message}, SN={sn}；已拦截扫码，未进入工序校验和报工流程。";
            }

            if (string.IsNullOrEmpty(actualWorkOrder))
            {
                return $"[扫码工单校验失败] 当前工单号/批号={currentWorkOrder}, 扫码工单为空, SN={sn}；已拦截扫码，未进入工序校验和报工流程。";
            }

            if (!string.Equals(currentWorkOrder, actualWorkOrder, StringComparison.OrdinalIgnoreCase))
            {
                return $"[扫码工单校验失败] 当前工单号/批号={currentWorkOrder}, 扫码工单={actualWorkOrder}, SN={sn}；已拦截扫码，未进入工序校验和报工流程。";
            }

            return string.Empty;
        }

        /// <summary>
        /// 处理PLC触发信号
        /// </summary>
        /// <param name="command">指令名称，如"A1"、"A2"或"ALL"</param>
        /// <param name="address">触发地址</param>
        /// <param name="triggerValue">触发值</param>
        /// <param name="modbusTcp">ModbusTcp连接对象（连接复用）</param>
        private void ProcessPLCTrigger(string command, int address, int triggerValue, ModbusTcpNet modbusTcp)
        {
            try
            {
                // 立即复位触发信号（使用传入的连接对象，连接复用）
                WriteDetailLog($"[PLC触发] 准备复位地址{address}为0");
                var resetResult = modbusTcp.Write(address.ToString(), (UInt16)0);
                WriteDetailLog($"[PLC触发] 地址{address}复位结果={resetResult.IsSuccess}");

                // 检查是否正在执行，防止竞态条件
                lock (_executionLock)
                {
                    if (_isExecuting)
                    {
                        writeLog($"系统忙碌: 忽略{command}指令 (地址{address}={triggerValue})");
                        WriteDetailLog($"[业务逻辑] 系统正在执行中，忽略地址{address}触发值{triggerValue}");
                        return;
                    }
                    _isExecuting = true; // 设置执行状态
                    WriteDetailLog($"[业务逻辑] 设置执行状态为true，开始处理{command}指令");
                }

                writeLog($"开始执行: {command}指令 (地址{address}={triggerValue})");
                WriteDetailLog($"[PLC->视觉] 接收指令{command}(地址:{address}, 触发值:{triggerValue})");

                // 设置当前执行指令（复用现有RCMD机制）
                DataModel.Processmodel.RCMD = command;

                // 记录扫码前的SN，用于判断是否是新产品
                // 注意：这里使用_lastProductSN而不是CurrentProductSN（输入框绑定会提前改值，避免误判“SN未变化”）
                string previousSN = _lastProductSN;

                // 先触发扫码（按配置使用HF800或通用ScannerModel），并复用现有MES转换与校验逻辑
                try
                {
                    string scanCode = string.Empty;
                    bool scanSuccess = false;
                    
                    if (DataModel.Settingmodel.ScannerMode == "HF800")
                    {
                        var r = DataModel.Settingmodel.HF800.Scanner();
                        scanCode = (r?.value ?? string.Empty).Replace("\r", "").Replace("\n", "");
                        UIInvoke(() =>
                        {
                            DataModel.Processmodel.Scannerstr = scanCode;
                            DataModel.Processmodel.SNList.Clear();
                            DataModel.Processmodel.Barcodes = string.Empty;
                        });
                        scanSuccess = (r != null && r.Status == Status.OK);
                    }
                    else
                    {
                        var r = DataModel.Settingmodel.ScannerModel.Scanner();
                        scanCode = (r?.receivestring ?? string.Empty).Replace("\r", "").Replace("\n", "");
                        UIInvoke(() => DataModel.Processmodel.Scannerstr = scanCode);
                        scanSuccess = (r != null && r.IsSuccess);
                    }

                    if (scanSuccess && !string.IsNullOrEmpty(scanCode))
                    {
                        // 立即更新产品SN输入框显示原始编码
                        UIInvoke(() => DataModel.Processmodel.CurrentProductSN = scanCode);

                        // 调用现有异步流程（编码转换为SN并工序校验）
                        // 使用超时机制避免死锁，在后台线程执行异步操作
                        WriteDetailLog($"[PLC触发] 开始转换编码为SN: {scanCode}");
                        try
                        {
                            // 在后台线程执行，使用超时机制（10秒）
                            var conversionTask = Task.Run(async () =>
                            {
                                await ProcessScanCodeWithMESAsync(scanCode).ConfigureAwait(false);
                            });
                            
                            // 等待转换完成，最多等待10秒
                            if (conversionTask.Wait(TimeSpan.FromSeconds(10)))
                            {
                                // 转换完成，等待一小段时间确保状态已完全同步（PropertyChanged事件已触发）
                                Thread.Sleep(50);
                                WriteDetailLog($"[PLC触发] 编码转换和校验完成，当前SN: {DataModel.Processmodel.CurrentProductSN}, 校验状态: {DataModel.Processmodel.ScanCodeStatus}");
                                
                                // 判断SN是否变化，如果是新产品，清除所有工具状态和阶段状态
                                string currentSN = DataModel.Processmodel.CurrentProductSN;
                                if (currentSN != previousSN)
                                {
                                    WriteDetailLog($"[PLC触发] 检测到新产品SN变化: {previousSN} -> {currentSN}，清除所有状态");
                                    writeLog($"新产品SN: {currentSN}，重置检测状态");
                                    
                                    // 在UI线程清除所有工具状态和阶段状态
                                    // 新产品：统一复位工具/阶段/报工提示等状态
                                    ResetForNewProduct(currentSN);
                                }
                                else
                                {
                                    WriteDetailLog($"[PLC触发] SN未变化({currentSN})，保持现有检测状态");
                                }
                            }
                            else
                            {
                                // 超时处理
                                writeLog($"编码转换超时（10秒），原始编码: {scanCode}，继续检查后续流程");
                                UIInvoke(() => DataModel.Processmodel.ScanCodeStatus = "转换超时");
                            }
                        }
                        catch (Exception ex)
                        {
                            writeLog($"编码转换异常: {ex.Message}，继续执行后续流程");
                            UIInvoke(() => DataModel.Processmodel.ScanCodeStatus = "转换异常");
                        }
                    }
                    else
                    {
                        writeLog($"PLC触发扫码失败或空码，跳过SN更新");
                    }
                }
                catch (Exception ex)
                {
                    writeLog($"PLC触发扫码异常,请检查配置数据.xml或设备连接，具体错误: {ex.Message}");
                }

                // 检查报工模块状态，确保有SN且工序校验通过
                if (!HasValidSNAndValidation())
                {
                    writeLog($"报工状态不满足: 跳过{command}指令 (SN={DataModel.Processmodel.CurrentProductSN ?? "空"}, 状态={DataModel.Processmodel.ScanCodeStatus})");
                    WriteDetailLog($"[业务逻辑] 报工模块状态不满足，跳过{command}指令执行 (SN={DataModel.Processmodel.CurrentProductSN ?? "空"}, 状态={DataModel.Processmodel.ScanCodeStatus})");
                    // 注意：这里需要在finally中释放_isExecuting锁
                    return;
                }

                writeLog($"报工验证通过: 开始{command}视觉检测");
                WriteDetailLog($"[业务逻辑] 报工状态验证通过，开始执行{command}指令");

                // 直接执行视觉检测，不进行扫码（机器人通信已禁用）
                SendMsgRobot("DOK");
                // 只清除当前指令对应的工具，避免清除其他指令（如A1/A2）的检测结果
                ClearToolsByCommand(command);
                WriteDetailLog($"[视觉检测] 开始执行{command}指令的视觉检测");
                ExecuteToolsByCommand(command);

                // 更新触发状态显示
                WriteDetailLog($"[界面更新] 更新界面触发标志从{DataModel.Processmodel.Trig_IO.IOstatus}到{triggerValue}");
                DataModel.Processmodel.Trig_IO.IOstatus = triggerValue;
            }
            catch (Exception ex)
            {
                writeError($"PLC触发处理异常: {ex.Message}");
                WriteDetailLog($"[PLC触发] 处理异常: {ex.Message}");
            }
            finally
            {
                // 确保释放执行锁
                lock (_executionLock)
                {
                    _isExecuting = false;
                    WriteDetailLog($"[业务逻辑] 释放执行锁，{command}指令处理完成");
                }
            }
        }

        /// <summary>
        /// 按指令执行匹配的工具（PLC指令匹配模式）
        /// </summary>
        /// <param name="command">指令名称，如"A1"、"A2"或"ALL"</param>
        private void ExecuteToolsByCommand(string command)
        {
            try
            {
                WriteDetailLog($"[工具执行] 开始执行指令{command}，总工具数={DataModel.Processmodel.Tools.Count}");

                // 检查是否可以开始检测（只检查是否在报工中）
                if (!CanStartDetection())
                {
                    writeLog($"检测被阻止: 报工正在进行中");
                    WriteDetailLog($"[工具执行] 检测被阻止，原因：报工正在进行中");
                    // 注释掉PLC返回，不向PLC返回OK/NG标志
                    // PLC_write((UInt16)2); // 返回NG状态
                    return;
                }

                // 收集所有匹配的工具
                List<int> matchedToolIndices = new List<int>();

                // 查找匹配的工具
                WriteDetailLog($"[工具匹配] 开始查找匹配指令{command}的工具");

                // 详细诊断工具配置
                for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
                {
                    var tool = DataModel.Processmodel.Tools[i];
                    WriteDetailLog($"[工具诊断] 工具{i}: Name='{tool.Name}', Command='{tool.Command}', Index={tool.Index}, CameraSerial='{tool.CameraSerialNumber}'");
                }

                // 第一遍：收集所有匹配的工具
                for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
                {
                    bool isMatch = false;

                    if (command == "ALL")
                    {
                        // ALL指令匹配所有工具（向后兼容）
                        isMatch = true;
                        WriteDetailLog($"[工具匹配] 工具{i}({DataModel.Processmodel.Tools[i].Name}): ALL指令匹配");
                    }
                    else
                    {
                        // 按Command字段匹配
                        isMatch = DataModel.Processmodel.Tools[i].Command == command;
                        WriteDetailLog($"[工具匹配] 工具{i}({DataModel.Processmodel.Tools[i].Name}): Command={DataModel.Processmodel.Tools[i].Command}, 匹配={isMatch}");
                    }

                    if (isMatch)
                    {
                        matchedToolIndices.Add(i);
                        writeLog($"找到匹配工具: {DataModel.Processmodel.Tools[i].Name}");
                        WriteDetailLog($"[工具执行] 找到匹配工具: 工具{i}({DataModel.Processmodel.Tools[i].Name})");
                    }
                }

                // 如果找到匹配的工具，执行它们
                if (matchedToolIndices.Count > 0)
                {
                    WriteDetailLog($"[工具执行] 共找到{matchedToolIndices.Count}个匹配工具");

                    // 清理状态（只清理当前指令对应的工具，避免清除其他指令的结果）
                    WriteDetailLog($"[工具执行] 清理指令{command}对应的工具状态");
                    ClearToolsByCommand(command);

                    // 使用第一个工具的相机和曝光设置（同一指令的所有工具共用）
                    int firstToolIndex = matchedToolIndices[0];
                    var firstTool = DataModel.Processmodel.Tools[firstToolIndex];

                    WriteDetailLog($"[工具执行] 使用工具{firstToolIndex}({firstTool.Name})的相机设置");

                    // 设置工具索引为第一个工具（相机回调会处理所有匹配的工具）
                    DataModel.Processmodel.ToolIndex = firstToolIndex + 1;

                    // 检查是否需要切换相机
                    WriteDetailLog($"[相机切换] 检查相机设置: {firstTool.CameraSerialNumber}");
                    SwitchCameraForTool(firstTool);

                    // 等待相机切换完成
                    Thread.Sleep(100);

                    // 设置曝光参数
                    //WriteDetailLog($"[相机设置] 设置曝光时间={firstTool.ExposureTime}");
                    DataModel.Settingmodel.camedata.CameraModel.exposuretime = firstTool.ExposureTime;
                    DataModel.Settingmodel.camedata.CameraModel.camera.Exposure = firstTool.ExposureTime;
                    DataModel.Settingmodel.camedata.CameraModel.camera.bnSetParam_Click();

                    //WriteDetailLog($"[相机设置] 曝光参数设置完成，等待{DataModel.Settingmodel.delaytime}ms");
                    Thread.Sleep(DataModel.Settingmodel.delaytime);

                    // 只触发一次拍照，相机回调会处理所有匹配的工具
                    string toolNames = string.Join(", ", matchedToolIndices.Select(idx => DataModel.Processmodel.Tools[idx].Name));
                    writeLog($"开始拍照: {command}指令 - 共{matchedToolIndices.Count}个工具({toolNames})");
                    //WriteDetailLog($"[相机触发] {command}指令触发拍照一次，{matchedToolIndices.Count}个工具将使用同一张图片");

                    // 触发拍照（只触发一次）
                    DataModel.Settingmodel.camedata.CameraModel.camera.bnTriggerExec_Click();
                    WriteDetailLog($"[相机触发] 拍照触发完成，等待相机回调处理");
                }

                if (matchedToolIndices.Count == 0)
                {
                    writeLog($"未找到匹配工具: {command}指令");
                    WriteDetailLog($"[工具执行] 未找到匹配指令{command}的工具");
                }
                else
                {
                    writeLog($"工具匹配完成: {command}指令 (共{matchedToolIndices.Count}个工具)");
                    WriteDetailLog($"[工具执行] 指令{command}执行完成，共匹配{matchedToolIndices.Count}个工具，图像处理将在相机回调中执行");
                }

                // 注释掉PLC返回逻辑，不向PLC返回OK/NG标志
                // 检测结果将通过软件界面显示，而不是返回给PLC
            }
            catch (Exception ex)
            {
                writeError($"工具执行异常: {command}指令 - {ex.Message}");
                WriteDetailLog($"[工具执行] 执行指令{command}时发生错误:{ex.Message}");
            }
        }

        private bool PLC_write(UInt16 result)
        {
            writeLog($"视觉->PLC:{result}、{(result == 1 ? "OK" : "NG")}", false);

            // 使用连接复用机制，最多重试4次
            int retryCount = 0;
            while (retryCount < 4)
            {
                try
                {
                    // 获取或创建PLC连接（连接复用）
                    var modbusTcp = GetOrCreatePLCConnection();
                    if (modbusTcp != null)
                    {
                        var writeResult = modbusTcp.Write((DataModel.Settingmodel.AddressStart + 1).ToString(), (UInt16)result);
                        if (writeResult.IsSuccess)
                        {
                            WriteDetailLog($"[PLC写入] 写入结果成功: {result} ({(result == 1 ? "OK" : "NG")})");
                            return true;
                        }
                        else
                        {
                            WriteDetailLog($"[PLC写入] 写入失败: {writeResult.Message}，重试 {retryCount + 1}/4");
                            // 写入失败，可能连接已断开，标记连接无效
                            lock (_plcConnectionLock)
                            {
                                if (_plcConnection != null)
                                {
                                    try { _plcConnection.ConnectClose(); } catch { }
                                    _plcConnection = null;
                                }
                            }
                            App.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DataModel.Settingmodel.PLCIsConnected = false;
                            }));
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteDetailLog($"[PLC写入] 写入异常: {ex.Message}，重试 {retryCount + 1}/4");
                    // 异常情况下关闭连接
                    lock (_plcConnectionLock)
                    {
                        if (_plcConnection != null)
                        {
                            try { _plcConnection.ConnectClose(); } catch { }
                            _plcConnection = null;
                        }
                    }
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DataModel.Settingmodel.PLCIsConnected = false;
                    }));
                }

                retryCount++;
                if (retryCount < 4)
                {
                    Thread.Sleep(100); // 重试前等待100ms
                }
            }

            WriteDetailLog("[PLC写入] 写入失败，已达到最大重试次数");
            return false;
        }

        #endregion
        #region 清空测试工具状态

        public void ClearToolStatus()
        {
            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                try
                {
                    var t = DataModel.Processmodel.Tools[i];
                    t.ActualDimension = 0;
                    t.ActualScore = 0;
                    t.ActualX = 0;
                    t.ActualY = 0;
                    t.BarcodeStr = "";
                    t.ToolStatus = ToolStatus.等待中;
                }
                catch
                {
                    continue;
                }
            }
            DataModel.Processmodel.Status = ToolStatus.等待中;
        }
        #endregion


        //#region PC
        //public bool ConnectPC()
        //{
        //    bool r = DataModel.Settingmodel.TcpClientH.Connect(DataModel.Settingmodel.SoftConnect.RemoteIP, DataModel.Settingmodel.SoftConnect.RemotePort);
        //    DataModel.Settingmodel.SoftConnect.IsConnected = r;
        //    return r;
        //}

        //public void DisconnectPC()
        //{
        //    DataModel.Settingmodel.TcpClientH.DisConnect();
        //}

        //public bool SendMsgPC(string cmd)
        //{
        //    bool r = DataModel.Settingmodel.TcpClientH.SendMsg(cmd);
        //    writeLog($"视觉->软件:{cmd}");
        //    return r;

        //}

        //#endregion


        #region 通用数据日志
        private object writeLog_Locker = new object();

        private object writeBug_Locker = new object();

        internal void writeLog(string LogContent, bool showdatarecord = true)
        {
            lock (writeLog_Locker)
            {
                try
                {
                    string logstr = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.FFF")}]{LogContent}";
                    if (showdatarecord)
                    {
                        // 检测是否在UI线程，避免死锁
                        // 如果在UI线程，使用Invoke；如果在后台线程，使用BeginInvoke
                        if (App.Current.Dispatcher.CheckAccess())
                        {
                            // 当前在UI线程，直接执行
                            int num = 200;
                            if (DataModel.Recordmodel.workLog.Count > num)
                            {
                                //DataModel.Recordmodel.workLog.RemoveAt(DataModel.Recordmodel.workLog.Count - 1);
                                DataModel.Recordmodel.workLog.Clear();
                            }
                            DataModel.Recordmodel.workLog.Insert(0, logstr);
                        }
                        else
                        {
                            // 当前在后台线程，使用BeginInvoke避免死锁
                            App.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                int num = 200;
                                if (DataModel.Recordmodel.workLog.Count > num)
                                {
                                    //DataModel.Recordmodel.workLog.RemoveAt(DataModel.Recordmodel.workLog.Count - 1);
                                    DataModel.Recordmodel.workLog.Clear();
                                }
                                DataModel.Recordmodel.workLog.Insert(0, logstr);
                            }));
                        }
                    }

                    string filename = $"{Environment.CurrentDirectory}\\日志\\日志\\{DateTime.Now.ToString("yyyyMMdd")}.txt";
                    string dir = Path.GetDirectoryName(filename);
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    using (StreamWriter sw = new StreamWriter(filename, true))
                    {
                        sw.WriteLine(logstr);
                        sw.Close();
                    }
                }
                catch (Exception ex)
                {
                    writeError(ex.Message + Environment.NewLine + ex.StackTrace);
                }
            }
        }

        internal void writeError(string Content)
        {
            lock (writeBug_Locker)
            {
                try
                {
                    int num = 200;
                    string bugstr = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.FFF")}]{Content}";
                    // 检测是否在UI线程，避免死锁
                    // 如果在UI线程，使用Invoke；如果在后台线程，使用BeginInvoke
                    if (App.Current.Dispatcher.CheckAccess())
                    {
                        // 当前在UI线程，直接执行
                        if (DataModel.Recordmodel.ErrorLog.Count > num)
                        {
                            //DataModel.Recordmodel.workLog.RemoveAt(DataModel.Recordmodel.workLog.Count - 1);
                            DataModel.Recordmodel.ErrorLog.Clear();
                        }
                        DataModel.Recordmodel.ErrorLog.Insert(0, bugstr);
                    }
                    else
                    {
                        // 当前在后台线程，使用BeginInvoke避免死锁
                        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (DataModel.Recordmodel.ErrorLog.Count > num)
                            {
                                //DataModel.Recordmodel.workLog.RemoveAt(DataModel.Recordmodel.workLog.Count - 1);
                                DataModel.Recordmodel.ErrorLog.Clear();
                            }
                            DataModel.Recordmodel.ErrorLog.Insert(0, bugstr);
                        }));
                    }
                    string filename = $"{Environment.CurrentDirectory}\\日志\\错误\\{DateTime.Now.ToString("yyyyMMdd")}.txt";
                    string dir = Path.GetDirectoryName(filename);
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    using (StreamWriter sw = new StreamWriter(filename, true))
                    {
                        sw.WriteLine(bugstr);
                        sw.Close();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 写入详细调试日志到文件，不显示在界面上，避免界面日志过于冗余
        /// </summary>
        /// <param name="LogContent">日志内容</param>
        private object writeDetailLog_Locker = new object();
        internal void WriteDetailLog(string LogContent)
        {
            lock (writeDetailLog_Locker)
            {
                try
                {
                    string logstr = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.FFF")}]{LogContent}";
                    string filename = $"{Environment.CurrentDirectory}\\日志\\详细调试\\{DateTime.Now.ToString("yyyyMMdd")}.txt";
                    string dir = Path.GetDirectoryName(filename);
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    using (StreamWriter sw = new StreamWriter(filename, true))
                    {
                        sw.WriteLine(logstr);
                        sw.Close();
                    }
                }
                catch (Exception ex)
                {
                    // 详细日志写入失败时，记录到错误日志
                    writeError($"详细日志写入失败: {ex.Message}");
                }
            }
        }
        #endregion
        #endregion

        #region 界面menuitem
        public RelayCommand NEW_PRJCMD { set; get; } = null;

        public void NEW_PRJCMD_process()
        {
            try
            {
                if (DataModel.Settingmodel.permission)
                {
                    if (MessageBoxX.Show("是否确定新建工程?", "提示", MessageBoxButton.YesNo, MessageBoxIcon.Question, DefaultButton.NoCancel) == MessageBoxResult.Yes)
                    {
                        newPrj newPrj = new newPrj();
                        if (newPrj.ShowDialog() == true)
                        {
                            string prjname = newPrj.Prj_Name;
                            string prjdir = $"{DataModel.Settingmodel.Prjdir}\\{prjname}";
                            if (Directory.Exists(prjdir))
                            {
                                if (MessageBoxX.Show("工程已经存在，是否删除旧工程?", "提示", MessageBoxButton.YesNo, MessageBoxIcon.Question, DefaultButton.NoCancel) == MessageBoxResult.Yes)
                                {
                                    Directory.Delete(prjdir, true);
                                }
                                else
                                {
                                    return;
                                }
                            }
                            else
                            {
                                DataModel.Settingmodel.Prjs.Add(prjname);
                                DataModel.Settingmodel.prjselected = DataModel.Settingmodel.Prjs.Count - 1;
                            }

                            #region 新建工程
                            Directory.CreateDirectory(prjdir);
                            DataModel.Settingmodel.Name = prjname;
                            DataModel.Processmodel.Tools.Clear();
                            //ClearToolStatus();
                            #endregion

                        }
                    }

                }
            }
            catch (Exception ex)
            {
                NoticeBox.Show($"新建工程失败\r\n{ex.Message}", "错误", MessageBoxIcon.Error, true, 5000);
            }
        }


        public RelayCommand SAVE_PRJCMD { set; get; } = null;
        public void SAVE_PRJ_process()
        {
            //SaveAPPXml();
            SaveProcessmodel();
            SaveSettingModel();
            SavePrjXmls();
            NoticeBox.Show($"工程保存完成", "成功", MessageBoxIcon.Success, true, 5000);

        }


        public RelayCommand RELOAD_PRJCMD { set; get; } = null;
        public void RELOAD_PRJ_process()
        {

        }


        #endregion

        #region Command
        public RelayCommand DeleteToolCMD { set; get; } = null;
        public RelayCommand AddToolCMD { set; get; } = null;
        public RelayCommand ClearToolCMD { set; get; } = null;
        public RelayCommand CopyToolCMD { set; get; } = null;
        public RelayCommand InsertToolCMD { set; get; } = null;


        public void DeleteTool()
        {
            int index = DataModel.Processmodel.selectedindex;
            DeleteTool(index);

        }
        public void DeleteTool(int index)
        {
            try
            {
                if (index < 0)
                {

                    NoticeBox.Show($"请先选择需要删除的工具", "失败", MessageBoxIcon.Error, true, 5000);
                    return;
                }
                if (MessageBoxX.Show("是否确定删除工具？", "提示", System.Windows.MessageBoxButton.YesNo, MessageBoxIcon.Question, DefaultButton.NoCancel) == System.Windows.MessageBoxResult.Yes)
                {

                    string jpgsrc1 = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{index + 1}.jpg";
                    string xmlsrc1 = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{index + 1}.xml";
                    string shmsrc1 = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{index + 1}.shm";

                    DeleteFile(jpgsrc1);
                    DeleteFile(xmlsrc1);
                    DeleteFile(shmsrc1);

                    if (index != DataModel.Processmodel.Tools.Count - 1)
                    {

                        for (int i = index; i < DataModel.Processmodel.Tools.Count - 1; i++)
                        {
                            string jpgsrc = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 1}.jpg";
                            string xmlsrc = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 1}.xml";
                            string shmsrc = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 1}.shm";

                            string jpgdst = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 2}.jpg";
                            string xmldst = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 2}.xml";
                            string shmdst = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 2}.shm";

                            MoveFile(jpgdst, jpgsrc);
                            MoveFile(xmldst, xmlsrc);
                            MoveFile(shmdst, shmsrc);


                        }
                    }
                    if (index >= 0)
                    {
                        DataModel.Processmodel.Tools.RemoveAt(index);
                    }
                    autoindex();
                }
            }
            catch (Exception ex)
            {
            }
        }


        public void DeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }




        public void AddTool()
        {
            int index = DataModel.Processmodel.Tools.Count;
            DataModel.Processmodel.selectedindex = 0;
            InsertTool(index);

        }
        public void InsertTool(int index, ToolModel tool = null)
        {
            try
            {

                if (DataModel.Processmodel.selectedindex < 0)
                {

                    NoticeBox.Show($"请先选择需要插入工具的位置", "失败", MessageBoxIcon.Error, true, 5000);
                    return;
                }

                for (int i = DataModel.Processmodel.Tools.Count - 1; i >= index; i--)
                {
                    string jpgsrc = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 1}.jpg";
                    string xmlsrc = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 1}.xml";
                    string shmsrc = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 1}.shm";

                    string jpgdst = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 2}.jpg";
                    string xmldst = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 2}.xml";
                    string shmdst = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 2}.shm";

                    MoveFile(jpgsrc, jpgdst);
                    MoveFile(xmlsrc, xmldst);
                    MoveFile(shmsrc, shmdst);


                }

                if (tool == null)
                {
                    tool = new ToolModel();
                }
                DataModel.Processmodel.Tools.Insert(index, tool);
                DataModel.Processmodel.selectedindex = index;
                autoindex();

            }
            catch (Exception ex) { }
        }

        public void MoveFile(string srcfile, string dstfilename)
        {
            if (File.Exists(srcfile))
            {
                File.Move(srcfile, dstfilename);
            }
        }


        public void ClearTool()
        {
            try
            {
                if (MessageBoxX.Show("是否确定清空所有工具？", "提示", System.Windows.MessageBoxButton.YesNo, MessageBoxIcon.Question, DefaultButton.NoCancel) == System.Windows.MessageBoxResult.Yes)
                {
                    DataModel.Processmodel.Tools.Clear();
                    #region 删除对应目录下面的工具文件
                    string dir = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}";
                    var files = Directory.GetFiles(dir);
                    for (int i = 0; i < files.Length; i++)
                    {
                        try
                        {
                            //if (files[i].ToUpper().EndsWith(".XML"))
                            //{
                            //    File.Delete(files[i]);
                            //}

                            File.Delete(files[i]);
                        }
                        catch (Exception ex) { continue; }
                        #endregion
                    }
                }
            }
            catch (Exception ex) {; }
        }
        public void CopyTool()
        {
            try
            {
                if (DataModel.Processmodel.selectedindex < 0)
                {

                    NoticeBox.Show($"请先选择需要复制的工具", "失败", MessageBoxIcon.Error, true, 5000);
                    return;
                }

                if (MessageBoxX.Show("是否确定复制选中的工具？", "提示", System.Windows.MessageBoxButton.YesNo, MessageBoxIcon.Question, DefaultButton.NoCancel) == System.Windows.MessageBoxResult.Yes)
                {

                    CopyTool(DataModel.Processmodel.selectedindex + 1, New_Tool_Model(DataModel.Processmodel.Tools[DataModel.Processmodel.selectedindex]));



                    autoindex();
                }
            }
            catch (Exception ex) { }
        }
        public void CopyTool(int index, ToolModel tool)
        {
            try
            {

                if (DataModel.Processmodel.selectedindex < 0)
                {

                    NoticeBox.Show($"请先选择需要插入工具的位置", "失败", MessageBoxIcon.Error, true, 5000);
                    return;
                }

                for (int i = DataModel.Processmodel.Tools.Count - 1; i >= index - 1; i--)
                {
                    string jpgsrc = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 1}.jpg";
                    string xmlsrc = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 1}.xml";
                    string shmsrc = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 1}.shm";

                    string jpgdst = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 2}.jpg";
                    string xmldst = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 2}.xml";
                    string shmdst = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{i + 2}.shm";

                    CopyFile(jpgsrc, jpgdst);
                    CopyFile(xmlsrc, xmldst);
                    CopyFile(shmsrc, shmdst);


                }

                DataModel.Processmodel.Tools.Insert(index, tool);
                DataModel.Processmodel.selectedindex = index;
                autoindex();

            }
            catch (Exception ex) { }
        }

        public void CopyFile(string srcfile, string dstfilename)
        {
            if (File.Exists(srcfile))
            {
                File.Copy(srcfile, dstfilename, true);
            }
        }

        public ROI New_ROI(ROI roi)
        {
            ROI r = new ROI();
            r.Row1 = roi.Row1;
            r.Row2 = roi.Row2;
            r.Col1 = roi.Col1;
            r.Col2 = roi.Col2;
            return r;

        }

        public ToolModel New_Tool_Model(ToolModel tool)
        {
            ToolModel t = new ToolModel();
            t.Index = tool.Index;
            t.Command = tool.Command;
            t.Name = tool.Name;
            //t.DecodeBarcode = tool.DecodeBarcode;
            t.BarcodeStr = tool.BarcodeStr;
            t.BarCodeROI = New_ROI(tool.BarCodeROI);
            //t.PositionDetect = tool.PositionDetect;
            t.ModelFileName = tool.ModelFileName;
            t.MinScore = tool.MinScore;
            t.ActualScore = tool.ActualScore;
            t.K = tool.K;
            t.InitX = tool.InitX;
            t.InitY = tool.InitY;
            t.ActualX = tool.ActualX;
            t.ActualY = tool.ActualY;
            t.ActualAngle = tool.ActualAngle;
            t.AllowAngleDelta = tool.AllowAngleDelta;
            t.DeltaX = tool.DeltaX;
            t.DeltaY = tool.DeltaY;
            //t.DirectX = tool.DirectX;
            //t.DirectY = tool.DirectY;
            //t.InvertXY = tool.InvertXY;
            t.SendStatus = tool.SendStatus;
            //t.SendBarcodeData = tool.SendBarcodeData;
            //t.SendPositionData = tool.SendPositionData;
            //t.InvertResult = tool.InvertResult;
            t.Allow_X_Delta = tool.Allow_X_Delta;
            t.Allow_Y_Delta = tool.Allow_Y_Delta;
            //t.StatusColor = tool.StatusColor;
            t.PositionROI = New_ROI(tool.PositionROI);

            t.DimensionROI = New_ROI(tool.DimensionROI);
            //t.DimensionDetect = tool.DimensionDetect;
            t.ActualDimension = tool.ActualDimension;
            t.MinDimension = tool.MinDimension;
            t.MaxDimension = tool.MaxDimension;
            t.GrayMode = tool.GrayMode;
            t.RedChannelEnabled = tool.RedChannelEnabled;
            t.GreenChannelEnabled = tool.GreenChannelEnabled;
            t.BlueChannelEnabled = tool.BlueChannelEnabled;
            t.MinGray = tool.MinGray;
            t.MaxGray = tool.MaxGray;
            t.MinRed = tool.MinRed;
            t.MaxRed = tool.MaxRed;
            t.MinGreen = tool.MinGreen;
            t.MaxGreen = tool.MaxGreen;
            t.MinBlue = tool.MinBlue;
            t.MaxBlue = tool.MaxBlue;
            t.MinAreaFilter = tool.MinAreaFilter;
            t.MaxAreaFilter = tool.MaxAreaFilter;
            t.BitmapSource = null;
            t.CurrentBitmapSource = null;
            t.BitmapSource = tool.BitmapSource.Clone();
            t.CurrentBitmapSource = tool.CurrentBitmapSource.Clone();
            t.CurrentBitmapFileName = tool.CurrentBitmapFileName;
            t.Image = tool.Image.Clone();



            return t;
        }
        public void InsertTool()
        {
            int index = DataModel.Processmodel.selectedindex;
            InsertTool(index);
        }

        public string autoindex()
        {
            string error = string.Empty;
            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                int initindex = DataModel.Processmodel.Tools[i].Index;
                int newindex = i + 1;

                if (initindex != newindex)
                {
                    DataModel.Processmodel.Tools[i].Index = i + 1;

                    string s1 = ChangeToolIndex(initindex, newindex, "xml");
                    string s2 = ChangeToolIndex(initindex, newindex, "shm");
                    string s3 = ChangeToolIndex(initindex, newindex, "jpg");

                    if (!string.IsNullOrEmpty(s1))
                    {
                        error += $"工具{initindex}修改序号错误:{s1}";
                    }
                    if (!string.IsNullOrEmpty(s2))
                    {
                        error += $"工具{initindex}修改序号错误:{s2}";
                    }
                    if (!string.IsNullOrEmpty(s3))
                    {
                        error += $"工具{initindex}修改序号错误:{s3}";
                    }
                }
            }
            return error;
        }

        public string ChangeToolIndex(int InitIndex, int NewIndex, string type)
        {
            try
            {
                string initfilename = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{InitIndex}.{type}";
                string newfilename = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{NewIndex}.{type}";

                if (File.Exists(initfilename))
                {
                    File.Move(initfilename, newfilename);
                }
                return string.Empty;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        #endregion

        #region 函数


        public void Load_Prj()
        {
            LoadPrjXmls();
            CheckPrj();
            Prjs_selectedindex();
            //ClearToolStatus();
        }
        public void Load_Prj(string PrjName)
        {
            DataModel.Settingmodel.Name = PrjName;
            Load_Prj();

        }

        /// <summary>
        /// 工程校验
        /// </summary>
        public void CheckPrj()
        {

            try
            {
                bool checkprj = false;
                DataModel.Settingmodel.Prjs.Clear();
                if (!Directory.Exists(DataModel.Settingmodel.Prjdir))
                {
                    Directory.CreateDirectory(DataModel.Settingmodel.Prjdir);
                }
                string[] dirs = Directory.GetDirectories(DataModel.Settingmodel.Prjdir);

                for (int i = 0; i < dirs.Length; i++)
                {
                    DirectoryInfo di = new DirectoryInfo(dirs[i]);
                    DataModel.Settingmodel.Prjs.Add(di.Name);
                    if (di.Name.ToUpper() == DataModel.Settingmodel.Name.ToUpper())
                    {
                        checkprj = true;
                        //break;
                    }
                }

                if (checkprj)
                {
                    NoticeBox.Show($"工程校验成功:{DataModel.Settingmodel.Name}", "成功", MessageBoxIcon.Success, true, 5000);
                }
                else
                {
                    NoticeBox.Show($"工程校验失败:{DataModel.Settingmodel.Name}", "失败", MessageBoxIcon.Error, true, 5000);
                }
            }
            catch (Exception ex)
            {
                NoticeBox.Show($"工程目录加载失败:{ex.Message}", "失败", MessageBoxIcon.Error, true, 5000);

            }
        }

        /// <summary>
        /// 刷新工程选择项
        /// </summary>
        public void Prjs_selectedindex()
        {
            for (int i = 0; i < DataModel.Settingmodel.Prjs.Count; i++)
            {
                if (DataModel.Settingmodel.Prjs[i] == DataModel.Settingmodel.Name)
                {
                    DataModel.Settingmodel.prjselected = i;
                    break;
                }
            }
        }
        #endregion

        #region 数据保存加载

        #region 过程数据
        public void SaveProcessmodel()
        {

            string filename = $"{Environment.CurrentDirectory}\\配置\\过程数据.xml";
            string dir = Path.GetDirectoryName(filename);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var stream = File.Open(filename, FileMode.Create))
            {
                var serializer = new XmlSerializer(typeof(Processmodel));
                serializer.Serialize(stream, DataModel.Processmodel);
            }
        }
        public void LoadProcessmodel()
        {
            try
            {
                string filename = $"{Environment.CurrentDirectory}\\配置\\过程数据.xml";
                string dir = Path.GetDirectoryName(filename);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                if (File.Exists(filename))
                {
                    using (var stream = File.OpenRead(filename))
                    {
                        var serializer = new XmlSerializer(typeof(Processmodel));
                        DataModel.Processmodel = serializer.Deserialize(stream) as Processmodel;
                    }
                }
                else
                {
                    DataModel.Processmodel = new Processmodel();
                }
            }
            catch (Exception ex)
            {
                DataModel.Processmodel = new Processmodel();

            }
        }
        #endregion

        #region 配置数据
        public void SaveSettingModel()
        {

            string filename = $"{Environment.CurrentDirectory}\\配置\\配置数据.xml";
            string dir = Path.GetDirectoryName(filename);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var stream = File.Open(filename, FileMode.Create))
            {
                var serializer = new XmlSerializer(typeof(SettingModel));
                serializer.Serialize(stream, DataModel.Settingmodel);
            }
        }
        public void LoadSettingModel()
        {
            try
            {
                string filename = $"{Environment.CurrentDirectory}\\配置\\配置数据.xml";
                string dir = Path.GetDirectoryName(filename);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                if (File.Exists(filename))
                {
                    using (var stream = File.OpenRead(filename))
                    {
                        var serializer = new XmlSerializer(typeof(SettingModel));
                        DataModel.Settingmodel = serializer.Deserialize(stream) as SettingModel;
                    }
                }
                else
                {
                    DataModel.Settingmodel = new SettingModel();
                }
            }
            catch (Exception ex)
            {
                DataModel.Settingmodel = new SettingModel();

                MessageBox.Show($"配置数据.xml加载失败,软件已重置配置，请进入配置文件按需求修改,再重新打开软件:\r\n{ex.Message}");

            }
        }
        #endregion

        #region 日志数据
        public void SaveRecordModel()
        {
            string filename = $"{Environment.CurrentDirectory}\\配置\\日志数据.xml";
            string dir = Path.GetDirectoryName(filename);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            using (var stream = File.Open(filename, FileMode.Create))
            {
                var serializer = new XmlSerializer(typeof(RecordModel));
                serializer.Serialize(stream, DataModel.Recordmodel);
            }
        }
        public void LoadRecordModel()
        {
            try
            {
                string filename = $"{Environment.CurrentDirectory}\\配置\\日志数据.xml";
                string dir = Path.GetDirectoryName(filename);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                if (File.Exists(filename))
                {
                    using (var stream = File.OpenRead(filename))
                    {
                        var serializer = new XmlSerializer(typeof(RecordModel));
                        DataModel.Recordmodel = serializer.Deserialize(stream) as RecordModel;
                    }
                }
                else
                {
                    DataModel.Recordmodel = new RecordModel();
                }
            }
            catch (Exception ex)
            {
                DataModel.Recordmodel = new RecordModel();
                //MessageBox.Show($"日志数据.xml加载失败,软件已重置配置，请进入配置文件按需求修改,再重新打开软件:\r\n{ex.Message}");

            }
        }
        #endregion
        #region 配方保存加载

        public void SavePrjXmls()
        {
            try
            {
                #region 删除旧xml文件
                foreach (string s in Directory.GetFiles($"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}"))
                {
                    if (s.ToUpper().EndsWith(".XML"))
                    {
                        try
                        {
                            File.Delete(s);
                        }
                        catch (Exception ex) { continue; }
                    }
                }
                #endregion
                for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
                {
                    SavePrjXml(DataModel.Processmodel.Tools[i], i + 1);
                }
            }
            catch (Exception ex) {; }
        }

        private void SavePrjXml(ToolModel td, int index)
        {
            string filename = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{index}.xml";
            string dir = Path.GetDirectoryName(filename);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var stream = File.Open(filename, FileMode.Create))
            {
                try
                {
                    var serializer = new XmlSerializer(typeof(ToolModel));
                    serializer.Serialize(stream, td);
                    return;
                }
                catch {; }
            }
        }

        public void LoadPrjXmls()
        {

            DataModel.Processmodel.Tools.Clear();
            string dir = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}";
            WriteDetailLog($"[工具加载] 工程目录: {dir}");

            if (!Directory.Exists(dir))
            {
                WriteDetailLog($"[工具加载] 工程目录不存在: {dir}");
                writeLog($"⚠️ 工程目录不存在: {dir}");
                return;
            }

            string[] toolfilenames = Directory.GetFiles(dir);
            WriteDetailLog($"[工具加载] 目录中文件数量: {toolfilenames.Length}");

            int count = 0;
            for (int i = 0; i < toolfilenames.Length; i++)
            {
                FileInfo fi = new FileInfo(toolfilenames[i]);
                WriteDetailLog($"[工具加载] 文件{i}: {fi.Name}, 扩展名: {fi.Extension}");
                if (fi.Extension.ToUpper() == ".XML")
                {
                    count++;
                }
            }

            WriteDetailLog($"[工具加载] 找到XML文件数量: {count}");
            int index = 0;
            for (int k = 0; k < count; k++)
            {
                try
                {
                    var tool = LoadPrjXml(k + 1);
                    if (tool != null)
                    {
                        tool.Index = (index++) + 1;
                        DataModel.Processmodel.Tools.Add(tool);
                        string jpgfilename = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{index}.jpg";
                        #region 加载模型图片
                        tool.Image?.Dispose();
                        HOperatorSet.GenEmptyObj(out tool.Image);
                        HOperatorSet.ReadImage(out tool.Image, jpgfilename);
                        #endregion


                        Bitmap bmp;
                        Bitmap src;
                        //HObject Image;
                        //HOperatorSet.GenEmptyObj(out Image);

                        try
                        {
                            //HOperatorSet.ReadImage(out Image, filename);
                            var dst = GetReducedImage(DataModel.Settingmodel.ImageSize, DataModel.Settingmodel.ImageSize, tool.Image);
                            //Hobject2Bitmap.HobjectToBitmap(tool.Image, out src);
                            Hobject2Bitmap.HobjectToBitmap24(dst, out bmp);

                            tool.CurrentBitmapSource = null;
                            tool.CurrentBitmapSource = Imaging.CreateBitmapSourceFromHBitmap(bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                            tool.BitmapSource = null;
                            tool.BitmapSource = Imaging.CreateBitmapSourceFromHBitmap(bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                            bmp?.Dispose();
                            dst?.Dispose();
                           // src?.Dispose();
                        }
                        catch (Exception ex)
                        {; }




                        //using (Bitmap bmp = (Bitmap)Bitmap.FromFile(jpgfilename))
                        //{
                        //    using (Bitmap bmp1 = GetReducedImage(DataModel.Settingmodel.ImageSize, DataModel.Settingmodel.ImageSize, bmp))
                        //    {
                        //        BitmapSource bs = Imaging.CreateBitmapSourceFromHBitmap(bmp1.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        //        tool.BitmapSource = null;
                        //        tool.CurrentBitmapSource = null;

                        //        tool.BitmapSource = bs;
                        //        tool.CurrentBitmapSource = bs.Clone();
                        //    }
                        //}
                    }
                    GC.Collect();
                }
                catch (Exception ex)
                {

                }
            }

            //for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            //{
            //    DataModel.Processmodel.Tools[i].Index = i + 1;
            //}
        }


        public bool LoadBitmapSource()
        {

            try
            {
                string jpgfilename = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{DataModel.Processmodel.selectedindex + 1}.jpg";
                using (Bitmap bmp = (Bitmap)Bitmap.FromFile(jpgfilename))
                {
                    BitmapSource bs = Imaging.CreateBitmapSourceFromHBitmap(bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    DataModel.Processmodel.ShowBitmapSource = null;
                    DataModel.Processmodel.ShowBitmapSource = bs;

                }
                GC.Collect();
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }

            GC.Collect();


        }

        private ToolModel LoadPrjXml(int index)
        {
            string dir = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}";
            string filename = $"{dir}\\Tool{index}.xml";

            if (File.Exists(filename))
            {
                using (var stream = File.OpenRead(filename))
                {
                    try
                    {
                        var serializer = new XmlSerializer(typeof(ToolModel));
                        var r = serializer.Deserialize(stream) as ToolModel;
                        return (ToolModel)r;
                    }
                    catch {; }
                }
            }
            return null;
        }
        #endregion
        #endregion

        #region 相机数据处理
        public void start()
        {
            DataModel.Settingmodel.camedata.CameraModel.camera.ImageReceived += OnReceive;
        }

        /// <summary>
        /// 启动扫码枪连接状态监测（当前针对HF800网口，其他型号可后续扩展）
        /// </summary>
        public void StartScannerMonitor()
        {
            Thread t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        bool connected = false;
                        if (DataModel != null && DataModel.Settingmodel != null)
                        {
                            if (DataModel.Settingmodel.ScannerMode == "HF800" && DataModel.Settingmodel.HF800 != null)
                            {
                                // 轻量连通性检查：快速连接并立刻断开
                                connected = DataModel.Settingmodel.HF800.connect(DataModel.Settingmodel.HF800.IP_address, DataModel.Settingmodel.HF800.port, 1000);
                                try { DataModel.Settingmodel.HF800.disconnect(); } catch {; }
                            }
                        }

                        if (App.Current != null)
                        {
                            App.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DataModel.Settingmodel.ScannerIsConnected = connected;
                            }));
                        }
                    }
                    catch {; }
                    Thread.Sleep(2000);
                }
            });
            t.IsBackground = true;
            t.Start();
        }
        private void OnReceive(object sender, EventArgs e)
        {

            try
            {
                writeLog($"相机->视觉:接收照片", false);

                MyEventArgs myEventArgs = e as MyEventArgs;

                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    OnReceiveProcess(myEventArgs.Image, myEventArgs.Height, myEventArgs.Width);

                    GC.Collect();
                }));

            }
            catch (Exception ex)
            {
                writeError($"[{DataModel.Processmodel.RCMD}]识别错误:{ex.ToString()}");
            }
        }


        public void OnReceiveProcess(HObject Image, int H, int W)
        {
            HObject processImage = null;

            try
            {

                #region 图片接收

                //Image


                HOperatorSet.CountChannels(Image, out var channels);

                if (channels == 1)
                {
                    HOperatorSet.Compose3(Image, Image, Image, out var multiChannelImage);
                    processImage = multiChannelImage; // 使用局部变量管理
                }
                else
                {
                    processImage = Image; // 直接使用原图像
                }



                HWindow hwindow = DataModel.Settingmodel.HWindow;
                hwindow.ClearWindow();
                //hwindow.SetPart(0, 0, H - 1, W - 1);
                hwindow.DispObj(Image);
                for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
                {
                    // 修复ALL指令匹配逻辑，与ExecuteToolsByCommand保持一致
                    bool isMatch = false;
                    if (DataModel.Processmodel.RCMD == "ALL")
                    {
                        // ALL指令匹配所有工具（向后兼容）
                        isMatch = true;
                    }
                    else
                    {
                        // 按Command字段精确匹配
                        isMatch = DataModel.Processmodel.Tools[i].Command == DataModel.Processmodel.RCMD;
                    }

                    if (isMatch)
                    {
                        Stopwatch stopwatch = new Stopwatch();
                        stopwatch.Start();

                        if (i == 0)
                        {
                            DataModel.Processmodel.Status = ToolStatus.识别中;
                        }

                        DataModel.Processmodel.ToolIndex = i + 1;

                        ToolModel tool = DataModel.Processmodel.Tools[i];


                        Bitmap bmp;
                        try
                        {
                            var dst = GetReducedImage(DataModel.Settingmodel.ImageSize, DataModel.Settingmodel.ImageSize, Image);
                            Hobject2Bitmap.HobjectToBitmap24(dst, out bmp);
                            tool.CurrentBitmapSource = null;
                            tool.CurrentBitmapSource = Imaging.CreateBitmapSourceFromHBitmap(bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bmp?.Dispose();
                            dst?.Dispose();
                        }
                        catch (Exception e)
                        {; }



                        //Bitmap bmp;
                        //try
                        //{
                        //    Hobject2Bitmap.HobjectToBitmap24(Image, out bmp);

                        //    using (Bitmap bmp1 = GetReducedImage(DataModel.Settingmodel.ImageSize, DataModel.Settingmodel.ImageSize, bmp))
                        //    {
                        //        tool.CurrentBitmapSource = null;
                        //        tool.CurrentBitmapSource = Imaging.CreateBitmapSourceFromHBitmap(bmp1.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        //    }
                        //    bmp.Dispose();
                        //}
                        //catch (Exception ex)
                        //{

                        //}


                        ClearTool(tool);
                        tool.ToolStatus = ToolStatus.识别中;
                        try
                        {
                            if (tool.TestMode == TestModes.二维码)
                            {
                                #region 读取二维码
                                List<Reg.Barcode> barcodelist = null;

                                try
                                {
                                    barcodelist = tool.Reg.ReadBarcode(Image, 1, tool.BarCodeROI.Row1, tool.BarCodeROI.Col1, tool.BarCodeROI.Row2, tool.BarCodeROI.Col2, true);
                                    if (barcodelist.Count > 0)
                                    {
                                        tool.BarcodeStr = barcodelist[0].codestr;
                                        //  NoticeBox.Show(barcodelist[0].codestr, "二维码读取成功", MessageBoxIcon.Success, true, 5000);
                                        //if (tool.SendBarcodeData)
                                        //{
                                        //    SendMsgSoft(tool.BarcodeStr);
                                        //}
                                        writeLog($"二维码读取成功:{barcodelist[0].codestr}");

                                        if (tool.MPMode)
                                        {
                                            UIInvoke(() =>
                                            {
                                                DataModel.Processmodel.Barcodes += barcodelist[0].codestr + ";";
                                            });
                                        }
                                        if (tool.SendBarcode)
                                        {
                                            tool.ToolStatus = ToolStatus.等待中;

                                            if (DataModel.Settingmodel.TcpClientH.Connect(DataModel.Settingmodel.BarcodeReporter.RemoteIP, DataModel.Settingmodel.BarcodeReporter.RemotePort))
                                            {

                                                DataModel.Settingmodel.TcpClientH.SendMsg($"{DataModel.Processmodel.Scannerstr};{DataModel.Processmodel.Barcodes}{tool.FinishedCode}\r\n");
                                                string s = DataModel.Settingmodel.TcpClientH.ReceiveMsg(10000);
                                                s = s.Replace("\r", "").Replace("\n", "");
                                                if (!string.IsNullOrEmpty(s))
                                                {
                                                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                                                    {
                                                        DataModel.Processmodel.SNList.Add(s);
                                                    }));
                                                    tool.ToolStatus = ToolStatus.OK;
                                                    writeLog($"扫码汇报软件返回成品编号:{s}");
                                                    UIInvoke(() => DataModel.Processmodel.Barcodes = string.Empty);


                                                }
                                                else
                                                {
                                                    tool.ToolStatus = ToolStatus.NG2;
                                                    writeLog($"扫码汇报软件返回为空");

                                                }

                                                DataModel.Settingmodel.TcpClientH.DisConnect();
                                            }
                                            else
                                            {
                                                tool.ToolStatus = ToolStatus.NG2;
                                                writeLog($"连接扫码汇报软件NG2");
                                            }


                                        }
                                        else
                                        {
                                            tool.ToolStatus = ToolStatus.OK;
                                        }



                                        //#region 发送二维码给上位机设备
                                        //DataModel.Settingmodel.TcpServerSoft.SendMessage(tool.BarcodeStr);
                                        //Thread.Sleep(tool.Delaytimes);
                                        //string s = DataModel.Settingmodel.TcpServerSoft.GetMsg();

                                        //writeLog($"二维码校验返回错误:{s}");

                                        //if (string.IsNullOrEmpty(s) || s != "OK")
                                        //{

                                        //}
                                        //else
                                        //{

                                        //}

                                        //if (!string.IsNullOrEmpty(tool.FinishedCode))
                                        //{
                                        //    DataModel.Settingmodel.TcpServerSoft.SendMessage(tool.FinishedCode);
                                        //    Thread.Sleep(tool.Delaytimes);
                                        //    string sn = DataModel.Settingmodel.TcpServerSoft.GetMsg();
                                        //    if (!string.IsNullOrEmpty(sn))
                                        //    {
                                        //        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                                        //        {
                                        //            DataModel.Processmodel.SNList.Add(sn);
                                        //        }));
                                        //    }
                                        //}

                                        //#endregion


                                    }
                                    else
                                    {
                                        tool.BarcodeStr = string.Empty;
                                        writeLog($"二维码读取失败:{barcodelist[0].codestr}");
                                        tool.ToolStatus = ToolStatus.NG;

                                    }
                                }
                                catch
                                {
                                    ;
                                }

                                #endregion
                            }
                            else if (tool.TestMode == TestModes.模板匹配)
                            {
                                #region 读取位置
                                /// NG  未安装好  NG2:缺少配件  OK:合格              

                                try
                                {
                                    ShapeMatch.Result shapmatchresult = new ShapeMatch.Result();
                                    tool.ShapeMatch.BasicData.matchcenter_X = tool.InitX;
                                    tool.ShapeMatch.BasicData.matchcenter_Y = tool.InitY;
                                    tool.ShapeMatch.BasicData.matchcenterX_Basic = tool.InitX;
                                    tool.ShapeMatch.BasicData.matchcenterY_Basic = tool.InitY;
                                    tool.ShapeMatch.BasicData.productcenter_X = tool.InitX;
                                    tool.ShapeMatch.BasicData.productcenter_Y = tool.InitY;
                                    //shapmatchresult = tool.ShapeMatch.Match(Image, 1, tool.PositionROI.Row1, tool.PositionROI.Col1, tool.PositionROI.Row2, tool.PositionROI.Col2, -180, 180, false);
                                    shapmatchresult = tool.ShapeMatch.Match(Image, 1, tool.PositionROI.Row1, tool.PositionROI.Col1, tool.PositionROI.Row2, tool.PositionROI.Col2, (int)-tool.AllowAngleDelta, (int)tool.AllowAngleDelta, false);
                                    var Result_Data = tool.ShapeMatch.Analysis_Result(shapmatchresult);
                                    if (Result_Data != null)
                                    {
                                        tool.ActualX = Result_Data.X_actual;
                                        tool.ActualY = Result_Data.Y_actual;
                                        tool.ActualAngle = (Result_Data.angle / Math.PI * 180.0);
                                        tool.ActualScore = Result_Data.score;
                                        tool.DeltaX = Result_Data.deltaX_actual * tool.K / 1000;
                                        tool.DeltaY = Result_Data.deltaY_actual * tool.K / 1000;


                                        if (tool.ActualScore >= tool.MinScore)
                                        {
                                            if (Math.Abs(tool.DeltaX) < tool.Allow_X_Delta &&
                                                Math.Abs(tool.DeltaY) < tool.Allow_Y_Delta &&
                                                Math.Abs(tool.ActualAngle) < tool.AllowAngleDelta
                                                )
                                            {
                                                //if (tool.SendPositionData)
                                                //{
                                                //    SendMsgRobot($"{sendstr},");
                                                //}
                                                tool.ToolStatus = ToolStatus.OK;
                                            }
                                            else
                                            {
                                                tool.ToolStatus = ToolStatus.NG;

                                            }

                                        }
                                        else
                                        {
                                            tool.ToolStatus = ToolStatus.NG2;
                                        }

                                    }
                                    else
                                    {
                                        tool.ToolStatus = ToolStatus.NG2;
                                    }
                                }
                                catch
                                {
                                    ;
                                }


                                #endregion
                            }
                            else if (tool.TestMode == TestModes.面积)
                            {
                                #region 读取面积

                                int Areaint = CoculateDimension(Image, tool, hwindow, false);
                                tool.ActualDimension = Areaint;

                                if (Areaint >= tool.MinDimension && Areaint <= tool.MaxDimension)
                                {
                                    tool.ToolStatus = ToolStatus.OK;
                                }
                                else
                                {
                                    tool.ToolStatus = ToolStatus.NG;
                                }

                                #endregion
                            }
                        }
                        catch {; }

                        writeLog($"视觉->视觉:计算完成", false);
                        string s1 = $"{DataModel.Processmodel.Tools[i].Name}:识别耗时:{stopwatch.ElapsedMilliseconds}ms";
                        Save_record(s1);
                        stopwatch.Restart();

                        #region 保存图片
                        try
                        {
                            if (tool.ToolStatus == ToolStatus.OK)
                            {
                                if (DataModel.Settingmodel.ImageSaveSetting.SaveOK)
                                {

                                    string prefix = GetImageFilePrefix(i);
                                    string savefilename = $"{DataModel.Settingmodel.ImageSaveSetting.ImageSaveDir}\\{DateTime.Now.ToString("yyyyMMdd")}\\OK\\{prefix}-{tool.Index.ToString("00")}-{tool.Name}-{tool.ToolStatus}-{DateTime.Now.ToString("yyyyMMddHHmmssFFF")}.jpg";

                                    string dir = Path.GetDirectoryName(savefilename);
                                    if (!Directory.Exists(dir))
                                    { Directory.CreateDirectory(dir); }
                                    HOperatorSet.WriteImage(Image, "jpg", 0, savefilename);
                                }
                            }
                            else
                            {
                                if (DataModel.Settingmodel.ImageSaveSetting.SaveNG)
                                {
                                    string prefix = GetImageFilePrefix(i);
                                    string savefilename = $"{DataModel.Settingmodel.ImageSaveSetting.ImageSaveDir}\\{DateTime.Now.ToString("yyyyMMdd")}\\NG\\{prefix}-{tool.Index.ToString("00")}-{tool.Name}-{tool.ToolStatus}-{DateTime.Now.ToString("yyyyMMddHHmmssFFF")}.jpg";
                                    string dir = Path.GetDirectoryName(savefilename);
                                    if (!Directory.Exists(dir))
                                    { Directory.CreateDirectory(dir); }
                                    HOperatorSet.WriteImage(Image, "jpg", 0, savefilename);

                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            writeLog($"视觉->保存照片:保存失败：{ex.ToString()}", false);
                        }

                        #endregion

                        if (tool.SendStatus)
                        {

                            int status = -1;

                            // 修复ALL指令匹配逻辑，与ExecuteToolsByCommand保持一致
                            var r = (from ToolModel in DataModel.Processmodel.Tools
                                     where (DataModel.Processmodel.RCMD == "ALL" || ToolModel.Command == DataModel.Processmodel.RCMD)
                                     select ToolModel);
                            var wait = (from ToolModel in r
                                        where (ToolModel.ToolStatus == ToolStatus.等待中 || ToolModel.ToolStatus == ToolStatus.识别中)
                                        select ToolModel);

                            if (wait.Count() == 0)
                            {
                                var ok = (from ToolModel in r
                                          where (ToolModel.ToolStatus == ToolStatus.OK)
                                          select ToolModel);

                                if (ok.Count() == r.Count())
                                {
                                    status = 0;
                                }
                                else
                                {
                                    var NG2 = (from ToolModel in r
                                               where (ToolModel.ToolStatus == ToolStatus.NG2)
                                               select ToolModel);
                                    if (NG2.Count() > 0)
                                    {
                                        status = 2;
                                    }
                                    else
                                    {
                                        status = 1;
                                    }

                                }
                            }

                            switch (status)
                            {

                                case 0:
                                    {
                                        SendMsgRobot(tool.OKCMD.Trim());
                                        break;
                                    }
                                case 1:
                                    {
                                        SendMsgRobot(tool.NG1CMD.Trim());
                                        break;
                                    }
                                case 2:
                                    {
                                        SendMsgRobot(tool.NG2CMD.Trim());
                                        break;
                                    }
                                default:
                                    {
                                        break;
                                    }
                            }
                        }


                        if (i == DataModel.Processmodel.Tools.Count - 1)
                        {
                            int status = -1;
                            
                            // 修复：只判断当前指令对应的工具，避免阶段状态判断错误
                            // 使用与 ExecuteToolsByCommand 一致的匹配逻辑
                            string currentCommand = DataModel.Processmodel.RCMD;
                            var currentCommandTools = (from ToolModel in DataModel.Processmodel.Tools
                                                       where (currentCommand == "ALL" || ToolModel.Command == currentCommand)
                                                       select ToolModel).ToList();
                            
                            int c = currentCommandTools.Count();
                            WriteDetailLog($"[阶段判断] 当前指令{currentCommand}，匹配工具数量={c}");

                            var ok = currentCommandTools.Where(t => t.ToolStatus == ToolStatus.OK);
                            var NG1 = currentCommandTools.Where(t => t.ToolStatus == ToolStatus.NG);
                            var NG2 = currentCommandTools.Where(t => t.ToolStatus == ToolStatus.NG2);
                            var wait = currentCommandTools.Where(t => t.ToolStatus == ToolStatus.等待中 || t.ToolStatus == ToolStatus.识别中);

                            WriteDetailLog($"[阶段判断] OK={ok.Count()}, NG1={NG1.Count()}, NG2={NG2.Count()}, 等待={wait.Count()}");
                            WriteDetailLog($"[阶段判断] 工具明细={GetToolStatusSummary(currentCommandTools)}");

                            if (ok.Count() == c && c > 0)
                            {
                                status = 0;
                                //WriteDetailLog($"[阶段判断] 当前指令{currentCommand}所有工具OK，status=0");
                            }
                            else if (NG2.Count() > 0)
                            {
                                status = 2;
                                //WriteDetailLog($"[阶段判断] 当前指令{currentCommand}有严重NG，status=2");
                            }
                            else if (wait.Count() == 0 && c > 0)
                            {
                                // 所有工具都已完成但不是全OK
                                status = 1;
                                //WriteDetailLog($"[阶段判断] 当前指令{currentCommand}有NG，status=1");
                            }
                            else
                            {
                                //WriteDetailLog($"[阶段判断] 当前指令{currentCommand}还有工具等待中，不更新阶段状态");
                            }

                            // 检查是否在点检模式
                            if (DataModel.Processmodel.IsInspectionMode)
                            {
                                string type = DataModel.Processmodel.InspectionType;
                                string toolSummary = GetToolStatusSummary(currentCommandTools);
                                WriteDetailLog($"[点检汇总] 类型={type}, 指令={currentCommand}, status={status}, OK={ok.Count()}, NG={NG1.Count()}, NG2={NG2.Count()}, 等待={wait.Count()}, 工具明细={toolSummary}");
                                writeLog($"点检结果汇总: 类型={type}, 指令={currentCommand}, status={status}, OK={ok.Count()}, NG={NG1.Count()}, NG2={NG2.Count()}, 等待={wait.Count()}");

                                if (status == -1)
                                {
                                    UpdateInspectionStatusMessage($"点检未结算：仍有工具处于等待/识别中。工具明细：{toolSummary}");
                                    writeLog("ℹ️ 点检未结算：仍有工具处于等待/识别中", false);
                                    return;
                                }

                                if (status == 2)
                                {
                                    DataModel.Processmodel.Status = ToolStatus.NG2;
                                    if (type == "NG")
                                    {
                                        UpdateInspectionStatusMessage($"NG点检失败：存在NG2结果，要求当前参与工具全部为NG。工具明细：{toolSummary}");
                                        writeLog("❌ NG点检失败：存在NG2结果，未满足“所有工具都为NG”", false);
                                    }
                                    else if (type == "OK")
                                    {
                                        UpdateInspectionStatusMessage($"OK点检失败：检测到NG2结果。工具明细：{toolSummary}");
                                        writeLog("❌ OK点检失败：检测到NG2结果", false);
                                    }
                                    return;
                                }

                                if (status == 1)
                                {
                                    DataModel.Processmodel.Status = ToolStatus.NG;
                                    if (type == "NG")
                                    {
                                        bool allNG = currentCommandTools.All(t => t.ToolStatus == ToolStatus.NG);
                                        if (allNG)
                                        {
                                            CompleteInspection("NG");
                                            UpdateInspectionStatusMessage($"NG点检通过：当前参与工具全部为NG。工具明细：{toolSummary}");
                                            writeLog("✅ NG点检通过：当前参与工具全部为NG", false);
                                        }
                                        else
                                        {
                                            UpdateInspectionStatusMessage($"NG点检失败：存在非NG结果，要求当前参与工具全部为NG。工具明细：{toolSummary}");
                                            writeLog("❌ NG点检失败：存在非NG结果，未满足“所有工具都为NG”", false);
                                        }
                                    }
                                    else if (type == "OK")
                                    {
                                        UpdateInspectionStatusMessage($"OK点检失败：检测到NG结果。工具明细：{toolSummary}");
                                        writeLog("❌ OK点检失败：检测到NG结果", false);
                                    }
                                    return;
                                }

                                if (status == 0)
                                {
                                    DataModel.Processmodel.Status = ToolStatus.OK;
                                    if (type == "NG")
                                    {
                                        UpdateInspectionStatusMessage($"NG点检失败：当前参与工具结果全为OK。工具明细：{toolSummary}");
                                        writeLog("❌ NG点检失败：当前参与工具结果全为OK", false);
                                        return;
                                    }
                                    
                                    if (type == "OK")
                                    {
                                        // OK点检：所有工具必须为OK
                                        var allOK = DataModel.Processmodel.Tools.All(t => t.ToolStatus == ToolStatus.OK);
                                        if (allOK)
                                        {
                                            CompleteInspection("OK");
                                            writeLog("✅ OK点检通过：所有工具检测OK", false);
                                        }
                                        else
                                        {
                                            UpdateInspectionStatusMessage($"OK点检失败：仍存在非OK工具。工具明细：{toolSummary}");
                                            writeLog("❌ OK点检失败：存在NG结果", false);
                                        }
                                    }
                                }
                                
                                // 点检模式下不执行报工，不更新阶段状态，直接返回
                                return;
                            }

                            // 正常流程：根据status更新阶段状态（成功或失败都要更新）
                            if (status >= 0)  // status=-1表示还在等待中，不更新；status=0/1/2表示已完成
                            {
                                // 使用前面已经声明的 currentCommand 变量
                                bool isSuccess = (status == 0);
                                
                                // 先同步更新阶段检测状态（避免异步更新导致的时序问题）
                                switch (currentCommand)
                                {
                                    case "A1":
                                        DataModel.Processmodel.A1StageCompleted = isSuccess;
                                        break;
                                    case "A2":
                                        DataModel.Processmodel.A2StageCompleted = isSuccess;
                                        break;
                                    case "ALL":
                                        DataModel.Processmodel.A1StageCompleted = isSuccess;
                                        DataModel.Processmodel.A2StageCompleted = isSuccess;
                                        break;
                                }

                                // 异步更新UI和日志（不影响报工判断）
                                UpdateStageStatus(currentCommand, isSuccess);

                                // 防御性校正：根据实际工具结果重新计算阶段完成标志，避免偶发未更新
                                try
                                {
                                    var a1Tools = DataModel.Processmodel.Tools.Where(t => t.Command == "A1" || t.Command == "ALL").ToList();
                                    if (a1Tools.Any())
                                    {
                                        var allA1Ok = a1Tools.All(t => t.ToolStatus == ToolStatus.OK);
                                        DataModel.Processmodel.A1StageCompleted = allA1Ok;
                                        WriteDetailLog($"A1StageCompleted={allA1Ok} (工具数:{a1Tools.Count})");
                                    }

                                    var a2Tools = DataModel.Processmodel.Tools.Where(t => t.Command == "A2" || t.Command == "ALL").ToList();
                                    if (a2Tools.Any())
                                    {
                                        var allA2Ok = a2Tools.All(t => t.ToolStatus == ToolStatus.OK);
                                        DataModel.Processmodel.A2StageCompleted = allA2Ok;
                                        WriteDetailLog($"A2StageCompleted={allA2Ok} (工具数:{a2Tools.Count})");
                                    }
                                }
                                catch { }
                            }

                            // 只有检测成功时才执行报工
                            if (status == 0)
                            {
                                DataModel.Processmodel.Status = ToolStatus.OK;

                                // 检测成功后执行报工逻辑（需要SN转换成功且工序校验通过且所有阶段完成）
                                if (DataModel.Settingmodel.ReportWorkConfig.EnableAutoReportWork &&
                                    !string.IsNullOrEmpty(DataModel.Processmodel.CurrentProductSN) &&
                                    DataModel.Processmodel.ScanCodeStatus == "工序校验通过" &&
                                    DataModel.Processmodel.CanExecuteReportWork &&
                                    !DataModel.Processmodel.IsReportingWork)
                                {
                                    writeLog("✅ 检测成功，开始自动报工...", false);
                                    _ = Task.Run(async () => await ExecuteReportWorkAsync("合格"));
                                }
                                else
                                {
                                    // 详细诊断报工条件不满足的原因
                                    List<string> reasons = new List<string>();
                                    if (!DataModel.Settingmodel.ReportWorkConfig.EnableAutoReportWork)
                                        reasons.Add("自动报工未启用");
                                    if (string.IsNullOrEmpty(DataModel.Processmodel.CurrentProductSN))
                                        reasons.Add("产品SN为空");
                                    if (DataModel.Processmodel.ScanCodeStatus != "工序校验通过")
                                        reasons.Add($"工序校验状态: {DataModel.Processmodel.ScanCodeStatus}");
                                    if (!DataModel.Processmodel.CanExecuteReportWork)
                                    {
                                        // 根据实际配置显示未完成的阶段
                                        List<string> incompleteStages = new List<string>();
                                        bool hasA1 = DataModel.Processmodel.Tools.Any(t => t.Command == "A1" || t.Command == "ALL");
                                        bool hasA2 = DataModel.Processmodel.Tools.Any(t => t.Command == "A2" || t.Command == "ALL");
                                        if (hasA1 && !DataModel.Processmodel.A1StageCompleted)
                                            incompleteStages.Add($"A1:{DataModel.Processmodel.A1StageCompleted}");
                                        if (hasA2 && !DataModel.Processmodel.A2StageCompleted)
                                            incompleteStages.Add($"A2:{DataModel.Processmodel.A2StageCompleted}");
                                        if (incompleteStages.Count > 0)
                                            reasons.Add($"阶段未完成({string.Join(", ", incompleteStages)})");
                                    }
                                    if (DataModel.Processmodel.IsReportingWork)
                                        reasons.Add("报工正在进行中");

                                    string reasonText = reasons.Count > 0 ? $" - 原因: {string.Join(", ", reasons)}" : "";
                                    writeLog($"⚠️ 检测完成，但未执行报工{reasonText}", false);

                                    // 更新界面状态显示
                                     App.Current.Dispatcher.BeginInvoke(new Action(() =>
                                     {
                                         DataModel.Processmodel.ReportWorkStatus = reasons.Count > 0 ?
                                             $"未报工: {string.Join(", ", reasons)}" : "检测完成";
                                     }));
                                 }

                                // 两阶段(A1/A2)场景：可能出现“当前阶段OK，但另一阶段为NG”的情况
                                // 例如：A1 NG、A2 OK。此时若A1/A2都已完成，应按整体结果报工NG（而不是一直停留在未报工状态）
                                if (DataModel.Settingmodel.ReportWorkConfig.EnableAutoReportWork &&
                                    !string.IsNullOrEmpty(DataModel.Processmodel.CurrentProductSN) &&
                                    DataModel.Processmodel.ScanCodeStatus == "工序校验通过" &&
                                    !DataModel.Processmodel.IsReportingWork)
                                {
                                    var a1StageTools = DataModel.Processmodel.Tools.Where(t => t.Command == "A1").ToList();
                                    var a2StageTools = DataModel.Processmodel.Tools.Where(t => t.Command == "A2").ToList();
                                    bool isTwoStage = a1StageTools.Any() && a2StageTools.Any();
                                    if (isTwoStage)
                                    {
                                        bool a1Done = a1StageTools.All(t => t.ToolStatus != ToolStatus.等待中 && t.ToolStatus != ToolStatus.识别中);
                                        bool a2Done = a2StageTools.All(t => t.ToolStatus != ToolStatus.等待中 && t.ToolStatus != ToolStatus.识别中);
                                        bool anyNG2 = a1StageTools.Any(t => t.ToolStatus == ToolStatus.NG2) ||
                                                      a2StageTools.Any(t => t.ToolStatus == ToolStatus.NG2);

                                        if (a1Done && a2Done && !anyNG2)
                                        {
                                            bool allA1Ok = a1StageTools.All(t => t.ToolStatus == ToolStatus.OK);
                                            bool allA2Ok = a2StageTools.All(t => t.ToolStatus == ToolStatus.OK);
                                            bool overallOk = allA1Ok && allA2Ok;

                                            if (!overallOk)
                                            {
                                                DataModel.Processmodel.Status = ToolStatus.NG;
                                                writeLog("⚠️ 两阶段检测完成，存在NG，开始报工（状态：NG）...", false);
                                                _ = Task.Run(async () => await ExecuteReportWorkAsync("不合格", "NG", "绝缘纸漏贴移位", "绝缘纸漏贴移位"));
                                            }
                                        }
                                    }
                                }
                            }
                            else if (status == 2)
                            {
                                DataModel.Processmodel.Status = ToolStatus.NG2;
                                
                                // 检查是否在点检模式
                                if (DataModel.Processmodel.IsInspectionMode)
                                {
                                    string type = DataModel.Processmodel.InspectionType;
                                    
                                    if (type == "NG")
                                    {
                                        // NG点检：至少一个NG即可
                                        CompleteInspection("NG");
                                        writeLog("✅ NG点检通过：检测到NG2结果", false);
                                    }
                                    else if (type == "OK")
                                    {
                                        writeLog("❌ OK点检失败：检测到NG2结果", false);
                                    }
                                    
                                    // 点检模式下不执行报工
                                    return;
                                }
                                
                                // 更新阶段检测状态
                                UpdateStageStatus(DataModel.Processmodel.RCMD, false);
                                // PLC_write((UInt16)3);
                                writeLog("检测结果：NG2，结果已显示在界面上", false);
                            }
                            else
                            {
                                DataModel.Processmodel.Status = ToolStatus.NG;
                                
                                // 检查是否在点检模式
                                if (DataModel.Processmodel.IsInspectionMode)
                                {
                                    string type = DataModel.Processmodel.InspectionType;
                                    
                                    if (type == "NG")
                                    {
                                        // NG点检：至少一个NG即可
                                        CompleteInspection("NG");
                                        writeLog("✅ NG点检通过：检测到NG结果", false);
                                    }
                                    else if (type == "OK")
                                    {
                                        writeLog("❌ OK点检失败：检测到NG结果", false);
                                    }
                                    
                                    // 点检模式下不执行报工
                                    return;
                                }
                                
                                // 更新阶段检测状态（会将阶段标记为未完成）
                                UpdateStageStatus(DataModel.Processmodel.RCMD, false);
                                // PLC_write((UInt16)2);
                                writeLog("检测结果：NG，结果已显示在界面上", false);
                                
                                // NG状态下也执行报工，但两阶段(A1/A2)场景需要等两阶段都完成后再报工
                                // NG2(工具执行失败)不报工（由上面的status==2分支处理）
                                if (DataModel.Settingmodel.ReportWorkConfig.EnableAutoReportWork &&
                                    !string.IsNullOrEmpty(DataModel.Processmodel.CurrentProductSN) &&
                                    DataModel.Processmodel.ScanCodeStatus == "工序校验通过" &&
                                    !DataModel.Processmodel.IsReportingWork)
                                {
                                    var a1StageTools = DataModel.Processmodel.Tools.Where(t => t.Command == "A1").ToList();
                                    var a2StageTools = DataModel.Processmodel.Tools.Where(t => t.Command == "A2").ToList();
                                    bool hasA1 = a1StageTools.Any();
                                    bool hasA2 = a2StageTools.Any();
                                    bool isTwoStage = hasA1 && hasA2;

                                    // 两阶段：必须等A1/A2都结束（不再有等待/识别中）后再报工，避免A1阶段先NG就报工
                                    bool a1Done = !hasA1 || a1StageTools.All(t => t.ToolStatus != ToolStatus.等待中 && t.ToolStatus != ToolStatus.识别中);
                                    bool a2Done = !hasA2 || a2StageTools.All(t => t.ToolStatus != ToolStatus.等待中 && t.ToolStatus != ToolStatus.识别中);
                                    bool canFinalize = !isTwoStage || (a1Done && a2Done);

                                    // 任一阶段存在NG2则不报工（执行失败）
                                    bool anyNG2 = (hasA1 && a1StageTools.Any(t => t.ToolStatus == ToolStatus.NG2)) ||
                                                  (hasA2 && a2StageTools.Any(t => t.ToolStatus == ToolStatus.NG2));

                                    if (canFinalize && !anyNG2)
                                    {
                                        writeLog("⚠️ 检测到NG，开始报工（状态：NG）...", false);
                                        _ = Task.Run(async () => await ExecuteReportWorkAsync("不合格", "NG", "绝缘纸漏贴移位", "绝缘纸漏贴移位"));
                                    }
                                    else if (isTwoStage && !canFinalize)
                                    {
                                        writeLog("⚠️ 当前阶段NG，等待另一阶段完成后再报工", false);
                                    }
                                }
                            }

                        }

                        writeLog($"视觉->视觉:保存完成", false);

                        string s2 = $"{DataModel.Processmodel.Tools[i].Name}:保存图片发送结果耗时:{stopwatch.ElapsedMilliseconds}ms";
                        Save_record(s2);

                    }
                }
                #endregion

            }
            catch (Exception ex)
            {
                writeError($"[{DataModel.Processmodel.RCMD}]图像处理异常:{ex.Message}");
            }
            finally
            {
                // 确保资源释放
                if (processImage != null && processImage != Image)
                {
                    processImage.Dispose();
                }
            }

        }


        public void Save_record(string info)
        {
            if (DataModel.Settingmodel.SaveProcessData)
            {
                DateTime dt = DateTime.Now;
                string filename = $"{Environment.CurrentDirectory}\\识别过程日志\\{dt.ToString("yyyy-MM-dd")}\\{dt.ToString("yyyyMMddHH")}.txt";
                string dir = Path.GetDirectoryName(filename);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (StreamWriter sw = new StreamWriter(filename, true))
                {
                    sw.WriteLine($"[{dt.ToString("yyyy-MM-dd HH:mm:ss.FFF")}]{info}");
                }
            }


        }


        #endregion


        #region
        public KColor GetPixelData(int X, int Y)
        {
            try
            {
                int w = DataModel.Processmodel.ShowBitmapSource.PixelWidth;
                int h = DataModel.Processmodel.ShowBitmapSource.PixelHeight;
                int stride = (w * DataModel.Processmodel.ShowBitmapSource.Format.BitsPerPixel + 7) / 8;

                byte[] pixels = new byte[h * stride];
                DataModel.Processmodel.ShowBitmapSource.CopyPixels(pixels, stride, 0);

                int pixelindex = Y * stride + X * (DataModel.Processmodel.ShowBitmapSource.Format.BitsPerPixel / 8);
                byte r = pixels[pixelindex + 2];
                byte g = pixels[pixelindex + 1];
                byte b = pixels[pixelindex + 0];

                return new KColor() { R = r, G = g, B = b };
            }
            catch { return null; }
        }




        #endregion



        #region 面积计算

        public int CoculateDimension(HObject image, ToolModel tool, HWindow hwindow, bool redraw = true)
        {
            try
            {

                HTuple Area = new HTuple(), Row = new HTuple(), Column = new HTuple();

                HObject ROI, ReduceImage;

                HObject Region, ConnectedRegions, SelectedRegions, RegionUnion;
                HOperatorSet.GenEmptyObj(out Region);
                HOperatorSet.GenEmptyObj(out ConnectedRegions);
                HOperatorSet.GenEmptyObj(out SelectedRegions);
                HOperatorSet.GenEmptyObj(out RegionUnion);

                HOperatorSet.GenEmptyObj(out ROI);
                HOperatorSet.GenEmptyObj(out ReduceImage);

                HObject ImageR, ImageG, ImageB;
                HObject RegionR, RegionG, RegionB;
                HObject RegionIntersection;

                HOperatorSet.GenEmptyObj(out ImageR);
                HOperatorSet.GenEmptyObj(out ImageG);
                HOperatorSet.GenEmptyObj(out ImageB);

                HOperatorSet.GenEmptyObj(out RegionR);
                HOperatorSet.GenEmptyObj(out RegionG);
                HOperatorSet.GenEmptyObj(out RegionB);

                HOperatorSet.GenEmptyObj(out RegionIntersection);

                bool areaCalculated = false;
                try
                {

                    HOperatorSet.GenRectangle1(out ROI, tool.DimensionROI.Row1, tool.DimensionROI.Col1, tool.DimensionROI.Row2, tool.DimensionROI.Col2);
                    HOperatorSet.ReduceDomain(image, ROI, out ReduceImage);

                    if (tool.GrayMode)
                    {
                        HOperatorSet.Threshold(ReduceImage, out Region, tool.MinGray, tool.MaxGray);
                    }
                    else
                    {
                        HOperatorSet.Decompose3(ReduceImage, out ImageR, out ImageG, out ImageB);

                        HOperatorSet.Threshold(ImageR, out RegionR, tool.MinRed, tool.MaxRed);
                        HOperatorSet.Threshold(ImageG, out RegionG, tool.MinGreen, tool.MaxGreen);
                        HOperatorSet.Threshold(ImageB, out RegionB, tool.MinBlue, tool.MaxBlue);

                        HOperatorSet.Intersection(RegionR, RegionG, out RegionIntersection);
                        HOperatorSet.Intersection(RegionB, RegionIntersection, out Region);
                    }


                    HOperatorSet.Connection(Region, out ConnectedRegions);
                    HOperatorSet.SelectShape(ConnectedRegions, out SelectedRegions, "area", "and", tool.MinAreaFilter, tool.MaxAreaFilter);
                    HOperatorSet.Union1(SelectedRegions, out RegionUnion);
                    HOperatorSet.AreaCenter(RegionUnion, out Area, out Row, out Column);
                    areaCalculated = true;

                    HTuple width, height;
                    HOperatorSet.GetImageSize(image, out width, out height);
                    //HWindow.HalconWindow.SetPart(0, 0, (int)height - 1, (int)width - 1);

                    if (redraw)
                    {

                        hwindow.ClearWindow();
                        //hwindow.SetPart(0, 0, (int)height - 1, (int)width - 1);
                        hwindow.DispObj(image);
                    }
                    hwindow.SetLineWidth(2);
                    hwindow.SetDraw("margin");
                    hwindow.SetColor("orange");
                    hwindow.DispObj(ROI);
                    hwindow.SetDraw("fill");
                    hwindow.SetColor("red");
                    hwindow.DispObj(SelectedRegions);

                }
                catch (Exception ex)
                {
                    try
                    {
                        WriteDetailLog(
                            $"[面积计算] 计算失败: " +
                            $"ToolIndex={tool?.Index}, ToolName='{tool?.Name}', GrayMode={tool?.GrayMode}, " +
                            $"ROI=({tool?.DimensionROI?.Row1},{tool?.DimensionROI?.Col1})-({tool?.DimensionROI?.Row2},{tool?.DimensionROI?.Col2}), " +
                            $"GrayRange=[{tool?.MinGray},{tool?.MaxGray}], " +
                            $"RRange=[{tool?.MinRed},{tool?.MaxRed}], GRange=[{tool?.MinGreen},{tool?.MaxGreen}], BRange=[{tool?.MinBlue},{tool?.MaxBlue}], " +
                            $"AreaFilter=[{tool?.MinAreaFilter},{tool?.MaxAreaFilter}], " +
                            $"Exception={ex.Message}");
                    }
                    catch
                    {
                        // ignore logging failures
                    }
                }

                ROI?.Dispose();
                ReduceImage?.Dispose();
                Region?.Dispose();
                ConnectedRegions?.Dispose();
                SelectedRegions?.Dispose();
                RegionUnion?.Dispose();
                ImageR?.Dispose();
                ImageG?.Dispose();
                ImageB?.Dispose();
                RegionR?.Dispose();
                RegionG?.Dispose();
                RegionB?.Dispose();
                RegionIntersection?.Dispose();

                if (!areaCalculated)
                {
                    return -1;
                }
                //tool.ActualDimension = (int)Area.L;
                return (int)Area.L;
            }
            catch (Exception e)
            {
                try { WriteDetailLog($"[面积计算] 外层异常: {e.Message}"); } catch { }
                //tool.ActualDimension = -1;
                return -1;
            }
        }
        #endregion

        public Bitmap GetReducedImage(double W, double H, Bitmap src)
        {
            try
            {
                double _wscale = W / (double)src.Width;
                double _Hscale = H / (double)src.Height;
                double _scale = Math.Min(_wscale, _Hscale);
                W = src.Width * _scale;
                H = src.Height * _scale;
                Bitmap r = new Bitmap((int)W, (int)H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Graphics g = Graphics.FromImage(r);
                g.Clear(Color.Transparent);
                g.DrawImage(src, new Rectangle(0, 0, (int)W, (int)H));
                g.Save();
                g.Dispose();
                return r;
            }
            catch (Exception e)
            {
                return null;
            }

        }

        public HObject GetReducedImage(double W, double H, HObject src)
        {
            HObject dst;
            HOperatorSet.GenEmptyObj(out dst);

            try
            {
                HTuple width = new HTuple();
                HTuple height = new HTuple();

                HOperatorSet.GetImageSize(src, out width, out height);
                double _wscale = W / width;
                double _Hscale = H / height;
                double _scale = Math.Min(_wscale, _Hscale);

                W = width * _scale;
                H = height * _scale;
                HOperatorSet.ZoomImageSize(src, out dst, W, H, "constant");
                //Bitmap r = new Bitmap((int)W, (int)H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                //Graphics g = Graphics.FromImage(r);
                //g.Clear(Color.Transparent);
                //g.DrawImage(src, new Rectangle(0, 0, (int)W, (int)H));
                //g.Save();
                //g.Dispose();
                return dst;
            }
            catch (Exception e)
            {
                return null;
            }

        }

        #region 报工相关方法

        /// <summary>
        /// 刷新上岗人员信息
        /// </summary>
        public async Task RefreshWorkerInfoAsync()
        {
            try
            {
                // 显示正在刷新状态
                DataModel.Processmodel.RefreshWorkerStatus = "正在刷新...";
                writeLog("开始刷新上岗人员信息", false);

                // 异步执行数据库查询，避免阻塞UI
                await Task.Run(() =>
                {
                    // 调用MES系统获取上岗人员信息
                    var workerInfo = MES_ORACLE_DATABASE.MES_ORACLE_DATABASE.get_worker_info(
                        DataModel.Settingmodel.ReportWorkConfig.StationCode,
                        out MES_ORACLE_DATABASE.MES_ORACLE_DATABASE.worker_info worker);

                    // 在UI线程更新界面
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (workerInfo)
                        {
                            DataModel.Processmodel.CurrentWorkerName = worker.worker_name;
                            DataModel.Processmodel.CurrentWorkerID = worker.worker_ID;
                            DataModel.Processmodel.RefreshWorkerStatus = "刷新成功";
                            writeLog($"上岗人员信息刷新成功: {worker.worker_name}({worker.worker_ID})", false);
                        }
                        else
                        {
                            DataModel.Processmodel.CurrentWorkerName = "未上岗";
                            DataModel.Processmodel.CurrentWorkerID = "";
                            DataModel.Processmodel.RefreshWorkerStatus = "未查询到上岗人员";
                            writeLog("未查询到上岗人员信息", false);
                        }

                        // 状态提示保留显示（不再定时清空）
                        Task.Delay(3000).ContinueWith(_ =>
                        {
                            App.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DataModel.Processmodel.RefreshWorkerStatus = "";
                            }));
                        });
                    }));
                });
            }
            catch (Exception ex)
            {
                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    writeError($"刷新上岗人员信息失败: {ex.Message}");
                    DataModel.Processmodel.CurrentWorkerName = "查询失败";
                    DataModel.Processmodel.CurrentWorkerID = "";
                    DataModel.Processmodel.RefreshWorkerStatus = "刷新失败";

                    // 3秒后清除状态提示
                    Task.Delay(3000).ContinueWith(_ =>
                    {
                        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DataModel.Processmodel.RefreshWorkerStatus = "";
                        }));
                    });
                }));
            }
        }

        /// <summary>
        /// 判断PLC触发后的作业资格。普通产品需要有效SN、工序校验通过且点检仍在有效周期内；点检到期后只放行点检SN触发形成的点检模式流程。
        /// </summary>
        /// <returns>true表示当前PLC指令可继续进入视觉检测；false表示停在业务门禁并保留状态与日志提示。</returns>
        private bool HasValidSNAndValidation()
        {
            bool hasSN = !string.IsNullOrEmpty(DataModel.Processmodel.CurrentProductSN);
            
            // 到期门禁以ScanCodeStatus中的点检类型状态作为放行凭据，避免普通产品或错误点检类型借用残留的IsInspectionMode标志进入检测。
            bool isInspectionMode = DataModel.Processmodel.IsInspectionMode;
            bool isInspectionScan = isInspectionMode && DataModel.Processmodel.ScanCodeStatus == $"点检模式-{DataModel.Processmodel.InspectionType}点检";
            bool isValidated = DataModel.Processmodel.ScanCodeStatus == "工序校验通过" || isInspectionScan;
            bool isInspectionRequired = IsInspectionRequired();
            RefreshInspectionProgressForDueCycle();
            bool result = hasSN && (isInspectionRequired ? isInspectionScan : isValidated);

            if (hasSN && isValidated && isInspectionRequired && !isInspectionScan)
            {
                UIInvoke(() => DataModel.Processmodel.ScanCodeStatus = "点检到期");
                UIBeginInvoke(new Action(() =>
                {
                    writeLog(BuildInspectionGateMessage(DataModel.Processmodel.CurrentProductSN), true);
                    ShowInspectionWindow();
                }));
            }

            // 详细调试日志：记录状态检查结果
            WriteDetailLog($"[报工状态] SN={DataModel.Processmodel.CurrentProductSN ?? "空"}, " +
                          $"校验状态={DataModel.Processmodel.ScanCodeStatus}, " +
                          $"点检模式={isInspectionMode}, " +
                          $"点检到期={isInspectionRequired}, " +
                          $"有SN={hasSN}, 校验通过={isValidated}, 最终结果={result}");

            return result;
        }

        /// <summary>
        /// 更新阶段检测状态（用于UI更新和日志输出）
        /// 注意：在OK状态下，阶段状态已在调用此方法前同步更新，这里主要用于日志和UI刷新
        /// </summary>
        /// <param name="command">检测指令</param>
        /// <param name="isSuccess">检测是否成功</param>
        private void UpdateStageStatus(string command, bool isSuccess)
        {
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                // 更新阶段状态（如果之前没有更新过，这里会更新；如果已更新，这里会设置相同的值，是安全的）
                switch (command)
                {
                    case "A1":
                        DataModel.Processmodel.A1StageCompleted = isSuccess;
                        writeLog($"A1阶段检测{(isSuccess ? "成功" : "失败")}", false);
                        break;
                    case "A2":
                        DataModel.Processmodel.A2StageCompleted = isSuccess;
                        writeLog($"A2阶段检测{(isSuccess ? "成功" : "失败")}", false);
                        break;
                    case "ALL":
                        // ALL指令同时更新所有阶段状态
                        DataModel.Processmodel.A1StageCompleted = isSuccess;
                        DataModel.Processmodel.A2StageCompleted = isSuccess;
                        writeLog($"全部检测{(isSuccess ? "成功" : "失败")}", false);
                        break;
                }

                // 如果检测失败，重置报工准备状态
                if (!isSuccess)
                {
                    writeLog("❌ 检测失败，支持重复触发检测直到成功", false);
                }
                else if (DataModel.Processmodel.CanExecuteReportWork)
                {
                    writeLog("✅ 所有阶段检测完成，准备报工", false);
                }
            }));
        }

        /// <summary>
        /// 检查是否可以开始检测
        /// </summary>
        private bool CanStartDetection()
        {
            // 修改：只要不在报工中就可以开始检测，不强制要求工序校验通过
            return !DataModel.Processmodel.IsReportingWork;
        }

        /// <summary>
        /// 执行报工操作
        /// </summary>
        /// <param name="qualityResult">检测结果（合格/不合格）</param>
        /// <param name="status">产品状态（OK/NG），默认为OK</param>
        /// <param name="errorType">错误类型，默认为空</param>
        /// <param name="errorDesc">错误描述，默认为空</param>
        private async Task<bool> ExecuteReportWorkAsync(string qualityResult = "合格", string status = "OK", string errorType = "", string errorDesc = "")
        {
            if (DataModel.Processmodel.IsReportingWork)
            {
                writeLog("报工正在进行中，跳过本次报工", false);
                return false;
            }

            try
            {
                DataModel.Processmodel.IsReportingWork = true;

                // 更新界面状态
                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 报工开始：覆盖当前提示
                    DataModel.Processmodel.ReportWorkStatus = "正在报工...";
                }));

                writeLog($"🔄 开始报工: SN={DataModel.Processmodel.CurrentProductSN}, 结果={qualityResult}, 状态={status}" + 
                    (string.IsNullOrEmpty(errorType) ? "" : $", 错误类型={errorType}"), false);

                var procedureCode = DataModel.Settingmodel.ReportWorkConfig.ProcedureCode;
                procedureCode = string.IsNullOrWhiteSpace(procedureCode) ? "" : procedureCode.Trim();

                var reportResult = MES_ORACLE_DATABASE.MES_ORACLE_DATABASE.Save_EquipmentRecord_mes(
                    DataModel.Settingmodel.ReportWorkConfig.StationCode,     // 工位码
                    "", // 工单号 - 由MES系统内部获取
                    DataModel.Processmodel.CurrentProductSN,                 // 产品SN
                    DataModel.Settingmodel.ReportWorkConfig.ProcedureName,   // 工序名称
                    DataModel.Settingmodel.ReportWorkConfig.EquipmentNumber, // 设备编号
                    qualityResult,                                           // 检测结果
                    procedureCode,                                           // 工序码（为空则传空）
                    status,                                                  // 产品状态（OK/NG）
                    errorType,                                               // 错误类型
                    errorDesc                                                 // 错误描述
                );

                if (reportResult)
                {
                    string logMessage = $"[{DateTime.Now:HH:mm:ss}] SN:{DataModel.Processmodel.CurrentProductSN} 报工成功";
                    if (!string.IsNullOrEmpty(errorType))
                    {
                        logMessage += $" (状态:{status}, 错误:{errorType})";
                    }

                    // 在UI线程更新界面
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DataModel.Processmodel.ReportWorkSuccessLog.Insert(0, logMessage);
                        string statusText = status == "NG" ? "⚠️ 报工成功（NG状态）" : "✅ 报工成功";
                        // 报工状态不再定时清空：保留到下一次报工覆盖，或新SN触发ResetForNewProduct()清空
                        DataModel.Processmodel.ReportWorkStatus = statusText;

                        // 保持最多50条记录
                        while (DataModel.Processmodel.ReportWorkSuccessLog.Count > 50)
                        {
                            DataModel.Processmodel.ReportWorkSuccessLog.RemoveAt(DataModel.Processmodel.ReportWorkSuccessLog.Count - 1);
                        }

                        // 状态提示保留显示（不再定时清空）
                        
                    }));

                    string logPrefix = status == "NG" ? "⚠️" : "✅";
                    writeLog($"{logPrefix} 报工成功: SN={DataModel.Processmodel.CurrentProductSN}, 状态={status}" + 
                        (string.IsNullOrEmpty(errorType) ? "" : $", 错误类型={errorType}"), false);

                    // 清空当前SN和阶段状态，准备下一个产品
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DataModel.Processmodel.CurrentProductSN = string.Empty;
                        DataModel.Processmodel.A1StageCompleted = false;
                        DataModel.Processmodel.A2StageCompleted = false;
                        DataModel.Processmodel.ScanCodeStatus = "等待扫码";
                        // 保留工具状态，便于扫码/拍照结束后即时查看检测结果；新产品SN变化时会统一清除
                        WriteDetailLog($"[报工完成] ");
                    }));

                    return true;
                }
                else
                {
                    // 在UI线程更新界面
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 报工失败信息保留显示（不再定时清空），便于现场确认
                        DataModel.Processmodel.ReportWorkStatus = "❌ 报工失败，失败原因已记录日志";

                        // 状态提示保留显示（不再定时清空）
                        
                    }));

                    writeError($"❌ 报工失败: SN={DataModel.Processmodel.CurrentProductSN}，详细错误信息已记录到报工错误日志");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // 在UI线程更新界面
                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 报工异常信息保留显示（不再定时清空），便于现场确认
                    DataModel.Processmodel.ReportWorkStatus = $"❌ 报工异常: {ex.Message}";

                    // 状态提示保留显示（不再定时清空）
                    
                }));

                writeError($"❌ 报工异常: {ex.Message}");
                return false;
            }
            finally
            {
                DataModel.Processmodel.IsReportingWork = false;
            }
        }



        #endregion

        #region 点检功能

        private void UpdateInspectionStatusMessage(string message)
        {
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                DataModel.Processmodel.ReportWorkStatus = message;
            }));
        }

        private string GetInspectionProgressSummary()
        {
            List<string> summary = new List<string>();
            summary.Add($"OK点检={(DataModel.Processmodel.OKInspectionCompleted ? "已完成" : "未完成")}");
            summary.Add($"NG点检={(DataModel.Processmodel.NGInspectionCompleted ? "已完成" : "未完成")}");
            return string.Join("，", summary);
        }

        private string GetToolStatusSummary(IEnumerable<ToolModel> tools)
        {
            return string.Join(", ", tools.Select(t => $"{t.Name}:{t.ToolStatus}"));
        }

        /// <summary>
        /// 判断当前点检周期是否已经超过配置有效期。首次启动时由CheckInspectionStatus写入起始时间，正常生产按该时间计算24小时有效窗口。
        /// </summary>
        /// <returns>true表示普通产品作业需要先完成点检；false表示普通产品可继续走工单、工序和视觉检测门禁。</returns>
        private bool IsInspectionRequired()
        {
            var lastTime = DataModel.Settingmodel.ReportWorkConfig.LastInspectionTime;
            if (lastTime == DateTime.MinValue)
            {
                return false;
            }

            return (DateTime.Now - lastTime).TotalHours >= InspectionIntervalHours;
        }

        /// <summary>
        /// 在新一轮点检周期到期时重置本轮OK/NG完成标志。该状态只影响点检弹窗和本轮进度显示，历史完成时间仍由LastInspectionTime保留。
        /// </summary>
        private void RefreshInspectionProgressForDueCycle()
        {
            if (!IsInspectionRequired() || !DataModel.Processmodel.OKInspectionCompleted || !DataModel.Processmodel.NGInspectionCompleted)
            {
                return;
            }

            UIInvoke(() =>
            {
                DataModel.Processmodel.OKInspectionCompleted = false;
                DataModel.Processmodel.NGInspectionCompleted = false;
            });
        }

        /// <summary>
        /// 生成普通产品被点检门禁拦截时的日志文本，记录SN以便现场追溯。
        /// </summary>
        /// <param name="sn">MES解析后的产品SN；用于日志定位被拦截的普通产品。</param>
        /// <returns>面向操作员和日志追踪的拦截原因。</returns>
        private string BuildInspectionGateMessage(string sn)
        {
            return $"[点检到期拦截] 24小时点检已到期，SN={sn}；请先完成OK点检和NG点检。";
        }

        /// <summary>
        /// 校验普通产品扫码是否满足点检周期要求。点检SN由扫码入口提前分流，该方法只服务普通产品进入工单和工序校验前的门禁。
        /// </summary>
        /// <param name="sn">MES解析后的普通产品SN，用于生成拦截日志。</param>
        /// <returns>空字符串表示点检周期有效；非空文本表示普通产品需要先完成点检。</returns>
        private string ValidateInspectionGateForProduction(string sn)
        {
            if (!IsInspectionRequired())
            {
                return string.Empty;
            }

            RefreshInspectionProgressForDueCycle();
            return BuildInspectionGateMessage(sn);
        }

        /// <summary>
        /// 初始化点检功能
        /// </summary>
        public void InitializeInspection()
        {
            // 启动时检查点检状态
            CheckInspectionStatus();
            
            // 启动定时器，每分钟更新一次倒计时
            _inspectionTimer = new System.Windows.Threading.DispatcherTimer();
            _inspectionTimer.Interval = TimeSpan.FromMinutes(1);
            _inspectionTimer.Tick += (s, e) =>
            {
                UpdateInspectionCountdown();
                CheckInspectionStatus();
            };
            _inspectionTimer.Start();
            
            // 立即更新一次
            UpdateInspectionCountdown();
        }

        /// <summary>
        /// 根据上次点检完成时间刷新点检弹窗。该检查负责提醒和进度显示，普通产品的实际作业拦截由扫码门禁和PLC作业门禁共同执行。
        /// </summary>
        private void CheckInspectionStatus()
        {
            var lastTime = DataModel.Settingmodel.ReportWorkConfig.LastInspectionTime;
            if (lastTime == DateTime.MinValue)
            {
                // 首次运行，设置为当前时间
                lastTime = DateTime.Now;
                DataModel.Settingmodel.ReportWorkConfig.LastInspectionTime = lastTime;
                SaveSettingModel();
            }
            
            bool isOverdue = IsInspectionRequired();
            RefreshInspectionProgressForDueCycle();
            bool needInspection = isOverdue && 
                                 (!DataModel.Processmodel.OKInspectionCompleted || 
                                  !DataModel.Processmodel.NGInspectionCompleted);
            WriteDetailLog($"[点检检查] overdue={isOverdue}, needInspection={needInspection}, {GetInspectionProgressSummary()}");
            
            if (needInspection && _inspectionWindow == null)
            {
                // 显示强制点检弹窗
                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ShowInspectionWindow();
                }));
            }
            else if (!needInspection && _inspectionWindow != null)
            {
                // 点检已完成，关闭弹窗
                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    CloseInspectionWindow();
                }));
            }
        }

        /// <summary>
        /// 刷新主界面点检倒计时。倒计时与作业门禁共享同一个24小时周期，红色到期状态与普通产品拦截口径一致。
        /// </summary>
        private void UpdateInspectionCountdown()
        {
            var lastTime = DataModel.Settingmodel.ReportWorkConfig.LastInspectionTime;
            if (lastTime == DateTime.MinValue)
            {
                DataModel.Processmodel.TimeToNextInspection = TimeSpan.FromHours(InspectionIntervalHours);
                return;
            }
            
            var elapsed = DateTime.Now - lastTime;
            var remaining = TimeSpan.FromHours(InspectionIntervalHours) - elapsed;
            DataModel.Processmodel.TimeToNextInspection = remaining;
        }

        /// <summary>
        /// 显示点检弹窗
        /// </summary>
        private void ShowInspectionWindow()
        {
            if (_inspectionWindow != null && _inspectionWindow.IsLoaded)
                return;
            
            try
            {
                _inspectionWindow = new F7_L12_Position_Detect.View.InspectionWindow();
                _inspectionWindow.DataContext = this;
                _inspectionWindow.Show();
                UpdateInspectionStatusMessage($"点检已到期：{GetInspectionProgressSummary()}");
                writeLog("⚠️ 点检已到期，请完成OK点检和NG点检", false);
            }
            catch (Exception ex)
            {
                writeError($"显示点检弹窗失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 关闭点检弹窗
        /// </summary>
        public void CloseInspectionWindow()
        {
            if (_inspectionWindow != null)
            {
                _inspectionWindow.Close();
                _inspectionWindow = null;
            }
        }

        /// <summary>
        /// 启动OK点检流程。该动作只设置当前点检类型和操作提示，真正的放行凭据仍来自后续扫描到OK点检SN。
        /// </summary>
        public void StartOKInspection()
        {
            DataModel.Processmodel.IsInspectionMode = true;
            DataModel.Processmodel.InspectionType = "OK";
            DataModel.Processmodel.OKInspectionCompleted = false;
            DataModel.Processmodel.ScanCodeStatus = "等待OK点检SN";
            UpdateInspectionStatusMessage($"开始OK点检，请触发PLC执行检测。当前进度：{GetInspectionProgressSummary()}");
            writeLog("开始OK点检，请使用PLC触发扫码和检测", false);
        }

        /// <summary>
        /// 启动NG点检流程。该动作只设置当前点检类型和操作提示，真正的放行凭据仍来自后续扫描到NG点检SN。
        /// </summary>
        public void StartNGInspection()
        {
            DataModel.Processmodel.IsInspectionMode = true;
            DataModel.Processmodel.InspectionType = "NG";
            DataModel.Processmodel.NGInspectionCompleted = false;
            DataModel.Processmodel.ScanCodeStatus = "等待NG点检SN";
            UpdateInspectionStatusMessage($"开始NG点检，请触发PLC执行检测。当前进度：{GetInspectionProgressSummary()}");
            writeLog("开始NG点检，请使用PLC触发扫码和检测", false);
        }

        /// <summary>
        /// 记录单项点检结果，并在OK和NG均完成后刷新点检完成时间。完成时间写入配置文件，是下一轮24小时周期的唯一起点。
        /// </summary>
        /// <param name="type">本次完成的点检类型，取值为OK或NG，来自当前点检模式的检测结果。</param>
        private void CompleteInspection(string type)
        {
            if (type == "OK")
            {
                DataModel.Processmodel.OKInspectionCompleted = true;
                UpdateInspectionStatusMessage($"OK点检完成。当前进度：{GetInspectionProgressSummary()}");
                writeLog("✅ OK点检完成", false);
            }
            else if (type == "NG")
            {
                DataModel.Processmodel.NGInspectionCompleted = true;
                UpdateInspectionStatusMessage($"NG点检完成。当前进度：{GetInspectionProgressSummary()}");
                writeLog("✅ NG点检完成", false);
            }
            
            // 如果两个都完成了，重置状态并更新计时
            if (DataModel.Processmodel.OKInspectionCompleted && 
                DataModel.Processmodel.NGInspectionCompleted)
            {
                DataModel.Processmodel.IsInspectionMode = false;
                DataModel.Processmodel.InspectionType = null;
                DataModel.Settingmodel.ReportWorkConfig.LastInspectionTime = DateTime.Now;
                SaveSettingModel();
                UpdateInspectionCountdown();
                CloseInspectionWindow();
                UpdateInspectionStatusMessage("✅ 点检全部完成，计时已重置");
                writeLog("✅ 点检全部完成，计时已重置", false);
            }
        }

        #endregion
    }
}
