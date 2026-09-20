using HalconDotNet;
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace F7_L12_Position_Detect
{
    public class Hobject2Bitmap
    {

        //public static void HobjectToBitmap(HObject ho_image, out Bitmap res)
        //{
        //    HTuple C = new HTuple();
        //    HOperatorSet.CountChannels(ho_image, out C);
        //    if (C == 1)
        //    {
        //        HobjectToBitmap24(ho_image, out res);
        //    }
        //    else if (C == 3)
        //    {
        //        HobjectToBitmap24(ho_image, out res);
        //    }
        //    else
        //    {
        //        HobjectToBitmap32(ho_image, out res);
        //    }
        //}




        /// <summary>
        /// 将 HALCON 图像复制为独立拥有像素内存的 24 位位图，供缩略图、调参预览和面积标注使用。
        /// 输入图像由调用方管理；中间 HALCON 图像在转换结束时释放，返回位图由调用方释放。
        /// </summary>
        /// <param name="ho_image">相机采集或视觉处理得到的灰度、彩色图像。</param>
        /// <param name="res24">可在 HALCON 中间图像释放后继续绘制和保存的位图。</param>
        public static void HobjectToBitmap24(HObject ho_image, out Bitmap res24)
        {
            res24 = null;
            HObject composedImage = null;
            HObject interImage = null;
            HTuple channels = null;
            HTuple pointer = null;
            HTuple type = null;
            HTuple width = null;
            HTuple height = null;
            try
            {
                if (ho_image == null)
                {
                    throw new ArgumentNullException(nameof(ho_image));
                }
                HOperatorSet.CountChannels(ho_image, out channels);
                HObject image = ho_image;
                if (channels.I == 1)
                {
                    HOperatorSet.Compose3(ho_image, ho_image, ho_image, out composedImage);
                    image = composedImage;
                }
                HOperatorSet.InterleaveChannels(image, out interImage, "rgb", "match", 255);
                HOperatorSet.GetImagePointer1(interImage, out pointer, out type, out width, out height);
                // 指针视图只在 HALCON 图像存活期间使用，复制后的位图独立持有像素。
                using (Bitmap view = new Bitmap(width.I / 3, height.I, width.I, PixelFormat.Format24bppRgb, pointer))
                {
                    res24 = new Bitmap(view);
                }
            }
            catch
            {
                res24?.Dispose();
                res24 = null;
                throw;
            }
            finally
            {
                height?.Dispose();
                width?.Dispose();
                type?.Dispose();
                pointer?.Dispose();
                channels?.Dispose();
                interImage?.Dispose();
                composedImage?.Dispose();
            }
        }
        /// <summary>
        /// hobject彩色图32位转bitmap
        /// </summary>
        /// <param name="ho_image"></param>
        /// <param name="res32"></param>
        public static void HobjectToBitmap32(HObject ho_image, out Bitmap res32)
        {
            HTuple type, width, height;
            //创建交错格式图像
            HOperatorSet.InterleaveChannels(ho_image, out HObject InterImage, "argb", "match", 255);
            //获取交错格式图像指针
            HOperatorSet.GetImagePointer1(InterImage, out HTuple Pointer, out type, out width, out height);
            IntPtr ptr = Pointer;
            res32 = new Bitmap(width / 4, height, width, PixelFormat.Format32bppRgb, ptr);
        }
        /// <summary>
        /// hobject灰度8位转bitmap
        /// </summary>
        /// <param name="ho_image"></param>
        /// <param name="res8"></param>
        public static void HobjectToBitmap8(HObject ho_image, out Bitmap res8)
        {
            HTuple type, width, height;
            HOperatorSet.GetImagePointer1(ho_image, out HTuple Pointer, out type, out width, out height);
            IntPtr ptr = Pointer;
            res8 = new Bitmap(width, height, width, PixelFormat.Format8bppIndexed, ptr);
            //设置灰度调色板
            ColorPalette cp = res8.Palette;
            for (int i = 0; i < 256; i++)
            {
                cp.Entries[i] = Color.FromArgb(i, i, i);
            }
            res8.Palette = cp;
        }

    }
}
