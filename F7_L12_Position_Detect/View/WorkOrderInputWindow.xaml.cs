using F7_L12_Position_Detect.ViewModel;
using Panuon.WPF.UI;
using System;
using System.Windows;

namespace F7_L12_Position_Detect.View
{
    /// <summary>
    /// 工单号/批号输入窗口。
    /// </summary>
    public partial class WorkOrderInputWindow : WindowX
    {
        private readonly F7_L12_Position_Detect.ViewModel.ViewModelLocator vml;

        public WorkOrderInputWindow()
        {
            InitializeComponent();
            vml = (F7_L12_Position_Detect.ViewModel.ViewModelLocator)FindResource("Locator");
            WorkOrderTextBox.Text = vml.Main.DataModel.Settingmodel.ReportWorkConfig.CurrentWorkOrderCode ?? string.Empty;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string workOrderCode = WorkOrderTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(workOrderCode))
            {
                NoticeBox.Show("请输入工单号/批号", "提示", MessageBoxIcon.Warning, true, 5000);
                return;
            }

            vml.Main.DataModel.Settingmodel.ReportWorkConfig.CurrentWorkOrderCode = workOrderCode;
            vml.Main.SaveSettingModel();
            vml.Main.writeLog($"[工单号/批号] 已保存当前工单号/批号: {workOrderCode}");
            NoticeBox.Show("工单号/批号已保存", "成功", MessageBoxIcon.Success, true, 5000);
            DialogResult = true;
            Close();
        }
    }
}
