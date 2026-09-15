using GalaSoft.MvvmLight;
using System;
using System.Xml.Serialization;

namespace F7_L12_Position_Detect.Model.Setting
{
    /// <summary>
    /// 当前产品工程使用的点检标准件配置。配置文件跟随工程目录保存，使每个产品配方维护独立的 OK/NG 最终产品 SN 和点检周期起点。
    /// </summary>
    [XmlRoot("点检配方")]
    public class InspectionRecipeConfig : ObservableObject
    {
        private string _okInspectionSN = string.Empty;
        private string _ngInspectionSN = string.Empty;
        private DateTime _lastInspectionTime = DateTime.MinValue;
        private bool _isConfirmed;
        private bool _loadFailed;

        /// <summary>
        /// 当前工程的 OK 标准件最终产品 SN。MES 解析结果命中后进入 OK 点检流程，并跳过普通产品的工单和工序校验。
        /// </summary>
        [XmlElement("OK点检SN")]
        public string OKInspectionSN
        {
            get { return _okInspectionSN; }
            set
            {
                _okInspectionSN = value;
                RaisePropertyChanged(() => OKInspectionSN);
            }
        }

        /// <summary>
        /// 当前工程的 NG 标准件最终产品 SN。扫码命中后进入 NG 点检流程，实际视觉结果继续决定本次点检是否通过。
        /// </summary>
        [XmlElement("NG点检SN")]
        public string NGInspectionSN
        {
            get { return _ngInspectionSN; }
            set
            {
                _ngInspectionSN = value;
                RaisePropertyChanged(() => NGInspectionSN);
            }
        }

        /// <summary>
        /// 当前工程最近一次完成 OK 和 NG 点检的时间。倒计时、到期提醒和普通产品门禁共同使用该时间，避免其他产品配方的点检记录放行当前产品。
        /// </summary>
        [XmlElement("上次点检完成时间")]
        public DateTime LastInspectionTime
        {
            get { return _lastInspectionTime; }
            set
            {
                _lastInspectionTime = value;
                RaisePropertyChanged(() => LastInspectionTime);
                RaisePropertyChanged(() => LastInspectionTimeText);
            }
        }

        /// <summary>
        /// 表示维护人员已经在软件界面确认本工程的两条点检 SN。首次升级沿用全局配置时保持 false，用于持续提示现场完成配方核对。
        /// </summary>
        [XmlElement("配置已确认")]
        public bool IsConfirmed
        {
            get { return _isConfirmed; }
            set
            {
                _isConfirmed = value;
                RaisePropertyChanged(() => IsConfirmed);
                RaisePropertyChanged(() => StatusText);
            }
        }

        /// <summary>
        /// 当前工程点检配置的加载健康状态。该状态只在本次运行中使用，加载失败时阻止默认内存值参与生产判断或覆盖现场文件。
        /// </summary>
        [XmlIgnore]
        public bool LoadFailed
        {
            get { return _loadFailed; }
            set
            {
                _loadFailed = value;
                RaisePropertyChanged(() => LoadFailed);
                RaisePropertyChanged(() => StatusText);
            }
        }

        /// <summary>
        /// 设置窗口展示的配置状态，帮助操作员区分已确认配方、升级迁移配方和损坏文件。
        /// </summary>
        [XmlIgnore]
        public string StatusText
        {
            get
            {
                if (LoadFailed)
                {
                    return "点检配置加载失败，请检查工程文件和恢复副本";
                }

                return IsConfirmed ? "当前配方已确认" : "沿用旧全局点检配置，请核对后保存";
            }
        }

        /// <summary>
        /// 上次点检完成时间的界面显示文本。首次投入或尚未完成本配方点检时显示“尚未完成”。
        /// </summary>
        [XmlIgnore]
        public string LastInspectionTimeText
        {
            get
            {
                return LastInspectionTime == DateTime.MinValue
                    ? "尚未完成"
                    : LastInspectionTime.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        /// <summary>
        /// 创建独立副本，隔离设置窗口编辑值与扫码流程正在使用的生效值。
        /// </summary>
        /// <returns>包含相同点检 SN、完成时间和确认状态的新对象。</returns>
        public InspectionRecipeConfig Clone()
        {
            return new InspectionRecipeConfig
            {
                OKInspectionSN = OKInspectionSN,
                NGInspectionSN = NGInspectionSN,
                LastInspectionTime = LastInspectionTime,
                IsConfirmed = IsConfirmed,
                LoadFailed = LoadFailed
            };
        }
    }
}
