using GalaSoft.MvvmLight;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace F7_L12_Position_Detect.Model
{
    public class DataModel : ObservableObject
    {
        #region 数据模型
        [XmlElement("过程参数模型")]
        public Processmodel Processmodel { get; set; } = new Processmodel();

        [XmlElement("日志模型")]
        public RecordModel Recordmodel { get; set; } = new RecordModel();

        [XmlElement("配置模型")]
        public SettingModel Settingmodel { get; set; } = new SettingModel();

 

        #endregion



    }


    public class Kposition : ObservableObject
    {
        /// <summary>
        /// X坐标
        /// </summary>
        public int X { set; get; } = 0;
        /// <summary>
        /// Y坐标
        /// </summary>
        public int Y { set; get; } = 0;
    }

    public class KColor : ObservableObject
    {
        public byte R { set; get; } = 0;
        public byte G { set; get; } = 0;
        public byte B { set; get; } = 0;

    }


}
