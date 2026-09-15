using F7_L12_Position_Detect.ViewModel;
using Panuon.WPF.UI;
using System.Windows;

namespace F7_L12_Position_Detect.View
{
    /// <summary>
    /// 当前工程点检标准件配置窗口。窗口只编辑候选值，保存成功后由主业务模型切换生产生效配置。
    /// </summary>
    public partial class InspectionRecipeConfigWindow : WindowX
    {
        private readonly ViewModelLocator _vml;

        public InspectionRecipeConfigWindow()
        {
            InitializeComponent();
            _vml = Application.Current.FindResource("Locator") as ViewModelLocator;
            DataContext = _vml?.Main;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_vml?.Main?.SaveInspectionRecipeConfiguration() == true)
            {
                DialogResult = true;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
