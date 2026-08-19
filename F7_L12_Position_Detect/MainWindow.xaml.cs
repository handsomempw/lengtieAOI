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
using System.IO.Ports;
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
        private readonly object _passiveSerialLock = new object();
        private readonly StringBuilder _passiveSerialBuffer = new StringBuilder();
        private SerialPort _passiveSerialPort;
        private CancellationTokenSource _passiveSerialFrameCts;

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

        private void CompleteScannerReadFailure(
            string mode,
            DateTime scanTriggeredAtUtc,
            Status? hf800Status = null)
        {
            DateTime completedAtUtc = DateTime.UtcNow;
            int scannerTimeoutMs = vml.Main.DataModel.Settingmodel.HF800.receivetimeout;
            bool scannerTimedOut = string.Equals(mode, "HF800", StringComparison.Ordinal)
                && (hf800Status == Status.NULL
                    || (scannerTimeoutMs > 0
                        && (completedAtUtc - scanTriggeredAtUtc).TotalMilliseconds >= scannerTimeoutMs));
            string scannerStatus = scannerTimedOut ? "扫码器超时" : "扫码失败";
            vml.Main.DataModel.Processmodel.ScanCodeStatus = scannerStatus;
            vml.Main.RecordScannerReadFailure(scanTriggeredAtUtc, scannerStatus);
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
                            ClosePassiveSerialPort();
                            await Task.Delay(300, token);
                            continue;
                        }

                        if (!vml.Main.DataModel.Settingmodel.EnableSerialSoftwareTrigger)
                        {
                            EnsurePassiveSerialPortOpen();
                            await Task.Delay(300, token);
                            continue;
                        }

                        ClosePassiveSerialPort();

                        DateTime scanTriggeredAtUtc = DateTime.UtcNow;
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

                        DateTime barcodeReceivedAtUtc = DateTime.UtcNow;
                        var nowUtc = barcodeReceivedAtUtc;
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
                            await vml.Main.ProcessScanCodeWithMESAsync(
                                scanCode,
                                scanTriggeredAtUtc,
                                barcodeReceivedAtUtc);
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
                ClosePassiveSerialPort();
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
        /// 按主配置和当前 WPF 有效工作区确定本次启动使用的布局。自动模式覆盖 1366×768 显示器、任务栏占用和 Windows DPI 缩放后的等效小工作区。
        /// </summary>
        private void ApplyConfiguredUiLayout()
        {
            SettingModel setting = vml?.Main?.DataModel?.Settingmodel;
            if (setting == null)
            {
                return;
            }

            string configuredMode = NormalizeUiLayoutMode(setting.UiLayoutMode);
            setting.UiLayoutMode = configuredMode;

            Rect workArea = SystemParameters.WorkArea;
            bool compact = configuredMode == SettingModel.UiLayoutModeCompact
                || (configuredMode == SettingModel.UiLayoutModeAuto
                    && (workArea.Width <= 1366 || workArea.Height <= 768));

            WindowXCaption.SetHeight(this, compact ? 44 : 60);
            FontSize = compact ? 14 : 16;
            MainLayoutRoot.Margin = compact ? new Thickness(2) : new Thickness(3);
            ToolStripRow.MinHeight = compact ? 72 : 100;
            ToolStripRow.MaxHeight = compact ? 112 : 160;
            double compactReportWidth = Math.Max(280, Math.Min(320, workArea.Width * 0.28));
            ReportPanelColumn.Width = new GridLength(compact ? compactReportWidth : 300);
            InspectionBanner.Margin = compact ? new Thickness(0, 0, 0, 3) : new Thickness(0, 0, 0, 6);
            InspectionBanner.Padding = compact ? new Thickness(8, 3, 8, 3) : new Thickness(10, 5, 10, 5);
            ReportPanel.Margin = compact ? new Thickness(3) : new Thickness(5);
            ReportTitle.FontSize = compact ? 18 : 22;
            ReportTitle.Margin = compact ? new Thickness(0, 2, 0, 2) : new Thickness(0, 8, 0, 8);
            ReportSuccessLogScroll.MaxHeight = compact ? 110 : 180;
            ToolDetailPanel.Visibility = compact && workArea.Width < 1150
                ? Visibility.Collapsed
                : Visibility.Visible;

            Thickness groupMargin = compact ? new Thickness(0, 2, 0, 2) : new Thickness(0, 5, 0, 5);
            ProductInfoGroup.Margin = groupMargin;
            ReportConfigGroup.Margin = groupMargin;
            WorkerInfoGroup.Margin = groupMargin;
            ReportStatusGroup.Margin = groupMargin;
            TextElement.SetFontSize(FooterStatusPanel, compact ? 12 : 16);

            int configuredImageSize = Math.Max(40, setting.ImageSize);
            setting.EffectiveImageSize = compact ? Math.Min(configuredImageSize, 96) : configuredImageSize;
            ConfigureReportGridLayout(compact);

            string effectiveMode = compact ? SettingModel.UiLayoutModeCompact : SettingModel.UiLayoutModeStandard;
            vml.Main.writeLog(
                $"界面布局已应用: 配置={configuredMode}, 生效={effectiveMode}, 有效工作区={workArea.Width:0}×{workArea.Height:0}, " +
                $"报工栏宽度={ReportPanelColumn.Width.Value:0}, 工具详情={ToolDetailPanel.Visibility}",
                false);
        }

        /// <summary>
        /// 规范 XML 中的布局模式。现场配置支持“自动”“标准”“紧凑”，其他值回退自动模式并写入诊断日志。
        /// </summary>
        private string NormalizeUiLayoutMode(string configuredMode)
        {
            string mode = (configuredMode ?? string.Empty).Trim();
            if (mode == SettingModel.UiLayoutModeStandard || mode == SettingModel.UiLayoutModeCompact)
            {
                return mode;
            }

            if (mode != SettingModel.UiLayoutModeAuto && !string.IsNullOrEmpty(mode))
            {
                vml?.Main?.writeLog($"界面布局模式配置值“{mode}”无法识别，已按自动模式处理", false);
            }

            return SettingModel.UiLayoutModeAuto;
        }

        /// <summary>
        /// 紧凑模式采用三行双列报工配置，标准模式保留六行单列结构。字段绑定和保存事件在两种布局中保持一致。
        /// </summary>
        private void ConfigureReportGridLayout(bool compact)
        {
            ReportConfigGrid.RowDefinitions.Clear();
            ReportConfigGrid.ColumnDefinitions.Clear();

            int rowCount = compact ? 4 : 7;
            for (int i = 0; i < rowCount; i++)
            {
                ReportConfigGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            if (compact)
            {
                ReportConfigGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                ReportConfigGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ReportConfigGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                ReportConfigGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                SetGridCell(StationCodeLabel, 0, 0);
                SetGridCell(txtStationCode, 0, 1);
                SetGridCell(ProcedureCodeLabel, 0, 2);
                SetGridCell(txtProcedureCode, 0, 3);

                SetGridCell(ProcedureNameLabel, 1, 0);
                SetGridCell(txtProcedureName, 1, 1);
                SetGridCell(EquipmentNumberLabel, 1, 2);
                SetGridCell(txtEquipmentNumber, 1, 3);

                SetGridCell(AutoReportLabel, 2, 0);
                SetGridCell(chkEnableAutoReportWork, 2, 1);
                SetGridCell(ScanAutoLabel, 2, 2);
                SetGridCell(chkEnableScanAutoTriggerStages, 2, 3);
                SetGridCell(SerialSoftwareTriggerLabel, 3, 0);
                SetGridCell(chkEnableSerialSoftwareTrigger, 3, 1);
                Grid.SetColumnSpan(chkEnableSerialSoftwareTrigger, 3);
                return;
            }

            ReportConfigGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ReportConfigGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            SetGridCell(StationCodeLabel, 0, 0);
            SetGridCell(txtStationCode, 0, 1);
            SetGridCell(ProcedureNameLabel, 1, 0);
            SetGridCell(txtProcedureName, 1, 1);
            SetGridCell(ProcedureCodeLabel, 2, 0);
            SetGridCell(txtProcedureCode, 2, 1);
            SetGridCell(EquipmentNumberLabel, 3, 0);
            SetGridCell(txtEquipmentNumber, 3, 1);
            SetGridCell(AutoReportLabel, 4, 0);
            SetGridCell(chkEnableAutoReportWork, 4, 1);
            SetGridCell(ScanAutoLabel, 5, 0);
            SetGridCell(chkEnableScanAutoTriggerStages, 5, 1);
            SetGridCell(SerialSoftwareTriggerLabel, 6, 0);
            SetGridCell(chkEnableSerialSoftwareTrigger, 6, 1);
        }

        private static void SetGridCell(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetRowSpan(element, 1);
            Grid.SetColumnSpan(element, 1);
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
            ApplyConfiguredUiLayout();
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
                HObject image = null;
                try
                {
                    HOperatorSet.ReadImage(out image, ofd.FileName);
                    var r = reg.ReadBarcode(image, 1, 165, 1837, 650, 2445, true);
                }
                finally
                {
                    image?.Dispose();
                }
            }
        }

        /// <summary>
        /// 在物理 IO 触发模式下常开配置的 COM 口。该入口只设置接收参数和订阅数据事件，不向扫码器写入启动或停止指令。
        /// </summary>
        private void EnsurePassiveSerialPortOpen()
        {
            lock (_passiveSerialLock)
            {
                string portName = vml.Main.DataModel.Settingmodel.ScannerModel.PortName;
                if (_passiveSerialPort != null && _passiveSerialPort.IsOpen &&
                    string.Equals(_passiveSerialPort.PortName, portName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                ClosePassiveSerialPortCore();
                var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
                {
                    Encoding = Encoding.ASCII,
                    ReadTimeout = 500,
                    NewLine = "\r"
                };
                port.DataReceived += PassiveSerialPort_DataReceived;
                port.Open();
                _passiveSerialPort = port;
                Dispatcher.BeginInvoke(new Action(() =>
                    vml.Main.writeLog($"串口扫码器已进入物理IO被动接收模式: {portName}，软件不发送扫码触发指令", false)));
            }
        }

        /// <summary>
        /// 接收物理 IO 触发后由扫码器主动上报的串口数据。连续字节静默 100ms 后形成一条条码，兼容带 CR/LF 结束符和一次性完整上报。
        /// </summary>
        /// <param name="sender">当前常开的扫码器串口。</param>
        /// <param name="e">串口数据到达事件参数。</param>
        private void PassiveSerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                string received;
                lock (_passiveSerialLock)
                {
                    if (_passiveSerialPort == null || !_passiveSerialPort.IsOpen)
                    {
                        return;
                    }

                    received = _passiveSerialPort.ReadExisting();
                    _passiveSerialBuffer.Append(received);
                    _passiveSerialFrameCts?.Cancel();
                    _passiveSerialFrameCts = new CancellationTokenSource();
                    _ = CompletePassiveSerialFrameAfterIdleAsync(_passiveSerialFrameCts.Token);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                    vml?.Main?.writeLog($"串口被动接收异常: {ex.Message}", true)));
            }
        }

        /// <summary>
        /// 在串口字节静默窗口结束后提交完整条码，随后复用现有扫码去重和 MES 处理链路。
        /// </summary>
        /// <param name="token">新字节到达时取消上一轮组帧等待。</param>
        private async Task CompletePassiveSerialFrameAfterIdleAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(100, token);
                string scanCode;
                lock (_passiveSerialLock)
                {
                    scanCode = _passiveSerialBuffer.ToString().Replace("\r", "").Replace("\n", "").Trim();
                    _passiveSerialBuffer.Clear();
                }

                if (string.IsNullOrEmpty(scanCode))
                {
                    return;
                }

                DateTime receivedAtUtc = DateTime.UtcNow;
                if (receivedAtUtc < _serialScanWarmupUntilUtc ||
                    (string.Equals(scanCode, _lastAcceptedSerialScanCode, StringComparison.Ordinal) &&
                     (receivedAtUtc - _lastAcceptedSerialScanTimeUtc).TotalMilliseconds < 1500))
                {
                    return;
                }

                _lastAcceptedSerialScanCode = scanCode;
                _lastAcceptedSerialScanTimeUtc = receivedAtUtc;
                await Dispatcher.InvokeAsync(async () =>
                {
                    vml.Main.DataModel.Processmodel.Scannerstr = scanCode;
                    vml.Main.DataModel.Processmodel.SNList.Clear();
                    vml.Main.DataModel.Processmodel.Barcodes = string.Empty;
                    await vml.Main.ProcessScanCodeWithMESAsync(scanCode, receivedAtUtc, receivedAtUtc);
                }).Task.Unwrap();
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// 关闭物理 IO 被动接收串口，供模式切换和软件退出释放 COM 口；扫码业务状态保持不变。
        /// </summary>
        private void ClosePassiveSerialPort()
        {
            lock (_passiveSerialLock)
            {
                ClosePassiveSerialPortCore();
            }
        }

        private void ClosePassiveSerialPortCore()
        {
            _passiveSerialFrameCts?.Cancel();
            _passiveSerialFrameCts?.Dispose();
            _passiveSerialFrameCts = null;
            _passiveSerialBuffer.Clear();
            if (_passiveSerialPort == null)
            {
                return;
            }

            _passiveSerialPort.DataReceived -= PassiveSerialPort_DataReceived;
            if (_passiveSerialPort.IsOpen)
            {
                _passiveSerialPort.Close();
            }
            _passiveSerialPort.Dispose();
            _passiveSerialPort = null;
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
            if (!vml.Main.CanRunManualImageTest)
            {
                NoticeBox.Show("检测或扫码处理正在执行，请在本轮结束后再进行照片测试", "提示", MessageBoxIcon.Warning, true, 5000);
                return;
            }

            try
            {
                OpenFileDialog ofd = new OpenFileDialog();
                ofd.Filter = "*.jpg|*.jpg";
                if (ofd.ShowDialog() == true)
                {
                    HTuple W = new HTuple(), H = new HTuple();
                    HObject image = null;
                    try
                    {
                        HOperatorSet.ReadImage(out image, ofd.FileName);
                        HOperatorSet.GetImageSize(image, out W, out H);
                        if (!vml.Main.TryRunManualToolTest(
                            image,
                            (int)H,
                            (int)W,
                            vml.Main.DataModel.Processmodel.selectedindex,
                            out string message))
                        {
                            NoticeBox.Show(message, "提示", MessageBoxIcon.Warning, true, 5000);
                        }
                    }
                    finally
                    {
                        image?.Dispose();
                    }
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

            if (IsSerialScannerMode() && !vml.Main.DataModel.Settingmodel.EnableSerialSoftwareTrigger)
            {
                vml.Main.writeLog("当前为物理IO被动接收模式：软件不发送扫码触发指令，请通过物理IO触发扫码器", false);
                return;
            }

            DateTime scanTriggeredAtUtc = DateTime.UtcNow;
            vml.Main.DataModel.Processmodel.ScanCodeStatus = "正在读取扫码器";
            string scanCode = string.Empty;
            bool scanSuccess = false;
            Status? hf800Status = null;

            try
            {
                // 串口/网口读码可能阻塞较久；放后台线程避免 UI 假死。串口 SDK 路径加全局锁，避免 COM 独占冲突。
                await Task.Run(() =>
                {
                    if (string.Equals(mode, "HF800", StringComparison.Ordinal))
                    {
                        var r = vml.Main.DataModel.Settingmodel.HF800.Scanner();
                        scanCode = (r.value ?? string.Empty).Replace("\r", "").Replace("\n", "");
                        hf800Status = r.Status;
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
                CompleteScannerReadFailure(mode, scanTriggeredAtUtc);
                vml.Main.writeLog($"扫码测试取码异常: {ex.Message}", true);
                return;
            }

            DateTime barcodeReceivedAtUtc = DateTime.UtcNow;
            if (scanSuccess && !string.IsNullOrEmpty(scanCode))
            {
                vml.Main.DataModel.Processmodel.ScanCodeStatus = "条码已接收，正在解析SN";
                await vml.Main.ProcessScanCodeWithMESAsync(
                    scanCode,
                    scanTriggeredAtUtc,
                    barcodeReceivedAtUtc);
            }
            else
            {
                CompleteScannerReadFailure(mode, scanTriggeredAtUtc, hf800Status);
            }
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

        /// <summary>
        /// 保存串口扫码触发方式。后台监听按该值在软件指令取码和物理 IO 被动接收之间切换，扫码后的 MES 与检测流程保持一致。
        /// </summary>
        private void chkEnableSerialSoftwareTrigger_Changed(object sender, RoutedEventArgs e)
        {
            if (vml?.Main?.DataModel?.Settingmodel?.permission == true)
            {
                SaveConfigWithDelay();
                vml.Main.writeLog(
                    vml.Main.DataModel.Settingmodel.EnableSerialSoftwareTrigger
                        ? "串口扫码已切换为软件主动触发模式"
                        : "串口扫码已切换为物理IO被动接收模式，软件不发送扫码触发指令",
                    false);
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
