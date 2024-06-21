using Camera;
using F7_L12_Position_Detect.ViewModel;
using HalconDotNet;
using Microsoft.Win32;
using Panuon.WPF.UI;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace F7_L12_Position_Detect.View
{
    /// <summary>
    /// ModelWindow.xaml 的交互逻辑
    /// </summary>
    public partial class ModelWindow : WindowX
    {
        ViewModelLocator vml = null;
        //public ModelWindow(string filename)
        //{
        //    InitializeComponent();
        //    vml = (ViewModelLocator)this.FindResource("Locator");
        //    HOperatorSet.GenEmptyObj(out vml.PositionDetectViewModel.DATA.image);
        //    HOperatorSet.ReadImage(out vml.PositionDetectViewModel.DATA.image, filename);

        //}


        //public ModelWindow(string filename,string Modelfilename)
        //{
        //    InitializeComponent();
        //    vml = (ViewModelLocator)this.FindResource("Locator");
        //    HOperatorSet.GenEmptyObj(out vml.PositionDetectViewModel.DATA.image);
        //    HOperatorSet.ReadImage(out vml.PositionDetectViewModel.DATA.image, filename);
        //    vml.PositionDetectViewModel.DATA.modelfilename = Modelfilename;
        //}

        //SettingForm settingForm = null;

        public ModelWindow(HObject image, string Modelfilename)
        {
            InitializeComponent();
            vml = (ViewModelLocator)this.FindResource("Locator");
            HOperatorSet.GenEmptyObj(out vml.PositionDetectViewModel.DATA.image);
            HOperatorSet.CopyImage(image, out vml.PositionDetectViewModel.DATA.image);
            vml.PositionDetectViewModel.DATA.modelfilename = Modelfilename;
            //settingForm = sf;
            //sf.Hide();
        }

        private void WindowX_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            //settingForm.Show();
        }

        private void getinitpositionbymodelimage_Click(object sender, RoutedEventArgs e)
        {
            try
            {

                //int w = (int)vml.Main.DataModel.Processmodel.tool.BitmapSource.Width;
                //int h = (int)vml.Main.DataModel.Processmodel.tool.BitmapSource.Height;
                //var r = vml.Main.DataModel.Processmodel.tool.ShapeMatch.Match(vml.Main.DataModel.Processmodel.tool.Image, 1, 0, h - 1, 0, w - 1);
                //vml.Main.DataModel.Processmodel.tool.InitX = r.points[0].column;
                //vml.Main.DataModel.Processmodel.tool.InitY = r.points[1].row;

                var shapmatchresult = vml.Main.DataModel.Processmodel.tool.ShapeMatch.Match(
                    vml.Main.DataModel.Processmodel.tool.Image, 1,
                    vml.Main.DataModel.Processmodel.tool.PositionROI.Row1,
                    vml.Main.DataModel.Processmodel.tool.PositionROI.Col1,
                    vml.Main.DataModel.Processmodel.tool.PositionROI.Row2,
                    vml.Main.DataModel.Processmodel.tool.PositionROI.Col2);

                var Result_Data = vml.Main.DataModel.Processmodel.tool.ShapeMatch.Analysis_Result(shapmatchresult);
                if (Result_Data != null)
                {
                    vml.Main.DataModel.Processmodel.tool.InitX = Result_Data.X_actual;
                    vml.Main.DataModel.Processmodel.tool.InitY = Result_Data.Y_actual;
                    MessageBox.Show("标定成功");
                }
            }
            catch (Exception ex) { }
        }

        private void getinitpositonfromimagefile_Click(object sender, RoutedEventArgs e)
        {
            try
            {

                OpenFileDialog ofd = new OpenFileDialog();
                ofd.Filter = "*.jpg|*.jpg";
                if (ofd.ShowDialog() == true)
                {

                    HObject image = null;
                    HOperatorSet.GenEmptyObj(out image);
                    HOperatorSet.ReadImage(out image, ofd.FileName);

                    var shapmatchresult = vml.Main.DataModel.Processmodel.tool.ShapeMatch.Match(
                   image, 1,
                    vml.Main.DataModel.Processmodel.tool.PositionROI.Row1,
                    vml.Main.DataModel.Processmodel.tool.PositionROI.Col1,
                    vml.Main.DataModel.Processmodel.tool.PositionROI.Row2,
                    vml.Main.DataModel.Processmodel.tool.PositionROI.Col2);

                    var Result_Data = vml.Main.DataModel.Processmodel.tool.ShapeMatch.Analysis_Result(shapmatchresult);
                    if (Result_Data != null)
                    {
                        vml.Main.DataModel.Processmodel.tool.InitX = Result_Data.X_actual;
                        vml.Main.DataModel.Processmodel.tool.InitY = Result_Data.Y_actual;
                        MessageBox.Show("标定成功");
                    }
                }

            }
            catch (Exception ex) { }
        }
    }
}
