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
        /// 相机配置本轮启动的加载健康状态。相机配置曾经存在但主文件和备份均无法读取时，该状态阻止关闭软件时写回默认相机参数；
        /// 首装无配置文件时，该状态保持为 false，允许默认相机模板生成首份 <c>相机配置.xml</c>。
        /// </summary>
        public bool ConfigLoadFailed { get; private set; }

        #region 保存设置
        /// <summary>
        /// 保存相机配置。相机序列号和曝光时间属于现场调机参数，保存入口通过安全 XML 组件保护已有配置。
        /// </summary>
        /// <param name="filename">相机配置 XML 完整路径。</param>
        public void save_setting(string filename)
        {
            ConfigXmlSaveHelper.TrySave(filename, CameraModel, ConfigLoadFailed);
        }

        /// <summary>
        /// 加载相机配置。主文件损坏时尝试从同名备份恢复；恢复失败时使用默认模板初始化相机对象，并阻止本轮默认参数覆盖磁盘文件。
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
