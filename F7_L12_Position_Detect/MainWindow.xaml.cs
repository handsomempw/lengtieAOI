using F7_L12_Position_Detect.View;
using F7_L12_Position_Detect.ViewModel;
using HalconDotNet;
using Microsoft.Win32;
using Panuon.WPF.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Camera;
using F7_L12_Position_Detect.Model;
using Honeywell;

namespace F7_L12_Position_Detect
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : WindowX
    {
        ViewModelLocator vml = null;
        public MainWindow()
        {
            InitializeComponent();

            vml = this.FindResource("Locator") as ViewModelLocator;

        }

        private void WindowX_Loaded(object sender, RoutedEventArgs e)
        {
            vml.Main.DataModel.Processmodel.Tools.Add(new Model.Tool.ToolModel());
            vml.Main.DataModel.Processmodel.Tools.Add(new Model.Tool.ToolModel());
            vml.Main.DataModel.Processmodel.Tools.Add(new Model.Tool.ToolModel());
            vml.Main.LoadSettingModel();
            //vml.Main.LoadProcessmodel();
            vml.Main.Load_Prj();
            //vml.Main.ConnectPC();
            vml.Main.InitSoftServer();
            vml.Main.InitRobotServer();

            Sqlserver.MicrosoftSql_DATABASE.connectstr = $"Provider=SQLOLEDB.1;Password=1234;Persist Security Info=True;User ID=sa;Initial Catalog=扫码库;Data Source={vml.Main.DataModel.Settingmodel.SNlistServerIP}";



            App.Current.Dispatcher.BeginInvoke((Action)(() =>
            {
                Thread.Sleep(200);

                vml.Main.DataModel.Settingmodel.camedata.init($"{Environment.CurrentDirectory}\\配置\\相机配置.xml");
                vml.Main.start();

                while (true)
                {
                    try
                    {
                        Thread.Sleep(1000);
                        cameractr.refreshHwindow();
                        //vml.Main.InitHwindow(Hwindow.HalconWindow);
                        //vml.Main.InitHwindow(vml.Main.DataModel.Settingmodel.camedata.CameraModel.HWindow.HalconWindow);
                        vml.Main.InitHwindow(cameractr.GetHwindow().HalconWindow);
                        break;

                    }
                    catch (Exception ex) { continue; }
                }
            }));

            vml.Main.InitShm();

            vml.Main.PLC_Start();

        }





        private void Button_Click(object sender, RoutedEventArgs e)
        {
            SettingForm SF = new SettingForm();
            SF.ShowDialog();
        }

        private void ListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!vml.Main.DataModel.Settingmodel.permission)
            {
                NoticeBox.Show("请先打开权限，再进行编辑", "提示", MessageBoxIcon.Warning, true, 5000);
                return;
            }
            if (MessageBoxX.Show("是否确定打开编辑工具？", "提示", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                vml.Main.DataModel.Processmodel.tool = vml.Main.DataModel.Processmodel.Tools[vml.Main.DataModel.Processmodel.selectedindex];
                vml.Main.LoadBitmapSource();
                //vml.Main.DataModel.Processmodel.tool = @vml.Main.DataModel.Processmodel.Tools[vml.Main.DataModel.Processmodel.selectedindex];
                SettingForm settingForm = new SettingForm();
                //settingForm.Topmost = true;
                settingForm.ShowDialog();
                //settingForm.Show();


            }
        }

        private void ChangePrj_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                vml.Main.Load_Prj(vml.Main.DataModel.Settingmodel.Prjs[vml.Main.DataModel.Settingmodel.prjselected]);
                vml.Main.InitHwindow(cameractr.GetHwindow().HalconWindow);
                vml.Main.InitShm();

            }
            catch (Exception ex)
            {
                NoticeBox.Show($"工程加载错误:{ex.ToString()}", "错误", MessageBoxIcon.Error, true, 5000);
            }
        }

        private void SettingBtn_Click(object sender, RoutedEventArgs e)
        {

        }

        private void permissionbtn_Click(object sender, RoutedEventArgs e)
        {
            if (vml.Main.DataModel.Settingmodel.permission)
            {
                vml.Main.DataModel.Settingmodel.permission = false;
                return;
            }
            InputPassword ip = new InputPassword("faracheck");
            if (ip.ShowDialog() == true)
            {
                vml.Main.DataModel.Settingmodel.permission = true;
            }
        }

        private void WindowX_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (MessageBoxX.Show("是否确定关闭软件？", "提示", MessageBoxButton.YesNo, MessageBoxIcon.Question, DefaultButton.NoCancel) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            vml.Main.DataModel.Settingmodel.camedata.closing($"{Environment.CurrentDirectory}\\配置\\相机配置.xml");
            vml.Main.SaveProcessmodel();
            vml.Main.SaveRecordModel();
            vml.Main.SaveSettingModel();
            vml.Main.SavePrjXmls();

            //vml.Main.close_device();
            Environment.Exit(0);
        }

        private void Send2Soft_btn_Click(object sender, RoutedEventArgs e)
        {
            vml.Main.SendMsgSoft($"softtest{DateTime.Now}");
        }

        private void Send2Robot_btn_Click(object sender, RoutedEventArgs e)
        {
            vml.Main.SendMsgRobot($"robottest{DateTime.Now}");
        }

        QRCode.Reg reg = new QRCode.Reg();

        private void Barcodetest_btn_Click(object sender, RoutedEventArgs e)
        {
            reg.Hwindow = Hwindow.HalconWindow;

            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = "*.jpg|*.jpg";
            if (ofd.ShowDialog() == true)
            {
                HObject ho_Image;
                HOperatorSet.GenEmptyObj(out ho_Image);
                HOperatorSet.ReadImage(out ho_Image, ofd.FileName);
                var r = reg.ReadBarcode(ho_Image, 1, 165, 1837, 650, 2445, true);
            }
        }

        private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (vml.Main.DataModel.Processmodel.selectedindex >= 0)
            {
                vml.Main.DataModel.Processmodel.tool = vml.Main.DataModel.Processmodel.Tools[vml.Main.DataModel.Processmodel.selectedindex];
            }
        }

        private void tooltest_Click(object sender, RoutedEventArgs e)
        {

            try
            {
                OpenFileDialog ofd = new OpenFileDialog();
                ofd.Filter = "*.jpg|*.jpg";
                if (ofd.ShowDialog() == true)
                {
                    HTuple W = new HTuple(), H = new HTuple();

                    HObject image;
                    HOperatorSet.GenEmptyObj(out image);
                    HOperatorSet.ReadImage(out image, ofd.FileName);
                    HOperatorSet.GetImageSize(image, out W, out H);
                    vml.Main.DataModel.Processmodel.ToolIndex = vml.Main.DataModel.Processmodel.selectedindex;
                    vml.Main.DataModel.Processmodel.RCMD = vml.Main.DataModel.Processmodel.Tools[vml.Main.DataModel.Processmodel.selectedindex].Command;
                    vml.Main.OnReceiveProcess(image, (int)H, (int)W);
                }
            }
            catch (Exception ex){
                MessageBox.Show($"发生错误，请先选择需要测试的工具再选择测试照片:\r\n{ex.ToString()}");
                ; }
        }

        private void scannertrig_Click(object sender, RoutedEventArgs e)
        {
            if ( vml.Main. DataModel.Settingmodel.ScannerMode == "HF800")
            {
                var r = vml.Main.DataModel.Settingmodel.HF800.Scanner();
                vml.Main.DataModel.Processmodel.Scannerstr = r.value.Replace("\r", "").Replace("\n", "");
                vml.Main.DataModel.Processmodel.SNList.Clear();
                vml.Main.DataModel.Processmodel.Barcodes = string.Empty;
                if (r.Status != Status.OK)
                {
                    //modbusTcp.Write((DataModel.Settingmodel.AddressStart + 1).ToString(), (UInt16)2);
                }
                else
                {
                    //SendMsgRobot("DOK");
                   // ClearTools();
                }
            }
            else
            {
                var r = vml.Main.DataModel.Settingmodel.ScannerModel.Scanner();
                vml.Main.DataModel.Processmodel.Scannerstr = r.receivestring.Replace("\r", "").Replace("\n", "");
                if (!r.IsSuccess)
                {
                 //   modbusTcp.Write((DataModel.Settingmodel.AddressStart + 1).ToString(), (UInt16)2);
                }
                else
                {
                    //SendMsgRobot("DOK");
                    //ClearTools();
                }
            }
        }
    }
}
