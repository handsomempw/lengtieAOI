using F7_L12_Position_Detect.Config;
using F7_L12_Position_Detect.Model;
using System;
using System.IO;
using System.Xml.Serialization;

namespace F7_L12_Position_Detect.ViewModel
{
    public partial class MainViewModel
    {
        /// <summary>软件本次启动采用的路径B组装开关，供生产界面显示实际运行模式。</summary>
        [XmlIgnore]
        public bool BusbarBindingEnabledAtStartup => BusbarConfig.Enabled;

        /// <summary>主界面显示启动快照；待重启的设置通过说明文字提示维护人员。</summary>
        [XmlIgnore]
        public string BusbarBindingStatusText => BusbarBindingEnabledAtStartup ? "本次开启" : "本次关闭";

        /// <summary>区分本次生效状态与下次启动配置，防止保存后误判设备已切换生产模式。</summary>
        [XmlIgnore]
        public string BusbarBindingConfigurationHint =>
            $"{BusbarBindingStatusText}；已保存：{(DataModel.Settingmodel.BusbarBinding?.Enabled == true ? "开启" : "关闭")}。设置保存后重启生效。";

        /// <summary>
        /// 维护权限有效时校验并保存绑定配置。运行快照固定到本次启动，失败时恢复原有内存配置，
        /// 使其他设置保存入口继续使用已确认参数。
        /// </summary>
        /// <param name="candidate">窗口编辑的绑定开关、工位和工序副本。</param>
        /// <param name="error">校验或保存失败的现场提示；成功时为空。</param>
        /// <returns>配置校验通过并完成安全落盘时返回 true。</returns>
        public bool TrySaveBusbarBindingConfiguration(BusbarBindingConfig candidate, out string error)
        {
            error = string.Empty;
            if (DataModel.Settingmodel.permission != true)
            {
                error = "请先解锁界面，再保存路径B组装设置";
                return false;
            }

            var updated = candidate?.Clone();
            error = updated == null ? "路径B组装设置为空" : updated.Validate();
            if (!string.IsNullOrEmpty(error)) return false;

            var previous = DataModel.Settingmodel.BusbarBinding;
            DataModel.Settingmodel.BusbarBinding = updated;
            if (!ConfigXmlSaveHelper.TrySave(
                Path.Combine(Environment.CurrentDirectory, "配置", "配置数据.xml"),
                DataModel.Settingmodel, _settingConfigLoadFailed, WriteDetailLog))
            {
                DataModel.Settingmodel.BusbarBinding = previous;
                error = "保存失败，请查看详细日志并检查配置目录";
                return false;
            }

            RaisePropertyChanged(() => BusbarBindingConfigurationHint);
            writeLog($"[路径B组装配置] 已保存，启用={updated.Enabled}，组装工位={updated.StationCode}，组装工序={updated.ProcedureCode}；重启后生效", true);
            return true;
        }
    }
}
