using F7_L12_Position_Detect.Model.Tool;
using F7_L12_Position_Detect.ViewModel;
using Microsoft.Win32;
using Panuon.WPF.UI;
using System;
using System.Collections.Generic;
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
using System.Drawing;
using System.Windows.Interop;
using System.Diagnostics;
using System.IO;
using F7_L12_Position_Detect.Model;

namespace F7_L12_Position_Detect
{




    /// <summary>
    /// SettingForm.xaml 的交互逻辑
    /// </summary>
    public partial class SettingForm : WindowX
    {
        ViewModelLocator vml;
        ToolModel t = null;


        bool selectbroi = false;
        bool selectproi = false;


        public SettingForm()
        {
            InitializeComponent();
            vml = this.FindResource("Locator") as ViewModelLocator;
            t = (ToolModel)DataContext;
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenFileDialog ofd = new OpenFileDialog();
                ofd.Filter = "*.jpg|*.jpg";
                if (ofd.ShowDialog() == true)
                {
                    string filename = ofd.FileName;
                    using (Bitmap bmp = (Bitmap)Bitmap.FromFile(filename))
                    {
                        t.BitmapSource = Imaging.CreateBitmapSourceFromHBitmap(bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                        // vml.Main.DataModel.Processmodel.tool.BitmapSource = Imaging.CreateBitmapSourceFromHBitmap(bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    }
                    string newfilename = $"{vml.Main.DataModel.Settingmodel.Prjdir}\\{vml.Main.DataModel.Settingmodel.Name}\\Tool{t.Index}.jpg";

                    File.Copy(filename, newfilename, true);


                    zoom_all();
                }
            }catch(Exception ex)
            {
               NoticeBox.Show(ex.Message,"错误",MessageBoxIcon.Error,true,10000);
            }

        }

        #region 画矩形

        private System.Windows.Point _downPoint;
        private bool _started = false;
        private void rectange_MouseMove(object sender, MouseEventArgs e)
        {
            if (_started)
            {
                var point = e.GetPosition(show_image_canvas);
                var rect = new Rect(_downPoint, point);
                if (selectbroi)
                {
                    RectangleBarcode.Margin = new Thickness(rect.Left, rect.Top, 0, 0);
                    RectangleBarcode.Width = rect.Width;
                    RectangleBarcode.Height = rect.Height;
                }
                else if (selectproi)
                {
                    RectanglePosition.Margin = new Thickness(rect.Left, rect.Top, 0, 0);
                    RectanglePosition.Width = rect.Width;
                    RectanglePosition.Height = rect.Height;
                }
            }
        }

        private void rectange_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (selectproi || selectbroi)
            {
                _downPoint = e.GetPosition(show_image_canvas);
                _started = true;
            }
        }

        private void rectange_MouseUp(object sender, MouseButtonEventArgs e)
        {

            if (selectbroi)
            {

                if (RectangleBarcode.Width == 0 || RectangleBarcode.Height == 0) { return; }
                if (double.IsNaN(RectangleBarcode.Width) || double.IsNaN(RectangleBarcode.Height)) { return; }
                _started = false;
                t.BarCodeROI.Row1 = (int)RectangleBarcode.Margin.Top;
                t.BarCodeROI.Col1 = (int)RectangleBarcode.Margin.Left;
                t.BarCodeROI.Row2 = (int)(RectangleBarcode.Margin.Top + RectangleBarcode.Height);
                t.BarCodeROI.Col2 = (int)(RectangleBarcode.Margin.Left + RectangleBarcode.Width);
                selectbroi = false;
                SelectBarcodeROI.Background = selectproi ? System.Windows.Media.Brushes.OrangeRed : System.Windows.Media.Brushes.Gray;
            }
            else if (selectproi)
            {
                if (RectanglePosition.Width == 0 || RectanglePosition.Height == 0) { return; }
                if (double.IsNaN(RectanglePosition.Width) || double.IsNaN(RectanglePosition.Height)) { return; }
                _started = false;
                t.PositionROI.Row1 = (int)RectanglePosition.Margin.Top;
                t.PositionROI.Col1 = (int)RectanglePosition.Margin.Left;
                t.PositionROI.Row2 = (int)(RectanglePosition.Margin.Top + RectanglePosition.Height);
                t.PositionROI.Col2 = (int)(RectanglePosition.Margin.Left + RectanglePosition.Width);
                selectproi = false;
                SelectPositionROI.Background = selectproi ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.Gray;
            }

        }
        #endregion

        private bool mouseDown = false;
        private System.Windows.Point mouseXY;
        int X, Y = 0;
        private void Show_image_ctr_MouseMove(object sender, MouseEventArgs e)
        {
            if (_started)
            {
                var group = show_image_ctr.FindResource("Imageview") as TransformGroup;
                var transform = group.Children[0] as ScaleTransform;
                System.Windows.Controls.Image img = sender as System.Windows.Controls.Image;
                X = Convert.ToInt32(e.GetPosition(show_image_ctr).X);
                Y = Convert.ToInt32(e.GetPosition(img).Y);
            }
        }
        private void ContentControl_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Debug.WriteLine("Left");
            rectange_MouseDown(sender, e);

        }
        private void ContentControl_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Debug.WriteLine("mouseup");
            rectange_MouseUp(sender, e);

        }

        private void ContentControl_MouseMove(object sender, MouseEventArgs e)
        {
            Debug.WriteLine("mousemove");
            rectange_MouseMove(sender, e);

        }
        private void Domousemove(ContentControl img, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            var group = show_image_ctr.FindResource("Imageview") as TransformGroup;
            var transform = group.Children[1] as TranslateTransform;
            var position = e.GetPosition(img);
            transform.X -= mouseXY.X - position.X;
            transform.Y -= mouseXY.Y - position.Y;
            mouseXY = position;
        }
        private void ContentControl_MouseWheel(object sender, MouseWheelEventArgs e)
        {

        }
        private void DowheelZoom(TransformGroup group, System.Windows.Point point, double delta)
        {
            var pointToContent = group.Inverse.Transform(point);
            var transform = group.Children[0] as ScaleTransform;
            if (transform.ScaleX + delta < 0.1) return;
            transform.ScaleX += delta;
            transform.ScaleY += delta;
            var transform1 = group.Children[1] as TranslateTransform;
            transform1.X = -1 * ((pointToContent.X * transform.ScaleX) - point.X);
            transform1.Y = -1 * ((pointToContent.Y * transform.ScaleY) - point.Y);

        }

        public void zoom_all()
        {
            try
            {
                zoom_all(t.BitmapSource.Width, t.BitmapSource.Height);
                //zoom_all(vml.Main.DataModel.Processmodel.tool.BitmapSource.Width, vml.Main.DataModel.Processmodel.tool.BitmapSource.Height);
            }
            catch (Exception ex) {; }
        }
        public void zoom_all(double w2, double h2)
        {
            try
            {
                TransformGroup group = show_image_ctr.FindResource("Imageview") as TransformGroup;
                var transform = group.Children[0] as ScaleTransform;
                double w1 = BackFrame.ActualWidth, h1 = BackFrame.ActualHeight;
                //double w2 = show_image_ctr.ActualWidth, h2 = show_image_ctr.ActualHeight;
                double scaleX = w1 / w2, scaleY = h1 / h2;
                double scale = Math.Min(scaleX, scaleY);
                transform.CenterX = 0;
                transform.CenterY = 0;
                transform.ScaleX = scale;
                transform.ScaleY = scale;


                move_to_center(group, scale, w1, h1);
            }
            catch {; }
        }

        private void WindowX_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            zoom_all();
        }

        private void SelectPositionROI_Click(object sender, RoutedEventArgs e)
        {
            selectproi = !selectproi;

            SelectPositionROI.Background = selectproi ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.Gray;
        }

        private void SelectBarcodeROI_Click(object sender, RoutedEventArgs e)
        {
            selectbroi = !selectbroi;
            SelectBarcodeROI.Background = selectbroi ? System.Windows.Media.Brushes.OrangeRed : System.Windows.Media.Brushes.Gray;

        }

        private void move_to_center(TransformGroup group, double scale, double w1, double h1)
        {
            double w3 = show_image_ctr.ActualWidth * scale, h3 = show_image_ctr.ActualHeight * scale;
            double distanceX = (w1 - w3) / 2;
            double distanceY = (h1 - h3) / 2;
            var position = show_image_ctr.TranslatePoint(new System.Windows.Point(0, 0), (UIElement)show_image_ctr.Parent);
            var transform1 = group.Children[1] as TranslateTransform;
            transform1.X = distanceX;
            transform1.Y = distanceY;
        }

    }
}
