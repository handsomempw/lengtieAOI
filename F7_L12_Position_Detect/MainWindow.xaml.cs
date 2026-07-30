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
using System.Windows.Threading;

namespace F7_L12_Position_Detect
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : WindowX
    {
        ViewModelLocator vml = null;

        /// <summary>防止 SN 框 TextChanged 与赋值 cleanScanCode 形成重入，重复跑 MES。</summary>
        private bool _productSnTextHandling;

        /// <summary>串口 SDK（ScannerModel）全局互斥：同一 COM 只能一处打开，避免连点/并发读导致占用失败或无响应。</summary>
        private static readonly object ScannerSerialApiLock = new object();
        private CancellationTokenSource _serialScanListenerCts;
        private Task _serialScanListenerTask;
        private DateTime _serialScanWarmupUntilUtc = DateTime.MinValue;
        private string _lastAcceptedSerialScanCode = string.Empty;
        private DateTime _lastAcceptedSerialScanTimeUtc = DateTime.MinValue;

        private static bool IsKeyboardWedgeScannerMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return false;
            var m = mode.Trim();
            return string.Equals(m, "Keyboard", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(m, "HID", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(m, "键盘口", StringComparison.Ordinal);
        }

        private bool IsSerialScannerMode()
        {
            string mode = vml?.Main?.DataModel?.Settingmodel?.ScannerMode;
            return !string.Equals(mode, "HF800", StringComparison.OrdinalIgnoreCase)
                   && !IsKeyboardWedgeScannerMode(mode);
        }

        private void StartSerialScannerListener()
        {
            if (_serialScanListenerTask != null && !_serialScanListenerTask.IsCompleted)
                return;

            _serialScanListenerCts = new CancellationTokenSource();
            CancellationToken token = _serialScanListenerCts.Token;
            _serialScanWarmupUntilUtc = DateTime.UtcNow.AddSeconds(2);
            _lastAcceptedSerialScanCode = string.Empty;
            _lastAcceptedSerialScanTimeUtc = DateTime.MinValue;

            _serialScanListenerTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!IsSerialScannerMode())
                        {
                            await Task.Delay(300, token);
                            continue;
                        }

                        string scanCode = string.Empty;
                        bool isSuccess = false;

                        lock (ScannerSerialApiLock)
                        {
                            var r = vml.Main.DataModel.Settingmodel.ScannerModel.Scanner();
                            scanCode = (r.receivestring ?? string.Empty).Replace("\r", "").Replace("\n", "").Trim();
                            isSuccess = r.IsSuccess && !string.IsNullOrEmpty(scanCode);
                        }

                        if (!isSuccess)
                        {
                            await Task.Delay(80, token);
                            continue;
                        }

                        var nowUtc = DateTime.UtcNow;
                        // 刚启动监听时，丢弃预热窗口内的历史缓存码/噪声，避免“启动即自动扫一枪”。
                        if (nowUtc < _serialScanWarmupUntilUtc)
                        {
                            await Task.Delay(80, token);
                            continue;
                        }
                        // 同一码短时间重复上报去抖，避免设备重复回传导致重复流程。
                        if (string.Equals(scanCode, _lastAcceptedSerialScanCode, StringComparison.Ordinal) &&
                            (nowUtc - _lastAcceptedSerialScanTimeUtc).TotalMilliseconds < 1500)
                        {
                            await Task.Delay(80, token);
                            continue;
                        }
                        _lastAcceptedSerialScanCode = scanCode;
                        _lastAcceptedSerialScanTimeUtc = nowUtc;

                        await Dispatcher.InvokeAsync(async () =>
                        {
                            vml.Main.DataModel.Processmodel.Scannerstr = scanCode;
                            vml.Main.DataModel.Processmodel.SNList.Clear();
                            vml.Main.DataModel.Processmodel.Barcodes = string.Empty;
                            await vml.Main.ProcessScanCodeWithMESAsync(scanCode);
                        }).Task.Unwrap();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            vml?.Main?.writeLog($"串口扫码监听异常: {ex.Message}", true);
                        });
                        await Task.Delay(500, token);
                    }
                }
            }, token);
        }

        private void StopSerialScannerListener()
        {
            try
            {
                _serialScanListenerCts?.Cancel();
            }
            catch
            {
            }
        }

        /// <summary>
        /// 主窗口构造：初始化界面并获取全局 ViewModel 定位器。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            vml = this.FindResource("Locator") as ViewModelLocator;

        }

        /// <summary>
        /// 主窗口加载完成后的启动流程：加载配置/工程、初始化相机与通讯、启动 PLC 轮询等后台逻辑。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void WindowX_Loaded(object sender, RoutedEventArgs e)
        {
            vml.Main.DataModel.Processmodel.Tools.Add(new Model.Tool.ToolModel());
            vml.Main.DataModel.Processmodel.Tools.Add(new Model.Tool.ToolModel());
            vml.Main.DataModel.Processmodel.Tools.Add(new Model.Tool.ToolModel());
            vml.Main.BackupConfigXmlsBeforeLoad();
            vml.Main.LoadSettingModel();
            //vml.Main.LoadProcessmodel();
            vml.Main.Load_Prj();
            //vml.Main.ConnectPC();
            vml.Main.InitSoftServer();
            // vml.Main.InitRobotServer(); // 机器人服务器已禁用

            Sqlserver.MicrosoftSql_DATABASE.connectstr = $"Provider=SQLOLEDB.1;Password=1234;Persist Security Info=True;User ID=sa;Initial Catalog=扫码库;Data Source={vml.Main.DataModel.Settingmodel.SNlistServerIP}";

            // 先启动与相机无关的模块（便于现场查看PLC/日志）
            vml.Main.InitShm();
            vml.Main.PLC_Start();
            _ = Task.Run(async () => await vml.Main.RefreshWorkerInfoAsync());

            // 启动XML配置周期的点检倒计时、弹窗和普通产品作业门禁
            vml.Main.InitializeInspection();

            // 相机初始化与HWindow绑定：必须在UI线程执行，使用定时器避免UI线程死循环，10秒超时
            try
            {
                // 保持与原逻辑一致：初始化前稍等
                var initDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                initDelay.Tick += (s, _) =>
                {
                    initDelay.Stop();
                    try
                    {
                        // 先绑定相机控件窗口，再初始化相机（避免相机模块内部触发跨线程/窗口未就绪问题）
                        try { cameractr.refreshHwindow(); } catch { }

                        vml.Main.DataModel.Processmodel.CameraStatus = "初始化中";
                        vml.Main.DataModel.Settingmodel.camedata.init($"{Environment.CurrentDirectory}\\配置\\相机配置.xml");
                        vml.Main.start();
                        vml.Main.DataModel.Processmodel.CameraStatus = "等待回图";
                        vml.Main.writeLog("[启动] 相机初始化完成，等待回图", false);
                    }
                    catch (Exception ex)
                    {
                        vml.Main.DataModel.Processmodel.CameraStatus = "初始化失败";
                        vml.Main.writeError($"[启动] 相机初始化失败: {ex.Message}\n{ex.StackTrace}");
                        NoticeBox.Show($"相机初始化失败: {ex.Message}", "错误", MessageBoxIcon.Error, true, 5000);
                        return;
                    }

                    int attempts = 0;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    timer.Tick += (s2, __) =>
                    {
                        attempts++;
                        try
                        {
                            cameractr.refreshHwindow();
                            var hw = cameractr.GetHwindow()?.HalconWindow;
                            if (hw != null)
                            {
                                vml.Main.InitHwindow(hw);
                                timer.Stop();
                                return;
                            }
                        }
                        catch
                        {
                            // ignore and retry
                        }

                        if (attempts >= 10)
                        {
                            timer.Stop();
                            NoticeBox.Show("相机控件初始化超时/失败，请检查相机连接", "提示", MessageBoxIcon.Warning, true, 5000);
                        }
                    };
                    timer.Start();
                };
                initDelay.Start();
            }
            catch (Exception ex)
            {
                NoticeBox.Show($"相机初始化调度失败: {ex.Message}", "错误", MessageBoxIcon.Error, true, 5000);
            }

            // 串口模式常驻监听：物理按键触发后自动接收并走 MES 流程（HF800/键盘口模式自动跳过）。
            StartSerialScannerListener();

        }





        /// <summary>
        /// 打开系统设置窗口（需要在设置窗内进行权限控制）。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void Button_Click(object sender, RoutedEventArgs e)
        {
            SettingForm SF = new SettingForm();
            SF.ShowDialog();
        }

        /// <summary>
        /// 双击工具列表进入编辑：需要权限开启并确认后才允许修改工具参数。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">鼠标事件参数。</param>
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
                if (!vml.Main.SavePrjXmls())
                {
                    NoticeBox.Show("工具参数未落盘，请查看详细日志", "提示", MessageBoxIcon.Warning, true, 5000);
                }
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

        /// <summary>
        /// 倒计时栏「开始点检」：打开点检选择窗口，走完整OK/NG流程后由业务层重置倒计时。
        /// </summary>
        /// <param name="sender">触发按钮的界面元素。</param>
        /// <param name="e">WPF 点击事件参数。</param>
        private void StartManualInspection_Click(object sender, RoutedEventArgs e)
        {
            vml?.Main?.RequestManualInspection();
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

            StopSerialScannerListener();
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
            catch (Exception ex)
            {
                MessageBox.Show($"发生错误，请先选择需要测试的工具再选择测试照片:\r\n{ex.ToString()}");
                ;
            }
        }

        private async void scannertrig_Click(object sender, RoutedEventArgs e)
        {
            var mode = vml.Main.DataModel.Settingmodel.ScannerMode;

            if (IsKeyboardWedgeScannerMode(mode))
            {
                vml.Main.writeLog("当前为键盘口/HID 模式：不占串口。已聚焦「产品 SN」框，请直接扫码（勿依赖本按钮读串口）。", false);
                txtProductSN?.Focus();
                txtProductSN?.SelectAll();
                return;
            }

            string scanCode = string.Empty;
            bool scanSuccess = false;

            try
            {
                // 串口/网口读码可能阻塞较久；放后台线程避免 UI 假死。串口 SDK 路径加全局锁，避免 COM 独占冲突。
                await Task.Run(() =>
                {
                    if (string.Equals(mode, "HF800", StringComparison.Ordinal))
                    {
                        var r = vml.Main.DataModel.Settingmodel.HF800.Scanner();
                        scanCode = (r.value ?? string.Empty).Replace("\r", "").Replace("\n", "");
                        scanSuccess = (r.Status == Status.OK);
                    }
                    else
                    {
                        lock (ScannerSerialApiLock)
                        {
                            var r = vml.Main.DataModel.Settingmodel.ScannerModel.Scanner();
                            scanCode = (r.receivestring ?? string.Empty).Replace("\r", "").Replace("\n", "");
                            scanSuccess = r.IsSuccess;
                        }
                    }
                });
                vml.Main.DataModel.Processmodel.Scannerstr = scanCode;
                vml.Main.DataModel.Processmodel.SNList.Clear();
                vml.Main.DataModel.Processmodel.Barcodes = string.Empty;
            }
            catch (Exception ex)
            {
                vml.Main.writeLog($"扫码测试取码异常: {ex.Message}", true);
                return;
            }

            if (scanSuccess && !string.IsNullOrEmpty(scanCode))
                await vml.Main.ProcessScanCodeWithMESAsync(scanCode);
            else if (!scanSuccess)
                vml.Main.DataModel.Processmodel.ScanCodeStatus = "扫码失败";
        }

        private void ClearBarcodes_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBoxX.Show("是否确定清空累积二维码？", "提示", MessageBoxButton.YesNo, MessageBoxIcon.Question, DefaultButton.NoCancel) == MessageBoxResult.Yes)
            {
                vml.Main.DataModel.Processmodel.Barcodes = string.Empty;
            }
        }

        // 报工相关事件处理
        private void btnClearSN_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBoxX.Show("是否确定清空产品SN？", "提示", MessageBoxButton.YesNo, MessageBoxIcon.Question, DefaultButton.NoCancel) == MessageBoxResult.Yes)
            {
                vml.Main.ClearCurrentProductContext();
            }
        }

        private async void btnRefreshWorker_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 调用刷新上岗人员的方法
                await vml.Main.RefreshWorkerInfoAsync();
            }
            catch (Exception ex)
            {
                MessageBoxX.Show($"刷新上岗人员失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxIcon.Error);
            }
        }

        // 配置参数变更事件处理
        private void txtStationCode_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (vml?.Main?.DataModel?.Settingmodel?.permission == true)
            {
                // 延迟保存，避免频繁保存
                SaveConfigWithDelay();
            }
        }

        private void txtProcedureName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (vml?.Main?.DataModel?.Settingmodel?.permission == true)
            {
                SaveConfigWithDelay();
            }
        }

        private void txtProcedureCode_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (vml?.Main?.DataModel?.Settingmodel?.permission == true)
            {
                SaveConfigWithDelay();
            }
        }

        private void txtEquipmentNumber_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (vml?.Main?.DataModel?.Settingmodel?.permission == true)
            {
                SaveConfigWithDelay();
            }
        }

        private void chkEnableAutoReportWork_Checked(object sender, RoutedEventArgs e)
        {
            if (vml?.Main?.DataModel?.Settingmodel?.permission == true)
            {
                SaveConfigWithDelay();
            }
        }

        private void chkEnableAutoReportWork_Unchecked(object sender, RoutedEventArgs e)
        {
            if (vml?.Main?.DataModel?.Settingmodel?.permission == true)
            {
                SaveConfigWithDelay();
            }
        }

        private void chkEnableScanAutoTriggerStages_Checked(object sender, RoutedEventArgs e)
        {
            if (vml?.Main?.DataModel?.Settingmodel?.permission == true)
            {
                SaveConfigWithDelay();
            }
        }

        private void chkEnableScanAutoTriggerStages_Unchecked(object sender, RoutedEventArgs e)
        {
            if (vml?.Main?.DataModel?.Settingmodel?.permission == true)
            {
                SaveConfigWithDelay();
            }
        }

        // 产品SN输入框文本变化事件处理 - 检测扫码枪输入的换行符
        private async void txtProductSN_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_productSnTextHandling)
                return;

            try
            {
                if (sender is TextBox textBox)
                {
                    string inputText = textBox.Text;

                    // 检测是否包含换行符（扫码枪输入完成的标志）
                    if (inputText.Contains("\n") || inputText.Contains("\r"))
                    {
                        string cleanScanCode = inputText.Replace("\r", "").Replace("\n", "").Trim();

                        if (!string.IsNullOrEmpty(cleanScanCode))
                        {
                            _productSnTextHandling = true;
                            try
                            {
                                textBox.Text = cleanScanCode;
                                await ProcessScanCodeFromInput(cleanScanCode);
                            }
                            finally
                            {
                                _productSnTextHandling = false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                vml?.Main?.writeLog($"产品SN输入处理异常: {ex.Message}", true);
            }
        }

        /// <summary>
        /// 处理从输入框获取的扫码内容
        /// 执行编码转换和工序校验流程
        /// </summary>
        /// <param name="scanCode">扫码内容</param>
        private async Task ProcessScanCodeFromInput(string scanCode)
        {
            try
            {
                // 调用MainViewModel中的扫码处理方法
                if (vml?.Main != null)
                {
                    // 更新扫码相关数据
                    vml.Main.DataModel.Processmodel.Scannerstr = scanCode;
                    vml.Main.DataModel.Processmodel.SNList.Clear();
                    vml.Main.DataModel.Processmodel.Barcodes = string.Empty;

                    // 调用MES服务处理扫码：包括编码转换为SN和工序校验
                    await vml.Main.ProcessScanCodeWithMESAsync(scanCode);

                    vml.Main.writeLog($"自动处理扫码输入: {scanCode}", false);
                }
            }
            catch (Exception ex)
            {
                vml?.Main?.writeLog($"处理扫码输入异常: {ex.Message}", true);
            }
        }

        private System.Windows.Threading.DispatcherTimer _saveTimer;
        private void SaveConfigWithDelay()
        {
            // 如果定时器已存在，重置它
            if (_saveTimer != null)
            {
                _saveTimer.Stop();
            }
            else
            {
                _saveTimer = new System.Windows.Threading.DispatcherTimer();
                _saveTimer.Interval = TimeSpan.FromSeconds(2); // 2秒延迟
                _saveTimer.Tick += (s, e) =>
                {
                    _saveTimer.Stop();
                    vml.Main.SaveSettingModel();
                };
            }
            _saveTimer.Start();
        }
    }
}
