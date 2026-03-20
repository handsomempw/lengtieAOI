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


        private string _lastProductSN = string.Empty;

        /// <summary>
        /// 从当前工具配置推导“串行阶段列表”。
        /// </summary>
        /// <remarks>
        /// 业务意图：
        /// - 根据 ToolModel.Command 动态形成阶段序列（A1/A2/A3...）。
        /// - 阶段顺序按工具列表首次出现顺序（Distinct 保序）生成，确保与现场配置习惯一致。
        /// - 该方法只负责阶段“配置”，阶段“进度”由 CompletedStages / CurrentStageIndex 管理。
        /// </remarks>
        private void BuildConfiguredStages()
        {
            DataModel.Processmodel.ConfiguredStages = DataModel.Processmodel.Tools
                .Select(t => t.Command)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// 重置“阶段进度”但不影响工具显示与SN。
        /// </summary>
        /// <remarks>
        /// 业务意图：
        /// - 报工成功后，只需要清 SN + 扫码状态 + 阶段进度，保留界面上工具 OK/NG 以便人员确认上一件结果。
        /// - NG 后下一次从头开始时，会在 ResetDetectionCycle 内同时清工具运行态。
        /// </remarks>
        private void ResetStageProgress()
        {
            DataModel.Processmodel.CompletedStages.Clear();
            DataModel.Processmodel.CurrentStageIndex = 0;
            DataModel.Processmodel.NeedRestart = false;
            DataModel.Processmodel.ReportWorkStatus = string.Empty;
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

        public void ClearCurrentProductContext()
        {
            App.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                // 手动清空：既清工具运行态（ResetDetectionCycle），也清产品上下文（ResetProductContext）
                ResetDetectionCycle();
                ResetProductContext();
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
        /// 按指令范围清理工具的运行态数据（不会改变工具配置）。
        /// </summary>
        /// <param name="command">指令名；传入 "ALL" 表示清理全部工具。</param>
        public void ClearToolsByCommand(string command)
        {
            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                if (command == "ALL" || DataModel.Processmodel.Tools[i].Command == command)
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
        private DateTime _executionStartTime = DateTime.MinValue;
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
         *   - 同一相机分组内的多个工具，共用这一张图做识别（同图多工具）。
         *
         * 时序与收口：
         * - ProcessSerialTrigger() 初始化 _pendingCameraGroups，并触发第一组相机取图；
         * - OnReceiveProcess() 只处理 RCMD 且匹配当前 _activeCameraSerial 的工具；
         * - OnReceiveProcess() finally 中：
         *     - 若还有相机组未处理，则继续触发下一组取图（不 EndExecution）；
         *     - 若 TriggerNextCameraGroupCapture 返回 false 或抛异常，立即 NG、NeedRestart、EndExecution，不等超时看门狗；
         *     - 若相机组处理完毕，才 EndExecution() 释放 PLC 执行互斥窗口。
         *
         * 等图门闩（_waitingImage）：
         * - 仅在成功软触发 bnTriggerExec 前置 true；相机线程 OnReceive 入口 TryAcceptImageCallback 原子消费为 false。
         * - 非执行态、或非“等图”态到达的回调直接丢弃，减少晚到帧误入当前轮（日志用 WriteDetailLog，不作为拦截依据）。
         * - 手动“照片测试”直接调用 OnReceiveProcess，不经 OnReceive，不受门闩影响。
         *
         * 注意：
         * - 当前实现为“串行多相机”（依次取图），避免并行回调竞态；
         * - NormalizeCameraSerial 将 null 视为空串，空串也会作为一个分组存在（用于未配置序列号的工具）。
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

        private Queue<CameraGroup> _pendingCameraGroups = null;
        /// <summary>等图门闩：仅在一次软触发之后为 true，回调入口原子消费为 false，避免非预期帧进入处理。</summary>
        private bool _waitingImage = false;
        private string _activeCameraSerial = null; // null 表示不按相机过滤（兼容旧逻辑）

        /// <summary>
        /// 归一化相机序列号：将 null 统一视为 ""，确保分组键稳定。
        /// </summary>
        private static string NormalizeCameraSerial(string cameraSerial)
        {
            return cameraSerial ?? string.Empty;
        }

        /// <summary>
        /// 构建当前阶段的“相机分组队列”。
        /// </summary>
        /// <remarks>
        /// - 仅收集 <paramref name="command"/> 对应阶段的工具；
        /// - 按工具列表的首次出现顺序稳定输出分组（避免 Dictionary 枚举导致顺序不稳定）。
        /// </remarks>
        private Queue<CameraGroup> BuildCameraGroupsForStageCommand(string command)
        {
            // 按工具列表顺序稳定分组：同一阶段内不同相机 -> 多次取图
            var groups = new Dictionary<string, List<int>>();
            var groupOrder = new List<string>();

            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                var tool = DataModel.Processmodel.Tools[i];
                if (tool.Command != command) continue;

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
        private bool TriggerNextCameraGroupCapture(string command)
        {
            if (_pendingCameraGroups == null || _pendingCameraGroups.Count == 0)
            {
                _activeCameraSerial = null;
                return false;
            }

            var group = _pendingCameraGroups.Dequeue();
            _activeCameraSerial = group.CameraSerial;

            if (group.ToolIndices.Count == 0)
            {
                return TriggerNextCameraGroupCapture(command);
            }

            int firstToolIndex = group.ToolIndices[0];
            var firstTool = DataModel.Processmodel.Tools[firstToolIndex];

            DataModel.Processmodel.ToolIndex = firstToolIndex + 1;

            // 切换到该相机并按该组第一个工具设置曝光
            SwitchCameraForTool(firstTool);
            Thread.Sleep(100);
            DataModel.Settingmodel.camedata.CameraModel.exposuretime = firstTool.ExposureTime;
            DataModel.Settingmodel.camedata.CameraModel.camera.Exposure = firstTool.ExposureTime;
            DataModel.Settingmodel.camedata.CameraModel.camera.bnSetParam_Click();
            Thread.Sleep(DataModel.Settingmodel.delaytime);

            writeLog($"开始拍照: {command}阶段 - 相机='{_activeCameraSerial}', 工具数={group.ToolIndices.Count}");
            lock (_executionLock)
            {
                _waitingImage = true;
            }
            DataModel.Settingmodel.camedata.CameraModel.camera.bnTriggerExec_Click();
            return true;
        }

        /// <summary>
        /// 尝试进入“单次指令执行窗口”，用于防止 PLC 触发重入/并发执行。
        /// </summary>
        /// <returns>成功进入执行态返回 true；若已有执行在进行中返回 false。</returns>
        private bool TryBeginExecution()
        {
            lock (_executionLock)
            {
                if (_isExecuting) return false;
                _isExecuting = true;
                _waitingImage = false;
                _executionStartTime = DateTime.UtcNow;
                return true;
            }
        }

        /// <summary>
        /// 相机 SDK 线程上调用：仅当处于 PLC 执行窗口且本帧为“软触发所等待的那一帧”时放行，并原子清除等图门闩。
        /// </summary>
        private bool TryAcceptImageCallback(out string dropReason)
        {
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

                _waitingImage = false;
                dropReason = string.Empty;
                return true;
            }
        }

        /// <summary>
        /// 结束当前执行窗口，释放 PLC 指令互斥状态。
        /// </summary>
        private void EndExecution()
        {
            lock (_executionLock)
            {
                _isExecuting = false;
                _waitingImage = false;
            }
            _pendingCameraGroups = null;
            _activeCameraSerial = null;
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

                // 超时看门狗：检测执行态是否超时（相机不回图时兜底释放）
                if (_isExecuting && (DateTime.UtcNow - _executionStartTime).TotalMilliseconds > ExecutionTimeoutMs)
                {
                    string timeoutCmd = DataModel.Processmodel.RCMD;
                    writeError($"执行超时({ExecutionTimeoutMs}ms): {timeoutCmd}指令强制释放");
                    WriteDetailLog($"[超时看门狗] 指令{timeoutCmd}执行超过{ExecutionTimeoutMs}ms，强制EndExecution并标记NG");
                    // 超时视为该阶段失败，下次触发必须从头开始（防止阶段卡死或状态污染）
                    DataModel.Processmodel.NeedRestart = true;
                    DataModel.Processmodel.Status = ToolStatus.NG;
                    DataModel.Processmodel.CameraStatus = "读图超时";
                    EndExecution();
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
                                writeLog($"PLC触发: 串行阶段执行 (地址{DataModel.Settingmodel.AddressStart}={currentValue})");
                                WriteDetailLog($"[PLC触发] 地址触发值变化: {lastTriggerValue} -> {currentValue}");
                                ProcessSerialTrigger(DataModel.Settingmodel.AddressStart, currentValue, modbusTcp);
                            }
                            lastTriggerValue = currentValue;
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
                    EndExecution();
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
                // 新一轮扫码开始时，先停止可能存在的“扫码自动触发执行循环”
                StopScanAutoExecution();

                // 更新扫码状态为处理中
                DataModel.Processmodel.ScanCodeStatus = "正在处理...";

                // 获取工序名称
                string procedureName = DataModel.Settingmodel.ReportWorkConfig.ProcedureName;

                // 调用MES服务进行完整处理
                var (scanResult, validationResult) = await _mesService.ProcessScanCodeWithValidationAsync(scanCode, procedureName);

                // 处理扫码结果
                if (scanResult.Success)
                {
                    // SN获取成功，更新到界面
                    ResetForNewProduct(scanResult.SN);
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DataModel.Processmodel.CurrentProductSN = scanResult.SN;
                        DataModel.Processmodel.ScanCodeStatus = scanResult.StatusMessage;
                        writeLog($"扫码处理成功: SN={scanResult.SN}");
                    }));

                    // 处理工序校验结果
                    if (validationResult != null)
                    {
                        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (validationResult.Success)
                            {
                                DataModel.Processmodel.ScanCodeStatus = validationResult.StatusMessage;
                                writeLog($"工序校验通过: SN={scanResult.SN}, 工序={procedureName}");

                                // 扫码触发模式：扫码校验成功后，自动依次执行后续阶段
                                if (DataModel.Settingmodel.EnableScanAutoTriggerStages)
                                {
                                    StartScanAutoExecution(scanResult.SN);
                                }
                                else
                                {
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
                }
                else
                {
                    // SN获取失败
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DataModel.Processmodel.ScanCodeStatus = scanResult.StatusMessage;
                        writeLog($"扫码处理失败: {scanResult.ErrorMessage}");

                        StopScanAutoExecution();
                    }));
                }
            }
            catch (Exception ex)
            {
                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    DataModel.Processmodel.ScanCodeStatus = "处理异常";
                    writeLog($"扫码处理异常: {ex.Message}");
                }));
            }
        }

        /// <summary>
        /// 扫码触发自动执行循环入口（不改动PLC触发代码）。
        /// - 仅当 EnableScanAutoTriggerStages=true 且工序校验通过时启动
        /// - 复用视觉端内部阶段状态机：CurrentStageIndex/CompletedStages/NeedRestart
        /// </summary>
        private void StartScanAutoExecution(string sn)
        {
            if (DataModel?.Settingmodel == null) return;
            if (!DataModel.Settingmodel.EnableScanAutoTriggerStages) return;
            if (string.IsNullOrEmpty(sn)) return;

            lock (_scanAutoLock)
            {
                _scanAutoTriggerCts?.Cancel();
                _scanAutoTriggerCts = new CancellationTokenSource();
            }

            var localCts = _scanAutoTriggerCts;
            var token = localCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        // 若已经切换到别的SN，停止
                        if (DataModel?.Processmodel?.CurrentProductSN != sn) break;

                        // 关闭开关时停止
                        if (!DataModel.Settingmodel.EnableScanAutoTriggerStages) break;

                        // 未满足“SN + 工序校验通过”，停止（避免误触发）
                        if (!HasValidSNAndValidation()) break;

                        // 已完成所有阶段，退出
                        if (DataModel.Processmodel.CurrentStageIndex >= DataModel.Processmodel.ConfiguredStages.Count)
                            break;

                        // 避免在报工过程中推进检测
                        if (DataModel.Processmodel.IsReportingWork)
                        {
                            await Task.Delay(100, token);
                            continue;
                        }

                        // 等待本轮执行窗口释放
                        if (_isExecuting)
                        {
                            await Task.Delay(50, token);
                            continue;
                        }

                        bool started = TriggerNextStageByScan();
                        if (!started) break;

                        // 等待相机回调收口并释放执行窗口
                        while (_isExecuting && !token.IsCancellationRequested)
                        {
                            await Task.Delay(50, token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    writeError($"扫码自动执行循环异常: {ex.Message}");
                }
                finally
                {
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
            try
            {
                if (!TryBeginExecution())
                {
                    return false;
                }

                if (!HasValidSNAndValidation())
                {
                    writeLog($"报工状态不满足: 跳过扫码阶段触发 (SN={DataModel.Processmodel.CurrentProductSN})");
                    EndExecution();
                    return false;
                }

                if (DataModel.Processmodel.NeedRestart)
                {
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
                int idx = DataModel.Processmodel.CurrentStageIndex;
                if (idx < 0) idx = 0;
                if (idx >= stages.Count)
                {
                    EndExecution();
                    return false;
                }

                string command = stages[idx];
                DataModel.Processmodel.RCMD = command;

                ClearToolsByCommand(command);

                _pendingCameraGroups = BuildCameraGroupsForStageCommand(command);
                if (_pendingCameraGroups.Count == 0)
                {
                    EndExecution();
                    return false;
                }

                bool triggered = TriggerNextCameraGroupCapture(command);
                if (!triggered)
                {
                    EndExecution();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                writeError($"扫码阶段触发处理异常: {ex.Message}");
                try { EndExecution(); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 处理PLC触发信号
        /// </summary>
        /// <param name="address">触发地址</param>
        /// <param name="triggerValue">触发值</param>
        /// <param name="modbusTcp">ModbusTcp连接对象</param>
        private void ProcessSerialTrigger(int address, int triggerValue, ModbusTcpNet modbusTcp)
        {
            try
            {
                WriteDetailLog($"[PLC触发] 准备复位地址{address}为0");
                var resetResult = modbusTcp.Write(address.ToString(), (UInt16)0);
                WriteDetailLog($"[PLC触发] 地址{address}复位结果={resetResult.IsSuccess}");

                if (!TryBeginExecution())
                {
                    writeLog($"系统忙碌: 忽略PLC触发 (地址{address}={triggerValue})");
                    WriteDetailLog($"[业务逻辑] 系统正在执行中，忽略地址{address}触发值{triggerValue}");
                    return;
                }
                WriteDetailLog($"[业务逻辑] 设置执行状态为true，开始处理串行阶段触发");

                if (!HasValidSNAndValidation())
                {
                    writeLog($"报工状态不满足: 跳过PLC触发 (SN={DataModel.Processmodel.CurrentProductSN})");
                    WriteDetailLog($"[业务逻辑] 报工模块状态不满足，跳过PLC触发执行 (SN={DataModel.Processmodel.CurrentProductSN})");
                    EndExecution();
                    return;
                }

                if (DataModel.Processmodel.NeedRestart)
                {
                    // 上一轮出现 NG/NG2/超时等失败：
                    // - 默认：清空检测周期并从第 0 阶段重跑（最保守，避免状态污染）
                    // - 可配置：不清理已有结果，仅重试当前失败阶段（保留CompletedStages与其它阶段工具显示）
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
                int idx = DataModel.Processmodel.CurrentStageIndex;
                if (idx < 0) idx = 0;
                if (idx >= stages.Count)
                {
                    // 已完成所有阶段：忽略多余触发
                    WriteDetailLog($"[业务逻辑] 所有阶段已完成，忽略本次PLC触发");
                    EndExecution();
                    return;
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
                    EndExecution();
                    return;
                }
                bool triggered = TriggerNextCameraGroupCapture(command);
                if (!triggered)
                {
                    EndExecution();
                    return;
                }

                WriteDetailLog($"[界面更新] 更新界面触发标志从{DataModel.Processmodel.Trig_IO.IOstatus}到{triggerValue}");
                DataModel.Processmodel.Trig_IO.IOstatus = triggerValue;
            }
            catch (Exception ex)
            {
                writeError($"PLC触发处理异常: {ex.Message}");
                WriteDetailLog($"[PLC触发] 处理异常: {ex.Message}");
                EndExecution();
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

                // 检查是否可以开始检测（只检查是否在报工中）
                if (!CanStartDetection())
                {
                    writeLog($"检测被阻止: 报工正在进行中");
                    if (detailOnNgOrTimeout)
                    {
                        WriteDetailLog($"[工具执行] 检测被阻止，原因：报工正在进行中");
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
                        isMatch = DataModel.Processmodel.Tools[i].Command == command;
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
                SwitchCameraForTool(firstTool);
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
            finally
            {
                BuildConfiguredStages();
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
            DataModel.Processmodel.CameraStatus = "等待回图";
            writeLog("[相机] 已订阅回调(ImageReceived)，等待回图", false);
        }
        /// <summary>
        /// 相机 SDK 的 ImageReceived 入口：
        /// - PLC 执行窗口且“等图门闩”通过时，投递到 UI 线程执行 <see cref="OnReceiveProcess"/>（识别/报工）。
        /// - 未在执行态时仅做预览显示（<see cref="TryAcceptImageCallback"/> 失败且原因为“当前不在执行态”），不走识别/报工。
        /// </summary>
        private void OnReceive(object sender, EventArgs e)
        {

            try
            {
                writeLog($"相机->视觉:接收照片", false);

                MyEventArgs myEventArgs = e as MyEventArgs;
                if (myEventArgs == null)
                {
                    WriteDetailLog("[回调丢弃] ImageReceived 参数类型异常");
                    return;
                }

                if (!TryAcceptImageCallback(out string dropReason))
                {
                    // 未在执行态：仅显示预览，不参与识别/报工逻辑。
                    if (dropReason == "当前不在执行态")
                    {
                        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                HWindow hwindow = DataModel.Settingmodel.HWindow;
                                hwindow.ClearWindow();
                                hwindow.DispObj(myEventArgs.Image);
                            }
                            catch {; }
                        }));
                        return;
                    }

                    // 执行窗口内但不满足“等图帧”：仍保持丢弃，避免晚到帧污染本轮识别序列。
                    WriteDetailLog($"[回调丢弃] {dropReason}");
                    return;
                }

                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    OnReceiveProcess(myEventArgs.Image, myEventArgs.Height, myEventArgs.Width);
                }));

            }
            catch (Exception ex)
            {
                DataModel.Processmodel.CameraStatus = "相机异常";
                writeError($"[{DataModel.Processmodel.RCMD}]识别错误:{ex.ToString()}");
            }
        }


        /// <summary>
        /// UI 线程上处理一帧图像：按当前 RCMD 与 <see cref="_activeCameraSerial"/> 匹配工具并跑识别，最后做阶段/报工判断。
        /// </summary>
        /// <remarks>
        /// 多相机阶段：在 finally 中若仍有 <see cref="_pendingCameraGroups"/>，会调用 <see cref="TriggerNextCameraGroupCapture"/>；
        /// 若返回 false 或抛异常，则立即 NG、NeedRestart、<see cref="EndExecution"/>，不等待超时看门狗。
        /// </remarks>
        public void OnReceiveProcess(HObject Image, int H, int W)
        {
            HObject processImage = null;
            try
            {
                DataModel.Processmodel.CameraStatus = "正常";

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
                    bool isMatch = DataModel.Processmodel.Tools[i].Command == DataModel.Processmodel.RCMD;
                    if (isMatch && _activeCameraSerial != null)
                    {
                        isMatch = NormalizeCameraSerial(DataModel.Processmodel.Tools[i].CameraSerialNumber) == _activeCameraSerial;
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
                            tool.CurrentBitmapSource = ImageHelper.ToBitmapSource(bmp);
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
                                            DataModel.Processmodel.Barcodes += barcodelist[0].codestr + ";";
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

                                    string savefilename = $"{DataModel.Settingmodel.ImageSaveSetting.ImageSaveDir}\\{DateTime.Now.ToString("yyyyMMdd")}\\OK\\{DataModel.Processmodel.BarcodeStr}-{tool.Index.ToString("00")}-{tool.Name}-{tool.ToolStatus}-{DateTime.Now.ToString("yyyyMMddHHmmssFFF")}.jpg";
                                    try
                                    {
                                        savefilename = $"{DataModel.Settingmodel.ImageSaveSetting.ImageSaveDir}\\{DateTime.Now.ToString("yyyyMMdd")}\\OK\\{DataModel.Processmodel.SNList[DataModel.Processmodel.Tools[i].ProductPositionNO]}-{tool.Index.ToString("00")}-{tool.Name}-{tool.ToolStatus}-{DateTime.Now.ToString("yyyyMMddHHmmssFFF")}.jpg";
                                    }
                                    catch {; }

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
                                    string savefilename = $"{DataModel.Settingmodel.ImageSaveSetting.ImageSaveDir}\\{DateTime.Now.ToString("yyyyMMdd")}\\NG\\{DataModel.Processmodel.BarcodeStr}-{tool.Index.ToString("00")}-{tool.Name}-{tool.ToolStatus}-{DateTime.Now.ToString("yyyyMMddHHmmssFFF")}.jpg";
                                    try
                                    {
                                        savefilename = $"{DataModel.Settingmodel.ImageSaveSetting.ImageSaveDir}\\{DateTime.Now.ToString("yyyyMMdd")}\\NG\\{DataModel.Processmodel.SNList[DataModel.Processmodel.Tools[i].ProductPositionNO]}-{tool.Index.ToString("00")}-{tool.Name}-{tool.ToolStatus}-{DateTime.Now.ToString("yyyyMMddHHmmssFFF")}.jpg";
                                    }
                                    catch {; }
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

                            var r = (from ToolModel in DataModel.Processmodel.Tools
                                     where (ToolModel.Command == DataModel.Processmodel.RCMD)
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


                        writeLog($"视觉->视觉:保存完成", false);

                        string s2 = $"{DataModel.Processmodel.Tools[i].Name}:保存图片发送结果耗时:{stopwatch.ElapsedMilliseconds}ms";
                        Save_record(s2);

                    }
                }

                #region 阶段结算（按当前指令过滤工具集合）
                string currentCommand = DataModel.Processmodel.RCMD;
                var currentCommandTools = (from t in DataModel.Processmodel.Tools
                                           where (t.Command == currentCommand)
                                           select t).ToList();
                int toolCount = currentCommandTools.Count;

                if (toolCount > 0)
                {
                    var okTools = currentCommandTools.Where(t => t.ToolStatus == ToolStatus.OK);
                    var ng2Tools = currentCommandTools.Where(t => t.ToolStatus == ToolStatus.NG2);
                    var waitTools = currentCommandTools.Where(t => t.ToolStatus == ToolStatus.等待中 || t.ToolStatus == ToolStatus.识别中);

                    if (waitTools.Count() == 0)
                    {
                        // 阶段结算规则：
                        // - 当前阶段内：所有工具 OK => 阶段 OK；存在 NG2 => 阶段 NG2；否则阶段 NG
                        // - 阶段 OK：推进到下一阶段（CurrentStageIndex++）并记录 CompletedStages
                        // - 阶段 NG/NG2：不推进，标记 NeedRestart=true，下一次 PLC 触发从第 0 阶段重跑
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

                        if (stageStatus == 0)
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
                #endregion

                #endregion

            }
            catch (Exception ex)
            {
                DataModel.Processmodel.CameraStatus = "相机异常";
                writeError($"[{DataModel.Processmodel.RCMD}]图像处理异常:{ex.Message}");
            }
            finally
            {
                if (processImage != null && processImage != Image)
                {
                    processImage.Dispose();
                }
                if (_isExecuting)
                {
                    // 若同一阶段存在多相机分组，则在上一张图处理结束后继续触发下一相机取图；
                    // 下一组触发失败或异常时立即 NG、NeedRestart、收口，不依赖超时看门狗。
                    if (_pendingCameraGroups != null && _pendingCameraGroups.Count > 0)
                    {
                        try
                        {
                            bool triggered = TriggerNextCameraGroupCapture(DataModel.Processmodel.RCMD);
                            if (!triggered)
                            {
                                DataModel.Processmodel.Status = ToolStatus.NG;
                                DataModel.Processmodel.NeedRestart = true;
                                DataModel.Processmodel.CameraStatus = "相机异常";
                                writeError("下一相机组触发失败，已按失败立即收口");
                                EndExecution();
                            }
                        }
                        catch (Exception ex)
                        {
                            DataModel.Processmodel.Status = ToolStatus.NG;
                            DataModel.Processmodel.NeedRestart = true;
                            DataModel.Processmodel.CameraStatus = "相机异常";
                            writeError($"下一相机组触发异常，已按失败立即收口: {ex.Message}");
                            EndExecution();
                        }
                    }
                    else
                    {
                        EndExecution();
                    }
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
        /// 检查报工模块是否有有效SN且工序校验成功
        /// </summary>
        /// <returns>true表示状态满足，可以进行PLC轮询和视觉检测</returns>
        private bool HasValidSNAndValidation()
        {
            bool hasSN = !string.IsNullOrEmpty(DataModel.Processmodel.CurrentProductSN);
            bool isValidated = DataModel.Processmodel.ScanCodeStatus == "工序校验通过";
            bool result = hasSN && isValidated;

            // 详细调试日志：记录状态检查结果
            WriteDetailLog($"[报工状态] SN={DataModel.Processmodel.CurrentProductSN ?? "空"}, " +
                          $"校验状态={DataModel.Processmodel.ScanCodeStatus}, " +
                          $"有SN={hasSN}, 校验通过={isValidated}, 最终结果={result}");

            return result;
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

                        // 3秒后清除状态提示
                        Task.Delay(3000).ContinueWith(_ =>
                        {
                            App.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DataModel.Processmodel.ReportWorkStatus = "";
                            }));
                        });
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
                        DataModel.Processmodel.ReportWorkStatus = "❌ 报工失败";

                        // 5秒后清除状态提示
                        Task.Delay(5000).ContinueWith(_ =>
                        {
                            App.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DataModel.Processmodel.ReportWorkStatus = "";
                            }));
                        });
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

                    // 5秒后清除状态提示
                    Task.Delay(5000).ContinueWith(_ =>
                    {
                        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DataModel.Processmodel.ReportWorkStatus = "";
                        }));
                    });
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
