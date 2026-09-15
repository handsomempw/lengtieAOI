using F7_L12_Position_Detect.Config;
using F7_L12_Position_Detect.Model;
using F7_L12_Position_Detect.Model.Setting;
using Panuon.WPF.UI;
using System;
using System.IO;
using System.Xml.Serialization;

namespace F7_L12_Position_Detect.ViewModel
{
    public partial class MainViewModel
    {
        private const string InspectionRecipeConfigFileName = "点检配置.xml";
        private bool _inspectionRecipeScanInProgress;
        private bool _inspectionRecipeConfigLoadFailed;
        private readonly object _inspectionConfigurationLogLock = new object();
        private string _lastLoggedInspectionConfigurationError = string.Empty;
        private InspectionRecipeConfig _currentInspectionRecipeConfig = new InspectionRecipeConfig();
        private InspectionRecipeConfig _editableInspectionRecipeConfig = new InspectionRecipeConfig();

        /// <summary>
        /// 当前工程正在参与扫码识别、点检倒计时和生产门禁的已保存配置。
        /// 设置窗口编辑独立副本，只有保存校验和文件落盘成功后才替换该对象。
        /// </summary>
        public InspectionRecipeConfig CurrentInspectionRecipeConfig
        {
            get { return _currentInspectionRecipeConfig; }
            private set
            {
                _currentInspectionRecipeConfig = value ?? new InspectionRecipeConfig();
                RaisePropertyChanged(() => CurrentInspectionRecipeConfig);
            }
        }

        /// <summary>
        /// 点检配置窗口使用的编辑副本。该对象不参与生产扫码，防止输入中的半成品 SN 提前生效。
        /// </summary>
        public InspectionRecipeConfig EditableInspectionRecipeConfig
        {
            get { return _editableInspectionRecipeConfig; }
            private set
            {
                _editableInspectionRecipeConfig = value ?? new InspectionRecipeConfig();
                RaisePropertyChanged(() => EditableInspectionRecipeConfig);
            }
        }

        /// <summary>
        /// 打开点检配置窗口前从当前生效配置创建编辑副本。关闭窗口会自然丢弃未保存输入。
        /// </summary>
        public void PrepareInspectionRecipeConfigEdit()
        {
            EditableInspectionRecipeConfig = CurrentInspectionRecipeConfig.Clone();
        }

        /// <summary>
        /// 加载当前工程目录中的点检配置。首次升级缺少文件时沿用旧全局 SN 和完成时间生成待确认配置，保持既有生产入口可用并提示维护人员逐配方核对。
        /// </summary>
        public void LoadInspectionRecipeConfiguration()
        {
            string filename = GetInspectionRecipeConfigPath();
            if (string.IsNullOrEmpty(filename))
            {
                SetUnavailableInspectionRecipeConfiguration("当前工程名称或工程路径为空");
                return;
            }

            string projectDirectory = Path.GetDirectoryName(filename);
            if (string.IsNullOrEmpty(projectDirectory) || !Directory.Exists(projectDirectory))
            {
                SetUnavailableInspectionRecipeConfiguration($"工程目录不存在：{projectDirectory}");
                return;
            }

            bool hadPersistedFile = File.Exists(filename)
                || File.Exists(filename + ".bak")
                || File.Exists(filename + ".lastgood")
                || File.Exists(Path.Combine(projectDirectory, "备份", Path.GetFileName(filename) + ".bak"))
                || File.Exists(Path.Combine(projectDirectory, "备份", Path.GetFileName(filename) + ".lastgood"));
            ConfigLoadResult<InspectionRecipeConfig> result = ConfigXmlSaveHelper.TryLoad(
                filename,
                CreateInspectionRecipeConfigFromLegacySettings,
                WriteDetailLog);

            InspectionRecipeConfig loaded = result.Data ?? CreateInspectionRecipeConfigFromLegacySettings();
            NormalizeInspectionRecipeConfig(loaded);
            loaded.LoadFailed = result.LoadFailed;
            _inspectionRecipeConfigLoadFailed = result.LoadFailed;
            CurrentInspectionRecipeConfig = loaded;
            EditableInspectionRecipeConfig = loaded.Clone();

            if (result.LoadFailed)
            {
                writeLog($"[点检配方] 工程={DataModel.Settingmodel.Name}，点检配置损坏且恢复失败，当前工程生产门禁保持关闭", true);
                return;
            }

            if (!hadPersistedFile)
            {
                if (WriteInspectionRecipeConfig(loaded))
                {
                    writeLog($"[点检配方] 工程={DataModel.Settingmodel.Name}，已沿用旧全局点检配置生成{InspectionRecipeConfigFileName}，请核对后保存", false);
                }
                else
                {
                    MarkInspectionRecipeConfigurationUnavailable();
                }
            }
            else if (result.RestoredFromBackup)
            {
                writeLog($"[点检配方] 工程={DataModel.Settingmodel.Name}，已从{Path.GetFileName(result.RestoredFrom)}恢复", false);
            }
        }

        /// <summary>
        /// 校验并保存设置窗口中的当前配方点检 SN。保存成功后生产扫码立即切换到新配置，点检完成时间保持原值。
        /// </summary>
        /// <returns>校验通过且工程配置完成安全落盘时返回 true。</returns>
        public bool SaveInspectionRecipeConfiguration()
        {
            if (DataModel?.Settingmodel?.permission != true)
            {
                NoticeBox.Show("当前处于只读状态，请先开启编辑权限", "点检配置", MessageBoxIcon.Warning, true, 5000);
                return false;
            }

            InspectionRecipeConfig candidate = EditableInspectionRecipeConfig.Clone();
            candidate.OKInspectionSN = (candidate.OKInspectionSN ?? string.Empty).Trim();
            candidate.NGInspectionSN = (candidate.NGInspectionSN ?? string.Empty).Trim();
            candidate.LastInspectionTime = CurrentInspectionRecipeConfig.LastInspectionTime;
            candidate.IsConfirmed = true;
            candidate.LoadFailed = false;

            string validationError = ValidateInspectionRecipeConfig(candidate);
            if (!string.IsNullOrEmpty(validationError))
            {
                NoticeBox.Show(validationError, "点检配置", MessageBoxIcon.Warning, true, 7000);
                return false;
            }

            string conflictError = FindConfirmedInspectionSnConflict(candidate);
            if (!string.IsNullOrEmpty(conflictError))
            {
                NoticeBox.Show(conflictError, "点检配置", MessageBoxIcon.Warning, true, 7000);
                return false;
            }

            if (!WriteInspectionRecipeConfig(candidate))
            {
                MarkInspectionRecipeConfigurationUnavailable();
                NoticeBox.Show("点检配置保存失败，请查看详细日志并检查工程目录", "点检配置", MessageBoxIcon.Error, true, 7000);
                return false;
            }

            _inspectionRecipeConfigLoadFailed = false;
            CurrentInspectionRecipeConfig = candidate;
            EditableInspectionRecipeConfig = candidate.Clone();
            UpdateInspectionCountdown();
            CheckInspectionStatus();
            writeLog($"[点检配方] 保存成功，工程={DataModel.Settingmodel.Name}，OK_SN={candidate.OKInspectionSN}，NG_SN={candidate.NGInspectionSN}", false);
            NoticeBox.Show("当前配方点检SN已保存并生效", "点检配置", MessageBoxIcon.Success, true, 4000);
            return true;
        }

        /// <summary>
        /// 保存当前工程的点检周期起点。点检全部完成、强制到期和时间纠正均通过该入口更新，确保内存状态只在文件落盘成功后生效。
        /// </summary>
        /// <param name="lastInspectionTime">新的完成时间；<see cref="DateTime.MinValue"/> 表示立即到期。</param>
        /// <returns>当前工程点检配置完成安全落盘时返回 true。</returns>
        private bool SaveCurrentRecipeInspectionTime(DateTime lastInspectionTime)
        {
            if (_inspectionRecipeConfigLoadFailed || CurrentInspectionRecipeConfig.LoadFailed)
            {
                writeLog($"[点检配方] 工程={DataModel?.Settingmodel?.Name}加载状态异常，点检时间未更新", true);
                return false;
            }

            InspectionRecipeConfig candidate = CurrentInspectionRecipeConfig.Clone();
            candidate.LastInspectionTime = lastInspectionTime;
            if (!WriteInspectionRecipeConfig(candidate))
            {
                MarkInspectionRecipeConfigurationUnavailable();
                return false;
            }

            CurrentInspectionRecipeConfig = candidate;
            EditableInspectionRecipeConfig = candidate.Clone();
            return true;
        }

        /// <summary>
        /// 返回当前配方点检配置阻止生产或点检的原因。空字符串表示配置具备业务使用条件。
        /// </summary>
        private string GetCurrentInspectionRecipeConfigurationError()
        {
            if (_inspectionRecipeConfigLoadFailed || CurrentInspectionRecipeConfig == null || CurrentInspectionRecipeConfig.LoadFailed)
            {
                return "当前配方点检配置加载失败";
            }

            return ValidateInspectionRecipeConfig(CurrentInspectionRecipeConfig);
        }

        /// <summary>
        /// 记录点检配置状态变化。配置轮询会频繁调用生产门禁，因此相同错误只记录一次，配置恢复时补充一条恢复日志。
        /// </summary>
        /// <param name="configurationError">当前配置错误；空字符串表示配置恢复有效。</param>
        private void LogInspectionConfigurationErrorIfChanged(string configurationError)
        {
            string normalizedError = (configurationError ?? string.Empty).Trim();
            bool changed;
            lock (_inspectionConfigurationLogLock)
            {
                changed = !string.Equals(
                    _lastLoggedInspectionConfigurationError,
                    normalizedError,
                    StringComparison.Ordinal);
                if (changed)
                {
                    _lastLoggedInspectionConfigurationError = normalizedError;
                }
            }

            if (!changed)
            {
                return;
            }

            string projectName = DataModel?.Settingmodel?.Name ?? string.Empty;
            if (string.IsNullOrEmpty(normalizedError))
            {
                writeLog($"[点检配方] 工程={projectName}，点检配置已恢复有效", false);
                return;
            }

            writeLog($"[点检配方] 工程={projectName}，配置无效：{normalizedError}", true);
        }

        /// <summary>
        /// 判断当前流程是否允许切换工程。已扫码产品、扫码/MES、相机执行、报工和点检上下文均需先完成或清理。
        /// </summary>
        private string GetProjectSwitchBlockReason()
        {
            if (_inspectionRecipeScanInProgress)
            {
                return "扫码/MES正在处理";
            }

            if (_isExecuting)
            {
                return "相机检测正在执行";
            }

            if (DataModel?.Processmodel?.IsReportingWork == true)
            {
                return "产品正在报工";
            }

            if (DataModel?.Processmodel?.IsInspectionMode == true)
            {
                return "点检流程正在进行";
            }

            string branchReason = string.Empty;
            AppendBranchProjectSwitchBlockReason(ref branchReason);
            if (!string.IsNullOrEmpty(branchReason))
            {
                return branchReason;
            }

            if (!string.IsNullOrWhiteSpace(DataModel?.Processmodel?.CurrentProductSN))
            {
                return "当前产品SN尚未清理";
            }

            return string.Empty;
        }

        /// <summary>
        /// 清理只属于上一工程的点检和产品运行态。新工程的工具与点检完成时间随后从各自工程文件加载。
        /// </summary>
        private void ResetRuntimeForProjectInspectionChange()
        {
            CloseInspectionWindow();
            DataModel.Processmodel.IsInspectionMode = false;
            DataModel.Processmodel.InspectionType = null;
            DataModel.Processmodel.OKInspectionCompleted = false;
            DataModel.Processmodel.NGInspectionCompleted = false;
            DataModel.Processmodel.CurrentProductSN = string.Empty;
            DataModel.Processmodel.ScanCodeStatus = "等待扫码";
            _lastProductSN = string.Empty;
            ResetBranchProjectInspectionRuntime();
        }

        partial void AppendBranchProjectSwitchBlockReason(ref string reason);
        partial void ResetBranchProjectInspectionRuntime();

        private InspectionRecipeConfig CreateInspectionRecipeConfigFromLegacySettings()
        {
            ReportWorkConfig legacy = DataModel?.Settingmodel?.ReportWorkConfig;
            return new InspectionRecipeConfig
            {
                OKInspectionSN = legacy?.OKInspectionSN ?? string.Empty,
                NGInspectionSN = legacy?.NGInspectionSN ?? string.Empty,
                LastInspectionTime = legacy?.LastInspectionTime ?? DateTime.MinValue,
                IsConfirmed = false
            };
        }

        private string GetInspectionRecipeConfigPath()
        {
            string projectRoot = DataModel?.Settingmodel?.Prjdir;
            string projectName = DataModel?.Settingmodel?.Name;
            if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(projectName))
            {
                return string.Empty;
            }

            return Path.Combine(projectRoot, projectName, InspectionRecipeConfigFileName);
        }

        private bool WriteInspectionRecipeConfig(InspectionRecipeConfig config)
        {
            string filename = GetInspectionRecipeConfigPath();
            if (string.IsNullOrEmpty(filename))
            {
                WriteDetailLog("[点检配方] 保存失败：当前工程名称或工程路径为空");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filename));
            return ConfigXmlSaveHelper.TrySave(
                filename,
                config,
                _inspectionRecipeConfigLoadFailed,
                WriteDetailLog);
        }

        private static void NormalizeInspectionRecipeConfig(InspectionRecipeConfig config)
        {
            config.OKInspectionSN = (config.OKInspectionSN ?? string.Empty).Trim();
            config.NGInspectionSN = (config.NGInspectionSN ?? string.Empty).Trim();
        }

        private static string ValidateInspectionRecipeConfig(InspectionRecipeConfig config)
        {
            if (config == null)
            {
                return "当前配方点检配置未加载";
            }

            if (string.IsNullOrWhiteSpace(config.OKInspectionSN) || string.IsNullOrWhiteSpace(config.NGInspectionSN))
            {
                return "OK点检SN和NG点检SN必须填写最终产品SN";
            }

            if (InspectionRecipeSnEquals(config.OKInspectionSN, config.NGInspectionSN))
            {
                return "OK点检SN和NG点检SN不能相同";
            }

            return string.Empty;
        }

        private string FindConfirmedInspectionSnConflict(InspectionRecipeConfig candidate)
        {
            string projectRoot = DataModel?.Settingmodel?.Prjdir;
            string currentProject = DataModel?.Settingmodel?.Name;
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            {
                return string.Empty;
            }

            foreach (string projectDirectory in Directory.GetDirectories(projectRoot))
            {
                string projectName = Path.GetFileName(projectDirectory);
                if (string.Equals(projectName, currentProject, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string filename = Path.Combine(projectDirectory, InspectionRecipeConfigFileName);
                if (!File.Exists(filename))
                {
                    continue;
                }

                InspectionRecipeConfig other;
                try
                {
                    var serializer = new XmlSerializer(typeof(InspectionRecipeConfig));
                    using (var stream = File.OpenRead(filename))
                    {
                        other = serializer.Deserialize(stream) as InspectionRecipeConfig;
                    }
                }
                catch (Exception ex)
                {
                    return $"配方“{projectName}”的点检配置无法读取：{ex.Message}";
                }

                if (other == null || !other.IsConfirmed)
                {
                    continue;
                }

                if (InspectionRecipeSnEquals(candidate.OKInspectionSN, other.OKInspectionSN)
                    || InspectionRecipeSnEquals(candidate.OKInspectionSN, other.NGInspectionSN))
                {
                    return $"OK点检SN已被配方“{projectName}”使用，请为不同产品配置独立标准件SN";
                }

                if (InspectionRecipeSnEquals(candidate.NGInspectionSN, other.OKInspectionSN)
                    || InspectionRecipeSnEquals(candidate.NGInspectionSN, other.NGInspectionSN))
                {
                    return $"NG点检SN已被配方“{projectName}”使用，请为不同产品配置独立标准件SN";
                }
            }

            return string.Empty;
        }

        private static bool InspectionRecipeSnEquals(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void SetUnavailableInspectionRecipeConfiguration(string reason)
        {
            _inspectionRecipeConfigLoadFailed = true;
            var unavailable = new InspectionRecipeConfig { LoadFailed = true };
            CurrentInspectionRecipeConfig = unavailable;
            EditableInspectionRecipeConfig = unavailable.Clone();
            writeLog($"[点检配方] {reason}，当前工程生产门禁保持关闭", true);
        }

        private void MarkInspectionRecipeConfigurationUnavailable()
        {
            _inspectionRecipeConfigLoadFailed = true;
            CurrentInspectionRecipeConfig.LoadFailed = true;
            EditableInspectionRecipeConfig.LoadFailed = true;
        }

        partial void AppendBranchProjectSwitchBlockReason(ref string reason)
        {
            if (_manualImageTestInProgress)
            {
                reason = "照片测试正在执行";
                return;
            }

            if (!string.IsNullOrEmpty(DataModel?.Processmodel?.PendingInspectionType))
            {
                reason = $"正在等待{DataModel.Processmodel.PendingInspectionType}点检样件扫码";
                return;
            }

            lock (_scanAutoLock)
            {
                if (_scanAutoTriggerCts != null)
                {
                    reason = "扫码自动阶段循环正在运行";
                }
            }
        }

        partial void ResetBranchProjectInspectionRuntime()
        {
            StopScanAutoExecution();
            DataModel.Processmodel.PendingInspectionType = null;
            DataModel.Processmodel.CurrentStageIndex = 0;
            DataModel.Processmodel.CompletedStages.Clear();
            DataModel.Processmodel.NeedRestart = false;
            _manualInspectionSessionActive = false;
        }
    }
}
