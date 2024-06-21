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
                if (DataModel.Processmodel.Tools[i].PositionDetect)
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
                NoticeBox.Show((string)TCPevent.Msg, "接收机器人信息", MessageBoxIcon.Info, true, 3000);
                DataModel.Processmodel.ToolIndex = -1;
                for (int i = 0; i < DataModel.Processmodel.Tools.Count; i++)
                {
                    if (DataModel.Processmodel.Tools[i].Command == cmd)
                    {
                        DataModel.Processmodel.ToolIndex = i + 1;

                        DataModel.Settingmodel.camedata.CameraModel.camera.bnTriggerExec_Click();


                        //ToolModel tool = DataModel.Processmodel.Tools[i];
                        //ClearTool(tool);
                        //bool result = true;
                        //if (tool.DecodeBarcode || tool.PositionDetect)
                        //{
                        //    OpenFileDialog ofd = new OpenFileDialog();
                        //    ofd.Filter = "*.jpg;*.bmp|*.jpg;*.bmp";
                        //    if (ofd.ShowDialog() == true)
                        //    {


                        //        #region 读取二维码
                        //        List<Reg.Barcode> barcodelist = null;
                        //        ShapeMatch.Result shapmatchresult = new ShapeMatch.Result();


                        //        if (tool.DecodeBarcode)
                        //        {
                        //            try
                        //            {
                        //                HObject ho_Image;
                        //                HOperatorSet.GenEmptyObj(out ho_Image);
                        //                HOperatorSet.ReadImage(out ho_Image, ofd.FileName);
                        //                barcodelist = tool.Reg.ReadBarcode(ho_Image, 1, tool.BarCodeROI.Row1, tool.BarCodeROI.Col1, tool.BarCodeROI.Row2, tool.BarCodeROI.Col2, true);
                        //                if (barcodelist.Count > 0)
                        //                {
                        //                    tool.BarcodeStr = barcodelist[0].codestr;
                        //                    NoticeBox.Show(barcodelist[0].codestr, "二维码读取成功", MessageBoxIcon.Success, true, 5000);
                        //                    if (!tool.SendStatus)
                        //                    {
                        //                        SendMsgSoft(tool.BarcodeStr);
                        //                    }
                        //                    writeLog($"二维码读取成功:{barcodelist[0].codestr}");
                        //                }
                        //                else
                        //                {
                        //                    tool.BarcodeStr = string.Empty;
                        //                    writeLog($"二维码读取失败:{barcodelist[0].codestr}");
                        //                }
                        //            }
                        //            catch
                        //            {
                        //                ;
                        //            }
                        //        }
                        //        #endregion


                        //        #region 读取位置

                        //        bool checkpositon = false;
                        //        if (tool.PositionDetect)
                        //        {
                        //            try
                        //            {
                        //                HObject ho_Image;
                        //                HOperatorSet.GenEmptyObj(out ho_Image);
                        //                HOperatorSet.ReadImage(out ho_Image, ofd.FileName);
                        //                tool.ShapeMatch.BasicData.matchcenter_X = tool.InitX;
                        //                tool.ShapeMatch.BasicData.matchcenter_Y = tool.InitY;
                        //                tool.ShapeMatch.BasicData.matchcenterX_Basic = tool.InitX;
                        //                tool.ShapeMatch.BasicData.matchcenterY_Basic = tool.InitY;
                        //                tool.ShapeMatch.BasicData.productcenter_X = tool.InitX;
                        //                tool.ShapeMatch.BasicData.productcenter_Y = tool.InitY;
                        //                shapmatchresult = tool.ShapeMatch.Match(ho_Image, 1, tool.PositionROI.Row1, tool.PositionROI.Col1, tool.PositionROI.Row2, tool.PositionROI.Col2);
                        //                var Result_Data = tool.ShapeMatch.Analysis_Result(shapmatchresult);
                        //                if (Result_Data != null)
                        //                {
                        //                    tool.ActualX = Result_Data.X_actual;
                        //                    tool.ActualY = Result_Data.Y_actual;
                        //                    tool.ActualScore = Result_Data.score;
                        //                    tool.DeltaX = Result_Data.deltaX_actual * tool.K / 1000;
                        //                    tool.DeltaY = Result_Data.deltaY_actual * tool.K / 1000;

                        //                    string sendstr = $"{(tool.DirectX ? -tool.DeltaX : tool.DeltaX).ToString("F2")},{(tool.DirectY ? -tool.DeltaY : tool.DeltaY).ToString("F2")},0.00";
                        //                    if (tool.InvertXY)
                        //                    {
                        //                        sendstr = $"{(tool.DirectY ? -tool.DeltaY : tool.DeltaY).ToString("F2")},{(tool.DirectX ? -tool.DeltaX : tool.DeltaX).ToString("F2")},0.00";
                        //                    }

                        //                    if (tool.ActualScore >= tool.MinScore)
                        //                    {
                        //                        if (!tool.SendStatus)
                        //                        {
                        //                            SendMsgRobot($"{sendstr}");
                        //                        }
                        //                        checkpositon = true;
                        //                    }

                        //                    //NoticeBox.Show("")
                        //                }
                        //            }
                        //            catch
                        //            {
                        //                ;
                        //            }

                        //            if (tool.DecodeBarcode)
                        //            {
                        //                tool.Reg.RefreshResult(barcodelist);
                        //            }
                        //        }
                        //        else
                        //        {
                        //            checkpositon = true;
                        //        }
                        //        #endregion

                        //        if (tool.SendStatus)
                        //        {
                        //            if (((!tool.DecodeBarcode) || barcodelist.Count > 0)
                        //                && (checkpositon)

                        //                )
                        //            {

                        //                SendMsgRobot($"OK");
                        //            }
                        //            else
                        //            {
                        //                SendMsgRobot($"NG");

                        //            }
                        //        }

                        //    }
                        //}
                        break;
                    }
                }
            }
            catch (Exception ex) {; }
        }

        public void ClearTool(ToolModel tool)
        {
            tool.BarcodeStr = string.Empty;
            tool.DeltaX = 0;
            tool.DeltaY = 0;
            tool.ActualScore = 0;
            tool.ActualX = 0;
            tool.ActualY = 0;

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
                NoticeBox.Show((string)TCPevent.Msg, "接收上位机信息", MessageBoxIcon.Info, true, 3000);
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

        internal void writeLog(string LogContent)
        {
            lock (writeLog_Locker)
            {
                try
                {
                    string logstr = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.FFF")}]{LogContent}";
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        DataModel.Recordmodel.workLog.Insert(0, logstr);
                        int num = 4000;
                        if (DataModel.Recordmodel.workLog.Count > num)
                        {
                            DataModel.Recordmodel.workLog.RemoveAt(DataModel.Recordmodel.workLog.Count - 1);
                        }
                    });


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
                    int num = 2000;
                    string bugstr = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.FFF")}]{Content}";

                    DataModel.Recordmodel.ErrorLog.Insert(0, bugstr);
                    if (DataModel.Recordmodel.ErrorLog.Count > num)
                    {
                        DataModel.Recordmodel.workLog.RemoveAt(DataModel.Recordmodel.workLog.Count - 1);
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
            try
            {
                if (DataModel.Processmodel.selectedindex < 0)
                {

                    NoticeBox.Show($"请先选择需要删除的工具", "失败", MessageBoxIcon.Error, true, 5000);
                    return;
                }
                if (MessageBoxX.Show("是否确定删除工具？", "提示", System.Windows.MessageBoxButton.YesNo, MessageBoxIcon.Question, DefaultButton.NoCancel) == System.Windows.MessageBoxResult.Yes)
                {
                    if (DataModel.Processmodel.selectedindex >= 0)
                    {
                        DataModel.Processmodel.Tools.RemoveAt(DataModel.Processmodel.selectedindex);
                    }
                    autoindex();
                }
            }
            catch { }
        }
        public void AddTool()
        {
            int index = DataModel.Processmodel.Tools.Count;
            DataModel.Processmodel.selectedindex = 0;
            InsertTool(index);

        }
        public void InsertTool(int index)
        {
            try
            {

                if (DataModel.Processmodel.selectedindex < 0)
                {

                    NoticeBox.Show($"请先选择需要插入工具的位置", "失败", MessageBoxIcon.Error, true, 5000);
                    return;
                }

                ToolModel tool = new ToolModel();
                DataModel.Processmodel.Tools.Insert(index, tool);
                DataModel.Processmodel.selectedindex = index;
                autoindex();

            }
            catch (Exception ex) { }
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

                    DataModel.Processmodel.Tools.Insert(
                        DataModel.Processmodel.selectedindex + 1,
                      New_Tool_Model(DataModel.Processmodel.Tools[DataModel.Processmodel.selectedindex])
                        );
                    autoindex();
                }
            }
            catch (Exception ex) { }
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
            t.DecodeBarcode = tool.DecodeBarcode;
            t.BarcodeStr = tool.BarcodeStr;
            t.BarCodeROI = New_ROI(tool.BarCodeROI);
            t.PositionDetect = tool.PositionDetect;
            t.ModelFileName = tool.ModelFileName;
            t.MinScore = tool.MinScore;
            t.ActualScore = tool.ActualScore;
            t.K = tool.K;
            t.InitX = tool.InitX;
            t.InitY = tool.InitY;
            t.ActualX = tool.ActualX;
            t.ActualY = tool.ActualY;
            t.DeltaX = tool.DeltaX;
            t.DeltaY = tool.DeltaY;
            t.DirectX = tool.DirectX;
            t.DirectY = tool.DirectY;
            t.InvertXY = tool.InvertXY;
            t.SendStatus = tool.SendStatus;
            t.InvertResult = tool.InvertResult;
            t.Allow_Delta= tool.Allow_Delta;
            //t.StatusColor = tool.StatusColor;
            t.PositionROI = New_ROI(tool.PositionROI);
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
        #endregion
        #region 配方保存加载

        public void SavePrjXmls()
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
                        HOperatorSet.GenEmptyObj(out tool.Image);
                        HOperatorSet.ReadImage(out tool.Image, jpgfilename);
                        #endregion
                        using (Bitmap bmp = (Bitmap)Bitmap.FromFile(jpgfilename))
                        {
                            tool.BitmapSource = Imaging.CreateBitmapSourceFromHBitmap(bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

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
            MyEventArgs myEventArgs = e as MyEventArgs;
            // NoticeBox.Show("接收图片");
            #region 图片接收

            //myEventArgs.Image



            HWindow hwindow = DataModel.Settingmodel.HWindow;
            hwindow.ClearWindow();
            hwindow.SetPart(0, 0, myEventArgs.Height - 1, myEventArgs.Width - 1);
            hwindow.DispObj(myEventArgs.Image);

            ToolModel tool = DataModel.Processmodel.Tools[DataModel.Processmodel.ToolIndex - 1];
            ClearTool(tool);
            tool.ToolStatus = ToolStatus.识别;
            bool result = true;
            if (tool.DecodeBarcode || tool.PositionDetect)
            {

                #region 读取二维码
                List<Reg.Barcode> barcodelist = null;
                ShapeMatch.Result shapmatchresult = new ShapeMatch.Result();

                if (tool.DecodeBarcode)
                {
                    try
                    {

                        barcodelist = tool.Reg.ReadBarcode(myEventArgs.Image, 1, tool.BarCodeROI.Row1, tool.BarCodeROI.Col1, tool.BarCodeROI.Row2, tool.BarCodeROI.Col2, true);
                        if (barcodelist.Count > 0)
                        {
                            tool.BarcodeStr = barcodelist[0].codestr;
                            NoticeBox.Show(barcodelist[0].codestr, "二维码读取成功", MessageBoxIcon.Success, true, 5000);
                            if (!tool.SendStatus)
                            {
                                SendMsgSoft(tool.BarcodeStr);
                            }
                            writeLog($"二维码读取成功:{barcodelist[0].codestr}");
                        }
                        else
                        {
                            tool.BarcodeStr = string.Empty;
                            writeLog($"二维码读取失败:{barcodelist[0].codestr}");
                        }
                    }
                    catch
                    {
                        ;
                    }
                }
                #endregion


                #region 读取位置
                /// -1:NG  -2:NG2 位置超过设定偏差范围  0:OK
                int  checkpositon = -1;
                if (tool.PositionDetect)
                {
                    try
                    {

                        tool.ShapeMatch.BasicData.matchcenter_X = tool.InitX;
                        tool.ShapeMatch.BasicData.matchcenter_Y = tool.InitY;
                        tool.ShapeMatch.BasicData.matchcenterX_Basic = tool.InitX;
                        tool.ShapeMatch.BasicData.matchcenterY_Basic = tool.InitY;
                        tool.ShapeMatch.BasicData.productcenter_X = tool.InitX;
                        tool.ShapeMatch.BasicData.productcenter_Y = tool.InitY;
                        shapmatchresult = tool.ShapeMatch.Match(myEventArgs.Image, 1, tool.PositionROI.Row1, tool.PositionROI.Col1, tool.PositionROI.Row2, tool.PositionROI.Col2);
                        var Result_Data = tool.ShapeMatch.Analysis_Result(shapmatchresult);
                        if (Result_Data != null)
                        {
                            tool.ActualX = Result_Data.X_actual;
                            tool.ActualY = Result_Data.Y_actual;
                            tool.ActualScore = Result_Data.score;
                            tool.DeltaX = Result_Data.deltaX_actual * tool.K / 1000;
                            tool.DeltaY = Result_Data.deltaY_actual * tool.K / 1000;

                            string sendstr = $"{(tool.DirectX ? -tool.DeltaX : tool.DeltaX).ToString("F2")},{(tool.DirectY ? -tool.DeltaY : tool.DeltaY).ToString("F2")},0.00";
                            if (tool.InvertXY)
                            {
                                sendstr = $"{(tool.DirectY ? -tool.DeltaY : tool.DeltaY).ToString("F2")},{(tool.DirectX ? -tool.DeltaX : tool.DeltaX).ToString("F2")},0.00";
                            }

                            if (tool.ActualScore >= tool.MinScore)
                            {
                                if (Math.Abs(tool.DeltaX) < tool.Allow_Delta && Math.Abs(tool.DeltaY) < tool.Allow_Delta)
                                {
                                    if (!tool.SendStatus)
                                    {
                                        SendMsgRobot($"{sendstr}");
                                    }
                                    checkpositon = 0;
                                }
                                else
                                {
                                    checkpositon = -2;
                                }
                                
                            }

                            //NoticeBox.Show("")
                        }
                    }
                    catch
                    {
                        ;
                    }

                    if (tool.DecodeBarcode)
                    {
                        tool.Reg.RefreshResult(barcodelist);
                    }
                }
                else
                {
                    checkpositon = 0;
                }
                #endregion


                if (((!tool.DecodeBarcode) || barcodelist.Count > 0)
                    && (checkpositon==0)

                    )
                {
                    if (DataModel.Settingmodel.ImageSaveSetting.SaveOK)
                    {
                        string savefilename = $"{DataModel.Settingmodel.ImageSaveSetting.ImageSaveDir}\\{DateTime.Now.ToString("yyyyMMdd")}\\OK\\{DateTime.Now.ToString("yyyyMMddHHmmssFFF")}.jpg";
                        string dir = Path.GetDirectoryName(savefilename);
                        if (!Directory.Exists(dir))
                        { Directory.CreateDirectory(dir); }
                        HOperatorSet.WriteImage(myEventArgs.Image, "jpg", 0, savefilename);

                    }
                    tool.ToolStatus = ToolStatus.OK;
                    if (tool.SendStatus)
                    {
                        if (!tool.InvertResult)
                        {
                            SendMsgRobot($"OK");
                        }
                        else
                        {
                            SendMsgRobot($"NG");
                        }
                    }
                }
                else
                {
                    if(checkpositon==-2)
                    {
                        tool.ToolStatus = ToolStatus.NG2;
                    }
                    else
                    {
                        tool.ToolStatus = ToolStatus.NG;
                    }

                    if (DataModel.Settingmodel.ImageSaveSetting.SaveNG)
                    {
                        string savefilename = $"{DataModel.Settingmodel.ImageSaveSetting.ImageSaveDir}\\{DateTime.Now.ToString("yyyyMMdd")}\\NG\\{DateTime.Now.ToString("yyyyMMddHHmmssFFF")}.jpg";

                        string dir = Path.GetDirectoryName(savefilename);
                        if (!Directory.Exists(dir))
                        { Directory.CreateDirectory(dir); }
                        HOperatorSet.WriteImage(myEventArgs.Image, "jpg", 0, savefilename);

                    }

                    if (tool.SendStatus)
                    {
                        if (!tool.InvertResult)
                        {
                            SendMsgRobot($"NG");
                        }
                        else
                        {
                            SendMsgRobot($"OK");
                        }
                    }
                }


            }

            //ToolModel tool = DataModel.Processmodel.Tools[DataModel.Processmodel.ToolIndex];
            //ClearTool(tool);
            //bool result = true;
            //if (tool.DecodeBarcode || tool.PositionDetect)
            //{

            //    #region 读取二维码
            //    if (tool.DecodeBarcode)
            //    {
            //        try
            //        {

            //            var r = tool.Reg.ReadBarcode(myEventArgs.Image, 1, tool.BarCodeROI.Row1, tool.BarCodeROI.Col1, tool.BarCodeROI.Row2, tool.BarCodeROI.Col2, true);
            //            if (r.Count > 0)
            //            {
            //                tool.BarcodeStr = r[0].codestr;
            //                NoticeBox.Show(r[0].codestr, "二维码读取成功", MessageBoxIcon.Success, true, 5000);
            //                //SendMsgPC(tool.BarcodeStr);
            //                SendMsgSoft(tool.BarcodeStr);
            //                writeLog($"二维码读取成功:{r[0].codestr}");
            //            }
            //            else
            //            {
            //                tool.BarcodeStr = string.Empty;
            //                writeLog($"二维码读取失败:{r[0].codestr}");
            //            }
            //        }
            //        catch
            //        {
            //            ;
            //        }
            //    }
            //    #endregion
            //    #region 检测位置
            //    if (tool.PositionDetect)
            //    {
            //        #region 读取位置
            //        try
            //        {
            //            var r = tool.ShapeMatch.Match(myEventArgs.Image, 1, tool.PositionROI.Row1, tool.PositionROI.Col1, tool.PositionROI.Row2, tool.PositionROI.Col2);
            //            var Result_Data = tool.ShapeMatch.Analysis_Result(r);
            //            if (Result_Data != null)
            //            {
            //                tool.ActualScore = Result_Data.score;
            //                tool.DeltaX = Result_Data.deltaX_actual;
            //                tool.DeltaY = Result_Data.deltaY_actual;

            //                string sendstr = $"{(tool.DirectX ? -tool.DeltaX : tool.DeltaX).ToString("F2")},{(tool.DirectY ? -tool.DeltaY : tool.DeltaY).ToString("F2")},0.00";
            //                if (tool.InvertXY)
            //                {
            //                    sendstr = $"{(tool.DirectY ? -tool.DeltaY : tool.DeltaY).ToString("F2")},{(tool.DirectX ? -tool.DeltaX : tool.DeltaX).ToString("F2")},0.00";
            //                }

            //                SendMsgRobot($"{sendstr}");

            //                //NoticeBox.Show("")
            //            }
            //        }
            //        catch
            //        {
            //            ;
            //        }
            //        #endregion

            //    }
            //    #endregion

            //}


            #endregion

        }
        #endregion



    }
}