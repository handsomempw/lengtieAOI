using Panuon.WPF.UI;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace F7_L12_Position_Detect
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        System.Threading.Mutex mutex;
        protected override void OnStartup(StartupEventArgs e)
        {
            string name = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name.ToString();
            bool ret;
            mutex = new System.Threading.Mutex(true, name, out ret);
            if (!ret)
            {
                MessageBox.Show("已有一个程序实例运行");
                Environment.Exit(0);
            }
        }

    }
}
