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
    /// XML 配置加载结果。保存流程通过该结果区分首次部署、备份恢复和现场文件损坏。
    /// </summary>
    /// <typeparam name="T">XML 根节点对应的数据模型类型。</typeparam>
    internal sealed class ConfigLoadResult<T> where T : class
    {
        /// <summary>
        /// 本轮进入内存的配置对象。
        /// </summary>
        public T Data { get; set; }

        /// <summary>
        /// 主文件和备份文件均无法反序列化时为 true，用于阻止默认内存对象覆盖现场文件。
        /// </summary>
        public bool LoadFailed { get; set; }

        /// <summary>
        /// 主文件不可用时是否已从备份恢复。
        /// </summary>
        public bool RestoredFromBackup { get; set; }

        /// <summary>
        /// 实际用于恢复的备份文件路径。
        /// </summary>
        public string RestoredFrom { get; set; }
    }

    /// <summary>
    /// 现场配置和工程工具 XML 的安全读写入口。
    /// <para>主文件优先生效，<c>.bak</c> 保留替换前版本，<c>.lastgood</c> 保留最近一次可反序列化版本。</para>
    /// </summary>
    internal static class ConfigXmlSaveHelper
    {
        private const string BackupExtension = ".bak";
        private const string LastGoodExtension = ".lastgood";
        private const int MaxTimestampBackupDirectories = 30;
        private const string RecoveryReadmeFileName = "readme.txt";

        /// <summary>
        /// 判断工具集合是否包含可保存的业务配方。
        /// </summary>
        /// <param name="tools">当前工程的工具集合。</param>
        /// <returns>至少存在一个非启动占位工具时返回 true。</returns>
        public static bool HasValidTools(IList<ToolModel> tools)
        {
            return tools != null && tools.Count > 0 && tools.Any(tool => !IsEmptyTool(tool));
        }

        /// <summary>
        /// 按主文件、<c>.bak</c>、<c>.lastgood</c> 的顺序加载 XML。
        /// </summary>
        /// <typeparam name="T">XML 对应的配置类型。</typeparam>
        /// <param name="filename">主 XML 文件路径。</param>
        /// <param name="createDefault">无可用文件时的内存默认对象工厂。</param>
        /// <param name="log">现场诊断日志入口。</param>
        /// <returns>配置对象及加载健康状态。</returns>
        public static ConfigLoadResult<T> TryLoad<T>(string filename, Func<T> createDefault, Action<string> log = null) where T : class
        {
            foreach (string sourceFile in GetLoadCandidates(filename))
            {
                T data;
                if (!TryDeserialize(sourceFile, out data))
                {
                    continue;
                }

                bool restored = !string.Equals(sourceFile, filename, StringComparison.OrdinalIgnoreCase);
                if (restored)
                {
                    log?.Invoke($"[配置加载] 从 {Path.GetFileName(sourceFile)} 恢复 {Path.GetFileName(filename)}");
                    TryCopyFile(sourceFile, filename, log);
                }

                return new ConfigLoadResult<T>
                {
                    Data = data,
                    LoadFailed = false,
                    RestoredFromBackup = restored,
                    RestoredFrom = restored ? sourceFile : null
                };
            }

            bool hasPersistedFile = HasAnyPersistedFile(filename);
            log?.Invoke(hasPersistedFile
                ? $"[配置加载] 失败：{Path.GetFileName(filename)} 损坏且无法从备份恢复"
                : $"[配置加载] 首次部署：{Path.GetFileName(filename)} 尚不存在");

            return new ConfigLoadResult<T>
            {
                Data = createDefault(),
                LoadFailed = hasPersistedFile
            };
        }

        /// <summary>
        /// 安全保存 XML。数据先写入临时文件并通过反序列化校验，再替换正式文件。
        /// </summary>
        /// <typeparam name="T">XML 对应的配置类型。</typeparam>
        /// <param name="filename">主 XML 文件路径。</param>
        /// <param name="data">准备落盘的配置对象。</param>
        /// <param name="loadFailed">本轮加载是否发生无法恢复的失败。</param>
        /// <param name="log">现场诊断日志入口。</param>
        /// <returns>正式文件完成更新时返回 true。</returns>
        public static bool TrySave<T>(string filename, T data, bool loadFailed, Action<string> log = null) where T : class
        {
            if (data == null)
            {
                log?.Invoke($"[配置保存] 跳过：{Path.GetFileName(filename)} 数据为空");
                return false;
            }

            if (loadFailed)
            {
                log?.Invoke($"[配置保存] 跳过：{Path.GetFileName(filename)} 本次加载失败，保留现场文件");
                return false;
            }

            try
            {
                SaveValidatedXml(filename, data, log);
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
        /// 启动加载前将目录中的 XML 复制到带时间戳的备份目录。
        /// </summary>
        /// <param name="sourceDir">待备份的配置目录。</param>
        /// <param name="backupRoot">备份根目录。</param>
        /// <param name="categoryName">备份分类名称。</param>
        /// <param name="log">现场诊断日志入口。</param>
        public static void BackupXmlFiles(string sourceDir, string backupRoot, string categoryName, Action<string> log = null)
        {
            try
            {
                PrepareBackupRoot(backupRoot, log);

                if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                {
                    return;
                }

                string[] xmlFiles = Directory.GetFiles(sourceDir, "*.xml", SearchOption.TopDirectoryOnly);
                if (xmlFiles.Length == 0)
                {
                    return;
                }

                string targetDir = Path.Combine(
                    backupRoot,
                    DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                    SanitizeDirectoryName(categoryName));
                Directory.CreateDirectory(targetDir);

                int copiedCount = 0;
                foreach (string sourceFile in xmlFiles)
                {
                    try
                    {
                        File.Copy(sourceFile, Path.Combine(targetDir, Path.GetFileName(sourceFile)), true);
                        copiedCount++;
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[配置备份] 文件备份失败：{Path.GetFileName(sourceFile)}，{ex.Message}");
                    }
                }

                if (copiedCount > 0)
                {
                    log?.Invoke($"[配置备份] 已备份{copiedCount}个XML到：{targetDir}");
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
                "2. 系统配置位于时间戳目录的“系统配置”中；工程工具位于“工程_工程名”中。\r\n" +
                "3. 正常运行时无需手工修改、移动或删除这些文件。\r\n" +
                "4. 需要人工恢复时：先关闭软件，备份当前故障文件，再把选定时间戳中的 XML 复制回原“配置”或工程目录。\r\n" +
                "5. 正式 XML 旁的 .bak 是上一次保存版本，.lastgood 是最近一次校验通过版本，由软件自动恢复。\r\n" +
                "6. 恢复后请核对当前工程、相机序列号、PLC参数和点检配置后再投入生产。\r\n";

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
        /// 仅保留最近的时间戳备份目录。readme 和其他非时间戳目录不受该清理策略影响。
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

        private static bool IsEmptyTool(ToolModel tool)
        {
            if (tool == null)
            {
                return true;
            }

            bool usesDefaultCommand = string.IsNullOrWhiteSpace(tool.Command)
                || string.Equals(tool.Command, "A1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tool.Command, "T1-1", StringComparison.OrdinalIgnoreCase);

            return string.IsNullOrWhiteSpace(tool.Name)
                && string.IsNullOrWhiteSpace(tool.CameraSerialNumber)
                && usesDefaultCommand;
        }

        private static bool HasAnyPersistedFile(string filename)
        {
            return File.Exists(filename)
                || File.Exists(filename + BackupExtension)
                || File.Exists(filename + LastGoodExtension);
        }

        private static IEnumerable<string> GetLoadCandidates(string filename)
        {
            yield return filename;
            yield return filename + BackupExtension;
            yield return filename + LastGoodExtension;
        }

        private static bool TryDeserialize<T>(string filename, out T data) where T : class
        {
            data = null;
            try
            {
                if (!File.Exists(filename))
                {
                    return false;
                }

                using (var stream = File.OpenRead(filename))
                {
                    var serializer = new XmlSerializer(typeof(T));
                    data = serializer.Deserialize(stream) as T;
                }

                return data != null;
            }
            catch
            {
                data = null;
                return false;
            }
        }

        private static void SaveValidatedXml<T>(string filename, T data, Action<string> log) where T : class
        {
            EnsureDirectory(filename);

            string tempFile = filename + ".tmp";
            string backupFile = filename + BackupExtension;
            string lastGoodFile = filename + LastGoodExtension;

            try
            {
                using (var stream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var serializer = new XmlSerializer(typeof(T));
                    serializer.Serialize(stream, data);
                    stream.Flush();
                }

                T verified;
                if (!TryDeserialize(tempFile, out verified))
                {
                    throw new InvalidOperationException("临时文件校验失败");
                }

                if (File.Exists(filename))
                {
                    TryCopyFile(filename, backupFile, log);
                    ReplaceExistingFile(tempFile, filename);
                }
                else
                {
                    File.Move(tempFile, filename);
                }

                TryCopyFile(filename, lastGoodFile, log);
            }
            finally
            {
                TryDeleteFile(tempFile);
            }
        }

        private static void TryCopyFile(string sourceFile, string destinationFile, Action<string> log)
        {
            try
            {
                EnsureDirectory(destinationFile);
                string tempCopy = destinationFile + ".copytmp";
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
            catch (Exception ex)
            {
                log?.Invoke($"[配置备份] 刷新失败：{Path.GetFileName(destinationFile)}，{ex.Message}");
            }
            finally
            {
                TryDeleteFile(destinationFile + ".copytmp");
            }
        }

        private static void EnsureDirectory(string filename)
        {
            string dir = Path.GetDirectoryName(filename);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
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
