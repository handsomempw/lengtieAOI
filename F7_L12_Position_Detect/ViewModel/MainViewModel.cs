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
            DataModel.Settingmodel.TcpServerRobot.SendMessage(cmd);
            writeLog($"视觉->机器人:{cmd}");
        }

        public void InitRobotServer()
        {

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

        }
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


        public void ClearTools()
        {
            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                ClearTool(DataModel.Processmodel.Tools[i]);
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
        public void PLC_Start()
        {
            Thread t = new Thread(PLC_Process);
            t.Start();

            //Thread t2 = new Thread(shakehand2);
            //t2.Start();
        }
        private void PLC_Process()
        {

            while (true)
            {
                ModbusTcpNet modbusTcp = new ModbusTcpNet();
                try
                {
                    modbusTcp.ConnectTimeOut = 1;
                    modbusTcp.ReceiveTimeOut = 1;
                    modbusTcp.IpAddress = DataModel.Settingmodel.PLC_IP;
                    modbusTcp.Port = DataModel.Settingmodel.PLC_Port;

                    modbusTcp.DataFormat = HslCommunication.Core.DataFormat.CDAB;
                    var connectresult = modbusTcp.ConnectServer();

                    if (connectresult.IsSuccess)
                    {
                        #region 读取数据
                        var readresult = modbusTcp.ReadUInt16(DataModel.Settingmodel.AddressStart.ToString(), 2);

                        if (readresult.IsSuccess)
                        {
                            int Trig = readresult.Content[0];
                            try
                            {
                                if (Trig == 1)
                                {
                                    modbusTcp.Write((DataModel.Settingmodel.AddressStart).ToString(), (UInt16)0);

                                    if (DataModel.Processmodel.Trig_IO.IOstatus == 0)
                                    {
                                        DataModel.Processmodel.Scannerstr = string.Empty;
                                        var r = DataModel.Settingmodel.ScannerModel.Scanner();
                                        DataModel.Processmodel.Scannerstr = r.receivestring.Replace("\r", "").Replace("\n", "");

                                        App.Current.Dispatcher.BeginInvoke(new Action(() =>
                                        {                                            
                                            GetSNList(DataModel.Processmodel.Scannerstr);
                                        }));

                                        if (!r.IsSuccess)
                                        {
                                            modbusTcp.Write((DataModel.Settingmodel.AddressStart + 1).ToString(), (UInt16)2);
                                        }
                                        else
                                        {
                                            SendMsgRobot("DOK");
                                            ClearTools();
                                        }
                                    }
                                }
                            }
                            catch {; }

                            #region 数据复制刷新
                            DataModel.Processmodel.Trig_IO.IOstatus = Trig;


                            #endregion
                        }
                        #endregion
                        modbusTcp.ConnectClose();
                        //#region 握手
                        //modbusTcp.Write((DATA.PLC.AddressStart + 18).ToString(), (UInt16)0);
                        //#endregion
                    }
                    else
                    {

                        #region 通讯失败数据置为-1
                        DataModel.Processmodel.Trig_IO.IOstatus = -1;

                        #endregion
                    }
                    //modbusTcp?.ConnectClose();

                }
                catch {; }
                Thread.Sleep(100);
            }

        }

        public bool GetSNList(string barcode)
        {
            try
            {

                string sql = $"SELECT [成品码] FROM [绑定完表格] WHERE  [物料名称]='容器码' and [条形码]= '{barcode}' ORDER BY [编号]";
                DataTable dt = Sqlserver.MicrosoftSql_DATABASE.Read(sql);
                DataModel.Processmodel.SNList.Clear();
                for (int i = 0; i < dt.Rows.Count; i++)
                {
                    DataModel.Processmodel.SNList.Add(dt.Rows[i][0].ToString());
                }
                return true;

            }
            catch { return false; }
        }

        private bool PLC_write(UInt16 result)
        {
            writeLog($"视觉->PLC:{result}、{(result == 1 ? "OK" : "NG")}", false);

            int i = 0;
            while (i++ < 4)
            {
                ModbusTcpNet modbusTcp = new ModbusTcpNet();
                try
                {
                    modbusTcp.ConnectTimeOut = 1;
                    modbusTcp.ReceiveTimeOut = 1;
                    modbusTcp.IpAddress = DataModel.Settingmodel.PLC_IP;
                    modbusTcp.Port = DataModel.Settingmodel.PLC_Port;
                    modbusTcp.DataFormat = HslCommunication.Core.DataFormat.CDAB;
                    var connectresult = modbusTcp.ConnectServer();

                    if (connectresult.IsSuccess)
                    {
                        var r = modbusTcp.Write((DataModel.Settingmodel.AddressStart + 1).ToString(), (UInt16)result);
                        modbusTcp.ConnectClose();
                        if (r.IsSuccess)
                        { return true; }
                    }
                }
                catch
                {
                    ;
                }
                Thread.Sleep(100);
            }

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
                        App.Current.Dispatcher.Invoke(() =>
                        {
                            int num = 200;
                            if (DataModel.Recordmodel.workLog.Count > num)
                            {
                                //DataModel.Recordmodel.workLog.RemoveAt(DataModel.Recordmodel.workLog.Count - 1);
                                DataModel.Recordmodel.workLog.Clear();
                            }
                            DataModel.Recordmodel.workLog.Insert(0, logstr);
                        });
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
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        if (DataModel.Recordmodel.ErrorLog.Count > num)
                        {
                            //DataModel.Recordmodel.workLog.RemoveAt(DataModel.Recordmodel.workLog.Count - 1);
                            DataModel.Recordmodel.ErrorLog.Clear();
                        }
                        DataModel.Recordmodel.ErrorLog.Insert(0, bugstr);
                    });
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
            if (!Directory.Exists(dir))
            {
                return;
            }
            string[] toolfilenames = Directory.GetFiles(dir);
            int count = 0;
            for (int i = 0; i < toolfilenames.Length; i++)
            {
                FileInfo fi = new FileInfo(toolfilenames[i]);
                if (fi.Extension.ToUpper() == ".XML")
                {
                    count++;
                }
            }
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
                        using (Bitmap bmp = (Bitmap)Bitmap.FromFile(jpgfilename))
                        {
                            using (Bitmap bmp1 = GetReducedImage(DataModel.Settingmodel.ImageSize, DataModel.Settingmodel.ImageSize, bmp))
                            {
                                BitmapSource bs = Imaging.CreateBitmapSourceFromHBitmap(bmp1.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                                tool.BitmapSource = null;
                                tool.CurrentBitmapSource = null;

                                tool.BitmapSource = bs;
                                tool.CurrentBitmapSource = bs.Clone();
                            }
                        }
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

            #region 图片接收

            //Image



            HWindow hwindow = DataModel.Settingmodel.HWindow;
            hwindow.ClearWindow();
            hwindow.SetPart(0, 0, H - 1, W - 1);
            hwindow.DispObj(Image);
            for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
            {
                if (DataModel.Processmodel.Tools[i].Command == DataModel.Processmodel.RCMD)
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

                    Hobject2Bitmap.HobjectToBitmap24(Image, out bmp);

                    using (Bitmap bmp1 = GetReducedImage(DataModel.Settingmodel.ImageSize, DataModel.Settingmodel.ImageSize, bmp))
                    {
                        tool.CurrentBitmapSource = null;
                        tool.CurrentBitmapSource = Imaging.CreateBitmapSourceFromHBitmap(bmp1.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    }

                    bmp.Dispose();

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

                                            DataModel.Settingmodel.TcpClientH.SendMsg($"{DataModel.Processmodel.Scannerstr};{DataModel.Processmodel.Barcodes}\r\n");
                                            string s = DataModel.Settingmodel.TcpClientH.ReceiveMsg(5000);
                                            if(!string.IsNullOrEmpty(s))
                                            {
                                                App.Current.Dispatcher.BeginInvoke(new Action(() =>
                                                {
                                                    DataModel.Processmodel.SNList.Add(s);
                                                }));
                                                tool.ToolStatus = ToolStatus.OK;
                                                writeLog($"扫码汇报软件返回成品编号:{s}");

                                            }else
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

                    #endregion

                    if (tool.SendStatus)
                    {

                        int status = -1;

                        var r = (from ToolModel in DataModel.Processmodel.Tools
                                 where ToolModel.Command == DataModel.Processmodel.RCMD
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
                        int c = DataModel.Processmodel.Tools.Count();


                        var ok = (from ToolModel in DataModel.Processmodel.Tools
                                  where (ToolModel.ToolStatus == ToolStatus.OK)
                                  select ToolModel);
                        var NG1 = (from ToolModel in DataModel.Processmodel.Tools
                                   where (ToolModel.ToolStatus == ToolStatus.NG)
                                   select ToolModel);
                        var NG2 = (from ToolModel in DataModel.Processmodel.Tools
                                   where (ToolModel.ToolStatus == ToolStatus.NG2)
                                   select ToolModel);
                        var wait = (from ToolModel in DataModel.Processmodel.Tools
                                    where (ToolModel.ToolStatus == ToolStatus.等待中 || ToolModel.ToolStatus == ToolStatus.识别中)
                                    select ToolModel);

                        if (ok.Count() == c)
                        {
                            status = 0;
                        }
                        else if (NG2.Count() > 0)
                        {
                            status = 2;
                        }
                        else
                        {
                            status = 1;
                        }

                        if (status == 0)
                        {
                            DataModel.Processmodel.Status = ToolStatus.OK;
                            PLC_write((UInt16)1);
                        }
                        else if (status == 0)
                        {
                            DataModel.Processmodel.Status = ToolStatus.NG2;
                            PLC_write((UInt16)3);
                        }
                        else
                        {
                            DataModel.Processmodel.Status = ToolStatus.NG;
                            PLC_write((UInt16)2);
                        }

                    }

                    writeLog($"视觉->视觉:保存完成", false);

                    string s2 = $"{DataModel.Processmodel.Tools[i].Name}:保存图片发送结果耗时:{stopwatch.ElapsedMilliseconds}ms";
                    Save_record(s2);

                }
            }
            #endregion

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
                        hwindow.SetPart(0, 0, (int)height - 1, (int)width - 1);
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
    }
}