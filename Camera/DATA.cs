using GalaSoft.MvvmLight;
using HalconDotNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;
using System.Xml.Serialization;

namespace Camera
{
    public class DATA : ObservableObject
    {
        public CameraModel CameraModel { get; set; } = new CameraModel();

        /// <summary>
        /// 相机配置本轮启动的加载健康状态。无法从主文件和备份恢复时，该状态会阻止关闭流程用默认参数覆盖现场文件。
        /// </summary>
        private bool ConfigLoadFailed { get; set; }

        #region 保存设置

        /// <summary>
        /// 保存相机配置。序列号和曝光参数经临时文件校验后替换正式文件。
        /// </summary>
        /// <param name="filename">相机配置 XML 完整路径。</param>
        public void save_setting(string filename)
        {
            ConfigXmlSaveHelper.TrySave(filename, CameraModel, ConfigLoadFailed);
        }

        /// <summary>
        /// 加载相机配置。主文件损坏时自动使用可读备份，恢复失败时保留原文件供维护人员处理。
        /// </summary>
        /// <param name="filename">相机配置 XML 完整路径。</param>
        public void load_setting(string filename)
        {
            ConfigLoadResult<CameraModel> result = ConfigXmlSaveHelper.TryLoad(filename);
            CameraModel = result.Data;
            ConfigLoadFailed = result.LoadFailed;
        }
        #endregion

        public void init(string filename)
        {
            load_setting(filename);
            CameraModel.camera.init(CameraModel.CameraID);
            CameraModel.camera.bnOpen_Click();
            CameraModel.camera.bnStartGrab_Click();
            CameraModel.camera.Exposure = CameraModel.exposuretime;
            CameraModel.camera.bnGetParam_Click();
        }
        public void closing(string filename)
        {
            save_setting(filename);
            CameraModel.camera.bnClose_Click();

        }

        public bool disp(HObject image)
        {
            try
            {
                HTuple w = 0, h = 0;
                HOperatorSet.GetImageSize(image, out w, out h);

                //CameraModel.HWindow.HalconWindow.SetPart((int)0, (int)0, (int)(h - 1), (int)(w - 1));
                CameraModel.HWindow.HalconWindow.DispObj(image);
                return true;
            }
            catch { return false; }
        }


        //public void saveimage(HObject hoimage, string filename)
        //{
        //    //string filename = $"D:\\图片\\{DATA.name}\\{DateTime.Now.ToString("yyyy-MM-dd")}\\{(result ? "OK" : "NG")}\\{DateTime.Now.ToString("yyyyMMddHHmmssFFF")}.jpg";
        //    string path = Path.GetDirectoryName(filename);
        //    if (!Directory.Exists(path))
        //    {
        //        Directory.CreateDirectory(path);
        //    }
        //    HOperatorSet.WriteImage(hoimage, "jpg", 0, filename);
        //}


    }
}
