using F7_L12_Position_Detect.Config;
using F7_L12_Position_Detect.Model;
using F7_L12_Position_Detect.Model.Setting;
using F7_L12_Position_Detect.View;
using GalaSoft.MvvmLight;
using Panuon.WPF.UI;
using System.IO;
using System.Windows.Markup;
using System;
using System.Xml.Serialization;
using System.Xml.Linq;
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
using F7_L12_Position_Detect.Helper;

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

        /// <summary>
        /// 串行化「扫码→MES 解析/校验」流程。
        /// </summary>
        /// <remarks>
        /// 业务意图：
        /// - 现场可能同时存在「扫码测试按钮」「串口常驻监听」「SN 框键盘楔入」等入口，若并发进入 MES，易造成串口占用异常或状态错乱。
        /// - <c>WaitAsync(0)</c>：上一轮未结束则直接丢弃本次并打日志，避免排队堆积。
        /// </remarks>
        private readonly SemaphoreSlim _mesScanProcessLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 表示当前存在一条扫码/MES处理链路。手动点检入口在该链路结束前保持普通产品上下文，避免扫码结果与点检模式切换交叉。
        /// </summary>
        private volatile bool _isScanProcessing;

        private string _lastProductSN = string.Empty;

        /// <summary>
        /// 归一化阶段指令字符串，供阶段列表、工具匹配、阶段结算统一使用。
        /// </summary>
        /// <remarks>
        /// 业务背景：
        /// - 工程 XML 或人工录入可能出现 <c>A1</c>、<c>a1</c>、<c>"A1 "</c> 等写法；若按原字符串比较，会被当成多个阶段，
        ///   进而出现「明明全是 A1 却提示阶段未全部完成」、<c>ConfiguredStages</c> 序列异常（如日志里 <c>A1-&gt;T1-1</c>）等问题。
        /// - 规则：<c>Trim</c> + <c>ToUpperInvariant</c>；空或仅空白视为无阶段指令。
        /// </remarks>
        private static string NormalizeStageCommand(string command)
        {
            return string.IsNullOrWhiteSpace(command)
                ? string.Empty
                : command.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// 从当前工具配置推导“串行阶段列表”。
        /// </summary>
        /// <remarks>
        /// 业务意图：
        /// - 根据 ToolModel.Command 动态形成阶段序列（A1/A2/A3...）。
        /// - 阶段顺序按工具列表首次出现顺序（Distinct 保序）生成，确保与现场配置习惯一致。
        /// - 该方法只负责阶段“配置”，阶段“进度”由 CompletedStages / CurrentStageIndex 管理。
        /// - 构建前对每个 Command 做 <see cref="NormalizeStageCommand"/>，并过滤空串，避免脏字符串拆成多阶段。
        /// </remarks>
        private void BuildConfiguredStages()
        {
            DataModel.Processmodel.ConfiguredStages = DataModel.Processmodel.Tools
                .Select(t => NormalizeStageCommand(t.Command))
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// 重置“阶段进度”但不影响工具显示与SN。
        /// </summary>
        /// <remarks>
        /// 业务意图：
        /// - 报工成功后，只需要清 SN + 扫码状态 + 阶段进度，保留界面上工具 OK/NG 以便人员确认上一件结果。
        /// - 普通生产阶段NG后按配置策略决定从头开始或保留失败阶段；点检阶段由点检状态机保留当前阶段，调参后继续同一SN。
        /// - 不在此处清空 <see cref="Processmodel.ReportWorkStatus"/>：否则报工成功后在 <see cref="ResetProductContext"/> 中会立刻抹掉
        ///   「报工成功」提示。报工文案由 <see cref="ExecuteReportWorkAsync"/>、新扫码入口、<see cref="ClearCurrentProductContext"/> 分别管理。
        /// </remarks>
        private void ResetStageProgress()
        {
            DataModel.Processmodel.CompletedStages.Clear();
            DataModel.Processmodel.CurrentStageIndex = 0;
            DataModel.Processmodel.NeedRestart = false;
        }

        /// <summary>
        /// 重置“检测周期”：清空工具运行态 + 清空阶段进度。
        /// </summary>
        /// <remarks>
        /// 触发场景（业务约束）：\n
        /// - 任一阶段 NG/NG2 后，下次触发必须从 A1 重跑（避免残留 OK 状态导致误判）。\n
        /// - 扫码工序校验重新通过（包含同一 SN 重新扫码）后，需要清空工具状态，从头开始检测。
        /// </remarks>
        private void ResetDetectionCycle()
        {
            ClearTools();
            ResetStageProgress();
            DataModel.Processmodel.Status = ToolStatus.等待中;
        }

        /// <summary>
        /// 重置“产品上下文”：清 SN + 扫码状态 + 阶段进度（不清工具显示）。
        /// </summary>
        /// <remarks>
        /// 触发场景（业务约束）：\n
        /// - 报工成功：清理本件产品上下文，等待下一件扫码；保留工具 OK/NG 便于追溯。\n
        /// - 手动“清空SN”：会先 ResetDetectionCycle 清工具，再 ResetProductContext 清产品上下文。
        /// </remarks>
        private void ResetProductContext()
        {
            _lastProductSN = string.Empty;
            ResetStageProgress();
            DataModel.Processmodel.CurrentProductSN = string.Empty;
            DataModel.Processmodel.ScanCodeStatus = "等待扫码";
            DataModel.Processmodel.PendingInspectionType = null;
            DataModel.Processmodel.IsInspectionMode = false;
            DataModel.Processmodel.InspectionType = null;
        }

        /// <summary>
        /// 当扫码解析出 SN 后，准备进入新一件产品的检测（即使 SN 相同也要重置）。
        /// </summary>
        /// <remarks>
        /// 业务意图：
        /// - 现场存在“同一 SN 重新扫码/重新校验”的情况，此时也必须清空工具运行态与阶段进度。
        /// - 因此取消“同 SN 跳过重置”的优化，始终走 ResetDetectionCycle。
        /// </remarks>
        private void ResetForNewProduct(string newProductSN)
        {
            if (string.IsNullOrEmpty(newProductSN))
            {
                return;
            }

            _lastProductSN = newProductSN;

            App.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                ResetDetectionCycle();
            }));
        }

        /// <summary>
        /// 手动清空当前产品：工具结果、SN、扫码状态、阶段进度及报工提示。
        /// </summary>
        /// <remarks>
        /// 业务意图：
        /// - 与「报工成功后仅 ResetProductContext」不同：操作员主动清空表示放弃当前件上下文，应同步清工具显示与报工提示，避免误读上一枪结果。
        /// - 必须在 UI 线程投递执行，与绑定属性更新一致。
        /// </remarks>
        public void ClearCurrentProductContext()
        {
            App.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                ResetDetectionCycle();
                ResetProductContext();
                DataModel.Processmodel.ReportWorkStatus = string.Empty;
            }));
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
                            if (!SwitchCameraForTool(DataModel.Processmodel.Tools[i]))
                            {
                                writeLog($"机器人->视觉:{cmd}指定相机不可用，本次停止触发", false);
                                return;
                            }

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
        /// 按指令范围清理工具的运行态数据（不会改变工具配置）。
        /// </summary>
        /// <param name="command">指令名；传入 "ALL" 表示清理全部工具。</param>
        /// <remarks>
        /// 业务意图：
        /// - 阶段触发前需把本阶段工具的识别结果、状态位复位，避免沿用上一张图的结果。
        /// - 与 <see cref="BuildCameraGroupsForStageCommand"/> 一致，对 command 与工具 Command 做 <see cref="NormalizeStageCommand"/> 再比较，
        ///   保证与阶段列表、结算逻辑使用同一套阶段键。
        /// </remarks>
        public void ClearToolsByCommand(string command)
        {
            string normalizedCommand = NormalizeStageCommand(command);
            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                string toolCommand = NormalizeStageCommand(DataModel.Processmodel.Tools[i].Command);
                if (normalizedCommand == "ALL" || toolCommand == normalizedCommand)
                {
                    ClearTool(DataModel.Processmodel.Tools[i]);
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
        /// 根据工具配置的完整序列号切换相机。指定相机离线时返回失败，调用方据此停止拍照触发。
        /// </summary>
        /// <param name="tool">准备执行的工具配置。</param>
        /// <returns>指定相机已就绪时返回 true；序列号为空、相机离线或切换异常时返回 false。</returns>
        private bool SwitchCameraForTool(ToolModel tool)
        {
            try
            {
                // 检查工具是否配置了相机序列号
                if (string.IsNullOrEmpty(tool.CameraSerialNumber))
                {
                    writeLog($"工具{tool.Index}未配置相机序列号，请在工具设置中选择相机", false);
                    DataModel.Processmodel.CameraStatus = "工具未配置相机";
                    return false;
                }

                var cameraData = DataModel.Settingmodel.camedata;
                var camera = cameraData.CameraModel.camera;

                WriteCameraDebugLog($"[工具相机切换] 工具序号={tool.Index} 工具相机序列号='{tool.CameraSerialNumber}' 可用相机数量={camera.cbDeviceList?.Count ?? -1} 当前所选相机下标={camera.SelectedIndex}");

                // 查找匹配的相机索引
                int targetIndex = -1;
                for (int j = 0; j < camera.cbDeviceList.Count; j++)
                {
                    string availableSerialNumber = NormalizeCameraSerial(Hikvision.ExtractSerialNumberFromDisplayName(camera.cbDeviceList[j]));
                    if (string.Equals(availableSerialNumber, NormalizeCameraSerial(tool.CameraSerialNumber), StringComparison.Ordinal))
                    {
                        targetIndex = j;
                        break;
                    }
                }

                bool requiresReopen = _cameraRecoveryRequired || !camera.IsOpen;
                if (targetIndex >= 0 && (camera.SelectedIndex != targetIndex || requiresReopen))
                {
                    var switchStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    WriteCameraDebugLog(
                        $"[工具相机切换] 准备相机 目标下标={targetIndex} 目标设备='{camera.cbDeviceList[targetIndex]}' " +
                        $"恢复重开={_cameraRecoveryRequired} 当前打开={camera.IsOpen}");
                    writeLog($"工具{tool.Index}切换到相机: {camera.cbDeviceList[targetIndex]}", false);

                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    long closeElapsedMs = 0;
                    bool closeCompleted = true;
                    if (camera.IsOpen)
                    {
                        camera.bnClose_Click();
                        while (stopwatch.ElapsedMilliseconds < 3000 && camera.IsOpen)
                        {
                            Thread.Sleep(50);
                        }
                        closeElapsedMs = stopwatch.ElapsedMilliseconds;
                        closeCompleted = !camera.IsOpen;
                        if (!closeCompleted)
                        {
                            DataModel.Processmodel.CameraStatus = "相机关闭失败";
                            WriteCameraDebugLog($"[工具相机切换] 关闭超时 工具序号={tool.Index} 耗时={closeElapsedMs}ms");
                            return false;
                        }
                    }

                    camera.SelectedIndex = targetIndex;
                    camera.bnOpen_Click();
                    stopwatch.Restart();
                    while (stopwatch.ElapsedMilliseconds < 3000 && !camera.IsOpen)
                    {
                        Thread.Sleep(50);
                    }
                    long openElapsedMs = stopwatch.ElapsedMilliseconds;
                    bool openCompleted = camera.IsOpen;
                    if (!openCompleted)
                    {
                        DataModel.Processmodel.CameraStatus = "相机打开失败";
                        WriteCameraDebugLog($"[工具相机切换] 打开失败 工具序号={tool.Index} 耗时={openElapsedMs}ms");
                        return false;
                    }

                    camera.bnStartGrab_Click();
                    if (!camera.IsGrabbing)
                    {
                        DataModel.Processmodel.CameraStatus = "相机取流失败";
                        WriteCameraDebugLog($"[工具相机切换] 取流失败 工具序号={tool.Index}");
                        return false;
                    }

                    _cameraRecoveryRequired = false;
                    writeLog($"工具{tool.Index}相机切换完成", false);
                    WriteCameraDebugLog(
                        $"[工具相机切换耗时] 工具序号={tool.Index} 关闭={closeElapsedMs}ms(完成={closeCompleted}) " +
                        $"打开={openElapsedMs}ms(完成={openCompleted}) 总耗时={switchStopwatch.ElapsedMilliseconds}ms");
                    return true;
                }
                else if (targetIndex >= 0)
                {
                    if (!camera.IsGrabbing)
                    {
                        camera.bnStartGrab_Click();
                        if (!camera.IsGrabbing)
                        {
                            DataModel.Processmodel.CameraStatus = "相机取流失败";
                            WriteCameraDebugLog($"[工具相机切换] 当前相机恢复取流失败 工具序号={tool.Index}");
                            return false;
                        }
                    }

                    WriteCameraDebugLog($"[工具相机切换] 匹配成功 目标下标={targetIndex} 当前相机已就绪");
                    writeLog($"工具{tool.Index}相机已就绪", false);
                    return true;
                }
                else
                {
                    WriteCameraDebugLog($"[工具相机切换] 匹配失败 未在当前枚举列表找到完整相机序列号='{tool.CameraSerialNumber}' 本次停止触发");
                    writeLog($"工具{tool.Index}指定的相机序列号{tool.CameraSerialNumber}未找到，请检查相机连接", false);
                    DataModel.Processmodel.CameraStatus = "指定相机离线";
                    return false;
                }
            }
            catch (Exception ex)
            {
                writeLog($"工具{tool.Index}相机切换失败: {ex.Message}", false);
                DataModel.Processmodel.CameraStatus = "相机切换失败";
                return false;
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
        private DateTime _executionStartTime = DateTime.MinValue;
        private bool _processingImage;
        private bool _manualImageTestInProgress;
        private long _executionSequence;
        private long _activeExecutionId;
        private long _captureRequestSequence;
        private long _expectedCaptureSessionId;
        private long _expectedCaptureRequestId;
        private volatile bool _cameraRecoveryRequired;
        private int ExecutionTimeoutMs => Math.Max(500, DataModel?.Settingmodel?.ExecutionTimeoutMs ?? 5000);
        private int _plcProbeCounter = 0;

        // 扫码自动触发阶段执行：用独立互斥与取消源，避免影响现有 PLC 互斥逻辑
        private CancellationTokenSource _scanAutoTriggerCts = null;
        private readonly object _scanAutoLock = new object();

        private bool TryConnectPLCOnce(out string message)
        {
            message = string.Empty;
            try
            {
                ModbusTcpNet modbusTcp = new ModbusTcpNet();
                modbusTcp.ConnectTimeOut = 1000;
                modbusTcp.ReceiveTimeOut = 1000;
                modbusTcp.IpAddress = DataModel.Settingmodel.PLC_IP;
                modbusTcp.Port = DataModel.Settingmodel.PLC_Port;
                modbusTcp.DataFormat = HslCommunication.Core.DataFormat.CDAB;

                var connect = modbusTcp.ConnectServer();
                if (!connect.IsSuccess)
                {
                    message = connect.Message;
                    try { modbusTcp.ConnectClose(); } catch { }
                    return false;
                }

                var result = modbusTcp.ReadUInt16(DataModel.Settingmodel.AddressStart.ToString(), 1);
                try { modbusTcp.ConnectClose(); } catch { }

                if (!result.IsSuccess)
                {
                    message = result.Message;
                    return false;
                }

                message = "OK";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        /*
         * 阶段内多相机取图（同一次 PLC 触发的扩展）
         *
         * 业务规则：
         * - PLC 只触发一次“下一步”，视觉端在一个阶段内可能需要多次取图：
         *   - 同一阶段工具若配置了不同相机序列号，则按相机分组，每个相机取图一次；
         *   - 同一相机分组内的多个工具，共用这一张图做识别（同图多工具）；
         *   - 曝光取该组工具列表中的首个工具，同组其余工具即使曝光配置不同也共用该帧。
         *
         * 时序与收口：
         * - ProcessSerialTrigger() 初始化 _pendingCameraGroups，并触发第一组相机取图；
         * - 相机回调受理时冻结执行代次、阶段、相机和实际曝光，OnReceiveProcess() 只处理该上下文匹配的工具；
         * - OnReceiveProcess() finally 中：
         *     - 若还有相机组未处理，则继续触发下一组取图（不 EndExecution）；
         *     - 若 TriggerNextCameraGroupCapture 返回 false 或抛异常，立即 NG、NeedRestart、EndExecution，不等超时看门狗；
         *     - 若相机组处理完毕，才 EndExecution() 释放 PLC 执行互斥窗口。
         *
         * 等图门闩（_waitingImage）：
         * - 仅在成功软触发 bnTriggerExec 前置 true；相机线程 OnReceive 入口 TryAcceptImageCallback 原子消费为 false。
         * - 非执行态、或非“等图”态到达的回调直接丢弃，减少晚到帧误入当前轮（日志用 WriteDetailLog，不作为拦截依据）。
         * - 手动“照片测试”使用独立上下文，只执行选中工具，并跳过生产留图、设备发送、阶段结算和报工。
         *
         * 注意：
         * - 当前实现为“串行多相机”（依次取图），避免并行回调竞态；
         * - NormalizeCameraSerial 将 null 视为空串，并统一去除首尾空格和字母大小写。
         */
        private sealed class CameraGroup
        {
            public string CameraSerial { get; }
            public List<int> ToolIndices { get; }
            public CameraGroup(string cameraSerial, List<int> toolIndices)
            {
                CameraSerial = cameraSerial ?? string.Empty;
                ToolIndices = toolIndices ?? new List<int>();
            }
        }

        /// <summary>
        /// 冻结一张回图所属的执行代次、阶段、相机和实际曝光。UI 排队期间即使现场触发了新一轮执行，旧帧也只能处理和释放自己的代次。
        /// ExposureTime 仅记录本组首工具实际下发的曝光，用于诊断日志，不参与工具匹配。
        /// </summary>
        private sealed class FrameProcessingContext
        {
            public long ExecutionId { get; }
            public string Command { get; }
            public string CameraSerial { get; }
            public int? ExposureTime { get; }
            public string ProductSerialNumber { get; }
            /// <summary>回图受理时冻结的点检标准件期望类别；普通生产和照片测试使用空值。</summary>
            public string InspectionExpectedResult { get; }
            public long CaptureSessionId { get; }
            public long CaptureRequestId { get; }
            public bool IsProduction { get; }
            public int ManualToolIndex { get; }

            private FrameProcessingContext(
                long executionId,
                string command,
                string cameraSerial,
                int? exposureTime,
                string productSerialNumber,
                long captureSessionId,
                long captureRequestId,
                bool isProduction,
                int manualToolIndex,
                string inspectionExpectedResult = null)
            {
                ExecutionId = executionId;
                Command = command ?? string.Empty;
                CameraSerial = cameraSerial;
                ExposureTime = exposureTime;
                ProductSerialNumber = productSerialNumber ?? string.Empty;
                InspectionExpectedResult = inspectionExpectedResult ?? string.Empty;
                CaptureSessionId = captureSessionId;
                CaptureRequestId = captureRequestId;
                IsProduction = isProduction;
                ManualToolIndex = manualToolIndex;
            }

            public static FrameProcessingContext Production(
                long executionId,
                string command,
                string cameraSerial,
                int? exposureTime,
                string productSerialNumber,
                long captureSessionId,
                long captureRequestId,
                string inspectionExpectedResult)
            {
                return new FrameProcessingContext(
                    executionId,
                    command,
                    cameraSerial,
                    exposureTime,
                    productSerialNumber,
                    captureSessionId,
                    captureRequestId,
                    true,
                    -1,
                    inspectionExpectedResult);
            }

            public static FrameProcessingContext Manual(string command, int toolIndex)
            {
                return new FrameProcessingContext(0, command, null, null, string.Empty, 0, 0, false, toolIndex);
            }
        }

        private Queue<CameraGroup> _pendingCameraGroups = null;
        private long _imageSaveSequence;
        /// <summary>等图门闩：仅在一次软触发之后为 true，回调入口原子消费为 false，避免非预期帧进入处理。</summary>
        private bool _waitingImage = false;
        private string _activeCameraSerial = null; // null 表示不按相机过滤（兼容旧逻辑）
        private int? _activeExposureTime = null; // 本组首工具实际下发的曝光，仅写入帧上下文供诊断

        /// <summary>
        /// 归一化相机序列号：将 null 统一视为空串，并去除首尾空格、统一为大写，确保分组和精确匹配口径一致。
        /// </summary>
        private static string NormalizeCameraSerial(string cameraSerial)
        {
            return (cameraSerial ?? string.Empty).Trim().ToUpperInvariant();
        }

        /// <summary>
        /// 构建当前阶段的拍照分组队列。同一相机序列号的工具共享一帧，并保持工具配置中的首次出现顺序。
        /// </summary>
        /// <remarks>
        /// - 仅收集 <paramref name="command"/> 对应阶段的工具；
        /// - 按工具列表的首次出现顺序稳定输出分组（避免 Dictionary 枚举导致顺序不稳定）。
        /// - 工具是否属于本阶段：对 <paramref name="command"/> 与 <see cref="ToolModel.Command"/> 均做 <see cref="NormalizeStageCommand"/> 后相等判断，
        ///   与 <see cref="BuildConfiguredStages"/>、阶段结算一致。
        /// - 曝光不参与分组；触发时取组内首个工具的曝光。
        /// </remarks>
        private Queue<CameraGroup> BuildCameraGroupsForStageCommand(string command)
        {
            // 按工具列表顺序稳定分组：同一阶段内不同相机 -> 多次取图
            var groups = new Dictionary<string, List<int>>();
            var groupOrder = new List<string>();
            string normalizedCommand = NormalizeStageCommand(command);

            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                var tool = DataModel.Processmodel.Tools[i];
                if (NormalizeStageCommand(tool.Command) != normalizedCommand) continue;

                string cam = NormalizeCameraSerial(tool.CameraSerialNumber);
                if (!groups.TryGetValue(cam, out var list))
                {
                    list = new List<int>();
                    groups[cam] = list;
                    groupOrder.Add(cam);
                }
                list.Add(i);
            }

            var q = new Queue<CameraGroup>();
            foreach (var cam in groupOrder)
            {
                q.Enqueue(new CameraGroup(cam, groups[cam]));
            }

            WriteCameraDebugLog(
                $"[阶段拍照分组] 指令='{normalizedCommand}' 分组数={q.Count} " +
                $"顺序=[{string.Join(" -> ", q.Select(group => $"{group.CameraSerial}/工具{group.ToolIndices.Count}"))}]");
            return q;
        }

        /// <summary>
        /// 触发队列中的下一相机分组取图。
        /// </summary>
        /// <remarks>
        /// 该方法只负责“切相机 + 设置曝光 + 触发一次取图”，
        /// 工具识别与阶段结算在相机回调 <see cref="OnReceiveProcess"/> 中完成。
        /// 软触发前在锁内置 <see cref="_waitingImage"/> 为 true，供 <see cref="TryAcceptImageCallback"/> 校验。
        /// </remarks>
        private bool TriggerNextCameraGroupCapture(string command, long? expectedExecutionId = null)
        {
            if (expectedExecutionId.HasValue)
            {
                lock (_executionLock)
                {
                    if (!_isExecuting || _activeExecutionId != expectedExecutionId.Value)
                    {
                        WriteDetailLog($"[阶段触发下一相机组] 执行代次已失效，跳过触发 expected={expectedExecutionId.Value} active={_activeExecutionId}");
                        return false;
                    }
                }
            }

            if (_pendingCameraGroups == null || _pendingCameraGroups.Count == 0)
            {
                _activeCameraSerial = null;
                _activeExposureTime = null;
                return false;
            }

            var group = _pendingCameraGroups.Dequeue();
            _activeCameraSerial = group.CameraSerial;

            if (group.ToolIndices.Count == 0)
            {
                return TriggerNextCameraGroupCapture(command, expectedExecutionId);
            }

            int firstToolIndex = group.ToolIndices[0];
            var firstTool = DataModel.Processmodel.Tools[firstToolIndex];
            int groupExposureTime = firstTool.ExposureTime;
            _activeExposureTime = groupExposureTime;

            DataModel.Processmodel.ToolIndex = firstToolIndex + 1;

            // 切换到该相机并按该组第一个工具设置曝光
            Stopwatch preparationStopwatch = Stopwatch.StartNew();
            WriteCameraDebugLog($"[阶段触发下一相机组] 指令='{command}' 当前激活相机序列号='{_activeCameraSerial}' 曝光={groupExposureTime}(首工具) 本组工具数={group.ToolIndices.Count} 首工具下标={firstToolIndex} 首工具相机序列号='{firstTool.CameraSerialNumber}'");
            if (!SwitchCameraForTool(firstTool))
            {
                DataModel.Processmodel.Status = ToolStatus.NG;
                DataModel.Processmodel.NeedRestart = true;
                WriteDetailLog($"[阶段触发下一相机组] 指定相机不可用 工具下标={firstToolIndex} 序列号='{firstTool.CameraSerialNumber}'");
                return false;
            }
            Thread.Sleep(100);
            DataModel.Settingmodel.camedata.CameraModel.exposuretime = groupExposureTime;
            Hikvision camera = DataModel.Settingmodel.camedata.CameraModel.camera;
            camera.Exposure = groupExposureTime;
            if (!camera.TrySetExposure(groupExposureTime, out int exposureErrorCode))
            {
                DataModel.Processmodel.Status = ToolStatus.NG;
                DataModel.Processmodel.NeedRestart = true;
                DataModel.Processmodel.CameraStatus = "相机曝光设置失败";
                _cameraRecoveryRequired = true;
                WriteCameraDebugLog(
                    $"[阶段曝光设置] 失败 指令='{command}' 相机='{_activeCameraSerial}' " +
                    $"曝光={groupExposureTime} 错误码=0x{exposureErrorCode:X8}");
                return false;
            }
            int configuredDelayMs = Math.Max(0, DataModel.Settingmodel.delaytime);
            Thread.Sleep(configuredDelayMs);

            writeLog($"开始拍照: {command}阶段 - 相机='{_activeCameraSerial}', 工具数={group.ToolIndices.Count}");
            WriteCameraDebugLog(
                $"[阶段拍照准备] 指令='{command}' 相机='{_activeCameraSerial}' 曝光={groupExposureTime}(首工具) " +
                $"配置等待={configuredDelayMs}ms 准备总耗时={preparationStopwatch.ElapsedMilliseconds}ms");
            long captureSessionId = camera.ActiveCaptureSessionId;
            long captureRequestId = Interlocked.Increment(ref _captureRequestSequence);
            if (captureSessionId <= 0)
            {
                DataModel.Processmodel.Status = ToolStatus.NG;
                DataModel.Processmodel.NeedRestart = true;
                DataModel.Processmodel.CameraStatus = "相机会话无效";
                _cameraRecoveryRequired = true;
                WriteCameraDebugLog($"[阶段软触发] 相机会话无效 指令='{command}' 请求={captureRequestId}");
                return false;
            }

            lock (_executionLock)
            {
                if (expectedExecutionId.HasValue && (!_isExecuting || _activeExecutionId != expectedExecutionId.Value))
                {
                    WriteDetailLog($"[阶段拍照准备] 执行代次在软触发前失效，跳过触发 expected={expectedExecutionId.Value} active={_activeExecutionId}");
                    return false;
                }

                _executionStartTime = DateTime.UtcNow;
                _expectedCaptureSessionId = captureSessionId;
                _expectedCaptureRequestId = captureRequestId;
                _waitingImage = true;
            }

            if (!camera.TryTriggerExec(captureRequestId, out int triggerErrorCode))
            {
                lock (_executionLock)
                {
                    if (_expectedCaptureSessionId == captureSessionId
                        && _expectedCaptureRequestId == captureRequestId)
                    {
                        _waitingImage = false;
                        _expectedCaptureSessionId = 0;
                        _expectedCaptureRequestId = 0;
                    }
                }

                _cameraRecoveryRequired = true;
                DataModel.Processmodel.Status = ToolStatus.NG;
                DataModel.Processmodel.NeedRestart = true;
                DataModel.Processmodel.CameraStatus = "相机软触发失败";
                WriteCameraDebugLog(
                    $"[阶段软触发] 失败 指令='{command}' 会话={captureSessionId} 请求={captureRequestId} " +
                    $"错误码=0x{triggerErrorCode:X8}");
                return false;
            }

            WriteCameraDebugLog(
                $"[阶段软触发] 成功 指令='{command}' 会话={captureSessionId} 请求={captureRequestId}");
            return true;
        }

        /// <summary>
        /// 对外提供照片测试按钮的可用状态。生产执行、扫码/MES 和手工测试共用一把互斥锁，避免测试图改变现场产品与检测状态。
        /// </summary>
        public bool CanRunManualImageTest
        {
            get
            {
                lock (_executionLock)
                {
                    return !_isExecuting && !_manualImageTestInProgress && !_isScanProcessing;
                }
            }
        }

        private void NotifyCanRunManualImageTestChanged()
        {
            Action notify = () => RaisePropertyChanged(() => CanRunManualImageTest);
            if (System.Windows.Application.Current?.Dispatcher == null
                || System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                notify();
            }
            else
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(notify);
            }
        }

        /// <summary>
        /// 尝试进入单次阶段执行窗口，防止 PLC、扫码/MES、扫码自动执行和照片测试并发修改产品与检测状态。
        /// </summary>
        /// <param name="executionId">成功进入后返回本轮唯一执行代次，用于隔离超时帧和迟到帧。</param>
        /// <returns>成功进入执行态时返回 true；已有生产执行、扫码/MES 或照片测试时返回 false。</returns>
        private bool TryBeginExecution(out long executionId)
        {
            executionId = 0;
            lock (_executionLock)
            {
                if (_isExecuting || _manualImageTestInProgress || _isScanProcessing) return false;
                _isExecuting = true;
                _waitingImage = false;
                _processingImage = false;
                _activeExecutionId = ++_executionSequence;
                _expectedCaptureSessionId = 0;
                _expectedCaptureRequestId = 0;
                executionId = _activeExecutionId;
                _executionStartTime = DateTime.UtcNow;
            }

            NotifyCanRunManualImageTestChanged();
            return true;
        }

        private bool TryBeginManualImageTest()
        {
            lock (_executionLock)
            {
                if (_isExecuting || _manualImageTestInProgress || _isScanProcessing)
                {
                    return false;
                }

                _manualImageTestInProgress = true;
            }

            NotifyCanRunManualImageTestChanged();
            return true;
        }

        private void EndManualImageTest()
        {
            lock (_executionLock)
            {
                _manualImageTestInProgress = false;
            }

            NotifyCanRunManualImageTestChanged();
        }

        /// <summary>
        /// 相机 SDK 线程上调用：仅当处于 PLC 执行窗口且本帧为“软触发所等待的那一帧”时放行，并冻结本帧执行上下文。
        /// </summary>
        private bool TryAcceptImageCallback(MyEventArgs imageEvent, out FrameProcessingContext context, out string dropReason)
        {
            context = null;
            lock (_executionLock)
            {
                if (!_isExecuting)
                {
                    dropReason = "当前不在执行态";
                    return false;
                }

                if (!_waitingImage)
                {
                    dropReason = "当前不在等待图像";
                    return false;
                }

                if (imageEvent == null
                    || imageEvent.CaptureSessionId != _expectedCaptureSessionId
                    || imageEvent.CaptureRequestId != _expectedCaptureRequestId)
                {
                    dropReason =
                        $"回图请求不匹配 expected={_expectedCaptureSessionId}/{_expectedCaptureRequestId} " +
                        $"actual={imageEvent?.CaptureSessionId ?? 0}/{imageEvent?.CaptureRequestId ?? 0} " +
                        $"frame={imageEvent?.FrameNumber ?? 0}";
                    return false;
                }

                _waitingImage = false;
                _processingImage = true;
                context = FrameProcessingContext.Production(
                    _activeExecutionId,
                    NormalizeStageCommand(DataModel.Processmodel.RCMD),
                    _activeCameraSerial,
                    _activeExposureTime,
                    DataModel.Processmodel.CurrentProductSN,
                    imageEvent.CaptureSessionId,
                    imageEvent.CaptureRequestId,
                    DataModel.Processmodel.IsInspectionMode ? DataModel.Processmodel.InspectionType : null);
                dropReason = string.Empty;
                return true;
            }
        }

        private bool IsExecutionContextActive(FrameProcessingContext context)
        {
            if (context == null || !context.IsProduction)
            {
                return false;
            }

            lock (_executionLock)
            {
                return _isExecuting && _activeExecutionId == context.ExecutionId;
            }
        }

        private bool CompleteFrameProcessing(FrameProcessingContext context)
        {
            lock (_executionLock)
            {
                if (context == null || !context.IsProduction || !_isExecuting || _activeExecutionId != context.ExecutionId)
                {
                    return false;
                }

                _processingImage = false;
                return true;
            }
        }

        /// <summary>
        /// 仅结束仍在等待当前软触发回图的执行代次。正在 UI 线程处理的图像保持到帧尾，避免看门狗释放后旧帧污染下一轮。
        /// </summary>
        private bool TryTimeoutWaitingExecution(out long executionId, out string command)
        {
            executionId = 0;
            command = string.Empty;
            bool timedOut = false;

            lock (_executionLock)
            {
                if (!_isExecuting || !_waitingImage || _processingImage
                    || (DateTime.UtcNow - _executionStartTime).TotalMilliseconds <= ExecutionTimeoutMs)
                {
                    return false;
                }

                executionId = _activeExecutionId;
                command = DataModel.Processmodel.RCMD;
                DataModel.Processmodel.NeedRestart = true;
                DataModel.Processmodel.Status = ToolStatus.NG;
                DataModel.Processmodel.CameraStatus = "读图超时";
                _cameraRecoveryRequired = true;
                _isExecuting = false;
                _waitingImage = false;
                _processingImage = false;
                _activeExecutionId = 0;
                _expectedCaptureSessionId = 0;
                _expectedCaptureRequestId = 0;
                _pendingCameraGroups = null;
                _activeCameraSerial = null;
                _activeExposureTime = null;
                timedOut = true;
            }

            if (timedOut)
            {
                QueueInspectionStatusRefresh();
            }

            return timedOut;
        }

        /// <summary>
        /// 结束当前执行窗口，释放 PLC 指令互斥状态。传入执行代次时，旧帧只能结束自己的执行窗口。
        /// </summary>
        private bool EndExecution(long? expectedExecutionId = null)
        {
            lock (_executionLock)
            {
                if (!_isExecuting || (expectedExecutionId.HasValue && _activeExecutionId != expectedExecutionId.Value))
                {
                    return false;
                }

                _isExecuting = false;
                _waitingImage = false;
                _processingImage = false;
                _activeExecutionId = 0;
                _expectedCaptureSessionId = 0;
                _expectedCaptureRequestId = 0;
                _pendingCameraGroups = null;
                _activeCameraSerial = null;
                _activeExposureTime = null;
            }

            NotifyCanRunManualImageTestChanged();
            QueueInspectionStatusRefresh();
            return true;
        }

        public void PLC_Start()
        {
            Thread t = new Thread(PLC_Process);
            t.Start();

            //Thread t2 = new Thread(shakehand2);
            //t2.Start();
        }

        /// <summary>
        /// PLC 轮询主循环：在报工条件满足时读取触发地址并驱动视觉执行。
        /// </summary>
        /// <remarks>
        /// 该循环同时承担执行态“超时看门狗”，用于相机无回调时兜底释放互斥并标记阶段 NG。
        /// </remarks>
        private void PLC_Process()
        {
            int statusLogCounter = 0; // 用于控制状态日志频率
            int lastTriggerValue = 0;

            // 启动即探测一次PLC连接状态（不依赖SN/工序校验）
            try
            {
                if (TryConnectPLCOnce(out string msg))
                {
                    DataModel.Processmodel.Trig_IO.IOstatus = 0;
                    writeLog($"[启动] PLC连接测试: 成功 ({DataModel.Settingmodel.PLC_IP}:{DataModel.Settingmodel.PLC_Port})", false);
                }
                else
                {
                    DataModel.Processmodel.Trig_IO.IOstatus = -1;
                    writeLog($"[启动] PLC连接测试: 失败 ({DataModel.Settingmodel.PLC_IP}:{DataModel.Settingmodel.PLC_Port}) - {msg}", false);
                }
            }
            catch (Exception ex)
            {
                DataModel.Processmodel.Trig_IO.IOstatus = -1;
                writeError($"[启动] PLC连接测试异常: {ex.Message}");
            }

            while (true)
            {
                // ========== 周期性状态日志 ==========
                statusLogCounter++;
                if (statusLogCounter >= 300) // 每30秒记录一次状态 (300 * 100ms = 30s)
                {
                    statusLogCounter = 0;
                    bool hasValidSN = HasValidSNAndValidation();
                    writeLog($"PLC状态: 报工={hasValidSN}, SN={DataModel.Processmodel.CurrentProductSN}, 执行={_isExecuting}");
                    WriteDetailLog($"[PLC状态] 报工状态={hasValidSN}, 当前SN={DataModel.Processmodel.CurrentProductSN}, 执行状态={_isExecuting}, 界面触发标志={DataModel.Processmodel.Trig_IO.IOstatus}");
                }

                // 超时看门狗只处理“已发软触发、仍等待回图”的代次；UI 正在处理回图时保持执行锁到帧尾。
                if (TryTimeoutWaitingExecution(out long timeoutExecutionId, out string timeoutCmd))
                {
                    writeError($"执行超时({ExecutionTimeoutMs}ms): {timeoutCmd}指令强制释放");
                    WriteDetailLog($"[超时看门狗] 代次{timeoutExecutionId} 指令{timeoutCmd}等待回图超过{ExecutionTimeoutMs}ms，标记NG");
                    if (DataModel.Processmodel.IsInspectionMode)
                    {
                        UpdateInspectionStatusMessage($"{DataModel.Processmodel.InspectionType}点检阶段{timeoutCmd}等待回图超时，请调参后重复触发当前阶段。 ");
                    }
                    NotifyCanRunManualImageTestChanged();
                }

                // 检查报工模块状态：只有有SN且工序校验成功才进行PLC轮询
                if (!HasValidSNAndValidation())
                {
                    // 无SN/未校验时也要周期性探测PLC连接状态，方便现场判断
                    _plcProbeCounter++;
                    if (_plcProbeCounter >= 2) // 约2秒一次（无SN时每次sleep 1000ms）
                    {
                        _plcProbeCounter = 0;
                        try
                        {
                            if (TryConnectPLCOnce(out _))
                            {
                                DataModel.Processmodel.Trig_IO.IOstatus = 0;
                            }
                            else
                            {
                                DataModel.Processmodel.Trig_IO.IOstatus = -1;
                            }
                        }
                        catch
                        {
                            DataModel.Processmodel.Trig_IO.IOstatus = -1;
                        }
                    }
                    Thread.Sleep(1000); // 等待状态满足，减少CPU占用
                    continue;
                }
                _plcProbeCounter = 0;

                ModbusTcpNet modbusTcp = new ModbusTcpNet();
                try
                {
                    modbusTcp.ConnectTimeOut = 1;
                    modbusTcp.ReceiveTimeOut = 1;
                    modbusTcp.IpAddress = DataModel.Settingmodel.PLC_IP;
                    modbusTcp.Port = DataModel.Settingmodel.PLC_Port;

                    modbusTcp.DataFormat = HslCommunication.Core.DataFormat.CDAB;
                    WriteDetailLog($"[PLC连接] 尝试连接PLC {DataModel.Settingmodel.PLC_IP}:{DataModel.Settingmodel.PLC_Port}");
                    var connectresult = modbusTcp.ConnectServer();

                    if (connectresult.IsSuccess)
                    {
                        WriteDetailLog($"[PLC连接] 连接成功，开始轮询地址");

                        // 单地址触发：仅监控 AddressStart
                        var result = modbusTcp.ReadUInt16(DataModel.Settingmodel.AddressStart.ToString(), 1);
                        if (result.IsSuccess)
                        {
                            int currentValue = result.Content[0];
                            WriteDetailLog($"[PLC轮询] 地址{DataModel.Settingmodel.AddressStart}: {currentValue}");

                            if (currentValue > 0 && currentValue != lastTriggerValue)
                            {
                                // PLC 只负责“触发下一步”，阶段推进完全由视觉端内部状态机控制
                                WriteDetailLog($"[PLC触发] 地址触发值变化: {lastTriggerValue} -> {currentValue}");
                                bool triggerAccepted = ProcessSerialTrigger(
                                    DataModel.Settingmodel.AddressStart,
                                    currentValue,
                                    modbusTcp);
                                if (triggerAccepted)
                                {
                                    writeLog($"PLC触发: 串行阶段执行 (地址{DataModel.Settingmodel.AddressStart}={currentValue})");
                                    lastTriggerValue = currentValue;
                                }
                                else
                                {
                                    lastTriggerValue = 0;
                                }
                            }
                            else
                            {
                                lastTriggerValue = currentValue;
                            }
                        }

                        WriteDetailLog($"[PLC连接] 关闭连接");
                        modbusTcp.ConnectClose();
                        //#region 握手
                        //modbusTcp.Write((DATA.PLC.AddressStart + 18).ToString(), (UInt16)0);
                        //#endregion
                    }
                    else
                    {
                        WriteDetailLog($"[PLC连接] 连接失败: {connectresult.Message}");

                        #region 通讯失败数据置为-1
                        DataModel.Processmodel.Trig_IO.IOstatus = -1;
                        #endregion
                    }
                    //modbusTcp?.ConnectClose();

                }
                catch (Exception ex)
                {
                    writeError($"PLC通讯异常: {ex.Message}");
                    WriteDetailLog($"[PLC异常] {ex.Message}\n{ex.StackTrace}");
                    // 已受理的相机回图继续持有执行代次到帧尾；PLC 下一轮轮询负责恢复通讯状态。
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
                for (int i = 0; i < dt.Rows.Count; i++)
                {
                    DataModel.Processmodel.SNList.Add(dt.Rows[i][0].ToString());
                }
                return true;

            }
            catch { return false; }
        }

        private static string FormatScanTimestamp(DateTime? utcTime)
        {
            return utcTime.HasValue
                ? utcTime.Value.ToLocalTime().ToString("HH:mm:ss.fff")
                : "-";
        }

        private static string FormatScanElapsedMilliseconds(DateTime? startUtc, DateTime? endUtc)
        {
            if (!startUtc.HasValue || !endUtc.HasValue)
            {
                return "-";
            }

            return Math.Max(0, (endUtc.Value - startUtc.Value).TotalMilliseconds).ToString("F0");
        }

        private void WriteScanTimingLog(
            DateTime scanTriggeredAtUtc,
            DateTime? barcodeReceivedAtUtc,
            DateTime? snResolvedAtUtc,
            DateTime? processValidatedAtUtc,
            string result)
        {
            DateTime endAtUtc = processValidatedAtUtc
                ?? snResolvedAtUtc
                ?? barcodeReceivedAtUtc
                ?? DateTime.UtcNow;

            WriteDetailLog(
                $"[扫码耗时] 结果={result ?? "未知"} " +
                $"T0触发={FormatScanTimestamp(scanTriggeredAtUtc)} " +
                $"T1收到条码={FormatScanTimestamp(barcodeReceivedAtUtc)} " +
                $"T2解析SN完成={FormatScanTimestamp(snResolvedAtUtc)} " +
                $"T3工序校验完成={FormatScanTimestamp(processValidatedAtUtc)} " +
                $"取码耗时={FormatScanElapsedMilliseconds(scanTriggeredAtUtc, barcodeReceivedAtUtc)}ms " +
                $"SN解析耗时={FormatScanElapsedMilliseconds(barcodeReceivedAtUtc, snResolvedAtUtc)}ms " +
                $"工序校验耗时={FormatScanElapsedMilliseconds(snResolvedAtUtc, processValidatedAtUtc)}ms " +
                $"流程耗时={FormatScanElapsedMilliseconds(scanTriggeredAtUtc, endAtUtc)}ms");
        }

        /// <summary>
        /// 记录扫码器阶段未取得有效条码的收口时间，供现场区分设备读取超时与后续MES耗时。
        /// </summary>
        public void RecordScannerReadFailure(DateTime scanTriggeredAtUtc, string status)
        {
            WriteScanTimingLog(scanTriggeredAtUtc, null, null, null, status);
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
        /// 处理扫码并集成 MES：解码 SN、更新界面、工序校验；可选启动扫码自动阶段循环。
        /// </summary>
        /// <param name="scanCode">扫码原始内容。</param>
        /// <param name="scanTriggeredAtUtc">扫码器读取或输入处理开始时刻；缺失时以本方法进入时刻记录T0。</param>
        /// <param name="barcodeReceivedAtUtc">上位机收到完整条码的时刻；缺失时以本方法进入时刻记录T1。</param>
        /// <remarks>
        /// 业务意图：
        /// - 任意入口（扫码测试、串口监听、SN 框换行）最终汇聚到此方法，统一走 MES 与状态更新。
        /// - 进入时先 <see cref="StopScanAutoExecution"/>，避免上一轮的扫码自动循环与新 SN 交错。
        /// - 清空 <see cref="Processmodel.ReportWorkStatus"/>：表示「新一件扫码开始」，避免仍显示上一枪的报工提示。
        /// - 使用 <see cref="_mesScanProcessLock"/> 保证同一时刻只有一条扫码/MES 链路，并与生产执行、照片测试共用产品上下文互斥。
        /// </remarks>
        public async Task ProcessScanCodeWithMESAsync(
            string scanCode,
            DateTime? scanTriggeredAtUtc = null,
            DateTime? barcodeReceivedAtUtc = null)
        {
            DateTime scanStartUtc = scanTriggeredAtUtc ?? DateTime.UtcNow;
            DateTime barcodeReceivedUtc = barcodeReceivedAtUtc ?? DateTime.UtcNow;
            if (barcodeReceivedUtc < scanStartUtc)
            {
                barcodeReceivedUtc = scanStartUtc;
            }

            DateTime? snResolvedUtc = null;
            DateTime? processValidatedUtc = null;
            string timingResult = "扫码流程异常";

            bool scanBlocked;
            bool executing;
            bool processingImage;
            bool manualImageTest;
            lock (_executionLock)
            {
                executing = _isExecuting;
                processingImage = _processingImage;
                manualImageTest = _manualImageTestInProgress;
                scanBlocked = executing || processingImage || manualImageTest;
            }

            if (scanBlocked)
            {
                writeLog("当前检测或照片测试尚未收口，本次扫码保持原产品上下文并等待下一次扫码", false);
                WriteDetailLog(
                    $"[扫码门禁] 忽略并发扫码 execution={executing} processing={processingImage} " +
                    $"manual={manualImageTest}");
                return;
            }

            if (!await _mesScanProcessLock.WaitAsync(0))
            {
                writeLog("扫码/MES 处理尚未结束，本次已忽略（请勿连续快速触发扫码测试或等待上一次完成）", false);
                return;
            }

            lock (_executionLock)
            {
                executing = _isExecuting;
                processingImage = _processingImage;
                manualImageTest = _manualImageTestInProgress;
                scanBlocked = executing || processingImage || manualImageTest;
                if (!scanBlocked)
                {
                    _isScanProcessing = true;
                }
            }

            if (scanBlocked)
            {
                _mesScanProcessLock.Release();
                writeLog("当前检测或照片测试已进入执行窗口，本次扫码保持原产品上下文并等待下一次扫码", false);
                WriteDetailLog(
                    $"[扫码门禁] 入口复核拒绝并发扫码 execution={executing} processing={processingImage} " +
                    $"manual={manualImageTest}");
                return;
            }

            NotifyCanRunManualImageTestChanged();
            _inspectionRecipeScanInProgress = true;

            try
            {
                // 新一轮扫码开始时，先停止可能存在的“扫码自动触发执行循环”
                StopScanAutoExecution();

                // 条码已进入上位机，后续状态按SN解析和工序校验阶段向操作员展示。
                DataModel.Processmodel.ReportWorkStatus = string.Empty;
                DataModel.Processmodel.ScanCodeStatus = "条码已接收，正在解析SN";

                // 所有扫码先按配置的SN解码方式解析产品SN；点检在工序校验前按配置的点检SN分流。
                string okInspectionSN = CurrentInspectionRecipeConfig.OKInspectionSN;
                string ngInspectionSN = CurrentInspectionRecipeConfig.NGInspectionSN;
                string normalizedScan = (scanCode ?? string.Empty).Trim();
                string snDecodeMode = DataModel?.Settingmodel?.ReportWorkConfig?.SnDecodeMode;
                string normalizedSnDecodeMode = MESService.NormalizeSnDecodeMode(snDecodeMode);
                WriteDetailLog($"[扫码解析] SN解码方式={normalizedSnDecodeMode} raw={normalizedScan}");
                var scanResult = await _mesService.ProcessScanCodeAsync(normalizedScan, normalizedSnDecodeMode);
                snResolvedUtc = DateTime.UtcNow;
                if (!scanResult.Success)
                {
                    timingResult = scanResult.StatusMessage;
                    _ = App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DataModel.Processmodel.ScanCodeStatus = scanResult.StatusMessage;
                        writeLog($"扫码处理失败: {scanResult.ErrorMessage}");
                        StopScanAutoExecution();
                    }));
                    return;
                }

                string resolvedSN = (scanResult.SN ?? string.Empty).Trim();
                string pendingInspectionType = DataModel?.Processmodel?.PendingInspectionType;
                if (!string.IsNullOrEmpty(pendingInspectionType))
                {
                    string expectedSN = pendingInspectionType == "OK" ? okInspectionSN : ngInspectionSN;
                    await TryBeginInspectionFromMesAsync(pendingInspectionType, resolvedSN, expectedSN, normalizedScan);
                    processValidatedUtc = DateTime.UtcNow;
                    timingResult = "点检身份确认完成";
                    return;
                }

                bool matchesOKInspection = InspectionSNEquals(resolvedSN, okInspectionSN);
                bool matchesNGInspection = InspectionSNEquals(resolvedSN, ngInspectionSN);
                if (matchesOKInspection && matchesNGInspection)
                {
                    timingResult = "点检配置无效";
                    RejectInspectionScan(
                        "点检配置无效：OK点检SN和NG点检SN不能相同",
                        null,
                        normalizedScan,
                        resolvedSN);
                    return;
                }

                // 点检选择窗口允许关闭，操作员仍可直接扫描配置的点检最终产品SN进入对应模式；该路径继续经过MES实时确认。
                if (matchesOKInspection || matchesNGInspection)
                {
                    string type = matchesOKInspection ? "OK" : "NG";
                    string expectedSN = matchesOKInspection ? okInspectionSN : ngInspectionSN;
                    await TryBeginInspectionFromMesAsync(type, resolvedSN, expectedSN, normalizedScan);
                    processValidatedUtc = DateTime.UtcNow;
                    timingResult = "点检身份确认完成";
                    return;
                }

                if (RejectNormalScanDuringInspection())
                {
                    timingResult = "点检模式中拒绝普通产品扫码";
                    return;
                }

                // 获取工序名称
                string procedureName = DataModel.Settingmodel.ReportWorkConfig.ProcedureName;

                ResetForNewProduct(resolvedSN);
                _ = App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    DataModel.Processmodel.CurrentProductSN = resolvedSN;
                    DataModel.Processmodel.ScanCodeStatus = "SN已确认，正在校验工序";
                    writeLog($"扫码处理成功: SN={resolvedSN}");
                }));

                string inspectionGateError = ValidateInspectionGateForProduction(resolvedSN);
                if (!string.IsNullOrEmpty(inspectionGateError))
                {
                    timingResult = "点检到期";
                    _ = App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DataModel.Processmodel.ScanCodeStatus = "点检到期";
                        writeLog(inspectionGateError, true);
                        // 扫码/MES链路结束后由finally中的状态刷新重新打开，避免在锁释放前显示点检选择窗口。
                        StopScanAutoExecution();
                    }));
                    return;
                }

                var validationResult = await _mesService.ValidateProcessAsync(resolvedSN, procedureName);
                processValidatedUtc = DateTime.UtcNow;
                timingResult = validationResult.StatusMessage;
                _ = App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (validationResult.Success)
                    {
                        DataModel.Processmodel.ScanCodeStatus = validationResult.StatusMessage;
                        writeLog($"工序校验通过: SN={resolvedSN}, 工序={procedureName}");

                        if (DataModel.Settingmodel.EnableScanAutoTriggerStages)
                        {
                            writeLog($"扫码自动执行: 工序校验通过，准备启动阶段循环 SN={resolvedSN}", false);
                            WriteDetailLog($"[扫码自动] 工序校验通过，调用 StartScanAutoExecution SN={resolvedSN}");
                            StartScanAutoExecution(resolvedSN);
                        }
                        else
                        {
                            WriteDetailLog("[扫码自动] EnableScanAutoTriggerStages=false，不启动阶段循环");
                            StopScanAutoExecution();
                        }
                    }
                    else
                    {
                        DataModel.Processmodel.ScanCodeStatus = validationResult.StatusMessage;
                        writeLog($"工序校验失败: {validationResult.ErrorMessage}");
                        StopScanAutoExecution();
                    }
                }));
            }
            catch (Exception ex)
            {
                timingResult = "处理异常";
                _ = App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    DataModel.Processmodel.ScanCodeStatus = "处理异常";
                    writeLog($"扫码处理异常: {ex.Message}");
                }));
            }
            finally
            {
                _inspectionRecipeScanInProgress = false;
                WriteScanTimingLog(
                    scanStartUtc,
                    barcodeReceivedUtc,
                    snResolvedUtc,
                    processValidatedUtc,
                    timingResult);
                lock (_executionLock)
                {
                    _isScanProcessing = false;
                }
                _mesScanProcessLock.Release();
                NotifyCanRunManualImageTestChanged();
                QueueInspectionStatusRefresh();
            }
        }

        /// <summary>
        /// 扫码触发自动执行循环入口（不改动 PLC 触发代码）。
        /// </summary>
        /// <param name="sn">本循环绑定的产品 SN；若运行中 SN 被替换则退出。</param>
        /// <remarks>
        /// 业务意图：
        /// - 仅当 <see cref="SettingModel.EnableScanAutoTriggerStages"/> 为 true 且扫码身份确认完成后调用：普通产品要求工序校验通过，点检样件要求MES确认固定点检SN并进入OK或NG模式。
        /// - 复用视觉端阶段状态机：<see cref="Processmodel.CurrentStageIndex"/> / <see cref="Processmodel.CompletedStages"/> / <see cref="Processmodel.NeedRestart"/>。
        /// - 每一轮阶段在相机回调收口后，若 <see cref="Processmodel.NeedRestart"/> 为 true（本阶段NG、NG2、超时或相机异常等），结束自动循环并等待现场处理；点检保留业务规则指定的重试阶段。
        /// </remarks>
        private void StartScanAutoExecution(string sn)
        {
            if (DataModel?.Settingmodel == null)
            {
                WriteDetailLog("[扫码自动] StartScanAutoExecution 放弃: Settingmodel 为空");
                return;
            }
            if (!DataModel.Settingmodel.EnableScanAutoTriggerStages)
            {
                WriteDetailLog("[扫码自动] StartScanAutoExecution 放弃: 开关未启用");
                return;
            }
            if (string.IsNullOrEmpty(sn))
            {
                writeLog("扫码自动执行: SN 为空，未启动阶段循环", false);
                WriteDetailLog("[扫码自动] StartScanAutoExecution 放弃: SN 为空");
                return;
            }

            int stageCount = DataModel.Processmodel.ConfiguredStages?.Count ?? 0;
            string stagesList = DataModel.Processmodel.ConfiguredStages == null || stageCount == 0
                ? "(无)"
                : string.Join("->", DataModel.Processmodel.ConfiguredStages);
            writeLog($"扫码自动执行: 已启动循环 SN={sn} 阶段数={stageCount} 序列={stagesList}", false);
            WriteDetailLog($"[扫码自动] 启动循环 SN={sn} CurrentStageIndex={DataModel.Processmodel.CurrentStageIndex} " +
                          $"NeedRestart={DataModel.Processmodel.NeedRestart} CompletedStages={DataModel.Processmodel.CompletedStages?.Count ?? 0} " +
                          $"阶段数={stageCount} 序列=[{stagesList}] Tools数量={DataModel.Processmodel.Tools?.Count ?? 0}");

            lock (_scanAutoLock)
            {
                _scanAutoTriggerCts?.Cancel();
                _scanAutoTriggerCts = new CancellationTokenSource();
            }

            var localCts = _scanAutoTriggerCts;
            var token = localCts.Token;

            Task.Run(async () =>
            {
                string exitReason = "正常结束";
                try
                {
                    if (stageCount == 0)
                    {
                        exitReason = "未配置阶段(工具「触发指令Command」为空或工程无工具)";
                        writeLog("扫码自动执行: 未配置阶段，无法执行 A1 等检测；请检查各工具的 Command 与工程加载", false);
                        WriteDetailLog($"[扫码自动] {exitReason} Tools数量={DataModel.Processmodel.Tools?.Count ?? 0}");
                        return;
                    }

                    while (!token.IsCancellationRequested)
                    {
                        // 若已经切换到别的SN，停止
                        if (DataModel?.Processmodel?.CurrentProductSN != sn)
                        {
                            exitReason = $"SN 已变更(期望={sn}, 当前={DataModel?.Processmodel?.CurrentProductSN ?? "空"})";
                            writeLog($"扫码自动执行: 中断-{exitReason}", false);
                            WriteDetailLog($"[扫码自动] 循环退出 {exitReason}");
                            break;
                        }

                        // 关闭开关时停止
                        if (!DataModel.Settingmodel.EnableScanAutoTriggerStages)
                        {
                            exitReason = "已关闭「扫码触发自动执行」开关";
                            writeLog($"扫码自动执行: 中断-{exitReason}", false);
                            WriteDetailLog($"[扫码自动] 循环退出 {exitReason}");
                            break;
                        }

                        // 未满足“SN + 当前业务资格”，停止（普通产品看工序校验，点检样件看MES确认后的点检模式）。
                        if (!HasValidSNAndValidation())
                        {
                            exitReason = $"检测前置不满足 SN={DataModel.Processmodel.CurrentProductSN ?? "空"} ScanCodeStatus={DataModel.Processmodel.ScanCodeStatus ?? "空"}";
                            writeLog($"扫码自动执行: 中断-SN/业务状态不满足 (详见详细调试日志 [报工状态])", false);
                            WriteDetailLog($"[扫码自动] 循环退出 HasValidSNAndValidation=false {exitReason}");
                            break;
                        }

                        // 已完成所有阶段，退出
                        if (DataModel.Processmodel.CurrentStageIndex >= DataModel.Processmodel.ConfiguredStages.Count)
                        {
                            exitReason = $"全部阶段已完成 Index={DataModel.Processmodel.CurrentStageIndex} 总阶段={DataModel.Processmodel.ConfiguredStages.Count}";
                            writeLog($"扫码自动执行: {exitReason}", false);
                            WriteDetailLog($"[扫码自动] 循环正常退出 {exitReason}");
                            break;
                        }

                        // 避免在报工过程中推进检测
                        if (DataModel.Processmodel.IsReportingWork)
                        {
                            await Task.Delay(100, token);
                            continue;
                        }

                        // 扫码入口可能已创建自动循环，MES 链路释放产品上下文互斥后再进入首个检测阶段。
                        if (_isScanProcessing)
                        {
                            await Task.Delay(50, token);
                            continue;
                        }

                        // 等待本轮执行窗口释放
                        if (_isExecuting)
                        {
                            await Task.Delay(50, token);
                            continue;
                        }

                        bool started = TriggerNextStageByScan();
                        if (!started)
                        {
                            exitReason = "TriggerNextStageByScan 返回 false(见上一条扫码阶段触发日志/详细调试)";
                            writeLog("扫码自动执行: 阶段触发失败，流程中断；请查看「未找到匹配工具」或错误日志及 日志\\详细调试", false);
                            WriteDetailLog($"[扫码自动] 循环退出 {exitReason}");
                            break;
                        }

                        WriteDetailLog($"[扫码自动] 已触发阶段，等待执行窗口释放 _isExecuting={_isExecuting}");

                        // 等待相机回调收口并释放执行窗口
                        while (_isExecuting && !token.IsCancellationRequested)
                        {
                            await Task.Delay(50, token);
                        }

                        WriteDetailLog($"[扫码自动] 本轮阶段执行窗口已释放 CurrentStageIndex={DataModel.Processmodel.CurrentStageIndex} NeedRestart={DataModel.Processmodel.NeedRestart}");

                        // 当前一轮检测已判定NG、NG2、超时或设备异常时，扫码自动循环收口并保留业务重试位置。
                        if (DataModel.Processmodel.NeedRestart)
                        {
                            exitReason = "当前阶段需要处理(NeedRestart=true)，停止当前SN的扫码自动循环并保留重试位置";
                            writeLog($"扫码自动执行: 中断-{exitReason}", false);
                            WriteDetailLog($"[扫码自动] 循环退出 {exitReason}");
                            break;
                        }
                    }

                    if (token.IsCancellationRequested && exitReason == "正常结束")
                        exitReason = "任务已取消(CancellationToken)";
                }
                catch (OperationCanceledException)
                {
                    exitReason = "OperationCanceledException";
                }
                catch (Exception ex)
                {
                    exitReason = $"异常: {ex.Message}";
                    writeError($"扫码自动执行循环异常: {ex.Message}");
                    WriteDetailLog($"[扫码自动] 循环异常 {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    writeLog($"扫码自动执行: 循环结束 原因={exitReason}", false);
                    WriteDetailLog($"[扫码自动] 循环结束 最终原因={exitReason}");
                    lock (_scanAutoLock)
                    {
                        if (ReferenceEquals(_scanAutoTriggerCts, localCts))
                        {
                            _scanAutoTriggerCts = null;
                        }
                    }
                }
            }, token);
        }

        private void StopScanAutoExecution()
        {
            lock (_scanAutoLock)
            {
                if (_scanAutoTriggerCts != null)
                {
                    WriteDetailLog("[扫码自动] StopScanAutoExecution: 取消正在运行的扫码自动循环");
                }
                _scanAutoTriggerCts?.Cancel();
                _scanAutoTriggerCts = null;
            }
        }

        /// <summary>
        /// 触发“下一阶段”（扫码驱动版本，不包含PLC的复位写0与IO状态更新）。
        /// 复用与 ProcessSerialTrigger 相同的阶段选择与重试策略。
        /// </summary>
        private bool TriggerNextStageByScan()
        {
            long executionId = 0;
            try
            {
                WriteDetailLog($"[扫码触发阶段] 进入 TryBeginExecution 前 _isExecuting={_isExecuting}");

                if (!TryBeginExecution(out executionId))
                {
                    writeLog("扫码自动执行: 系统忙碌(_isExecuting)，无法开始新阶段；若持续出现请检查 PLC 与扫码是否并发触发", false);
                    WriteDetailLog("[扫码触发阶段] TryBeginExecution 失败: 已有执行窗口未释放");
                    return false;
                }

                if (!HasValidSNAndValidation())
                {
                    writeLog($"报工状态不满足: 跳过扫码阶段触发 (SN={DataModel.Processmodel.CurrentProductSN})");
                    WriteDetailLog($"[扫码触发阶段] HasValidSNAndValidation=false SN={DataModel.Processmodel.CurrentProductSN} Status={DataModel.Processmodel.ScanCodeStatus}");
                    EndExecution(executionId);
                    return false;
                }

                if (DataModel.Processmodel.NeedRestart && DataModel.Processmodel.IsInspectionMode)
                {
                    // 点检模式使用独立规则：当前失败阶段重复触发；XML中的StageFailRetryStrategy只影响普通生产。
                    DataModel.Processmodel.NeedRestart = false;
                    writeLog($"点检阶段{DataModel.Processmodel.RCMD}上次执行失败，保留当前阶段，等待调参后重复触发", false);
                }
                else if (DataModel.Processmodel.NeedRestart)
                {
                    if (DataModel.Settingmodel.StageFailRetryStrategy == StageFailRetryStrategy.从头开始重跑)
                    {
                        WriteDetailLog("[扫码触发阶段] NeedRestart=true，策略=从头开始重跑，执行 ResetDetectionCycle");
                        ResetDetectionCycle();
                    }
                    else
                    {
                        WriteDetailLog("[扫码触发阶段] NeedRestart=true，策略=仅重试失败阶段，清除 NeedRestart 标志");
                        DataModel.Processmodel.NeedRestart = false;
                    }
                }

                var stages = DataModel.Processmodel.ConfiguredStages;
                int idx = DataModel.Processmodel.CurrentStageIndex;
                if (idx < 0) idx = 0;
                if (stages == null || stages.Count == 0)
                {
                    writeLog("扫码自动执行: ConfiguredStages 为空，无法匹配阶段", false);
                    WriteDetailLog("[扫码触发阶段] ConfiguredStages 为空或 null");
                    EndExecution(executionId);
                    return false;
                }
                if (idx >= stages.Count)
                {
                    writeLog($"扫码自动执行: 阶段索引越界 idx={idx} 阶段数={stages.Count}", false);
                    WriteDetailLog($"[扫码触发阶段] idx>=stages.Count idx={idx} count={stages.Count}");
                    EndExecution(executionId);
                    return false;
                }

                string command = stages[idx];
                DataModel.Processmodel.RCMD = command;

                writeLog($"扫码自动执行: 开始阶段 {command} (序号 {idx + 1}/{stages.Count})", false);
                WriteDetailLog($"[扫码触发阶段] RCMD={command} idx={idx} stages=[{string.Join(",", stages)}]");

                ClearToolsByCommand(command);

                _pendingCameraGroups = BuildCameraGroupsForStageCommand(command);
                if (_pendingCameraGroups == null || _pendingCameraGroups.Count == 0)
                {
                    writeLog($"未找到匹配工具: {command}阶段(扫码触发)；请检查工具 Command 是否与阶段一致、相机序列号是否配置", false);
                    WriteDetailLog($"[扫码触发阶段] 相机分组为空 command={command}，各工具 Command 一览: " +
                                  string.Join("; ", DataModel.Processmodel.Tools.Select((t, i) => $"[{i}]'{t.Command}' Cam='{t.CameraSerialNumber}'")));
                    EndExecution(executionId);
                    return false;
                }

                bool triggered = TriggerNextCameraGroupCapture(command, executionId);
                if (!triggered)
                {
                    writeLog($"扫码自动执行: 相机软触发失败({command})；请检查相机初始化、曝光与连接", false);
                    WriteDetailLog($"[扫码触发阶段] TriggerNextCameraGroupCapture 返回 false command={command} 剩余分组队列长度={_pendingCameraGroups?.Count ?? -1}");
                    EndExecution(executionId);
                    return false;
                }

                WriteDetailLog($"[扫码触发阶段] 已发送软触发 command={command} 等待回图");
                return true;
            }
            catch (Exception ex)
            {
                writeError($"扫码阶段触发处理异常: {ex.Message}");
                WriteDetailLog($"[扫码触发阶段] 异常: {ex.Message}\n{ex.StackTrace}");
                try { EndExecution(executionId); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 复位已完成相机准备的 PLC 触发值。相机准备失败时不调用此方法，保留 PLC 值供轮询重试。
        /// </summary>
        /// <param name="address">触发地址。</param>
        /// <param name="triggerValue">触发值。</param>
        /// <param name="modbusTcp">ModbusTcp 连接对象。</param>
        /// <returns>PLC 复位成功时返回 true。</returns>
        private bool TryAcknowledgePlcTrigger(int address, int triggerValue, ModbusTcpNet modbusTcp)
        {
            var resetResult = modbusTcp.Write(address.ToString(), (UInt16)0);
            WriteDetailLog($"[PLC触发] 地址{address}触发值{triggerValue}复位结果={resetResult.IsSuccess}");
            return resetResult.IsSuccess;
        }

        /// <summary>
        /// 处理 PLC 触发信号。
        /// </summary>
        /// <param name="address">触发地址。</param>
        /// <param name="triggerValue">触发值。</param>
        /// <param name="modbusTcp">ModbusTcp 连接对象。</param>
        /// <returns>触发已经受理并完成 PLC 复位时返回 true；执行窗口忙碌、相机准备失败或复位失败时返回 false，轮询层保留触发等待重试。</returns>
        private bool ProcessSerialTrigger(int address, int triggerValue, ModbusTcpNet modbusTcp)
        {
            long executionId = 0;
            bool triggerAcknowledged = false;
            try
            {
                if (!TryBeginExecution(out executionId))
                {
                    WriteDetailLog($"[业务逻辑] 系统正在执行中，保留地址{address}触发值{triggerValue}等待重试");
                    return false;
                }
                WriteDetailLog($"[业务逻辑] 设置执行状态为true，开始处理串行阶段触发");

                if (!HasValidSNAndValidation())
                {
                    writeLog($"报工状态不满足: 跳过PLC触发 (SN={DataModel.Processmodel.CurrentProductSN})");
                    WriteDetailLog($"[业务逻辑] 报工模块状态不满足，跳过PLC触发执行 (SN={DataModel.Processmodel.CurrentProductSN})");
                    if (!TryAcknowledgePlcTrigger(address, triggerValue, modbusTcp))
                    {
                        EndExecution(executionId);
                        return false;
                    }
                    triggerAcknowledged = true;
                    EndExecution(executionId);
                    return true;
                }

                if (DataModel.Processmodel.NeedRestart && DataModel.Processmodel.IsInspectionMode)
                {
                    DataModel.Processmodel.NeedRestart = false;
                    writeLog($"点检阶段{DataModel.Processmodel.RCMD}上次执行失败，保留当前阶段，等待调参后重复触发", false);
                }
                else if (DataModel.Processmodel.NeedRestart)
                {
                    // 普通生产阶段失败沿用配置的重试策略，点检阶段的重试已在上方按当前阶段处理。
                    if (DataModel.Settingmodel.StageFailRetryStrategy == StageFailRetryStrategy.从头开始重跑)
                    {
                        ResetDetectionCycle();
                    }
                    else
                    {
                        DataModel.Processmodel.NeedRestart = false;
                    }
                }

                var stages = DataModel.Processmodel.ConfiguredStages;
                if (stages == null || stages.Count == 0)
                {
                    WriteDetailLog("[业务逻辑] 未配置阶段，复位并消费本次PLC触发");
                    if (!TryAcknowledgePlcTrigger(address, triggerValue, modbusTcp))
                    {
                        EndExecution(executionId);
                        return false;
                    }
                    triggerAcknowledged = true;
                    EndExecution(executionId);
                    return true;
                }

                int idx = DataModel.Processmodel.CurrentStageIndex;
                if (idx < 0) idx = 0;
                if (idx >= stages.Count)
                {
                    // 已完成所有阶段：忽略多余触发
                    WriteDetailLog($"[业务逻辑] 所有阶段已完成，忽略本次PLC触发");
                    if (!TryAcknowledgePlcTrigger(address, triggerValue, modbusTcp))
                    {
                        EndExecution(executionId);
                        return false;
                    }
                    triggerAcknowledged = true;
                    EndExecution(executionId);
                    return true;
                }

                string command = stages[idx];
                DataModel.Processmodel.RCMD = command;

                // 每次 PLC 触发只执行“一个阶段”的全部工具：
                // - 同一相机下多工具：同图多工具
                // - 同一阶段多相机：按相机分组，多次取图
                writeLog($"开始执行: {command}阶段 (地址{address}={triggerValue})");
                WriteDetailLog($"[PLC->视觉] 串行触发进入阶段{command}(地址:{address}, 触发值:{triggerValue})");

                ClearToolsByCommand(command);
                WriteDetailLog($"[视觉检测] 开始执行阶段{command}的视觉检测");

                _pendingCameraGroups = BuildCameraGroupsForStageCommand(command);
                if (_pendingCameraGroups.Count == 0)
                {
                    writeLog($"未找到匹配工具: {command}阶段", false);
                    if (!TryAcknowledgePlcTrigger(address, triggerValue, modbusTcp))
                    {
                        EndExecution(executionId);
                        return false;
                    }
                    triggerAcknowledged = true;
                    EndExecution(executionId);
                    return true;
                }
                bool triggered = TriggerNextCameraGroupCapture(command, executionId);
                if (!triggered)
                {
                    DataModel.Processmodel.Status = ToolStatus.NG;
                    DataModel.Processmodel.NeedRestart = true;
                    if (string.IsNullOrWhiteSpace(DataModel.Processmodel.CameraStatus))
                    {
                        DataModel.Processmodel.CameraStatus = "相机准备失败";
                    }
                    WriteDetailLog($"[PLC触发] 首组相机准备失败，保留地址{address}触发值{triggerValue}等待重试");
                    EndExecution(executionId);
                    return false;
                }

                // 相机已经完成切换、曝光配置和软触发后再复位 PLC；准备阶段失败时保留原触发值供下一轮重试。
                if (!TryAcknowledgePlcTrigger(address, triggerValue, modbusTcp))
                {
                    DataModel.Processmodel.Status = ToolStatus.NG;
                    DataModel.Processmodel.NeedRestart = true;
                    DataModel.Processmodel.CameraStatus = "PLC触发复位失败";
                    _cameraRecoveryRequired = true;
                    WriteDetailLog($"[PLC触发] 相机已触发但地址{address}复位失败，结束本轮并等待恢复后重试");
                    EndExecution(executionId);
                    return false;
                }
                triggerAcknowledged = true;

                WriteDetailLog($"[界面更新] 更新界面触发标志从{DataModel.Processmodel.Trig_IO.IOstatus}到{triggerValue}");
                DataModel.Processmodel.Trig_IO.IOstatus = triggerValue;
                return true;
            }
            catch (Exception ex)
            {
                writeError($"PLC触发处理异常: {ex.Message}");
                WriteDetailLog($"[PLC触发] 处理异常: {ex.Message}");
                if (triggerAcknowledged)
                {
                    try
                    {
                        var restoreResult = modbusTcp.Write(address.ToString(), (UInt16)triggerValue);
                        WriteDetailLog($"[PLC触发] 异常后恢复地址{address}触发值{triggerValue}结果={restoreResult.IsSuccess}");
                    }
                    catch (Exception restoreException)
                    {
                        WriteDetailLog($"[PLC触发] 异常后恢复地址{address}失败: {restoreException.Message}");
                    }
                }
                EndExecution(executionId);
                return false;
            }
        }

        /// <summary>
        /// 按指令执行匹配的工具（PLC指令匹配模式）
        /// </summary>
        /// <param name="command">指令名称，如"A1"、"A2"或"ALL"</param>
        /// <returns>是否实际触发了相机（用于判断执行状态收口点）</returns>
        private bool ExecuteToolsByCommand(string command)
        {
            try
            {
                // 详细调试日志仅在 NG/超时/异常场景输出
                bool detailOnNgOrTimeout = DataModel.Processmodel.NeedRestart;
                if (detailOnNgOrTimeout)
                {
                    WriteDetailLog($"[工具执行] 开始执行指令{command}，总工具数={DataModel.Processmodel.Tools.Count}");
                }

                string detectionBlockReason = GetDetectionBlockReason();
                if (!string.IsNullOrEmpty(detectionBlockReason))
                {
                    writeLog($"检测被阻止: {detectionBlockReason}");
                    if (detailOnNgOrTimeout)
                    {
                        WriteDetailLog($"[工具执行] 检测被阻止，原因：{detectionBlockReason}");
                    }
                    return false;
                }

                // 收集所有匹配的工具
                List<int> matchedToolIndices = new List<int>();

                // 查找匹配的工具
                if (detailOnNgOrTimeout)
                {
                    WriteDetailLog($"[工具匹配] 开始查找匹配指令{command}的工具");
                }

                // 详细诊断工具配置
                for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
                {
                    var tool = DataModel.Processmodel.Tools[i];
                    if (detailOnNgOrTimeout)
                    {
                        WriteDetailLog($"[工具诊断] 工具{i}: Name='{tool.Name}', Command='{tool.Command}', Index={tool.Index}, CameraSerial='{tool.CameraSerialNumber}'");
                    }
                }

                // 第一遍：收集所有匹配的工具
                for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
                {
                    bool isMatch = false;

                    if (command == "ALL")
                    {
                        // ALL指令匹配所有工具（向后兼容）
                        isMatch = true;
                        if (detailOnNgOrTimeout)
                        {
                            WriteDetailLog($"[工具匹配] 工具{i}({DataModel.Processmodel.Tools[i].Name}): ALL指令匹配");
                        }
                    }
                    else
                    {
                        // 按Command字段匹配
                        isMatch = NormalizeStageCommand(DataModel.Processmodel.Tools[i].Command)
                            == NormalizeStageCommand(command);
                        if (detailOnNgOrTimeout)
                        {
                            WriteDetailLog($"[工具匹配] 工具{i}({DataModel.Processmodel.Tools[i].Name}): Command={DataModel.Processmodel.Tools[i].Command}, 匹配={isMatch}");
                        }
                    }

                    if (isMatch)
                    {
                        matchedToolIndices.Add(i);
                        if (detailOnNgOrTimeout)
                        {
                            WriteDetailLog($"[工具执行] 找到匹配工具: 工具{i}({DataModel.Processmodel.Tools[i].Name})");
                        }
                    }
                }

                if (matchedToolIndices.Count == 0)
                {
                    writeLog($"未找到匹配工具: {command}指令");
                    if (detailOnNgOrTimeout)
                    {
                        WriteDetailLog($"[工具执行] 未找到匹配指令{command}的工具");
                    }
                    return false;
                }

                if (detailOnNgOrTimeout)
                {
                    WriteDetailLog($"[工具执行] 共找到{matchedToolIndices.Count}个匹配工具，单次拍照模式");
                }

                int firstToolIndex = matchedToolIndices[0];
                var firstTool = DataModel.Processmodel.Tools[firstToolIndex];

                DataModel.Processmodel.ToolIndex = firstToolIndex + 1;

                if (detailOnNgOrTimeout)
                {
                    WriteDetailLog($"[相机切换] 使用工具{firstToolIndex}({firstTool.Name})的相机设置: {firstTool.CameraSerialNumber}");
                }
                if (!SwitchCameraForTool(firstTool))
                {
                    WriteDetailLog($"[相机切换] 指定相机不可用，停止{command}指令触发");
                    return false;
                }
                Thread.Sleep(100);

                DataModel.Settingmodel.camedata.CameraModel.exposuretime = firstTool.ExposureTime;
                DataModel.Settingmodel.camedata.CameraModel.camera.Exposure = firstTool.ExposureTime;
                DataModel.Settingmodel.camedata.CameraModel.camera.bnSetParam_Click();
                Thread.Sleep(DataModel.Settingmodel.delaytime);

                string toolNames = string.Join(", ", matchedToolIndices.Select(idx => DataModel.Processmodel.Tools[idx].Name));
                writeLog($"开始拍照: {command}指令 - 共{matchedToolIndices.Count}个工具({toolNames})");
                if (detailOnNgOrTimeout)
                {
                    WriteDetailLog($"[相机触发] {command}指令触发拍照一次，{matchedToolIndices.Count}个工具将使用同一张图像");
                }

                DataModel.Settingmodel.camedata.CameraModel.camera.bnTriggerExec_Click();
                if (detailOnNgOrTimeout)
                {
                    WriteDetailLog($"[相机触发] 拍照触发完成，等待相机回调处理");
                }

                writeLog($"工具匹配完成: {command}指令 (共{matchedToolIndices.Count}个工具)");
                return true;
            }
            catch (Exception ex)
            {
                writeError($"工具执行异常: {command}指令 - {ex.Message}");
                WriteDetailLog($"[工具执行] 执行指令{command}时发生错误:{ex.Message}");
                return false;
            }
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

        private object writeCameraDebugLog_Locker = new object();

        internal void writeLog(string LogContent, bool showdatarecord = true)
        {
            lock (writeLog_Locker)
            {
                try
                {
                    string logstr = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.FFF")}]{LogContent}";
                    if (showdatarecord)
                    {
                        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            int num = 200;
                            if (DataModel.Recordmodel.workLog.Count > num)
                            {
                                DataModel.Recordmodel.workLog.Clear();
                            }
                            DataModel.Recordmodel.workLog.Insert(0, logstr);
                        }));
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
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (DataModel.Recordmodel.ErrorLog.Count > num)
                            DataModel.Recordmodel.ErrorLog.Clear();
                        DataModel.Recordmodel.ErrorLog.Insert(0, bugstr);

                        // 同步到 workLog，利用现有日志区显示错误，不改布局
                        if (DataModel.Recordmodel.workLog.Count > num)
                            DataModel.Recordmodel.workLog.Clear();
                        DataModel.Recordmodel.workLog.Insert(0, bugstr);
                    }));
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

        /// <summary>
        /// 相机专项排查日志：单独落盘，避免与界面日志/详细调试日志混在一起。
        /// </summary>
        internal void WriteCameraDebugLog(string LogContent)
        {
            lock (writeCameraDebugLog_Locker)
            {
                try
                {
                    string logstr = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.FFF")}]{LogContent}";
                    string filename = $"{Environment.CurrentDirectory}\\日志\\相机排查\\{DateTime.Now.ToString("yyyyMMdd")}_camera.txt";
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
                catch
                {
                    // 相机排查日志不影响主流程
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
                    string switchBlockReason = GetProjectSwitchBlockReason();
                    if (!string.IsNullOrEmpty(switchBlockReason))
                    {
                        NoticeBox.Show($"当前无法新建工程：{switchBlockReason}", "工程切换", MessageBoxIcon.Warning, true, 6000);
                        Prjs_selectedindex();
                        return;
                    }

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
                            ResetRuntimeForProjectInspectionChange();
                            DataModel.Settingmodel.Name = prjname;
                            DataModel.Processmodel.Tools.Clear();
                            LoadInspectionRecipeConfiguration();
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
            bool projectSaved = SavePrjXmls();
            NoticeBox.Show(
                projectSaved ? "工程保存完成" : "工程配方未保存，请查看详细日志",
                projectSaved ? "成功" : "提示",
                projectSaved ? MessageBoxIcon.Success : MessageBoxIcon.Warning,
                true,
                5000);

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
                    BuildConfiguredStages();
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
                BuildConfiguredStages();

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
                    BuildConfiguredStages();
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
                    BuildConfiguredStages();
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
                BuildConfiguredStages();

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
            BuildConfiguredStages();
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
            BackupCurrentProjectFilesBeforeLoad();
            LoadInspectionRecipeConfiguration();
            LoadPrjXmls();
            CheckPrj();
            Prjs_selectedindex();
            //ClearToolStatus();
        }
        public bool Load_Prj(string PrjName)
        {
            string switchBlockReason = GetProjectSwitchBlockReason();
            if (!string.IsNullOrEmpty(switchBlockReason))
            {
                NoticeBox.Show($"当前无法切换工程：{switchBlockReason}", "工程切换", MessageBoxIcon.Warning, true, 6000);
                writeLog($"[工程切换] 已拦截，目标工程={PrjName}，原因={switchBlockReason}", false);
                Prjs_selectedindex();
                return false;
            }

            ResetRuntimeForProjectInspectionChange();
            DataModel.Settingmodel.Name = PrjName;
            Load_Prj();
            UpdateInspectionCountdown();
            CheckInspectionStatus();
            writeLog($"[工程切换] 当前工程={PrjName}，点检配方已加载", false);
            return true;
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

        /// <summary>
        /// 过程数据本轮启动的加载健康状态，用于防止加载失败后的默认运行状态覆盖现场文件。
        /// </summary>
        private bool _processConfigLoadFailed;

        public void SaveProcessmodel()
        {
            string filename = $"{Environment.CurrentDirectory}\\配置\\过程数据.xml";
            ConfigXmlSaveHelper.TrySave(
                filename,
                DataModel.Processmodel,
                _processConfigLoadFailed,
                WriteDetailLog);
        }

        public void LoadProcessmodel()
        {
            string filename = $"{Environment.CurrentDirectory}\\配置\\过程数据.xml";
            ConfigLoadResult<Processmodel> result = ConfigXmlSaveHelper.TryLoad(
                filename,
                () => new Processmodel(),
                WriteDetailLog);

            DataModel.Processmodel = result.Data;
            _processConfigLoadFailed = result.LoadFailed;
            BuildConfiguredStages();
        }
        #endregion

        #region 配置数据

        /// <summary>
        /// 主配置本轮启动的加载健康状态。现场文件损坏且备份无法恢复时，该状态会阻止关闭和点检流程写回默认配置。
        /// </summary>
        private bool _settingConfigLoadFailed;

        /// <summary>
        /// 启动加载前备份系统配置 XML。备份位于程序目录的“配置备份”下，用于现场追溯和人工恢复。
        /// </summary>
        public void BackupConfigXmlsBeforeLoad()
        {
            ConfigXmlSaveHelper.BackupXmlFiles(
                Path.Combine(Environment.CurrentDirectory, "配置"),
                Path.Combine(Environment.CurrentDirectory, "配置备份"),
                "系统配置",
                WriteDetailLog);
        }

        /// <summary>
        /// 主配置加载后、工程工具加载前备份当前工程。快照覆盖工具 XML、示教图片、视觉模型和子目录，工程选择与生产执行流程保持原有行为。
        /// </summary>
        public void BackupCurrentProjectFilesBeforeLoad()
        {
            if (string.IsNullOrWhiteSpace(DataModel.Settingmodel.Prjdir)
                || string.IsNullOrWhiteSpace(DataModel.Settingmodel.Name))
            {
                WriteDetailLog("[配置备份] 跳过工程备份：工程目录或工程名为空");
                return;
            }

            string projectDir = Path.Combine(DataModel.Settingmodel.Prjdir, DataModel.Settingmodel.Name);
            ConfigXmlSaveHelper.BackupProjectFiles(
                projectDir,
                Path.Combine(Environment.CurrentDirectory, "配置备份"),
                $"工程_{DataModel.Settingmodel.Name}",
                WriteDetailLog);
        }

        /// <summary>
        /// 保存主配置。工位、工序、点检时间、PLC 参数和当前工程经临时文件校验后原子替换。
        /// </summary>
        public void SaveSettingModel()
        {
            string filename = $"{Environment.CurrentDirectory}\\配置\\配置数据.xml";
            ConfigXmlSaveHelper.TrySave(
                filename,
                DataModel.Settingmodel,
                _settingConfigLoadFailed,
                WriteDetailLog,
                DecorateSettingConfigXml);
        }

        /// <summary>
        /// 在主配置中写入长期保留的中文操作说明。全局周期、判定规则和旧版迁移字段完成安全保存后都会刷新对应说明。
        /// </summary>
        /// <param name="filename">已经完成序列化、等待校验和原子替换的临时主配置文件。</param>
        private static void DecorateSettingConfigXml(string filename)
        {
            XDocument document = XDocument.Load(filename);
            XElement reportWorkConfig = document.Root?.Element("报工配置");
            if (reportWorkConfig == null)
            {
                throw new InvalidOperationException("主配置缺少报工配置节点，无法写入点检操作说明");
            }

            XElement intervalElement = reportWorkConfig.Element("点检周期小时");
            XElement ngConditionElement = reportWorkConfig.Element("NG点检合格条件");
            XElement okSnElement = reportWorkConfig.Element("OK点检SN码");
            XElement ngSnElement = reportWorkConfig.Element("NG点检SN码");
            XElement lastTimeElement = reportWorkConfig.Element("上次点检时间");
            XElement manualInspectionModeElement = reportWorkConfig.Element("手动点检模式");
            XElement snDecodeModeElement = reportWorkConfig.Element("SN解码方式");
            XElement uiLayoutModeElement = document.Root?.Element("界面布局模式");
            XElement captureDelayElement = document.Root?.Element("测试延时");
            XElement executionTimeoutElement = document.Root?.Element("执行超时ms");
            XElement stageRetryElement = document.Root?.Element("阶段失败重试策略");
            XElement serialSoftwareTriggerElement = document.Root?.Element("串口软件主动触发");
            if (intervalElement == null || ngConditionElement == null || okSnElement == null || ngSnElement == null || lastTimeElement == null)
            {
                throw new InvalidOperationException("主配置缺少点检配置节点，无法写入点检操作说明");
            }

            intervalElement.AddBeforeSelf(new XComment(
                " 点检周期和NG判定规则属于全局设置；OK和NG点检SN、上次完成时间跟随产品工程保存，请在主界面切换到目标工程后打开“点检配置”维护。 "));
            intervalElement.AddBeforeSelf(new XComment(
                " 点检周期有效范围为1-48小时，单位为小时；建议填写12，配置节点缺失、填写小于1或大于48的值时按12小时处理。 "));
            ngConditionElement.AddBeforeSelf(new XComment(
                " NG点检合格条件只支持“任一NG”或“全部NG”；任一NG要求至少一个参与判定框为NG，全部NG要求所有参与判定框均为NG，NG2始终表示点检异常。 "));
            okSnElement.AddBeforeSelf(new XComment(
                " 旧版本全局OK点检SN，仅在工程缺少“点检配置.xml”时作为首次迁移来源；运行配置请在当前工程的“点检配置”界面维护。 "));
            ngSnElement.AddBeforeSelf(new XComment(
                " 旧版本全局NG点检SN，仅在工程缺少“点检配置.xml”时作为首次迁移来源；运行配置请在当前工程的“点检配置”界面维护。 "));
            lastTimeElement.AddBeforeSelf(new XComment(
                " 旧版本全局点检完成时间，仅在工程缺少“点检配置.xml”时作为首次迁移来源；运行时由各工程独立记录点检周期起点。 "));
            if (manualInspectionModeElement != null)
            {
                manualInspectionModeElement.AddBeforeSelf(new XComment(
                    " 手动点检模式只支持“提前点检”或“强制到期”，控制倒计时栏“开始点检”按钮。提前点检：未到期时只打开窗口，不拦截普通生产；强制到期：清空上次点检时间并立即开启普通生产门禁，直到OK和NG点检完成。配置缺失或非法值按提前点检处理；请先退出软件再改，保存后重启生效。 "));
            }
            if (snDecodeModeElement != null)
            {
                snDecodeModeElement.AddBeforeSelf(new XComment(
                    " SN解码方式只支持“DecodeSN”或“最终绑定”。DecodeSN：按扫码内容用MES DecodeSN解析产品SN（与3月可用版本一致，现场默认推荐）。最终绑定：先查MES零件码最终绑定的产品SN，查不到再回退DecodeSN。配置缺失或非法值按DecodeSN处理；请先退出软件再改，保存后重启生效。 "));
            }
            if (uiLayoutModeElement != null)
            {
                uiLayoutModeElement.AddBeforeSelf(new XComment(
                    " 界面布局模式支持“自动”“标准”“紧凑”。自动模式在WPF有效工作区宽度不大于1366或高度不大于768时采用紧凑布局，可覆盖任务栏和Windows DPI缩放；修改后重启软件生效。 "));
            }
            if (captureDelayElement != null)
            {
                captureDelayElement.AddBeforeSelf(new XComment(
                    " 测试延时按“触发指令 + 相机序列号”拍照组执行一次，单位为毫秒；同组多个工具共享本次等待和同一张回图，曝光取组内首个工具。两台相机各一组且填写1000时，拍照准备等待合计约2秒；现场以稳定回图为前提逐步调低。 "));
            }
            if (executionTimeoutElement != null)
            {
                executionTimeoutElement.AddBeforeSelf(new XComment(
                    " 执行超时ms用于单个相机组软触发后的回图等待，单位为毫秒，最小生效值500；图像进入检测后由当前执行代次处理到帧尾。 "));
            }
            if (stageRetryElement != null)
            {
                stageRetryElement.AddBeforeSelf(new XComment(
                    " 普通生产阶段失败重试策略：从头开始重跑会从A1重新执行；仅重试失败阶段会保留已完成阶段。点检模式使用独立规则，当前失败阶段重复触发，XML全局策略不改变点检重试方式。 "));
            }
            if (serialSoftwareTriggerElement != null)
            {
                serialSoftwareTriggerElement.AddBeforeSelf(new XComment(
                    " 串口扫码触发方式：true表示软件通过串口发送启动指令后取码；false表示由扫码器物理IO触发，软件只接收串口数据。该配置只影响串口模式，修改后重启软件生效。 "));
            }

            document.Save(filename);
        }

        /// <summary>
        /// 加载主配置。主文件不可用时按 <c>.bak</c>、<c>.lastgood</c> 恢复，全部失败时保留磁盘文件并禁止本轮回写。
        /// </summary>
        public void LoadSettingModel()
        {
            string filename = $"{Environment.CurrentDirectory}\\配置\\配置数据.xml";
            ConfigLoadResult<SettingModel> result = ConfigXmlSaveHelper.TryLoad(
                filename,
                () => new SettingModel(),
                WriteDetailLog);

            DataModel.Settingmodel = result.Data;
            _settingConfigLoadFailed = result.LoadFailed;

            if (result.RestoredFromBackup)
            {
                writeLog($"配置已从备份恢复：{Path.GetFileName(result.RestoredFrom)}", false);
            }
            else if (result.LoadFailed)
            {
                MessageBox.Show(
                    "配置数据.xml 损坏且无法从 .bak / .lastgood 恢复。\r\n" +
                    "软件本轮使用内存默认配置，关闭时会保留磁盘文件，请检查配置备份目录。");
            }
        }
        #endregion

        #region 日志数据

        /// <summary>
        /// 日志数据本轮启动的加载健康状态，用于保护已存在的现场记录文件。
        /// </summary>
        private bool _recordConfigLoadFailed;

        public void SaveRecordModel()
        {
            string filename = $"{Environment.CurrentDirectory}\\配置\\日志数据.xml";
            ConfigXmlSaveHelper.TrySave(
                filename,
                DataModel.Recordmodel,
                _recordConfigLoadFailed,
                WriteDetailLog);
        }

        public void LoadRecordModel()
        {
            string filename = $"{Environment.CurrentDirectory}\\配置\\日志数据.xml";
            ConfigLoadResult<RecordModel> result = ConfigXmlSaveHelper.TryLoad(
                filename,
                () => new RecordModel(),
                WriteDetailLog);

            DataModel.Recordmodel = result.Data;
            _recordConfigLoadFailed = result.LoadFailed;
        }
        #endregion
        #region 配方保存加载

        /// <summary>
        /// 当前工程配方本轮加载的健康状态。任一工具 XML 无法恢复时，整套工程禁止回写，避免部分工具覆盖完整现场配方。
        /// </summary>
        private bool _projectConfigLoadFailed;

        /// <summary>
        /// 保存当前工程工具。全部工具成功安全落盘后，才会清理超出当前工具数量的旧配方及备份。
        /// </summary>
        /// <returns>整套工程配方完成落盘时返回 true。</returns>
        public bool SavePrjXmls()
        {
            try
            {
                if (_projectConfigLoadFailed)
                {
                    WriteDetailLog("[配置保存] 跳过：当前工程存在无法恢复的工具配置，保留整套现场文件");
                    return false;
                }

                if (!ConfigXmlSaveHelper.HasValidTools(DataModel.Processmodel.Tools))
                {
                    WriteDetailLog("[配置保存] 跳过：当前工程仅包含空白占位工具，保留现有 Tool*.xml");
                    return false;
                }

                for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
                {
                    if (!SavePrjXml(DataModel.Processmodel.Tools[i], i + 1))
                    {
                        WriteDetailLog("[配置保存] 工程配方未全部落盘，保留多余工具文件供恢复");
                        return false;
                    }
                }

                string projectDir = Path.Combine(DataModel.Settingmodel.Prjdir, DataModel.Settingmodel.Name);
                CleanupExtraToolConfigFiles(projectDir, DataModel.Processmodel.Tools.Count);
                return true;
            }
            catch (Exception ex)
            {
                WriteDetailLog($"[配置保存] 工程配方保存异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 保存单个工具 XML。工具相机序列号、曝光参数、ROI 和检测阈值在同一个经校验的文件中更新。
        /// </summary>
        /// <param name="td">待保存的工具配置。</param>
        /// <param name="index">工具文件序号。</param>
        /// <returns>工具正式文件更新完成时返回 true。</returns>
        private bool SavePrjXml(ToolModel td, int index)
        {
            string filename = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{index}.xml";
            WriteCameraDebugLog($"[保存工具配置] 工具下标={td?.Index ?? -1} 工具XML='{filename}' 相机序列号='{td?.CameraSerialNumber}' 触发指令='{td?.Command}' 曝光时间={td?.ExposureTime ?? -1}");
            return ConfigXmlSaveHelper.TrySaveProjectFile(filename, td, false, WriteDetailLog);
        }

        /// <summary>
        /// 加载当前工程的工具配方。配方索引同时从主文件和备份文件识别，支持主文件缺失时的备份恢复。
        /// </summary>
        public void LoadPrjXmls()
        {
            _projectConfigLoadFailed = false;
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

            var toolFileIndexes = new SortedSet<int>();
            for (int i = 0; i < toolfilenames.Length; i++)
            {
                FileInfo fi = new FileInfo(toolfilenames[i]);
                WriteDetailLog($"[工具加载] 文件{i}: {fi.Name}, 扩展名: {fi.Extension}");
                int toolFileIndex;
                if (TryGetToolIndexFromConfigArtifact(fi.Name, out toolFileIndex))
                {
                    toolFileIndexes.Add(toolFileIndex);
                }
            }

            WriteDetailLog($"[工具加载] 找到工具配置数量: {toolFileIndexes.Count}");
            if (toolFileIndexes.Count > 0)
            {
                int highestToolIndex = toolFileIndexes.Max;
                for (int expectedIndex = 1; expectedIndex <= highestToolIndex; expectedIndex++)
                {
                    if (!toolFileIndexes.Contains(expectedIndex))
                    {
                        _projectConfigLoadFailed = true;
                        WriteDetailLog($"[工具加载] 缺少 Tool{expectedIndex}.xml 及恢复副本，本轮禁止覆盖工程文件");
                    }
                }
            }

            int index = 0;
            foreach (int toolFileIndex in toolFileIndexes)
            {
                try
                {
                    var tool = LoadPrjXml(toolFileIndex);
                    if (tool != null)
                    {
                        tool.Index = (index++) + 1;
                        DataModel.Processmodel.Tools.Add(tool);
                        string jpgfilename = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}\\Tool{toolFileIndex}.jpg";
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
                            tool.CurrentBitmapSource = ImageHelper.ToBitmapSource(bmp);

                            tool.BitmapSource = null;
                            tool.BitmapSource = ImageHelper.ToBitmapSource(bmp);

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
                }
                catch (Exception ex)
                {

                }
            }

            // 工程工具从 XML 加载后必须重建阶段列表；否则 ConfiguredStages 为空，扫码自动执行会报「未配置阶段」。
            // （MainWindow 启动时 LoadProcessmodel 未调用，不能依赖其 finally 中的 BuildConfiguredStages。）
            BuildConfiguredStages();
            WriteDetailLog($"[工具加载] BuildConfiguredStages 完成 工具数={DataModel.Processmodel.Tools.Count} " +
                          $"阶段序列=[{string.Join("->", DataModel.Processmodel.ConfiguredStages ?? new List<string>())}]");

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
                    BitmapSource bs = ImageHelper.ToBitmapSource(bmp);
                    DataModel.Processmodel.ShowBitmapSource = null;
                    DataModel.Processmodel.ShowBitmapSource = bs;

                }
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        /// <summary>
        /// 清理已超出当前工具数量的配方文件及恢复副本。该流程只在当前全部工具成功落盘后执行。
        /// </summary>
        /// <param name="projectDir">当前工程目录。</param>
        /// <param name="toolCount">当前工具数量。</param>
        private void CleanupExtraToolConfigFiles(string projectDir, int toolCount)
        {
            if (!Directory.Exists(projectDir))
            {
                return;
            }

            foreach (string filePath in Directory.GetFiles(projectDir))
            {
                int toolIndex;
                if (!TryGetToolIndexFromConfigArtifact(Path.GetFileName(filePath), out toolIndex) || toolIndex <= toolCount)
                {
                    continue;
                }

                try
                {
                    File.Delete(filePath);
                    WriteDetailLog($"[配置保存] 删除多余配方文件：{Path.GetFileName(filePath)}");
                }
                catch (Exception ex)
                {
                    WriteDetailLog($"[配置保存] 删除多余配方失败：{Path.GetFileName(filePath)}，{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 从 <c>ToolN.xml</c>、<c>ToolN.xml.bak</c> 或 <c>ToolN.xml.lastgood</c> 中解析工具序号。
        /// </summary>
        /// <param name="fileName">工程目录中的文件名。</param>
        /// <param name="toolIndex">成功时返回工具文件序号。</param>
        /// <returns>文件属于可加载的工具 XML 或恢复副本时返回 true。</returns>
        private static bool TryGetToolIndexFromConfigArtifact(string fileName, out int toolIndex)
        {
            toolIndex = 0;
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.StartsWith("Tool", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int xmlPosition = fileName.IndexOf(".xml", StringComparison.OrdinalIgnoreCase);
            if (xmlPosition <= 4)
            {
                return false;
            }

            string suffix = fileName.Substring(xmlPosition + 4);
            bool supportedSuffix = suffix.Length == 0
                || string.Equals(suffix, ".bak", StringComparison.OrdinalIgnoreCase)
                || string.Equals(suffix, ".lastgood", StringComparison.OrdinalIgnoreCase);

            return supportedSuffix
                && int.TryParse(fileName.Substring(4, xmlPosition - 4), out toolIndex)
                && toolIndex > 0;
        }

        /// <summary>
        /// 加载指定序号的工具配方。主文件不可用时按备份顺序恢复；全部失败时标记整套工程禁止回写。
        /// </summary>
        /// <param name="index">工具文件序号。</param>
        /// <returns>成功加载或恢复的工具配置；无可用文件时返回 null。</returns>
        private ToolModel LoadPrjXml(int index)
        {
            string dir = $"{DataModel.Settingmodel.Prjdir}\\{DataModel.Settingmodel.Name}";
            string filename = $"{dir}\\Tool{index}.xml";

            ConfigLoadResult<ToolModel> result = ConfigXmlSaveHelper.TryLoad<ToolModel>(
                filename,
                () => null,
                WriteDetailLog);

            if (result.RestoredFromBackup)
            {
                writeLog($"配方 Tool{index} 已从备份恢复：{Path.GetFileName(result.RestoredFrom)}", false);
            }
            else if (result.LoadFailed)
            {
                _projectConfigLoadFailed = true;
                writeLog($"配方 Tool{index}.xml 损坏且无法从备份恢复，本轮禁止覆盖工程文件", true);
            }

            return result.Data;
        }
        #endregion
        #endregion

        #region 相机数据处理
        public void start()
        {
            DataModel.Settingmodel.camedata.CameraModel.camera.ImageReceived += OnReceive;
            DataModel.Processmodel.CameraStatus = "等待回图";
            writeLog("[相机] 已订阅回调(ImageReceived)，等待回图", false);
        }
        /// <summary>
        /// 相机 SDK 的 ImageReceived 入口：
        /// - PLC 执行窗口且“等图门闩”通过时，冻结执行代次并投递到 UI 线程执行识别/报工。
        /// - 未在执行态且请求号为 0 的操作员软触发只做预览显示；带生产请求号的超时迟到帧直接释放，避免覆盖当前产品证据。
        /// - 回调图像的所有权在此入口完成交接；预览、丢弃、生产处理和投递异常路径都会释放 HALCON 图像句柄。
        /// </summary>
        private void OnReceive(object sender, EventArgs e)
        {
            HObject receivedImage = null;
            bool ownershipTransferredToUi = false;
            FrameProcessingContext acceptedContext = null;
            try
            {
                writeLog($"相机->视觉:接收照片", false);

                MyEventArgs myEventArgs = e as MyEventArgs;
                if (myEventArgs == null)
                {
                    WriteDetailLog("[回调丢弃] ImageReceived 参数类型异常");
                    return;
                }

                receivedImage = myEventArgs.Image;
                int imageHeight = myEventArgs.Height;
                int imageWidth = myEventArgs.Width;

                if (!TryAcceptImageCallback(myEventArgs, out acceptedContext, out string dropReason))
                {
                    if (dropReason == "当前不在执行态" && myEventArgs.CaptureRequestId == 0)
                    {
                        HObject previewImage = receivedImage;
                        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                HWindow hwindow = DataModel.Settingmodel.HWindow;
                                hwindow.ClearWindow();
                                hwindow.DispObj(previewImage);
                            }
                            catch (Exception ex)
                            {
                                WriteDetailLog($"[相机预览] 显示失败: {ex.Message}");
                            }
                            finally
                            {
                                previewImage?.Dispose();
                            }
                        }));
                        ownershipTransferredToUi = true;
                        return;
                    }

                    WriteDetailLog(
                        $"[回调丢弃] {dropReason} session={myEventArgs.CaptureSessionId} " +
                        $"request={myEventArgs.CaptureRequestId} frame={myEventArgs.FrameNumber}");
                    return;
                }

                HObject productionImage = receivedImage;
                FrameProcessingContext productionContext = acceptedContext;
                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        OnReceiveProcess(productionImage, imageHeight, imageWidth, productionContext);
                    }
                    finally
                    {
                        productionImage?.Dispose();
                    }
                }));
                ownershipTransferredToUi = true;
            }
            catch (Exception ex)
            {
                DataModel.Processmodel.CameraStatus = "相机异常";
                writeError($"[{acceptedContext?.Command ?? DataModel.Processmodel.RCMD}]回图投递错误:{ex}");
                if (acceptedContext != null)
                {
                    DataModel.Processmodel.Status = ToolStatus.NG2;
                    DataModel.Processmodel.NeedRestart = true;
                    EndExecution(acceptedContext.ExecutionId);
                }
            }
            finally
            {
                if (!ownershipTransferredToUi)
                {
                    receivedImage?.Dispose();
                }
            }
        }


        /// <summary>
        /// 为本次回图生成一份可由同组工具共用的冻结缩略图。缩略图只服务主界面状态展示，检测算法持续使用原始回图。
        /// </summary>
        private BitmapSource CreateFrameThumbnail(HObject image)
        {
            HObject reducedImage = null;
            Bitmap bitmap = null;
            try
            {
                int imageSize = DataModel?.Settingmodel?.EffectiveImageSize ?? 140;
                imageSize = Math.Max(40, imageSize);
                reducedImage = GetReducedImage(imageSize, imageSize, image);
                Hobject2Bitmap.HobjectToBitmap24(reducedImage, out bitmap);
                BitmapSource source = ImageHelper.ToBitmapSource(bitmap);
                if (source != null && source.CanFreeze)
                {
                    source.Freeze();
                }
                return source;
            }
            catch (Exception ex)
            {
                WriteDetailLog($"[帧缩略图] 生成失败: {ex.Message}");
                return null;
            }
            finally
            {
                bitmap?.Dispose();
                reducedImage?.Dispose();
            }
        }

        /// <summary>
        /// 清理 SN、指令、相机标识和工具名称中的文件名非法字符，保证现场码值无法改变照片目录层级。
        /// </summary>
        private static string SanitizeFileNamePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (char character in value.Trim())
            {
                builder.Append(character < 32 || invalidChars.Contains(character) ? '_' : character);
            }

            return builder.ToString().Trim(' ', '.', '_');
        }

        /// <summary>
        /// 获取当前生产图片的规格名称。dev-mpw 沿用工程配方作为规格身份，
        /// 规格目录和文件名前缀使用同一值，空工程名时保留可检索的 UNKNOWN_SPEC 兜底。
        /// </summary>
        /// <returns>完成文件名清理和长度限制的规格名称。</returns>
        private string GetImageSpecificationName()
        {
            string specification = SanitizeFileNamePart(DataModel?.Settingmodel?.Name);
            if (string.IsNullOrEmpty(specification))
            {
                WriteDetailLog("[图片保存] 当前工程名称为空，使用UNKNOWN_SPEC作为规格目录和文件名前缀");
                return "UNKNOWN_SPEC";
            }

            return LimitFileNamePart(specification, 48);
        }

        /// <summary>
        /// 控制动态文件名片段长度。超长内容保留前缀和稳定哈希，兼顾现场可读性与 Windows 路径长度限制。
        /// </summary>
        private static string LimitFileNamePart(string value, int maxLength)
        {
            string normalized = value ?? string.Empty;
            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            byte[] hashBytes;
            using (SHA256 sha256 = SHA256.Create())
            {
                hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            }

            string hash = BitConverter.ToString(hashBytes, 0, 4).Replace("-", string.Empty);
            int prefixLength = Math.Max(1, maxLength - hash.Length - 1);
            return normalized.Substring(0, prefixLength) + "_" + hash;
        }

        /// <summary>
        /// 生成整帧追溯标识。MES 解析后的当前产品 SN 优先；其后依次汇总本帧工具对应工位 SN、工具条码、扫码原文和兼容条码字段。
        /// </summary>
        private string ResolveFrameImageSn(IList<ToolModel> tools, string frozenProductSn = null)
        {
            string currentProductSn = SanitizeFileNamePart(
                string.IsNullOrWhiteSpace(frozenProductSn)
                    ? DataModel?.Processmodel?.CurrentProductSN
                    : frozenProductSn);
            if (!string.IsNullOrEmpty(currentProductSn))
            {
                return currentProductSn;
            }

            var positionSns = new List<string>();
            if (tools != null && DataModel?.Processmodel?.SNList != null)
            {
                foreach (ToolModel tool in tools)
                {
                    int position = tool.ProductPositionNO;
                    if (position < 0 || position >= DataModel.Processmodel.SNList.Count)
                    {
                        continue;
                    }

                    string positionSn = SanitizeFileNamePart(DataModel.Processmodel.SNList[position]);
                    if (!string.IsNullOrEmpty(positionSn) && !positionSns.Contains(positionSn))
                    {
                        positionSns.Add(positionSn);
                    }
                }
            }
            if (positionSns.Count > 0)
            {
                return string.Join("+", positionSns);
            }

            if (tools != null)
            {
                string toolBarcode = tools
                    .Select(tool => SanitizeFileNamePart(tool.BarcodeStr))
                    .FirstOrDefault(value => !string.IsNullOrEmpty(value));
                if (!string.IsNullOrEmpty(toolBarcode))
                {
                    return toolBarcode;
                }
            }

            string scannerCode = SanitizeFileNamePart(DataModel?.Processmodel?.Scannerstr);
            if (!string.IsNullOrEmpty(scannerCode))
            {
                return scannerCode;
            }

            string compatibilityBarcode = SanitizeFileNamePart(DataModel?.Processmodel?.BarcodeStr);
            return string.IsNullOrEmpty(compatibilityBarcode) ? "NOSN" : compatibilityBarcode;
        }

        /// <summary>
        /// 按“一次受理回图对应一个 JPEG”保存检测证据。普通生产按整帧实际结果及存图开关归档；
        /// 点检强制保存到“点检/规格/日期/期望OK或NG”，文件名保留标准件类别与实际工具结果，便于追溯误判。
        /// </summary>
        /// <returns>本帧实际写入 JPEG 时返回 true；图像、工具或保存配置缺失，以及普通生产对应存图开关关闭时返回 false。</returns>
        private bool SaveFrameImage(HObject image, IList<ToolModel> matchedTools, FrameProcessingContext context)
        {
            if (image == null || matchedTools == null || matchedTools.Count == 0)
            {
                return false;
            }

            bool frameOk = matchedTools.All(tool => tool.ToolStatus == ToolStatus.OK);
            string expectedResult = context?.InspectionExpectedResult;
            bool isInspectionImage = expectedResult == "OK" || expectedResult == "NG";
            var imageSaveSetting = DataModel?.Settingmodel?.ImageSaveSetting;
            if (imageSaveSetting == null || (!isInspectionImage
                && ((frameOk && !imageSaveSetting.SaveOK) || (!frameOk && !imageSaveSetting.SaveNG))))
            {
                return false;
            }

            string resultFolder = frameOk ? "OK" : "NG";
            DateTime now = DateTime.Now;
            long frameSequence = Interlocked.Increment(ref _imageSaveSequence);
            string sn = LimitFileNamePart(
                SanitizeFileNamePart(ResolveFrameImageSn(matchedTools, context?.ProductSerialNumber)),
                32);
            string command = LimitFileNamePart(SanitizeFileNamePart(context?.Command), 12);
            string camera = LimitFileNamePart(SanitizeFileNamePart(context?.CameraSerial), 16);
            string fullToolSummary = string.Join(
                "+",
                matchedTools.Select(tool =>
                    $"{tool.Index:00}_{SanitizeFileNamePart(tool.Name)}_P{tool.ProductPositionNO}_{tool.ToolStatus}"));
            string toolSummary = LimitFileNamePart(fullToolSummary, 48);
            string specification = GetImageSpecificationName();
            string inspectionIdentity = isInspectionImage ? $"点检-{expectedResult}标准件-{resultFolder}-" : string.Empty;

            string fileName =
                $"{specification}-" +
                inspectionIdentity +
                $"{(string.IsNullOrEmpty(sn) ? "NOSN" : sn)}-" +
                $"{(string.IsNullOrEmpty(command) ? "NOCMD" : command)}-" +
                $"{(string.IsNullOrEmpty(camera) ? "NOCAM" : camera)}-" +
                $"{(string.IsNullOrEmpty(toolSummary) ? "NOTOOL" : toolSummary)}-" +
                $"{now:yyyyMMddHHmmssFFF}-{frameSequence:D6}.jpg";
            string directory = isInspectionImage
                ? Path.Combine(imageSaveSetting.ImageSaveDir, "点检", specification, now.ToString("yyyyMMdd"), expectedResult)
                : Path.Combine(imageSaveSetting.ImageSaveDir, specification, now.ToString("yyyyMMdd"), resultFolder);
            Directory.CreateDirectory(directory);
            string savePath = Path.Combine(directory, fileName);

            HOperatorSet.WriteImage(image, "jpg", 0, savePath);
            WriteDetailLog(
                $"[图片保存] 一帧一图完成 结果={resultFolder} 指令='{context?.Command}' " +
                $"相机='{context?.CameraSerial}' 工具数={matchedTools.Count} 工具结果='{fullToolSummary}' 路径='{savePath}'");
            return true;
        }

        /// <summary>
        /// UI 线程上处理一帧图像。生产上下文按冻结的阶段和相机执行；照片测试上下文只执行操作员选中的工具。
        /// </summary>
        /// <remarks>
        /// 多相机阶段：在 finally 中若仍有 <see cref="_pendingCameraGroups"/>，会调用 <see cref="TriggerNextCameraGroupCapture"/>；
        /// 若返回 false 或抛异常，则立即 NG、NeedRestart、<see cref="EndExecution"/>，不等待超时看门狗。
        /// 阶段结算：按冻结的阶段指令与工具 <see cref="ToolModel.Command"/> 做 <see cref="NormalizeStageCommand"/> 后筛选本阶段工具，
        /// 再聚合 OK/NG/NG2 决定阶段是否完成、是否推进 <see cref="Processmodel.CurrentStageIndex"/> 以及是否触发自动报工；点检样件由独立结算方法累计全部配置阶段后判定。
        /// </remarks>
        private void OnReceiveProcess(HObject Image, int H, int W, FrameProcessingContext context)
        {
            bool isProduction = context?.IsProduction == true;
            string frameCommand = NormalizeStageCommand(context?.Command);
            Stopwatch frameStopwatch = Stopwatch.StartNew();
            long thumbnailElapsedMs = 0;
            long toolDetectionElapsedMs = 0;
            long outputElapsedMs = 0;
            long imageSaveElapsedMs = 0;
            var processRecords = new List<string>();
            bool frameProcessingFailed = false;
            // 同一拍照组的工具复用回图和缩略图，帧尾按整帧结果只保存一个 JPEG。
            List<ToolModel> matchedTools = new List<ToolModel>();
            try
            {
                if (isProduction && !IsExecutionContextActive(context))
                {
                    WriteDetailLog($"[回图丢弃] 执行代次已失效 id={context.ExecutionId} 指令={frameCommand}");
                    return;
                }

                if (isProduction)
                {
                    DataModel.Processmodel.CameraStatus = "正常";
                }

                #region 图片接收
                HWindow hwindow = DataModel.Settingmodel.HWindow;
                hwindow.ClearWindow();
                //hwindow.SetPart(0, 0, H - 1, W - 1);
                hwindow.DispObj(Image);

                Stopwatch thumbnailStopwatch = Stopwatch.StartNew();
                BitmapSource frameThumbnail = CreateFrameThumbnail(Image);
                thumbnailElapsedMs = thumbnailStopwatch.ElapsedMilliseconds;

                for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
                {
                    bool isMatch = isProduction
                        ? NormalizeStageCommand(DataModel.Processmodel.Tools[i].Command) == frameCommand
                        : i == context.ManualToolIndex;
                    if (isMatch && isProduction && context.CameraSerial != null)
                    {
                        isMatch = NormalizeCameraSerial(DataModel.Processmodel.Tools[i].CameraSerialNumber) == context.CameraSerial;
                    }

                    if (isMatch)
                    {
                        if (isProduction && matchedTools.Count == 0)
                        {
                            DataModel.Processmodel.Status = ToolStatus.识别中;
                        }

                        DataModel.Processmodel.ToolIndex = i + 1;

                        ToolModel tool = DataModel.Processmodel.Tools[i];
                        tool.CurrentBitmapSource = frameThumbnail;

                        Stopwatch stopwatch = Stopwatch.StartNew();
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

                                        if (isProduction && tool.MPMode)
                                        {
                                            DataModel.Processmodel.Barcodes += barcodelist[0].codestr + ";";
                                        }
                                        if (isProduction && tool.SendBarcode)
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
                                                    DataModel.Processmodel.Barcodes = string.Empty;


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
                                        writeLog("二维码读取失败: 未识别到有效条码");
                                        tool.ToolStatus = ToolStatus.NG;

                                    }
                                }
                                catch (Exception ex)
                                {
                                    tool.ToolStatus = ToolStatus.NG2;
                                    writeError($"工具{tool.Index}({tool.Name})二维码识别异常: {ex.Message}");
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
                                catch (Exception ex)
                                {
                                    tool.ToolStatus = ToolStatus.NG2;
                                    writeError($"工具{tool.Index}({tool.Name})模板匹配异常: {ex.Message}");
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
                        catch (Exception ex)
                        {
                            tool.ToolStatus = ToolStatus.NG2;
                            writeError($"工具{tool.Index}({tool.Name})检测异常: {ex.Message}");
                        }

                        long detectionElapsedMs = stopwatch.ElapsedMilliseconds;
                        toolDetectionElapsedMs += detectionElapsedMs;
                        processRecords.Add($"{DataModel.Processmodel.Tools[i].Name}:识别耗时:{detectionElapsedMs}ms");
                        stopwatch.Restart();

                        // 记录本帧匹配工具的最终状态；图片保存改到帧尾合并处理
                        matchedTools.Add(tool);

                        if (isProduction && tool.SendStatus)
                        {

                            int status = -1;

                            var r = (from ToolModel in DataModel.Processmodel.Tools
                                     where (NormalizeStageCommand(ToolModel.Command) == frameCommand)
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
                        long sendElapsedMs = stopwatch.ElapsedMilliseconds;
                        outputElapsedMs += sendElapsedMs;
                        processRecords.Add($"{DataModel.Processmodel.Tools[i].Name}:发送结果耗时:{sendElapsedMs}ms");

                    }
                }

                #region 保存图片（一次受理回图只保存一个 JPEG）
                if (isProduction)
                {
                    try
                    {
                        Stopwatch imageSaveStopwatch = Stopwatch.StartNew();
                        SaveFrameImage(Image, matchedTools, context);
                        imageSaveElapsedMs = imageSaveStopwatch.ElapsedMilliseconds;
                    }
                    catch (Exception ex)
                    {
                        writeLog($"视觉->保存照片(一帧一图)失败：{ex}", false);
                    }
                }
                #endregion

                #region 阶段结算（按当前指令过滤工具集合）
                // 业务说明：currentCommand 与工具 Command 均经 NormalizeStageCommand，与 ConfiguredStages、PLC/扫码触发的阶段键一致，
                // 避免「全 A1 却判阶段未完成」等由字符串脏数据导致的误判。
                if (isProduction)
                {
                    string currentCommand = frameCommand;
                    var currentCommandTools = (from t in DataModel.Processmodel.Tools
                                               where (NormalizeStageCommand(t.Command) == currentCommand)
                                               select t).ToList();
                    int toolCount = currentCommandTools.Count;

                    if (toolCount > 0)
                    {
                        var okTools = currentCommandTools.Where(t => t.ToolStatus == ToolStatus.OK);
                        var ng2Tools = currentCommandTools.Where(t => t.ToolStatus == ToolStatus.NG2);
                        var waitTools = currentCommandTools.Where(t => t.ToolStatus == ToolStatus.等待中 || t.ToolStatus == ToolStatus.识别中);

                        if (waitTools.Count() == 0)
                        {
                        // 普通生产阶段结算：所有工具OK推进阶段；NG/NG2按配置重试策略处理。
                        // 点检阶段结算：OK样件要求当前阶段全OK；NG样件允许当前阶段出现普通NG并继续后续阶段，最终再汇总全部阶段工具。
                        int stageStatus;
                        if (okTools.Count() == toolCount)
                        {
                            stageStatus = 0;
                        }
                        else if (ng2Tools.Count() > 0)
                        {
                            stageStatus = 2;
                        }
                        else
                        {
                            stageStatus = 1;
                        }

                        // 点检阶段边界：当前点检按本次PLC触发的RCMD阶段执行，阶段通过推进点检内部索引，样件最终结果不触发普通报工。
                        if (DataModel.Processmodel.IsInspectionMode)
                        {
                            HandleInspectionStageResult(currentCommand, currentCommandTools, stageStatus);
                        }
                        else if (stageStatus == 0)
                        {
                            DataModel.Processmodel.Status = ToolStatus.OK;
                            DataModel.Processmodel.CompletedStages.Add(currentCommand);
                            DataModel.Processmodel.CurrentStageIndex = DataModel.Processmodel.CurrentStageIndex + 1;

                            // 自动报工触发点：
                            // - 仅当“所有已配置阶段均完成”且 SN/工序校验通过时才允许报工
                            // - 只配 A1 时：完成 A1 即满足 CanExecuteReportWork
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
                                List<string> reasons = new List<string>();
                                if (!DataModel.Settingmodel.ReportWorkConfig.EnableAutoReportWork)
                                    reasons.Add("自动报工未启用");
                                if (string.IsNullOrEmpty(DataModel.Processmodel.CurrentProductSN))
                                    reasons.Add("产品SN为空");
                                if (DataModel.Processmodel.ScanCodeStatus != "工序校验通过")
                                    reasons.Add($"工序校验状态: {DataModel.Processmodel.ScanCodeStatus}");
                                if (!DataModel.Processmodel.CanExecuteReportWork)
                                    reasons.Add($"阶段未全部完成({DataModel.Processmodel.CompletedStages.Count}/{DataModel.Processmodel.ConfiguredStages.Count})");
                                if (DataModel.Processmodel.IsReportingWork)
                                    reasons.Add("报工正在进行中");

                                string reasonText = reasons.Count > 0 ? $" - 原因: {string.Join(", ", reasons)}" : "";
                                writeLog($"⚠️ 检测完成，但未执行报工{reasonText}", false);

                                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    DataModel.Processmodel.ReportWorkStatus = reasons.Count > 0 ?
                                        $"未报工: {string.Join(", ", reasons)}" : "检测完成";
                                }));
                            }
                        }
                        else if (stageStatus == 2)
                        {
                            DataModel.Processmodel.Status = ToolStatus.NG2;
                            DataModel.Processmodel.NeedRestart = true;
                            WriteDetailLog($"[阶段判断NG2] 指令{currentCommand}，匹配工具数={toolCount}，OK={okTools.Count()}, NG2={ng2Tools.Count()}, 等待={waitTools.Count()}");
                            writeLog("检测结果：NG2，结果已显示在界面上", false);
                        }
                        else
                        {
                            DataModel.Processmodel.Status = ToolStatus.NG;
                            DataModel.Processmodel.NeedRestart = true;
                            WriteDetailLog($"[阶段判断NG] 指令{currentCommand}，匹配工具数={toolCount}，OK={okTools.Count()}, NG2={ng2Tools.Count()}, 等待={waitTools.Count()}");
                            writeLog("检测结果：NG，结果已显示在界面上", false);
                        }
                    }
                }
                }
                #endregion

                #endregion

            }
            catch (Exception ex)
            {
                frameProcessingFailed = true;
                if (isProduction)
                {
                    foreach (ToolModel tool in DataModel.Processmodel.Tools.Where(tool =>
                        NormalizeStageCommand(tool.Command) == frameCommand
                        && (context.CameraSerial == null || NormalizeCameraSerial(tool.CameraSerialNumber) == context.CameraSerial)))
                    {
                        if (tool.ToolStatus == ToolStatus.等待中 || tool.ToolStatus == ToolStatus.识别中)
                        {
                            tool.ToolStatus = ToolStatus.NG2;
                        }
                    }

                    lock (_executionLock)
                    {
                        if (_isExecuting && _activeExecutionId == context.ExecutionId)
                        {
                            _pendingCameraGroups?.Clear();
                        }
                    }

                    DataModel.Processmodel.Status = ToolStatus.NG2;
                    DataModel.Processmodel.NeedRestart = true;
                    DataModel.Processmodel.CameraStatus = "图像处理异常";
                    if (DataModel.Processmodel.IsInspectionMode)
                    {
                        UpdateInspectionStatusMessage($"{DataModel.Processmodel.InspectionType}点检阶段{frameCommand}图像处理异常，请调参后重复触发当前阶段。 ");
                    }
                }
                else if (context != null && context.ManualToolIndex >= 0 && context.ManualToolIndex < DataModel.Processmodel.Tools.Count)
                {
                    DataModel.Processmodel.Tools[context.ManualToolIndex].ToolStatus = ToolStatus.NG2;
                }

                writeError($"[{frameCommand}]图像处理异常:{ex.Message}");
            }
            finally
            {
                long frameElapsedMs = frameStopwatch.ElapsedMilliseconds;
                string processingMode = isProduction ? "生产" : "照片测试";
                string frameSummary =
                    $"模式={processingMode},代次={(isProduction ? context.ExecutionId.ToString() : "手工")},指令={frameCommand}," +
                    $"相机={context?.CameraSerial ?? "空"},曝光={(context?.ExposureTime.HasValue == true ? context.ExposureTime.Value.ToString() : "兼容")}," +
                    $"工具数={matchedTools.Count},缩略图={thumbnailElapsedMs}ms,工具识别合计={toolDetectionElapsedMs}ms," +
                    $"结果发送合计={outputElapsedMs}ms,留图={imageSaveElapsedMs}ms,整帧={frameElapsedMs}ms";
                processRecords.Add("帧汇总:" + frameSummary);
                Save_records(processRecords);
                writeLog("视觉帧处理完成: " + frameSummary, false);

                if (isProduction)
                {
                    bool contextActive = CompleteFrameProcessing(context);
                    if (!contextActive)
                    {
                        WriteDetailLog($"[帧尾收口] 执行代次已失效，跳过队列推进 id={context.ExecutionId} 指令={frameCommand}");
                    }
                    else if (frameProcessingFailed)
                    {
                        EndExecution(context.ExecutionId);
                    }
                    else if (_pendingCameraGroups != null && _pendingCameraGroups.Count > 0)
                    {
                        try
                        {
                            bool triggered = TriggerNextCameraGroupCapture(frameCommand, context.ExecutionId);
                            if (!triggered)
                            {
                                DataModel.Processmodel.Status = ToolStatus.NG;
                                DataModel.Processmodel.NeedRestart = true;
                                DataModel.Processmodel.CameraStatus = "相机异常";
                                if (DataModel.Processmodel.IsInspectionMode)
                                {
                                    UpdateInspectionStatusMessage($"{DataModel.Processmodel.InspectionType}点检阶段{frameCommand}相机触发失败，请调参后重复触发当前阶段。 ");
                                }
                                writeError("下一相机组触发失败，已按失败立即收口");
                                EndExecution(context.ExecutionId);
                            }
                        }
                        catch (Exception ex)
                        {
                            DataModel.Processmodel.Status = ToolStatus.NG;
                            DataModel.Processmodel.NeedRestart = true;
                            DataModel.Processmodel.CameraStatus = "相机异常";
                            if (DataModel.Processmodel.IsInspectionMode)
                            {
                                UpdateInspectionStatusMessage($"{DataModel.Processmodel.InspectionType}点检阶段{frameCommand}相机触发异常，请调参后重复触发当前阶段。 ");
                            }
                            writeError($"下一相机组触发异常，已按失败立即收口: {ex.Message}");
                            EndExecution(context.ExecutionId);
                        }
                    }
                    else
                    {
                        EndExecution(context.ExecutionId);
                    }
                }
            }

        }

        /// <summary>
        /// 使用本地照片执行当前选中工具。该入口与生产执行互斥，只更新所选工具的显示和检测结果，并跳过生产留图、设备发送、阶段结算与报工。
        /// </summary>
        /// <param name="image">由界面加载并在调用完成后释放的 HALCON 图像。</param>
        /// <param name="height">图像高度，单位为像素。</param>
        /// <param name="width">图像宽度，单位为像素。</param>
        /// <param name="toolIndex">工具列表的零基下标。</param>
        /// <param name="message">拒绝执行时返回操作员可读原因。</param>
        /// <returns>照片测试已进入检测流程时返回 true；生产忙碌或工具下标无效时返回 false。</returns>
        public bool TryRunManualToolTest(HObject image, int height, int width, int toolIndex, out string message)
        {
            message = string.Empty;
            if (image == null || toolIndex < 0 || toolIndex >= DataModel.Processmodel.Tools.Count)
            {
                message = "请先选择需要测试的工具和有效照片";
                return false;
            }

            if (!TryBeginManualImageTest())
            {
                message = "检测或扫码处理正在执行，请在本轮结束后再进行照片测试";
                return false;
            }

            try
            {
                ToolModel tool = DataModel.Processmodel.Tools[toolIndex];
                FrameProcessingContext context = FrameProcessingContext.Manual(
                    NormalizeStageCommand(tool.Command),
                    toolIndex);
                OnReceiveProcess(image, height, width, context);
                return true;
            }
            finally
            {
                EndManualImageTest();
            }
        }


        public void Save_record(string info)
        {
            Save_records(new[] { info });
        }

        /// <summary>
        /// 将同一帧的工具耗时和帧汇总一次性追加到识别过程日志，减少多工具流程中的重复文件打开与关闭。
        /// </summary>
        private void Save_records(IEnumerable<string> records)
        {
            if (DataModel?.Settingmodel?.SaveProcessData != true || records == null)
            {
                return;
            }

            List<string> recordList = records.Where(record => !string.IsNullOrWhiteSpace(record)).ToList();
            if (recordList.Count == 0)
            {
                return;
            }

            try
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
                    foreach (string record in recordList)
                    {
                        sw.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.FFF}]{record}");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteDetailLog($"[识别过程日志] 批量写入失败: {ex.Message}");
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
                        HOperatorSet.Threshold(ImageG, out RegionG, tool.MinGreen, tool.MaxBlue);
                        HOperatorSet.Threshold(ImageB, out RegionB, tool.MinBlue, tool.MaxBlue);

                        HOperatorSet.Intersection(RegionR, RegionG, out RegionIntersection);
                        HOperatorSet.Intersection(RegionB, RegionIntersection, out Region);
                    }


                    HOperatorSet.Connection(Region, out ConnectedRegions);
                    HOperatorSet.SelectShape(ConnectedRegions, out SelectedRegions, "area", "and", tool.MinAreaFilter, tool.MaxAreaFilter);
                    HOperatorSet.Union1(SelectedRegions, out RegionUnion);
                    HOperatorSet.AreaCenter(RegionUnion, out Area, out Row, out Column);

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
                catch (Exception ex) { }

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

                //tool.ActualDimension = (int)Area.L;
                return (int)Area.L;
            }
            catch (Exception e)
            {
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

                        // 3秒后清除状态提示
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
        /// 检查当前上下文是否允许PLC/视觉检测：正常生产需SN+工序校验通过；点检需已扫描匹配的点检SN并进入点检模式。
        /// </summary>
        /// <returns>true表示状态满足，可以进行PLC轮询和视觉检测</returns>
        private bool HasValidSNAndValidation()
        {
            UpdateInspectionCountdown();

            string configurationError = GetCurrentInspectionRecipeConfigurationError();
            if (!string.IsNullOrEmpty(configurationError))
            {
                LogInspectionConfigurationErrorIfChanged(configurationError);
                return false;
            }
            LogInspectionConfigurationErrorIfChanged(string.Empty);

            bool hasSN = !string.IsNullOrEmpty(DataModel.Processmodel.CurrentProductSN);
            bool isInspectionMode = DataModel.Processmodel.IsInspectionMode;
            bool isInspectionReady = IsInspectionReadyForDetection();
            bool isValidated = DataModel.Processmodel.ScanCodeStatus == "工序校验通过" || isInspectionReady;
            bool isInspectionRequired = IsInspectionRequired();
            RefreshInspectionProgressForDueCycle();
            bool result = hasSN && (isInspectionRequired ? isInspectionReady : isValidated);

            if (hasSN && isValidated && isInspectionRequired && !isInspectionReady
                && DataModel.Processmodel.ScanCodeStatus != "点检到期")
            {
                _ = App.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    DataModel.Processmodel.ScanCodeStatus = "点检到期";
                    writeLog(BuildInspectionGateMessage(DataModel.Processmodel.CurrentProductSN), true);
                    ShowInspectionWindow();
                }));
            }

            // 详细调试日志：记录状态检查结果
            WriteDetailLog($"[报工状态] SN={DataModel.Processmodel.CurrentProductSN ?? "空"}, " +
                          $"校验状态={DataModel.Processmodel.ScanCodeStatus}, " +
                          $"点检模式={isInspectionMode}, " +
                          $"点检到期={isInspectionRequired}, " +
                          $"待点检={DataModel.Processmodel.PendingInspectionType ?? "空"}, " +
                          $"有SN={hasSN}, 校验通过={isValidated}, 最终结果={result}");

            return result;
        }

        /// <summary>
        /// 获取工具执行入口的轻量拦截原因。PLC主路径继续由HasValidSNAndValidation负责SN与工序资格，该入口负责点检配置无效、空SN、报工中和点检到期保护。
        /// </summary>
        /// <returns>空字符串表示工具可以启动；其它文本用于现场日志说明拦截原因。</returns>
        private string GetDetectionBlockReason()
        {
            UpdateInspectionCountdown();

            string configurationError = GetCurrentInspectionRecipeConfigurationError();
            if (!string.IsNullOrEmpty(configurationError))
            {
                LogInspectionConfigurationErrorIfChanged(configurationError);
                return $"点检配置无效：{configurationError}";
            }

            if (DataModel.Processmodel.CanStartDetection)
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(DataModel.Processmodel.CurrentProductSN))
            {
                return "SN为空";
            }

            if (DataModel.Processmodel.IsReportingWork)
            {
                return "报工正在进行中";
            }

            if (DataModel.Processmodel.IsInspectionOverdue && !IsInspectionReadyForDetection())
            {
                return $"{GetInspectionIntervalHours()}小时点检已到期，请先完成OK点检和NG点检";
            }

            return "作业状态不满足";
        }

        #region 点检功能（可配置周期强制点检）

        private System.Windows.Threading.DispatcherTimer _inspectionTimer;
        private Window _inspectionWindow = null;
        /// <summary>
        /// 操作员手动提前点检时保持选择窗口不被周期定时器关掉。该标记不改写上次点检时间，也不开启普通生产门禁。
        /// </summary>
        private bool _manualInspectionSessionActive;

        private void UpdateInspectionStatusMessage(string message)
        {
            App.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                // 点检提示属于“扫码状态/流程提示”的一部分：避免新增额外 UI 字段，复用现有状态区展示
                DataModel.Processmodel.ScanCodeStatus = string.IsNullOrWhiteSpace(message) ? DataModel.Processmodel.ScanCodeStatus : message;
            }));
        }

        /// <summary>
        /// 读取点检入口的运行占用状态。点检会重置工具和阶段上下文，因此相机执行、报工、自动阶段循环或扫码/MES处理期间保持当前业务链路完整。
        /// </summary>
        /// <param name="includeScanProcessing">是否把当前扫码/MES链路作为占用判断；直接扫码点检SN时由当前链路完成身份确认，因此该入口传入false。</param>
        /// <returns>空字符串表示可以进入点检；非空文本表示当前阻塞原因。</returns>
        private string GetInspectionEntryBlockReason(bool includeScanProcessing)
        {
            if (_isExecuting)
            {
                return "相机检测正在执行";
            }

            if (DataModel?.Processmodel?.IsReportingWork == true)
            {
                return "产品正在报工";
            }

            bool scanAutoExecutionActive;
            lock (_scanAutoLock)
            {
                scanAutoExecutionActive = _scanAutoTriggerCts != null;
            }

            if (scanAutoExecutionActive)
            {
                return "扫码自动阶段循环正在运行";
            }

            if (includeScanProcessing && _isScanProcessing)
            {
                return "扫码/MES正在处理";
            }

            return string.Empty;
        }

        /// <summary>
        /// 记录无法进入点检的原因。该提示保留当前产品和阶段上下文，供操作员等待当前流程完成后重试。
        /// </summary>
        /// <param name="entryName">触发入口名称，例如“手动开始点检”或“点检样件扫码”。</param>
        /// <param name="reason">占用点检入口的当前业务状态。</param>
        private void ReportInspectionEntryBlocked(string entryName, string reason)
        {
            string message = $"{entryName}暂缓：{reason}，请等待当前流程完成后再试";
            UpdateInspectionStatusMessage(message);
            writeLog(message, false);
        }

        /// <summary>
        /// 在相机或扫码链路释放后回到UI线程检查点检窗口。到期弹窗等待设备链路收口，避免在当前产品检测中途重置工具状态。
        /// </summary>
        private void QueueInspectionStatusRefresh()
        {
            App.Current?.Dispatcher?.BeginInvoke(new Action(CheckInspectionStatus));
        }

        /// <summary>
        /// 汇总当前点检周期中OK和NG两类样件的完成状态，供点检窗口、扫码状态和日志共用。
        /// </summary>
        private string GetInspectionProgressSummary()
        {
            List<string> summary = new List<string>();
            summary.Add($"OK点检={(DataModel.Processmodel.OKInspectionCompleted ? "已完成" : "未完成")}");
            summary.Add($"NG点检={(DataModel.Processmodel.NGInspectionCompleted ? "已完成" : "未完成")}");
            return string.Join("，", summary);
        }

        /// <summary>
        /// 判断当前SN是否已完成点检样件身份确认并进入可执行的OK或NG点检阶段。
        /// </summary>
        /// <remarks>
        /// 该判断服务于检测启动按钮和阶段启动保护。普通产品的到期门禁仍由生产扫码流程单独校验，点检样件身份只通过MES最终产品SN确认。
        /// </remarks>
        private bool IsInspectionReadyForDetection()
        {
            return DataModel.Processmodel.IsInspectionMode
                && string.IsNullOrEmpty(DataModel.Processmodel.PendingInspectionType)
                && (DataModel.Processmodel.InspectionType == "OK"
                    || DataModel.Processmodel.InspectionType == "NG");
        }

        /// <summary>
        /// 读取运行时生效的点检周期，单位为小时。加载遗漏或运行时被改写为超出1至48范围的值时回退12小时，确保倒计时和到期门禁使用同一口径。
        /// </summary>
        private int GetInspectionIntervalHours()
        {
            int configuredHours = DataModel?.Settingmodel?.ReportWorkConfig?.InspectionIntervalHours
                ?? ReportWorkConfig.DefaultInspectionIntervalHours;
            return configuredHours >= ReportWorkConfig.MinInspectionIntervalHours
                && configuredHours <= ReportWorkConfig.MaxInspectionIntervalHours
                ? configuredHours
                : ReportWorkConfig.DefaultInspectionIntervalHours;
        }

        /// <summary>
        /// 读取NG点检的合格条件标识。有效值限定为“任一NG”和“全部NG”，供阶段结算使用统一口径。
        /// </summary>
        private string GetInspectionNgPassCondition()
        {
            string configuredCondition = (DataModel?.Settingmodel?.ReportWorkConfig?.InspectionNgPassCondition ?? string.Empty).Trim();
            return configuredCondition == ReportWorkConfig.InspectionNgConditionAll
                ? ReportWorkConfig.InspectionNgConditionAll
                : ReportWorkConfig.InspectionNgConditionAny;
        }

        /// <summary>
        /// 读取倒计时栏“开始点检”按钮生效的模式。有效值限定为“提前点检”和“强制到期”，非法配置回退提前点检以保持既有不强制门禁行为。
        /// </summary>
        private string GetManualInspectionMode()
        {
            string configuredMode = (DataModel?.Settingmodel?.ReportWorkConfig?.ManualInspectionMode ?? string.Empty).Trim();
            return configuredMode == ReportWorkConfig.ManualInspectionModeForceDue
                ? ReportWorkConfig.ManualInspectionModeForceDue
                : ReportWorkConfig.ManualInspectionModeEarly;
        }

        /// <summary>
        /// 规范点检与扫码相关XML中的人工可编辑参数。周期有效范围为1至48小时，非法周期回退12小时；未知NG条件回退“任一NG”；未知手动点检模式回退“提前点检”；非法SN解码方式回退DecodeSN，并保留日志供现场核对实际生效值。
        /// </summary>
        private void NormalizeInspectionConfiguration()
        {
            ReportWorkConfig cfg = DataModel?.Settingmodel?.ReportWorkConfig;
            if (cfg == null)
            {
                return;
            }

            if (cfg.InspectionIntervalHours < ReportWorkConfig.MinInspectionIntervalHours
                || cfg.InspectionIntervalHours > ReportWorkConfig.MaxInspectionIntervalHours)
            {
                writeLog($"点检周期配置无效：{cfg.InspectionIntervalHours}，有效范围为{ReportWorkConfig.MinInspectionIntervalHours}-{ReportWorkConfig.MaxInspectionIntervalHours}小时，已按{ReportWorkConfig.DefaultInspectionIntervalHours}小时处理", false);
                cfg.InspectionIntervalHours = ReportWorkConfig.DefaultInspectionIntervalHours;
            }

            string configuredCondition = (cfg.InspectionNgPassCondition ?? string.Empty).Trim();
            if (configuredCondition != ReportWorkConfig.InspectionNgConditionAny
                && configuredCondition != ReportWorkConfig.InspectionNgConditionAll)
            {
                writeLog($"NG点检合格条件配置无效：{configuredCondition}，已按{ReportWorkConfig.InspectionNgConditionAny}处理", false);
                cfg.InspectionNgPassCondition = ReportWorkConfig.InspectionNgConditionAny;
            }
            else
            {
                cfg.InspectionNgPassCondition = configuredCondition;
            }

            string configuredManualMode = (cfg.ManualInspectionMode ?? string.Empty).Trim();
            if (configuredManualMode != ReportWorkConfig.ManualInspectionModeEarly
                && configuredManualMode != ReportWorkConfig.ManualInspectionModeForceDue)
            {
                writeLog($"手动点检模式配置无效：{configuredManualMode}，仅支持“{ReportWorkConfig.ManualInspectionModeEarly}”或“{ReportWorkConfig.ManualInspectionModeForceDue}”，已按{ReportWorkConfig.ManualInspectionModeEarly}处理", false);
                cfg.ManualInspectionMode = ReportWorkConfig.ManualInspectionModeEarly;
            }
            else
            {
                cfg.ManualInspectionMode = configuredManualMode;
            }

            cfg.OKInspectionSN = (cfg.OKInspectionSN ?? string.Empty).Trim();
            cfg.NGInspectionSN = (cfg.NGInspectionSN ?? string.Empty).Trim();

            string configuredSnDecodeMode = (cfg.SnDecodeMode ?? string.Empty).Trim();
            string normalizedSnDecodeMode = MESService.NormalizeSnDecodeMode(configuredSnDecodeMode);
            if (!string.Equals(configuredSnDecodeMode, normalizedSnDecodeMode, StringComparison.Ordinal)
                && !(string.IsNullOrEmpty(configuredSnDecodeMode) && normalizedSnDecodeMode == ReportWorkConfig.SnDecodeModeDecodeSN))
            {
                writeLog($"SN解码方式配置无效：{configuredSnDecodeMode}，仅支持“{ReportWorkConfig.SnDecodeModeDecodeSN}”或“{ReportWorkConfig.SnDecodeModeLastBinding}”，已按{normalizedSnDecodeMode}处理", false);
            }
            cfg.SnDecodeMode = normalizedSnDecodeMode;
        }

        /// <summary>
        /// 清理明显晚于设备当前时间的点检完成时间，防止人工修改XML或设备时钟异常延长点检有效期。
        /// </summary>
        private void SanitizeInspectionTimestamp()
        {
            InspectionRecipeConfig cfg = CurrentInspectionRecipeConfig;
            if (cfg == null || cfg.LastInspectionTime == DateTime.MinValue)
            {
                return;
            }

            DateTime latestAcceptedTime = DateTime.Now.AddMinutes(5);
            if (cfg.LastInspectionTime > latestAcceptedTime)
            {
                writeLog($"上次点检时间晚于设备当前时间，已按点检到期处理：配置={cfg.LastInspectionTime:yyyy-MM-dd HH:mm:ss}", true);
                SaveCurrentRecipeInspectionTime(DateTime.MinValue);
            }
        }

        /// <summary>
        /// 判断当前点检周期是否到期。没有有效完成时间时立即到期，完成时间达到配置周期后继续拦截普通产品。
        /// </summary>
        private bool IsInspectionRequired()
        {
            InspectionRecipeConfig cfg = CurrentInspectionRecipeConfig;
            if (cfg == null || cfg.LastInspectionTime == DateTime.MinValue)
            {
                return true;
            }

            return DateTime.Now - cfg.LastInspectionTime >= TimeSpan.FromHours(GetInspectionIntervalHours());
        }

        /// <summary>
        /// 在新点检周期到期时清除上一周期的OK和NG完成标记，保证两个样件都重新执行。
        /// </summary>
        private void RefreshInspectionProgressForDueCycle()
        {
            if (!IsInspectionRequired()
                || !DataModel.Processmodel.OKInspectionCompleted
                || !DataModel.Processmodel.NGInspectionCompleted)
            {
                return;
            }

            DataModel.Processmodel.OKInspectionCompleted = false;
            DataModel.Processmodel.NGInspectionCompleted = false;
        }

        /// <summary>
        /// 生成普通产品被点检门禁拦截时的现场提示，保留周期和产品SN用于操作员判断与日志追溯。
        /// </summary>
        private string BuildInspectionGateMessage(string sn)
        {
            return $"[点检到期拦截] {GetInspectionIntervalHours()}小时点检已到期，SN={sn}；请先完成OK点检和NG点检。";
        }

        /// <summary>
        /// 校验普通生产SN是否满足点检周期门禁。该入口只影响普通产品流程，点检样件扫码走独立的MES身份确认路径。
        /// </summary>
        private string ValidateInspectionGateForProduction(string sn)
        {
            string configurationError = GetInspectionConfigurationError();
            if (!string.IsNullOrEmpty(configurationError))
            {
                return $"[点检配方无效] 工程={DataModel.Settingmodel.Name}，{configurationError}；普通产品已拦截。";
            }

            if (!IsInspectionRequired())
            {
                return string.Empty;
            }

            RefreshInspectionProgressForDueCycle();
            return BuildInspectionGateMessage(sn);
        }

        /// <summary>
        /// 在点检等待扫码或点检检测期间保留当前点检上下文，阻止普通SN进入工序校验和阶段执行。
        /// </summary>
        /// <returns>当前存在点检占用时返回true；普通生产可继续时返回false。</returns>
        private bool RejectNormalScanDuringInspection()
        {
            if (!DataModel.Processmodel.IsInspectionActive)
            {
                return false;
            }

            string inspectionState = DataModel.Processmodel.IsInspectionMode
                ? $"{DataModel.Processmodel.InspectionType}点检检测中"
                : $"等待{DataModel.Processmodel.PendingInspectionType}点检样件扫码";
            UpdateInspectionStatusMessage($"{inspectionState}，当前普通产品SN暂不进入工序校验，请先完成点检或切换点检SN。");
            writeLog($"普通产品扫码已拦截：{inspectionState}，保留当前点检上下文", false);
            StopScanAutoExecution();
            return true;
        }

        /// <summary>
        /// 检查点检样件最终产品SN配置是否具备进入点检流程的条件。
        /// </summary>
        private string GetInspectionConfigurationError()
        {
            return GetCurrentInspectionRecipeConfigurationError();
        }

        /// <summary>
        /// 初始化点检周期、时间清理、倒计时和到期窗口，并启动一分钟一次的状态刷新定时器。
        /// </summary>
        /// <remarks>
        /// 初始化只建立点检门禁和提示，不替代普通产品的工序校验，也不自动完成任何点检样件判定。
        /// </remarks>
        public void InitializeInspection()
        {
            NormalizeInspectionConfiguration();
            SanitizeInspectionTimestamp();
            SaveSettingModel();
            UpdateInspectionCountdown();
            CheckInspectionStatus();

            _inspectionTimer = new System.Windows.Threading.DispatcherTimer();
            _inspectionTimer.Interval = TimeSpan.FromMinutes(1);
            _inspectionTimer.Tick += (s, e) =>
            {
                UpdateInspectionCountdown();
                CheckInspectionStatus();
            };
            _inspectionTimer.Start();
            writeLog($"点检功能已启动：周期={GetInspectionIntervalHours()}小时，NG合格条件={GetInspectionNgPassCondition()}，手动点检模式={GetManualInspectionMode()}", false);
        }

        /// <summary>
        /// 根据点检周期和两类样件完成状态维护点检窗口的显示状态。
        /// </summary>
        /// <remarks>
        /// 手动提前点检会话期间即使周期未到期，也不自动关闭选择窗口；到期门禁仍只由 IsInspectionRequired 控制。
        /// </remarks>
        private void CheckInspectionStatus()
        {
            string configurationError = GetInspectionConfigurationError();
            if (!string.IsNullOrEmpty(configurationError))
            {
                UpdateInspectionStatusMessage($"点检配置无效：{configurationError}");
                _manualInspectionSessionActive = false;
                if (_inspectionWindow != null)
                {
                    App.Current?.Dispatcher?.BeginInvoke(new Action(CloseInspectionWindow));
                }
                return;
            }

            bool isOverdue = IsInspectionRequired();
            RefreshInspectionProgressForDueCycle();
            bool needInspection = isOverdue &&
                                  (!DataModel.Processmodel.OKInspectionCompleted ||
                                   !DataModel.Processmodel.NGInspectionCompleted);

            if (needInspection
                && _inspectionWindow == null
                && string.IsNullOrEmpty(GetInspectionEntryBlockReason(includeScanProcessing: true)))
            {
                App.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    if (string.IsNullOrEmpty(GetInspectionEntryBlockReason(includeScanProcessing: true)))
                    {
                        ShowInspectionWindow(false);
                    }
                }));
            }
            else if (!needInspection && _inspectionWindow != null && !_manualInspectionSessionActive)
            {
                App.Current?.Dispatcher?.BeginInvoke(new Action(CloseInspectionWindow));
            }
        }

        /// <summary>
        /// 由操作员从倒计时栏手动打开点检选择窗口。具体行为由XML“手动点检模式”决定：提前点检不改写周期时间、不开启门禁；强制到期会清空上次点检时间并拦截普通生产。倒计时仍在OK和NG都通过后由 CompleteInspection 重置。
        /// </summary>
        public void RequestManualInspection()
        {
            string configurationError = GetInspectionConfigurationError();
            if (!string.IsNullOrEmpty(configurationError))
            {
                UpdateInspectionStatusMessage($"点检配置无效：{configurationError}");
                writeLog($"无法手动开始点检：{configurationError}", true);
                MessageBoxX.Show($"点检配置无效：{configurationError}", "开始点检", MessageBoxButton.OK, MessageBoxIcon.Error);
                return;
            }

            string entryBlockReason = GetInspectionEntryBlockReason(includeScanProcessing: true);
            if (!string.IsNullOrEmpty(entryBlockReason))
            {
                ReportInspectionEntryBlocked("手动开始点检", entryBlockReason);
                return;
            }

            if (DataModel.Processmodel.IsInspectionActive)
            {
                string inspectionState = DataModel.Processmodel.IsInspectionMode
                    ? $"{DataModel.Processmodel.InspectionType}点检检测中"
                    : $"等待{DataModel.Processmodel.PendingInspectionType}点检样件扫码";
                UpdateInspectionStatusMessage($"{inspectionState}，请先完成当前点检后再重新开始。");
                writeLog($"手动开始点检已忽略：{inspectionState}", false);
                ActivateInspectionWindowIfOpen();
                return;
            }

            if (_inspectionWindow != null && _inspectionWindow.IsLoaded)
            {
                ActivateInspectionWindowIfOpen();
                return;
            }

            bool isOverdue = IsInspectionRequired();
            bool forceDueMode = GetManualInspectionMode() == ReportWorkConfig.ManualInspectionModeForceDue;
            string confirmMessage;
            if (isOverdue)
            {
                confirmMessage = $"点检已到期，请完成OK点检和NG点检。\n当前进度：{GetInspectionProgressSummary()}\n是否打开点检窗口？";
            }
            else if (forceDueMode)
            {
                confirmMessage =
                    $"当前配置为“{ReportWorkConfig.ManualInspectionModeForceDue}”（剩余 {DataModel.Processmodel.TimeToNextInspectionText}）。\n" +
                    "确认后将立即视为点检到期并拦截普通生产，需完成OK和NG点检后才能继续。\n是否继续？";
            }
            else
            {
                confirmMessage =
                    $"当前点检尚未到期（剩余 {DataModel.Processmodel.TimeToNextInspectionText}）。\n" +
                    "提前点检不会拦截普通生产；完成OK和NG点检后将重置倒计时。\n是否继续？";
            }

            if (MessageBoxX.Show(confirmMessage, "开始点检", MessageBoxButton.YesNo, MessageBoxIcon.Question, DefaultButton.NoCancel)
                != MessageBoxResult.Yes)
            {
                return;
            }

            entryBlockReason = GetInspectionEntryBlockReason(includeScanProcessing: true);
            if (!string.IsNullOrEmpty(entryBlockReason))
            {
                ReportInspectionEntryBlocked("手动开始点检", entryBlockReason);
                return;
            }

            if (!isOverdue && forceDueMode)
            {
                ForceInspectionDueForManualStart();
                ShowInspectionWindow(isManualEarly: false);
                return;
            }

            if (!isOverdue)
            {
                _manualInspectionSessionActive = true;
            }

            ShowInspectionWindow(isManualEarly: !isOverdue);
        }

        /// <summary>
        /// 将手动点检按强制到期生效：清空上次点检时间并重置本轮OK/NG完成标记，写回主配置后刷新倒计时，使普通生产门禁立即生效。
        /// </summary>
        private void ForceInspectionDueForManualStart()
        {
            if (!SaveCurrentRecipeInspectionTime(DateTime.MinValue))
            {
                UpdateInspectionStatusMessage("当前配方点检时间保存失败，强制到期未生效");
                writeLog($"手动强制到期失败：工程={DataModel.Settingmodel.Name}点检配置未落盘", true);
                return;
            }

            _manualInspectionSessionActive = false;
            DataModel.Processmodel.OKInspectionCompleted = false;
            DataModel.Processmodel.NGInspectionCompleted = false;
            UpdateInspectionCountdown();
            UpdateInspectionStatusMessage($"手动强制到期已生效：{GetInspectionProgressSummary()}；普通生产已拦截，请完成OK和NG点检");
            writeLog("手动点检已按强制到期模式生效：上次点检时间已清空，普通生产门禁已开启", false);
        }

        /// <summary>
        /// 若点检选择窗口已打开则将其置于前台，便于操作员继续选择OK或NG。
        /// </summary>
        private void ActivateInspectionWindowIfOpen()
        {
            if (_inspectionWindow == null || !_inspectionWindow.IsLoaded)
            {
                return;
            }

            try
            {
                _inspectionWindow.Activate();
            }
            catch (Exception ex)
            {
                WriteDetailLog($"[点检窗口] 激活窗口失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 更新距离下一次强制点检的剩余时间，单位为系统本地时间的时间间隔。
        /// </summary>
        private void UpdateInspectionCountdown()
        {
            if (!string.IsNullOrEmpty(GetInspectionConfigurationError()))
            {
                DataModel.Processmodel.TimeToNextInspection = TimeSpan.Zero;
                return;
            }

            var cfg = CurrentInspectionRecipeConfig;
            if (cfg == null) return;

            if (cfg.LastInspectionTime == DateTime.MinValue)
            {
                DataModel.Processmodel.TimeToNextInspection = TimeSpan.Zero;
                return;
            }

            var elapsed = DateTime.Now - cfg.LastInspectionTime;
            var remaining = TimeSpan.FromHours(GetInspectionIntervalHours()) - elapsed;
            DataModel.Processmodel.TimeToNextInspection = remaining;
        }

        /// <summary>
        /// 显示点检选择窗口。窗口只负责选择OK或NG类型，窗口关闭后仍可通过配置的点检SN直接进入对应模式。
        /// </summary>
        /// <param name="isManualEarly">为true表示操作员在未到期时手动提前点检；提示文案和会话保持与到期强制弹窗区分，且不改写周期时间。</param>
        private void ShowInspectionWindow(bool isManualEarly = false)
        {
            if (_inspectionWindow != null && _inspectionWindow.IsLoaded)
            {
                ActivateInspectionWindowIfOpen();
                return;
            }

            try
            {
                Window inspectionWindow = new InspectionWindow();
                inspectionWindow.Closed += (sender, args) =>
                {
                    if (ReferenceEquals(_inspectionWindow, inspectionWindow))
                    {
                        _inspectionWindow = null;
                    }

                    _manualInspectionSessionActive = false;
                };
                _inspectionWindow = inspectionWindow;
                inspectionWindow.Show();
                if (isManualEarly)
                {
                    UpdateInspectionStatusMessage($"手动提前点检：{GetInspectionProgressSummary()}；完成OK和NG后重置倒计时，期间不拦截普通生产");
                    writeLog("操作员手动启动提前点检，已打开点检选择窗口", false);
                }
                else
                {
                    UpdateInspectionStatusMessage($"点检已到期：{GetInspectionProgressSummary()}");
                    writeLog("⚠️ 点检已到期，请完成OK点检和NG点检", false);
                }
            }
            catch (Exception ex)
            {
                _manualInspectionSessionActive = false;
                writeLog($"显示点检弹窗失败: {ex.Message}", false);
            }
        }

        /// <summary>
        /// 关闭点检选择窗口并清理窗口引用，不改变已通过MES确认的点检模式和样件状态。
        /// </summary>
        public void CloseInspectionWindow()
        {
            if (App.Current?.Dispatcher != null && !App.Current.Dispatcher.CheckAccess())
            {
                _ = App.Current.Dispatcher.BeginInvoke(new Action(CloseInspectionWindow));
                return;
            }

            Window inspectionWindow = _inspectionWindow;
            _inspectionWindow = null;
            _manualInspectionSessionActive = false;
            if (inspectionWindow == null)
            {
                return;
            }

            try { inspectionWindow.Close(); } catch { }
        }

        /// <summary>
        /// 按忽略大小写并去除首尾空白的规则比较点检最终产品SN，适配扫码文本和XML配置的常见格式差异。
        /// </summary>
        private static bool InspectionSNEquals(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 使用MES实时确认最终产品SN后进入点检模式。该入口只确认点检样件身份，产品编码、工单和工序状态均不参与放行。
        /// </summary>
        private async Task TryBeginInspectionFromMesAsync(
            string type,
            string resolvedSN,
            string expectedSN,
            string rawScan)
        {
            string entryBlockReason = GetInspectionEntryBlockReason(includeScanProcessing: false);
            if (!string.IsNullOrEmpty(entryBlockReason))
            {
                RejectInspectionScan(
                    $"当前无法切换点检模式：{entryBlockReason}",
                    type,
                    rawScan,
                    resolvedSN,
                    clearCurrentProductContext: false);
                return;
            }

            string configurationError = GetInspectionConfigurationError();
            if (!string.IsNullOrEmpty(configurationError))
            {
                RejectInspectionScan($"点检配置无效：{configurationError}", type, rawScan, resolvedSN);
                return;
            }

            MESService.InspectionSnValidationResult validationResult =
                await _mesService.ValidateInspectionSNAsync(resolvedSN, expectedSN);
            if (validationResult.Success)
            {
                BeginInspectionMode(type, validationResult.SN);
                return;
            }

            RejectInspectionScan(validationResult.ErrorMessage, type, rawScan, validationResult.SN ?? resolvedSN);
        }

        /// <summary>
        /// 结束一次未通过的点检扫码尝试。空闲状态清除点检扫码上下文；检测或报工占用时保留普通产品上下文，并留下可定位MES、配置或扫码问题的状态和日志。
        /// </summary>
        /// <param name="clearCurrentProductContext">是否清除当前SN和点检模式。普通空闲状态下的无效点检扫码使用true；检测或报工占用时使用false，保留正在处理的普通产品上下文。</param>
        private void RejectInspectionScan(string reason, string type, string rawScan, string resolvedSN, bool clearCurrentProductContext = true)
        {
            string inspectionTypeText = string.IsNullOrWhiteSpace(type) ? "" : $"{type}点检";
            _ = App.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (clearCurrentProductContext)
                {
                    DataModel.Processmodel.CurrentProductSN = string.Empty;
                    DataModel.Processmodel.IsInspectionMode = false;
                    DataModel.Processmodel.InspectionType = null;
                }
                DataModel.Processmodel.ScanCodeStatus = $"{inspectionTypeText}扫码未通过：{reason}";
                writeLog($"点检扫码未通过: type={type ?? "未识别"}, raw={rawScan}, resolvedSN={resolvedSN}, reason={reason}", false);
            }));
        }

        /// <summary>
        /// 将MES确认成功的固定点检最终产品SN绑定到当前点检模式，并重置检测工具运行态。
        /// </summary>
        /// <remarks>
        /// 该入口可由选择窗口后的扫码触发，也可由窗口关闭后直接扫码匹配SN触发；点检样件不进入普通工序校验和报工链路。
        /// 开启扫码自动执行时，在点检状态和SN绑定完成后启动阶段循环；循环等待当前扫码/MES互斥释放后再发送首个相机软触发。关闭开关时继续等待PLC触发。
        /// </remarks>
        private void BeginInspectionMode(string type, string inspectionSN)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(inspectionSN))
                return;

            _lastProductSN = inspectionSN;
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                ResetDetectionCycle();
                DataModel.Processmodel.PendingInspectionType = null;
                DataModel.Processmodel.IsInspectionMode = true;
                DataModel.Processmodel.InspectionType = type;
                DataModel.Processmodel.CurrentProductSN = inspectionSN;
                DataModel.Processmodel.ScanCodeStatus = $"点检模式({type})";
                writeLog($"进入点检模式: type={type}, SN={inspectionSN}", false);

                if (DataModel.Settingmodel.EnableScanAutoTriggerStages)
                {
                    writeLog($"点检扫码自动执行: MES确认通过，准备启动阶段循环 type={type}, SN={inspectionSN}", false);
                    WriteDetailLog($"[点检扫码自动] 状态绑定完成，调用 StartScanAutoExecution type={type} SN={inspectionSN}");
                    StartScanAutoExecution(inspectionSN);
                }
                else
                {
                    writeLog($"点检扫码确认通过: 自动执行开关关闭，等待PLC触发 type={type}, SN={inspectionSN}", false);
                    WriteDetailLog($"[点检扫码自动] EnableScanAutoTriggerStages=false，保留PLC触发流程 type={type} SN={inspectionSN}");
                }
            }));
        }

        /// <summary>
        /// 选择OK点检并进入等待扫码状态。完成标记清除后，操作员需扫描配置的OK点检样件SN。
        /// </summary>
        public void StartOKInspection()
        {
            string configurationError = GetInspectionConfigurationError();
            if (!string.IsNullOrEmpty(configurationError))
            {
                UpdateInspectionStatusMessage($"点检配置无效：{configurationError}");
                writeLog($"无法启动OK点检：{configurationError}", true);
                return;
            }

            string entryBlockReason = GetInspectionEntryBlockReason(includeScanProcessing: true);
            if (!string.IsNullOrEmpty(entryBlockReason))
            {
                ReportInspectionEntryBlocked("OK点检", entryBlockReason);
                return;
            }

            StopScanAutoExecution();
            ResetDetectionCycle();
            ResetProductContext();

            DataModel.Processmodel.PendingInspectionType = "OK";
            DataModel.Processmodel.IsInspectionMode = false;
            DataModel.Processmodel.InspectionType = "OK";
            DataModel.Processmodel.OKInspectionCompleted = false;
            DataModel.Processmodel.CurrentProductSN = string.Empty;
            DataModel.Processmodel.ScanCodeStatus = "等待OK点检扫码";

            UpdateInspectionStatusMessage($"已选择OK点检，请扫描OK点检样件码。当前进度：{GetInspectionProgressSummary()}");
            writeLog("已选择OK点检，请扫描OK点检样件码", false);
        }

        /// <summary>
        /// 选择NG点检并进入等待扫码状态。完成标记清除后，操作员需扫描配置的NG点检样件SN。
        /// </summary>
        public void StartNGInspection()
        {
            string configurationError = GetInspectionConfigurationError();
            if (!string.IsNullOrEmpty(configurationError))
            {
                UpdateInspectionStatusMessage($"点检配置无效：{configurationError}");
                writeLog($"无法启动NG点检：{configurationError}", true);
                return;
            }

            string entryBlockReason = GetInspectionEntryBlockReason(includeScanProcessing: true);
            if (!string.IsNullOrEmpty(entryBlockReason))
            {
                ReportInspectionEntryBlocked("NG点检", entryBlockReason);
                return;
            }

            StopScanAutoExecution();
            ResetDetectionCycle();
            ResetProductContext();

            DataModel.Processmodel.PendingInspectionType = "NG";
            DataModel.Processmodel.IsInspectionMode = false;
            DataModel.Processmodel.InspectionType = "NG";
            DataModel.Processmodel.NGInspectionCompleted = false;
            DataModel.Processmodel.CurrentProductSN = string.Empty;
            DataModel.Processmodel.ScanCodeStatus = "等待NG点检扫码";

            UpdateInspectionStatusMessage($"已选择NG点检，请扫描NG点检样件码。当前进度：{GetInspectionProgressSummary()}");
            writeLog("已选择NG点检，请扫描NG点检样件码", false);
        }

        /// <summary>
        /// 汇总当前点检阶段并推进多阶段点检样件。阶段通过只推进样件内部阶段索引，最后一个配置阶段完成后才按全部判定框结算OK或NG。
        /// </summary>
        /// <param name="currentCommand">本次PLC或扫码自动触发并完成结算的阶段指令，例如A1或A2。</param>
        /// <param name="currentCommandTools">当前阶段参与判定的工具集合；该集合用于当前阶段状态和诊断日志。</param>
        /// <param name="stageStatus">当前阶段状态，0表示全OK，1表示存在普通NG，2表示存在NG2。</param>
        /// <remarks>
        /// 点检模式使用独立重试规则：OK点检或NG-全部NG点检的当前阶段未满足阶段条件时停留当前阶段；NG-任一NG点检只要当前阶段工具全部完成判定即可进入下一阶段，整件样件结果在最后阶段统一汇总。
        /// XML中的StageFailRetryStrategy只影响普通生产，点检失败不会按该配置跳转到其它阶段。
        /// </remarks>
        private void HandleInspectionStageResult(string currentCommand, List<ToolModel> currentCommandTools, int stageStatus)
        {
            string type = DataModel.Processmodel.InspectionType;
            string toolSummary = string.Join(", ", currentCommandTools.Select(t => $"{t.Name}:{t.ToolStatus}"));
            if (type != "OK" && type != "NG")
            {
                DataModel.Processmodel.Status = ToolStatus.NG;
                DataModel.Processmodel.NeedRestart = true;
                UpdateInspectionStatusMessage($"点检类型无效，当前阶段{currentCommand}未结算，请重新选择OK或NG点检。");
                writeLog($"点检阶段结算失败：InspectionType={type ?? "空"}，阶段={currentCommand}，工具明细={toolSummary}", true);
                return;
            }

            DataModel.Processmodel.NeedRestart = false;
            if (stageStatus == 2)
            {
                DataModel.Processmodel.Status = ToolStatus.NG2;
                DataModel.Processmodel.NeedRestart = true;
                UpdateInspectionStatusMessage($"{type}点检阶段{currentCommand}存在NG2，请调参后重复触发当前阶段。工具明细：{toolSummary}");
                writeLog($"{type}点检阶段失败：阶段={currentCommand}存在NG2，保留当前阶段等待重试，工具明细={toolSummary}", false);
                return;
            }

            if (type == "OK" && stageStatus != 0)
            {
                DataModel.Processmodel.Status = ToolStatus.NG;
                DataModel.Processmodel.NeedRestart = true;
                UpdateInspectionStatusMessage($"OK点检阶段{currentCommand}未通过，请调参后重复触发当前阶段。工具明细：{toolSummary}");
                writeLog($"OK点检阶段失败：阶段={currentCommand}存在NG，保留当前阶段等待重试，工具明细={toolSummary}", false);
                return;
            }

            if (type == "NG"
                && GetInspectionNgPassCondition() == ReportWorkConfig.InspectionNgConditionAll
                && !currentCommandTools.All(t => t.ToolStatus == ToolStatus.NG))
            {
                DataModel.Processmodel.Status = ToolStatus.NG;
                DataModel.Processmodel.NeedRestart = true;
                UpdateInspectionStatusMessage($"NG点检阶段{currentCommand}未满足全部NG条件，请调参后重复触发当前阶段。工具明细：{toolSummary}");
                writeLog($"NG点检阶段失败：阶段={currentCommand}未达到全部NG条件，保留当前阶段等待重试，工具明细={toolSummary}", false);
                return;
            }

            if (!TryAdvanceInspectionStage(currentCommand, out bool isFinalStage))
            {
                DataModel.Processmodel.Status = ToolStatus.NG;
                DataModel.Processmodel.NeedRestart = true;
                UpdateInspectionStatusMessage($"点检阶段{currentCommand}不在当前阶段配置中，无法继续点检。");
                writeLog($"点检阶段结算失败：阶段={currentCommand}未在ConfiguredStages中找到，工具明细={toolSummary}", true);
                return;
            }

            if (!isFinalStage)
            {
                DataModel.Processmodel.Status = stageStatus == 0 ? ToolStatus.OK : ToolStatus.NG;
                string nextStage = DataModel.Processmodel.ConfiguredStages[DataModel.Processmodel.CurrentStageIndex];
                bool autoExecutionEnabled = DataModel.Settingmodel.EnableScanAutoTriggerStages;
                string nextAction = autoExecutionEnabled
                    ? $"即将自动执行下一阶段{nextStage}"
                    : $"请触发下一阶段{nextStage}";
                UpdateInspectionStatusMessage($"{type}点检阶段{currentCommand}完成，{nextAction}。工具明细：{toolSummary}");
                writeLog($"{type}点检阶段完成：{currentCommand}，下一阶段={nextStage}，自动执行={autoExecutionEnabled}，工具明细={toolSummary}", false);
                return;
            }

            List<ToolModel> inspectionTools = GetInspectionToolsForConfiguredStages();
            string allToolSummary = string.Join(", ", inspectionTools.Select(t => $"{t.Name}:{t.ToolStatus}"));
            bool allToolsSettled = inspectionTools.Count > 0
                && inspectionTools.All(t => t.ToolStatus != ToolStatus.等待中 && t.ToolStatus != ToolStatus.识别中);
            bool hasNG2 = inspectionTools.Any(t => t.ToolStatus == ToolStatus.NG2);
            bool allOK = allToolsSettled && inspectionTools.All(t => t.ToolStatus == ToolStatus.OK);
            bool anyNG = allToolsSettled && inspectionTools.Any(t => t.ToolStatus == ToolStatus.NG);
            bool allNG = allToolsSettled && inspectionTools.All(t => t.ToolStatus == ToolStatus.NG);
            bool passed = type == "OK"
                ? allOK && !hasNG2
                : !hasNG2
                    && (GetInspectionNgPassCondition() == ReportWorkConfig.InspectionNgConditionAll ? allNG : anyNG);

            if (passed)
            {
                DataModel.Processmodel.Status = type == "OK" ? ToolStatus.OK : ToolStatus.NG;
                string successConditionText = type == "NG" ? $"，NG规则={GetInspectionNgPassCondition()}" : string.Empty;
                writeLog($"{type}点检样件通过{successConditionText}，全部阶段工具明细={allToolSummary}", false);
                CompleteInspection(type);
                return;
            }

            string failureReason = !allToolsSettled
                ? "存在工具未完成判定"
                : hasNG2
                    ? "存在NG2"
                    : type == "OK"
                        ? "全部阶段工具未全部OK"
                        : $"未满足NG规则{GetInspectionNgPassCondition()}";
            ResetInspectionAttemptForRetry(type, failureReason, allToolSummary);
        }

        /// <summary>
        /// 将已完成的点检阶段写入阶段状态，并把下一次PLC或扫码自动触发位置移动到下一个配置阶段。
        /// </summary>
        /// <param name="currentCommand">本次完成的阶段指令。</param>
        /// <param name="isFinalStage">输出值；当前阶段是ConfiguredStages最后一项时为true。</param>
        /// <returns>阶段指令存在于当前配置中并完成推进时返回true。</returns>
        private bool TryAdvanceInspectionStage(string currentCommand, out bool isFinalStage)
        {
            isFinalStage = false;
            List<string> stages = DataModel.Processmodel.ConfiguredStages;
            if (stages == null || stages.Count == 0)
            {
                return false;
            }

            string normalizedCommand = NormalizeStageCommand(currentCommand);
            int stageIndex = stages.FindIndex(s => NormalizeStageCommand(s) == normalizedCommand);
            if (stageIndex < 0)
            {
                return false;
            }

            if (DataModel.Processmodel.CurrentStageIndex < 0
                || DataModel.Processmodel.CurrentStageIndex >= stages.Count)
            {
                writeLog($"点检阶段推进失败：当前阶段索引越界，Index={DataModel.Processmodel.CurrentStageIndex}，阶段数={stages.Count}", true);
                return false;
            }

            string expectedCommand = NormalizeStageCommand(stages[DataModel.Processmodel.CurrentStageIndex]);
            if (!string.Equals(expectedCommand, normalizedCommand, StringComparison.Ordinal))
            {
                writeLog($"点检阶段推进拒绝：收到{normalizedCommand}，当前应执行{expectedCommand}，Index={DataModel.Processmodel.CurrentStageIndex}", true);
                UpdateInspectionStatusMessage($"点检阶段顺序异常：当前应执行{expectedCommand}，收到{normalizedCommand}，请重新触发当前阶段");
                return false;
            }

            DataModel.Processmodel.CompletedStages.Add(normalizedCommand);
            if (DataModel.Processmodel.CurrentStageIndex <= stageIndex)
            {
                DataModel.Processmodel.CurrentStageIndex = stageIndex + 1;
            }

            isFinalStage = DataModel.Processmodel.CurrentStageIndex >= stages.Count;
            return true;
        }

        /// <summary>
        /// 获取当前点检样件在全部配置阶段中的工具集合，供最后一个阶段完成后的整件样件判定使用。
        /// </summary>
        /// <returns>属于ConfiguredStages的工具集合；配置包含ALL时返回全部已配置工具。</returns>
        private List<ToolModel> GetInspectionToolsForConfiguredStages()
        {
            List<string> stages = DataModel.Processmodel.ConfiguredStages ?? new List<string>();
            HashSet<string> normalizedStages = new HashSet<string>(stages.Select(NormalizeStageCommand));
            if (normalizedStages.Contains("ALL"))
            {
                return DataModel.Processmodel.Tools.ToList();
            }

            return DataModel.Processmodel.Tools
                .Where(t => normalizedStages.Contains(NormalizeStageCommand(t.Command)))
                .ToList();
        }

        /// <summary>
        /// 清理本次点检样件的阶段结果并回到第一个配置阶段，保留已确认的点检SN、点检类型和周期进度。
        /// </summary>
        /// <param name="type">当前点检类型，取值为OK或NG。</param>
        /// <param name="failureReason">整件样件未通过的业务原因。</param>
        /// <param name="toolSummary">全部参与工具的结果摘要，用于现场日志定位调参范围。</param>
        private void ResetInspectionAttemptForRetry(string type, string failureReason, string toolSummary)
        {
            ResetDetectionCycle();
            DataModel.Processmodel.NeedRestart = true;
            string firstStage = DataModel.Processmodel.ConfiguredStages != null
                && DataModel.Processmodel.ConfiguredStages.Count > 0
                ? DataModel.Processmodel.ConfiguredStages[0]
                : "首阶段";
            UpdateInspectionStatusMessage($"{type}点检样件未通过：{failureReason}。请调参后从{firstStage}重新触发，当前点检SN继续有效。");
            writeLog($"{type}点检样件未通过：{failureReason}，已清理阶段结果并回到{firstStage}，工具明细={toolSummary}", false);
        }

        /// <summary>
        /// 结算当前点检类型并在OK、NG两类样件均完成后更新周期时间和普通生产门禁。
        /// </summary>
        /// <remarks>
        /// 点检类型的完成标记只在全部配置阶段汇总通过后更新；单个阶段完成只推进点检样件内部阶段，不更新周期时间。
        /// </remarks>
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

            if (DataModel.Processmodel.OKInspectionCompleted && DataModel.Processmodel.NGInspectionCompleted)
            {
                if (!SaveCurrentRecipeInspectionTime(DateTime.Now))
                {
                    UpdateInspectionStatusMessage("点检结果已完成，当前配方完成时间保存失败；普通生产继续保持拦截");
                    writeLog($"[点检配方] 工程={DataModel.Settingmodel.Name}完成时间保存失败，点检周期未重置", true);
                    return;
                }

                DataModel.Processmodel.IsInspectionMode = false;
                DataModel.Processmodel.InspectionType = null;

                UpdateInspectionCountdown();
                CloseInspectionWindow();
                writeLog("✅ 点检全部完成，计时已重置", false);

                // 点检结束后清理“点检SN”上下文，避免影响正常生产
                ClearCurrentProductContext();
                UpdateInspectionStatusMessage("✅ 点检全部完成，计时已重置");
            }
            else
            {
                DataModel.Processmodel.PendingInspectionType = null;
                DataModel.Processmodel.IsInspectionMode = false;
                DataModel.Processmodel.InspectionType = null;
                DataModel.Processmodel.CurrentProductSN = string.Empty;
                ResetStageProgress();
            }
        }

        #endregion

        /// <summary>
        /// 调用 MES 保存设备/报工记录，并更新界面报工状态与成功列表。
        /// </summary>
        /// <param name="qualityResult">检测结果文案（如「合格」），传入 DLL 由 MES 侧映射 OK/NG 及不良参数。</param>
        /// <remarks>
        /// 业务意图：
        /// - 成功：写入成功列表、<see cref="Processmodel.ReportWorkStatus"/> 显示成功；提示保留至下一次扫码（由 <see cref="ProcessScanCodeWithMESAsync"/> 清空），
        ///   不再用定时器清空，避免现场看不清结果。
        /// - 失败/异常：状态文案保留，并引导查看日志与「数据库\错误日志」目录下 MES 诊断输出。
        /// - 成功后 <see cref="ResetProductContext"/> 清理 SN 与阶段进度，保留工具红绿便于追溯上一件。
        /// </remarks>
        private async Task<bool> ExecuteReportWorkAsync(string qualityResult = "合格")
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
                    DataModel.Processmodel.ReportWorkStatus = "正在报工...";
                }));

                writeLog($"🔄 开始报工: SN={DataModel.Processmodel.CurrentProductSN}, 结果={qualityResult}", false);

                // 兼容旧配置：历史版本把“工序编号”误用成了工位号；现在优先使用工序码，空则回退工位号
                string procedureNumber = string.IsNullOrWhiteSpace(DataModel.Settingmodel.ReportWorkConfig.ProcedureCode)
                    ? DataModel.Settingmodel.ReportWorkConfig.StationCode
                    : DataModel.Settingmodel.ReportWorkConfig.ProcedureCode;

                var reportResult = MES_ORACLE_DATABASE.MES_ORACLE_DATABASE.Save_EquipmentRecord_mes(
                    DataModel.Settingmodel.ReportWorkConfig.StationCode,     // 工位码
                    "", // 工单号 - 由MES系统内部获取
                    DataModel.Processmodel.CurrentProductSN,                 // 产品SN
                    DataModel.Settingmodel.ReportWorkConfig.ProcedureName,   // 工序名称
                    DataModel.Settingmodel.ReportWorkConfig.EquipmentNumber, // 设备编号
                    qualityResult,                                           // 检测结果
                    procedureNumber                                        // 工序编号(PPROCEDURE_NUMBER)
                );

                if (reportResult)
                {
                    string logMessage = $"[{DateTime.Now:HH:mm:ss}] SN:{DataModel.Processmodel.CurrentProductSN} 报工成功";

                    // 在UI线程更新界面
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DataModel.Processmodel.ReportWorkSuccessLog.Insert(0, logMessage);
                        DataModel.Processmodel.ReportWorkStatus = "✅ 报工成功";

                        // 保持最多50条记录
                        while (DataModel.Processmodel.ReportWorkSuccessLog.Count > 50)
                        {
                            DataModel.Processmodel.ReportWorkSuccessLog.RemoveAt(DataModel.Processmodel.ReportWorkSuccessLog.Count - 1);
                        }

                        // 不报工状态：成功提示保留至下一次扫码（ProcessScanCodeWithMESAsync 会清空），便于现场核对
                    }));

                    writeLog($"✅ 报工成功: SN={DataModel.Processmodel.CurrentProductSN}", false);

                    // 清空当前SN与阶段进度（保留工具状态显示），准备下一个产品
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ResetProductContext();
                    }));

                    return true;
                }
                else
                {
                    // 在UI线程更新界面
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DataModel.Processmodel.ReportWorkStatus = "❌ 报工失败（详情见日志与 数据库\\错误日志）";
                    }));

                    writeError($"❌ 报工失败: SN={DataModel.Processmodel.CurrentProductSN}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // 在UI线程更新界面
                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    DataModel.Processmodel.ReportWorkStatus = $"❌ 报工异常: {ex.Message}";
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
    }
}
