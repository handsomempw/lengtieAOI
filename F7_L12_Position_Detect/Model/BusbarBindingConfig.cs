using System.Xml.Serialization;

namespace F7_L12_Position_Detect.Model
{
    /// <summary>
    /// 路径B单母排组装取号的设备配置。启动时读取快照，维护人员保存后重启生效。
    /// 组装工位和工序服务 SerializedProductAssemble 与组装报工，视觉检测继续使用报工配置中的 AOI 工序。
    /// </summary>
    public class BusbarBindingConfig
    {
        /// <summary>启用路径B单母排自动组装；关闭时沿用成品 SN 扫码流程。</summary>
        [XmlElement("启用母排自动绑定")]
        public bool Enabled { get; set; }

        /// <summary>MES 路径B组装所属标准工序码，例如现场验证的 00025。</summary>
        [XmlElement("绑定工序码")]
        public string ProcedureCode { get; set; } = string.Empty;

        /// <summary>MES 路径B组装工位号；上岗工号从该工位实时上岗记录获取。</summary>
        [XmlElement("绑定工位号")]
        public string StationCode { get; set; } = string.Empty;

        /// <summary>生成独立的运行或编辑副本，隔离输入中的配置和当前生产流程。</summary>
        public BusbarBindingConfig Clone()
        {
            return new BusbarBindingConfig
            {
                Enabled = Enabled,
                ProcedureCode = (ProcedureCode ?? string.Empty).Trim(),
                StationCode = (StationCode ?? string.Empty).Trim()
            };
        }

        /// <summary>检查路径B启用模式需要的 MES 路由信息；空字符串表示完整。</summary>
        public string Validate()
        {
            if (!Enabled) return string.Empty;
            if (string.IsNullOrWhiteSpace(ProcedureCode)) return "请填写路径B组装工序码";
            if (string.IsNullOrWhiteSpace(StationCode)) return "请填写路径B组装工位号";
            return string.Empty;
        }
    }
}
