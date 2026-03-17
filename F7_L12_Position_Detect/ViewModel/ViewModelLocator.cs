/*
  In App.xaml:
  <Application.Resources>
      <vm:ViewModelLocator xmlns:vm="clr-namespace:F7_L12_Position_Detect"
                           x:Key="Locator" />
  </Application.Resources>
  
  In the View:
  DataContext="{Binding Source={StaticResource Locator}, Path=ViewModelName}"

  You can also use Blend to do all this with the tool's support.
  See http://www.galasoft.ch/mvvm
*/

using CommonServiceLocator;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Ioc;

namespace F7_L12_Position_Detect.ViewModel
{
    /// <summary>
    /// ViewModel 定位器：提供 XAML 绑定入口，并集中注册/获取各个 ViewModel 实例。
    /// </summary>
    public class ViewModelLocator
    {
        /// <summary>
        /// 初始化定位器并注册需要被容器管理的 ViewModel。
        /// </summary>
        public ViewModelLocator()
        {
            ServiceLocator.SetLocatorProvider(() => SimpleIoc.Default);

            ////if (ViewModelBase.IsInDesignModeStatic)
            ////{
            ////    // Create design time view services and models
            ////    SimpleIoc.Default.Register<IDataService, DesignDataService>();
            ////}
            ////else
            ////{
            ////    // Create run time view services and models
            ////    SimpleIoc.Default.Register<IDataService, DataService>();
            ////}

            SimpleIoc.Default.Register<MainViewModel>();
            SimpleIoc.Default.Register<PositionDetect.ViewModel.PositionDetectViewModel>();

        }

        /// <summary>
        /// 主窗口 ViewModel（供 XAML 绑定使用）。
        /// </summary>
        public MainViewModel Main
        {
            get
            {
                return ServiceLocator.Current.GetInstance<MainViewModel>();
            }
        }

        /// <summary>
        /// 位置检测模块 ViewModel（供 XAML 绑定使用）。
        /// </summary>
        public PositionDetect.ViewModel.PositionDetectViewModel PositionDetectViewModel
        {
            get
            {
                return ServiceLocator.Current.GetInstance<PositionDetect.ViewModel.PositionDetectViewModel>();
            }
        }

        /// <summary>
        /// 释放/清理 ViewModel（通常用于应用退出或需要重建容器的场景）。
        /// </summary>
        public static void Cleanup()
        {
            // TODO Clear the ViewModels
        }
    }
}