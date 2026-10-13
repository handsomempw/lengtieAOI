using F7_L12_Position_Detect.Model;
using F7_L12_Position_Detect.ViewModel;
using Panuon.WPF.UI;
using System.Windows;

namespace F7_L12_Position_Detect.View
{
    /// <summary>
    /// 母排组装的维护入口。编辑副本仅在校验和持久化成功后提交，取消时继续保留已保存配置。
    /// </summary>
    public partial class BusbarBindingSettingsWindow : WindowX
    {
        private readonly MainViewModel _main;
        private readonly BusbarBindingConfig _candidate;

        public BusbarBindingSettingsWindow(MainViewModel main)
        {
            InitializeComponent();
            _main = main;
            _candidate = (main.DataModel.Settingmodel.BusbarBinding ?? new BusbarBindingConfig()).Clone();
            DataContext = _candidate;
            RuntimeStatus.Text = "当前生效：母排自动绑定" + (_main.BusbarBindingEnabledAtStartup ? "开启" : "关闭");
        }

        /// <summary>提交维护配置，保存失败时保留窗口和输入，便于更正或再次保存。</summary>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!_main.TrySaveBusbarBindingConfiguration(_candidate, out string error))
            {
                NoticeBox.Show(error, "母排组装设置", MessageBoxIcon.Warning, true, 6000);
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
