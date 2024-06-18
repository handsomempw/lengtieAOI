using F7_L12_Position_Detect.Log;
using GalaSoft.MvvmLight;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace F7_L12_Position_Detect.Model
{
    public class RecordModel : ObservableObject
    {
        [XmlElement("通讯过程数据")]
        public ObservableCollection<ConnectRecord> ComuncationLog { get; set; } = new ObservableCollection<ConnectRecord>();

        [XmlElement("日志")]
        public ObservableCollection<string> workLog { set; get; } = new ObservableCollection<string>();

        [XmlElement("错误")]
        public ObservableCollection<string> ErrorLog { set; get; } = new ObservableCollection<string>();
    }
}
