using F7_L12_Position_Detect.Model;
using F7_L12_Position_Detect.Model.Tool;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace F7_L12_Position_Detect.Config
{
    /// <summary>
    /// 配置 XML 的加载结果。调用方通过该结果区分正常加载、备份恢复、首次部署生成默认模板、文件损坏后回退默认值等现场状态。
    /// <para>该结果会参与后续保存决策：首装无文件允许落盘，已有文件损坏会阻止默认内存配置覆盖磁盘文件。</para>
    /// </summary>
    /// <typeparam name="T">XML 根节点对应的配置模型类型。</typeparam>
    public sealed class ConfigLoadResult<T> where T : class
    {
        /// <summary>
        /// 当前进入内存的配置对象。正常加载和备份恢复时来自 XML；首装无文件或恢复失败时来自默认模板。
        /// </summary>
        public T Data { get; set; }

        /// <summary>
        /// 表示主文件或备份文件曾经存在，但本轮启动均无法反序列化。保存流程依据该标志保护现场文件，避免默认值覆盖损坏样本。
        /// </summary>
        public bool LoadFailed { get; set; }

        /// <summary>
        /// 表示主文件不可用时已从 <c>.bak</c> 或 <c>.lastgood</c> 恢复，用于现场日志提示维护人员确认恢复来源。
        /// </summary>
        public bool RestoredFromBackup { get; set; }

        /// <summary>
        /// 实际用于恢复的备份文件路径。该值仅用于日志和排查。
        /// </summary>
        public string RestoredFrom { get; set; }
    }

    /// <summary>
    /// 现场配置和工程工具 XML 的安全读写入口。启动加载、窗口保存和软件关闭保存共用该入口，统一处理文件校验和原子替换。
    /// <para>配置恢复文件集中保存在“配置\备份”；工程文件通过时间戳快照保留完整配方、图片和模型。</para>
    /// </summary>
    public static class ConfigXmlSaveHelper
    {
        private const string BackupExtension = ".bak";
        private const string LastGoodExtension = ".lastgood";
        private const string RecoveryDirectoryName = "备份";
        private const int MaxTimestampBackupDirectories = 30;
        private const string RecoveryReadmeFileName = "readme.txt";

        /// <summary>
        /// 判断工具配置是否属于启动阶段的空白占位。工程配方保存以该结果保护现有 <c>Tool*.xml</c>，防止加载失败后的占位工具清空现场配方。
        /// </summary>
        /// <param name="tool">待判断的工具配置。</param>
        /// <returns>返回 true 表示该工具缺少名称和相机序列号，并保持默认触发指令，可视为启动占位。</returns>
        public static bool IsEmptyTool(ToolModel tool)
        {
            if (tool == null)
            {
                return true;
            }

            return string.IsNullOrEmpty(tool.Name)
                && tool.Command == "T1-1"
                && string.IsNullOrEmpty(tool.CameraSerialNumber);
        }

        /// <summary>
        /// 判断当前工具集合是否包含可保存的业务配方。该检查位于工程配方落盘前，决定是否允许写入并清理多余 <c>Tool*.xml</c>。
        /// </summary>
        /// <param name="tools">内存中的工程工具集合。</param>
        /// <returns>返回 true 表示至少存在一个非空占位工具，后续保存流程可以执行。</returns>
        public static bool HasValidTools(IList<ToolModel> tools)
        {
            return tools != null
                && tools.Count > 0
                && tools.Any(tool => !IsEmptyTool(tool));
        }

        /// <summary>
        /// 按主文件、<c>.bak</c>、<c>.lastgood</c> 的顺序加载配置。主文件损坏时自动恢复最近可用备份；首装无任何候选文件时返回默认模板并允许后续保存。
        /// </summary>
        /// <typeparam name="T">XML 对应的配置模型类型。</typeparam>
        /// <param name="filename">主配置文件完整路径。</param>
        /// <param name="createDefault">无任何候选文件或恢复失败时用于创建内存模板的函数。</param>
        /// <param name="log">诊断日志入口，用于记录恢复来源、首装状态和损坏状态。</param>
        /// <returns>返回配置对象及其加载状态，供调用方决定是否继续保存。</returns>
        public static ConfigLoadResult<T> TryLoad<T>(string filename, Func<T> createDefault, Action<string> log = null) where T : class
        {
            MigrateLegacyRecoveryFiles(filename, log);

            foreach (string sourceFile in GetLoadCandidates(filename))
            {
                T data = TryDeserialize<T>(sourceFile);
                if (data == null)
                {
                    continue;
                }

                if (!string.Equals(sourceFile, filename, StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"[配置加载] 从 {Path.GetFileName(sourceFile)} 恢复 {Path.GetFileName(filename)}");
                    TryCopyFile(sourceFile, filename);

                    return new ConfigLoadResult<T>
                    {
                        Data = data,
                        LoadFailed = false,
                        RestoredFromBackup = true,
                        RestoredFrom = sourceFile
                    };
                }

                return new ConfigLoadResult<T>
                {
                    Data = data,
                    LoadFailed = false
                };
            }

            bool hasPersistedFile = HasAnyPersistedFile(filename);
            if (hasPersistedFile)
            {
                log?.Invoke($"[配置加载] 失败：{Path.GetFileName(filename)} 损坏且无法从备份恢复，使用内存默认配置");
            }
            else
            {
                log?.Invoke($"[配置加载] 首次部署：{Path.GetFileName(filename)} 尚不存在，使用内存默认配置");
            }

            return new ConfigLoadResult<T>
            {
                Data = createDefault(),
                LoadFailed = hasPersistedFile
            };
        }

        /// <summary>
        /// 安全保存配置 XML。加载失败场景跳过写回，正常场景写入临时文件、校验临时文件并完成原子替换。
        /// </summary>
        /// <typeparam name="T">XML 对应的配置模型类型。</typeparam>
        /// <param name="filename">主配置文件完整路径。</param>
        /// <param name="data">准备保存的内存配置对象。</param>
        /// <param name="loadFailed">本轮启动时该配置是否发生无法恢复的加载失败。</param>
        /// <param name="log">诊断日志入口，用于记录跳过、成功和失败原因。</param>
        /// <returns>配置完成更新或内容保持一致时返回 true；保护策略拦截或写入异常时返回 false。</returns>
        public static bool TrySave<T>(string filename, T data, bool loadFailed, Action<string> log = null) where T : class
        {
            if (data == null)
            {
                log?.Invoke($"[配置保存] 跳过：{Path.GetFileName(filename)} 数据为空");
                return false;
            }

            if (loadFailed)
            {
                log?.Invoke($"[配置保存] 跳过：{Path.GetFileName(filename)} 本次加载失败，内存配置不可信");
                return false;
            }

            try
            {
                AtomicSerialize(filename, data, true, log);
                log?.Invoke($"[配置保存] 成功：{Path.GetFileName(filename)}");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[配置保存] 失败：{Path.GetFileName(filename)}，{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 安全保存工程工具 XML。正式文件继续使用临时校验和原子替换，恢复能力由加载前的完整工程时间戳快照提供。
        /// </summary>
        /// <typeparam name="T">工程工具 XML 对应的数据类型。</typeparam>
        /// <param name="filename">工程目录中的正式 XML 路径。</param>
        /// <param name="data">准备保存的工具配置。</param>
        /// <param name="loadFailed">本轮工程加载是否发生无法恢复的失败。</param>
        /// <param name="log">诊断日志入口。</param>
        /// <returns>工程文件完成更新或内容保持一致时返回 true。</returns>
        public static bool TrySaveProjectFile<T>(string filename, T data, bool loadFailed, Action<string> log = null) where T : class
        {
            if (data == null)
            {
                log?.Invoke($"[配置保存] 跳过：{Path.GetFileName(filename)} 数据为空");
                return false;
            }

            if (loadFailed)
            {
                log?.Invoke($"[配置保存] 跳过：{Path.GetFileName(filename)} 本次加载失败，内存配置不可信");
                return false;
            }

            try
            {
                AtomicSerialize(filename, data, false, log);
                log?.Invoke($"[配置保存] 成功：{Path.GetFileName(filename)}");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[配置保存] 失败：{Path.GetFileName(filename)}，{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 启动加载前将目录中的 XML 复制到带时间戳的备份目录。系统配置和当前工程配方使用分类子目录保存。
        /// </summary>
        /// <param name="sourceDir">待备份的配置目录。</param>
        /// <param name="backupRoot">程序目录下的备份根目录。</param>
        /// <param name="categoryName">系统配置或工程名称，用于区分同一启动时刻的文件来源。</param>
        /// <param name="log">现场诊断日志入口。</param>
        public static void BackupXmlFiles(string sourceDir, string backupRoot, string categoryName, Action<string> log = null)
        {
            BackupFiles(sourceDir, backupRoot, categoryName, "*.xml", SearchOption.TopDirectoryOnly, false, log);
        }

        /// <summary>
        /// 启动加载前递归备份工程目录，保留工具 XML、示教图片、视觉模型及其子目录结构。
        /// </summary>
        /// <param name="sourceDir">当前工程目录。</param>
        /// <param name="backupRoot">时间戳备份根目录。</param>
        /// <param name="categoryName">包含工程名称的备份分类。</param>
        /// <param name="log">现场诊断日志入口。</param>
        public static void BackupProjectFiles(string sourceDir, string backupRoot, string categoryName, Action<string> log = null)
        {
            BackupFiles(sourceDir, backupRoot, categoryName, "*", SearchOption.AllDirectories, true, log);
        }

        private static void BackupFiles(
            string sourceDir,
            string backupRoot,
            string categoryName,
            string searchPattern,
            SearchOption searchOption,
            bool preserveRelativePath,
            Action<string> log)
        {
            try
            {
                PrepareBackupRoot(backupRoot, log);

                if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                {
                    return;
                }

                string[] sourceFiles = Directory.GetFiles(sourceDir, searchPattern, searchOption);
                if (sourceFiles.Length == 0)
                {
                    return;
                }

                string targetDir = Path.Combine(
                    backupRoot,
                    DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                    SanitizeDirectoryName(categoryName));
                Directory.CreateDirectory(targetDir);

                int copiedCount = 0;
                foreach (string sourceFile in sourceFiles)
                {
                    try
                    {
                        string relativePath = preserveRelativePath
                            ? GetRelativePath(sourceDir, sourceFile)
                            : Path.GetFileName(sourceFile);
                        string targetFile = Path.Combine(targetDir, relativePath);
                        EnsureDirectory(targetFile);
                        File.Copy(sourceFile, targetFile, true);
                        copiedCount++;
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[配置备份] 文件备份失败：{Path.GetFileName(sourceFile)}，{ex.Message}");
                    }
                }

                if (copiedCount > 0)
                {
                    string fileType = searchOption == SearchOption.AllDirectories ? "个工程文件" : "个XML";
                    log?.Invoke($"[配置备份] 已备份{copiedCount}{fileType}到：{targetDir}");
                }

                PruneTimestampBackupDirectories(backupRoot, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[配置备份] 目录备份失败：{sourceDir}，{ex.Message}");
            }
        }

        /// <summary>
        /// 初始化备份根目录，生成现场恢复说明，并按保留上限清理旧时间戳目录。
        /// </summary>
        /// <param name="backupRoot">备份根目录。</param>
        /// <param name="log">现场诊断日志入口。</param>
        private static void PrepareBackupRoot(string backupRoot, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                return;
            }

            Directory.CreateDirectory(backupRoot);
            WriteRecoveryReadme(backupRoot, log);
            PruneTimestampBackupDirectories(backupRoot, log);
        }

        /// <summary>
        /// 生成配置恢复说明。文件位于备份根目录，只服务现场维护，不参与程序加载。
        /// </summary>
        /// <param name="backupRoot">备份根目录。</param>
        /// <param name="log">现场诊断日志入口。</param>
        private static void WriteRecoveryReadme(string backupRoot, Action<string> log)
        {
            const string content =
                "配置备份与恢复说明\r\n" +
                "\r\n" +
                "1. 本目录由软件自动管理，保留最近30个 yyyyMMdd_HHmmss 时间戳目录。\r\n" +
                "2. 系统配置位于时间戳目录的“系统配置”中；完整工程文件位于“工程_工程名”中，并保留图片、模型和子目录。\r\n" +
                "3. 正常运行时无需手工修改、移动或删除这些文件。\r\n" +
                "4. 需要人工恢复时：先关闭软件，备份当前故障文件，再把选定时间戳中的文件复制回原“配置”或工程目录。\r\n" +
                "5. 配置 XML 的 .bak 和 .lastgood 集中位于“配置\\备份”目录，由软件自动恢复；工程文件由时间戳目录恢复。\r\n" +
                "6. 恢复后请核对当前工程、相机序列号、PLC参数、工单工序和点检配置后再投入生产。\r\n";

            try
            {
                string readmePath = Path.Combine(backupRoot, RecoveryReadmeFileName);
                if (!File.Exists(readmePath) || !string.Equals(File.ReadAllText(readmePath), content, StringComparison.Ordinal))
                {
                    File.WriteAllText(readmePath, content, new UTF8Encoding(true));
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[配置备份] 恢复说明生成失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 仅保留最近的时间戳备份目录。恢复说明和其他非时间戳目录不受该清理策略影响。
        /// </summary>
        /// <param name="backupRoot">备份根目录。</param>
        /// <param name="log">现场诊断日志入口。</param>
        private static void PruneTimestampBackupDirectories(string backupRoot, Action<string> log)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(backupRoot) || !Directory.Exists(backupRoot))
                {
                    return;
                }

                var timestampDirectories = new List<KeyValuePair<DateTime, DirectoryInfo>>();
                foreach (DirectoryInfo directory in new DirectoryInfo(backupRoot).GetDirectories())
                {
                    DateTime timestamp;
                    if (DateTime.TryParseExact(
                        directory.Name,
                        "yyyyMMdd_HHmmss",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out timestamp))
                    {
                        timestampDirectories.Add(new KeyValuePair<DateTime, DirectoryInfo>(timestamp, directory));
                    }
                }

                DirectoryInfo[] expiredDirectories = timestampDirectories
                    .OrderByDescending(item => item.Key)
                    .ThenByDescending(item => item.Value.Name, StringComparer.Ordinal)
                    .Skip(MaxTimestampBackupDirectories)
                    .Select(item => item.Value)
                    .ToArray();

                foreach (DirectoryInfo directory in expiredDirectories)
                {
                    try
                    {
                        directory.Delete(true);
                        log?.Invoke($"[配置备份] 已清理超出保留数量的旧备份：{directory.Name}");
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[配置备份] 旧备份清理失败：{directory.FullName}，{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[配置备份] 保留策略执行失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 判断主文件或任一备份文件是否曾经存在。该判断用于区分首装无文件和已有文件损坏两类现场状态。
        /// </summary>
        /// <param name="filename">主配置文件完整路径。</param>
        /// <returns>返回 true 表示存在主文件、<c>.bak</c> 或 <c>.lastgood</c> 中的任一文件。</returns>
        private static bool HasAnyPersistedFile(string filename)
        {
            return GetLoadCandidates(filename).Any(File.Exists);
        }

        /// <summary>
        /// 返回同一配置文件的恢复候选顺序。主文件优先保证最新设置生效，<c>.bak</c> 来自上一次替换，<c>.lastgood</c> 保留最近一次成功保存后的可读副本。
        /// </summary>
        /// <param name="filename">主配置文件完整路径。</param>
        /// <returns>按加载优先级排列的候选文件路径。</returns>
        private static IEnumerable<string> GetLoadCandidates(string filename)
        {
            yield return filename;

            if (IsConfigurationFile(filename) || IsInspectionRecipeConfigFile(filename))
            {
                yield return GetRecoveryFilePath(filename, BackupExtension);
                yield return GetRecoveryFilePath(filename, LastGoodExtension);
            }

            // 兼容既有工程配方和升级前的配置恢复文件。
            yield return filename + BackupExtension;
            yield return filename + LastGoodExtension;
        }

        private static bool IsConfigurationFile(string filename)
        {
            string directory = Path.GetDirectoryName(filename);
            return !string.IsNullOrWhiteSpace(directory)
                && string.Equals(new DirectoryInfo(directory).Name, "配置", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInspectionRecipeConfigFile(string filename)
        {
            return string.Equals(Path.GetFileName(filename), "点检配置.xml", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRecoveryFilePath(string filename, string extension)
        {
            string directory = Path.GetDirectoryName(filename) ?? string.Empty;
            return Path.Combine(directory, RecoveryDirectoryName, Path.GetFileName(filename) + extension);
        }

        private static void MigrateLegacyRecoveryFiles(string filename, Action<string> log)
        {
            if (!IsConfigurationFile(filename))
            {
                return;
            }

            MigrateLegacyRecoveryFile(filename, BackupExtension, log);
            MigrateLegacyRecoveryFile(filename, LastGoodExtension, log);
        }

        private static void MigrateLegacyRecoveryFile(string filename, string extension, Action<string> log)
        {
            string legacyFile = filename + extension;
            if (!File.Exists(legacyFile))
            {
                return;
            }

            string recoveryFile = GetRecoveryFilePath(filename, extension);
            try
            {
                EnsureDirectory(recoveryFile);
                if (!File.Exists(recoveryFile))
                {
                    File.Move(legacyFile, recoveryFile);
                }
                else if (FilesHaveSameContent(legacyFile, recoveryFile))
                {
                    File.Delete(legacyFile);
                }
                else
                {
                    string retainedLegacyFile = recoveryFile + ".legacy_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
                    File.Move(legacyFile, retainedLegacyFile);
                }

                log?.Invoke($"[配置备份] 已整理旧恢复文件：{Path.GetFileName(legacyFile)}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[配置备份] 旧恢复文件整理失败：{Path.GetFileName(legacyFile)}，{ex.Message}");
            }
        }

        private static string SanitizeDirectoryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "XML";
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (invalidChars.Contains(chars[i]))
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        /// <summary>
        /// 尝试反序列化指定 XML 文件。该方法只表达文件是否可读，异常由上层转换成恢复、首装或保存保护策略。
        /// </summary>
        /// <typeparam name="T">XML 对应的配置模型类型。</typeparam>
        /// <param name="filename">待读取的文件路径。</param>
        /// <returns>返回配置对象；文件缺失、格式损坏或类型不匹配时返回 null。</returns>
        private static T TryDeserialize<T>(string filename) where T : class
        {
            try
            {
                if (!File.Exists(filename))
                {
                    return null;
                }

                using (var stream = File.OpenRead(filename))
                {
                    var serializer = new XmlSerializer(typeof(T));
                    return serializer.Deserialize(stream) as T;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 将可用备份复制回主文件路径。复制失败只影响磁盘修复结果，已恢复的内存配置仍可服务本轮启动。
        /// </summary>
        /// <param name="sourceFile">可用备份文件路径。</param>
        /// <param name="destinationFile">主配置文件路径。</param>
        private static void TryCopyFile(string sourceFile, string destinationFile)
        {
            string tempCopy = destinationFile + ".copytmp";
            try
            {
                if (File.Exists(destinationFile) && FilesHaveSameContent(sourceFile, destinationFile))
                {
                    return;
                }

                EnsureDirectory(destinationFile);
                File.Copy(sourceFile, tempCopy, true);
                if (File.Exists(destinationFile))
                {
                    ReplaceExistingFile(tempCopy, destinationFile);
                }
                else
                {
                    File.Move(tempCopy, destinationFile);
                }
            }
            catch
            {
            }
            finally
            {
                TryDeleteFile(tempCopy);
            }
        }

        /// <summary>
        /// 将 XML 写入临时文件并校验可反序列化，再通过原子替换更新正式文件。
        /// <para>配置 XML 刷新“配置\备份”内的恢复副本；工程工具 XML 使用加载前的完整时间戳快照恢复。</para>
        /// </summary>
        /// <typeparam name="T">XML 对应的配置模型类型。</typeparam>
        /// <param name="filename">主配置文件完整路径。</param>
        /// <param name="data">准备写入的配置对象。</param>
        /// <param name="maintainRecoveryCopies">配置 XML 为 true，工程工具 XML 为 false。</param>
        /// <param name="log">诊断日志入口。</param>
        private static void AtomicSerialize<T>(string filename, T data, bool maintainRecoveryCopies, Action<string> log) where T : class
        {
            EnsureDirectory(filename);

            string backupFile = maintainRecoveryCopies ? GetRecoveryFilePath(filename, BackupExtension) : null;
            string lastGoodFile = maintainRecoveryCopies ? GetRecoveryFilePath(filename, LastGoodExtension) : null;
            string tempFile = filename + ".tmp";

            try
            {
                using (var stream = File.Open(tempFile, FileMode.Create))
                {
                    var serializer = new XmlSerializer(typeof(T));
                    serializer.Serialize(stream, data);
                }

                if (TryDeserialize<T>(tempFile) == null)
                {
                    throw new InvalidOperationException("临时文件校验失败");
                }

                if (File.Exists(filename) && FilesHaveSameContent(tempFile, filename))
                {
                    if (maintainRecoveryCopies)
                    {
                        TryCopyFile(filename, lastGoodFile);
                    }

                    log?.Invoke($"[配置保存] 内容未变化，跳过替换：{Path.GetFileName(filename)}");
                    return;
                }

                if (File.Exists(filename))
                {
                    if (maintainRecoveryCopies)
                    {
                        EnsureDirectory(backupFile);
                        File.Replace(tempFile, filename, backupFile);
                    }
                    else
                    {
                        ReplaceExistingFile(tempFile, filename);
                    }
                }
                else
                {
                    File.Move(tempFile, filename);
                }

                if (maintainRecoveryCopies)
                {
                    TryCopyFile(filename, lastGoodFile);
                }
            }
            finally
            {
                TryDeleteFile(tempFile);
            }
        }

        private static bool FilesHaveSameContent(string firstFile, string secondFile)
        {
            var firstInfo = new FileInfo(firstFile);
            var secondInfo = new FileInfo(secondFile);
            if (!firstInfo.Exists || !secondInfo.Exists || firstInfo.Length != secondInfo.Length)
            {
                return false;
            }

            const int bufferSize = 81920;
            byte[] firstBuffer = new byte[bufferSize];
            byte[] secondBuffer = new byte[bufferSize];
            using (var firstStream = new FileStream(firstFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize))
            using (var secondStream = new FileStream(secondFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize))
            {
                int firstRead;
                while ((firstRead = firstStream.Read(firstBuffer, 0, firstBuffer.Length)) > 0)
                {
                    int secondRead = secondStream.Read(secondBuffer, 0, secondBuffer.Length);
                    if (firstRead != secondRead)
                    {
                        return false;
                    }

                    for (int i = 0; i < firstRead; i++)
                    {
                        if (firstBuffer[i] != secondBuffer[i])
                        {
                            return false;
                        }
                    }
                }

                return secondStream.ReadByte() == -1;
            }
        }

        private static string GetRelativePath(string rootDirectory, string filename)
        {
            string normalizedRoot = Path.GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string normalizedFile = Path.GetFullPath(filename);
            if (normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedFile.Substring(normalizedRoot.Length);
            }

            return Path.GetFileName(filename);
        }

        private static void EnsureDirectory(string filename)
        {
            string directory = Path.GetDirectoryName(filename);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static void ReplaceExistingFile(string sourceFile, string destinationFile)
        {
            string replaceBackupFile = destinationFile + ".replacebak";
            TryDeleteFile(replaceBackupFile);
            File.Replace(sourceFile, destinationFile, replaceBackupFile);
            TryDeleteFile(replaceBackupFile);
        }

        private static void TryDeleteFile(string filename)
        {
            try
            {
                if (File.Exists(filename))
                {
                    File.Delete(filename);
                }
            }
            catch
            {
            }
        }
    }
}
