using F7_L12_Position_Detect.Config;
using F7_L12_Position_Detect.Model;
using System;
using System.IO;
using System.Xml.Serialization;

namespace F7_L12_Position_Detect.ViewModel
{
    public partial class MainViewModel
    {
        /// <summary>当前扫码实际采用的母排组装开关，保存设置后立即刷新。</summary>
        [XmlIgnore]
        public bool BusbarBindingEnabledAtStartup => BusbarConfig.Enabled;

        /// <summary>主界面显示当前生效的母排组装开关状态。</summary>
        [XmlIgnore]
        public string BusbarBindingStatusText => BusbarBindingEnabledAtStartup ? "已开启" : "已关闭";

        /// <summary>展示当前生效的组装工位和工序，便于现场确认保存后已切换。</summary>
        [XmlIgnore]
        public string BusbarBindingConfigurationHint =>
            $"{BusbarBindingStatusText}；组装工位={BusbarConfig.StationCode}；组装工序={BusbarConfig.ProcedureCode}。设置保存后立即生效。";

        /// <summary>
        /// 维护权限有效时校验并保存绑定配置，落盘成功后立即刷新运行快照，下一次扫码按新配置分流。
        /// 正在进行中的母排组装会话仍沿用其开始时克隆的参数，避免中途切换造成半次写入。
        /// </summary>
        /// <param name="candidate">窗口编辑的绑定开关、工位和工序副本。</param>
        /// <param name="error">校验或保存失败的现场提示；成功时为空。</param>
        /// <returns>配置校验通过并完成安全落盘时返回 true。</returns>
        public bool TrySaveBusbarBindingConfiguration(BusbarBindingConfig candidate, out string error)
        {
            error = string.Empty;
            if (DataModel.Settingmodel.permission != true)
            {
                error = "请先解锁界面，再保存母排组装设置";
                return false;
            }

            var updated = candidate?.Clone();
            error = updated == null ? "母排组装设置为空" : updated.Validate();
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

            ApplyBusbarBindingRuntimeConfig(updated, "设置保存");
            writeLog(
                $"[母排组装配置] 已保存并立即生效，启用={updated.Enabled}，组装工位={updated.StationCode}，组装工序={updated.ProcedureCode}",
                true);
            return true;
        }

        /// <summary>
        /// 用已确认配置刷新母排组装运行快照，并通知主界面状态文字。
        /// </summary>
        /// <param name="source">要生效的配置；为空时按空配置关闭母排组装。</param>
        /// <param name="reason">触发刷新的来源，写入详细日志便于追溯。</param>
        private void ApplyBusbarBindingRuntimeConfig(BusbarBindingConfig source, string reason)
        {
            _busbarBindingRuntimeConfig = (source ?? new BusbarBindingConfig()).Clone();
            RaisePropertyChanged(() => BusbarBindingEnabledAtStartup);
            RaisePropertyChanged(() => BusbarBindingStatusText);
            RaisePropertyChanged(() => BusbarBindingConfigurationHint);

            string validationError = _busbarBindingRuntimeConfig.Validate();
            WriteDetailLog(
                $"[母排组装配置] {reason}：启用={_busbarBindingRuntimeConfig.Enabled}，组装工位={_busbarBindingRuntimeConfig.StationCode}，组装工序={_busbarBindingRuntimeConfig.ProcedureCode}" +
                (string.IsNullOrEmpty(validationError) ? string.Empty : $"；校验提示={validationError}"));
        }
    }
}
