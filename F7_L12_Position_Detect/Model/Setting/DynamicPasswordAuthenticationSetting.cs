using System.Xml.Serialization;

namespace F7_L12_Position_Detect.Model.Setting
{
    /// <summary>
    /// 冷贴 AOI 编辑权限的动态密码服务参数。
    /// 设备编号继续读取报工配置，使认证服务与 MES 使用同一台设备标识。
    /// </summary>
    public class DynamicPasswordAuthenticationSetting
    {
        [XmlElement("启用")]
        public bool Enabled { get; set; } = true;

        [XmlElement("应用名称")]
        public string ApplicationName { get; set; } = "F7_L12_Position_Detect";

        [XmlElement("权限等级")]
        public int PrivilegeLevel { get; set; } = 5;

        [XmlElement("有效期分钟")]
        public int PeriodMinutes { get; set; } = 60;

        [XmlElement("接收人工号")]
        public string ReceiverNo { get; set; } = string.Empty;
    }
}
