using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace Camera
{
    /// <summary>
    /// 相机配置 XML 的加载结果。相机启动流程用该结果区分正常加载、备份恢复、首装默认模板和配置损坏回退。
    /// <para>已有配置损坏时，保存流程会阻止默认相机参数覆盖磁盘文件；首装无文件时，默认相机参数允许在关闭软件时生成初始 XML。</para>
    /// </summary>
    /// <typeparam name="T">XML 根节点对应的配置模型类型。</typeparam>
    internal sealed class ConfigLoadResult<T> where T : class
    {
        /// <summary>
        /// 当前进入内存的相机配置对象。正常加载和备份恢复时来自 XML；首装或恢复失败时来自默认模板。
        /// </summary>
        public T Data { get; set; }

        /// <summary>
        /// 表示相机配置文件曾经存在但主文件和备份均无法反序列化。该标志会阻止默认相机参数写回现场文件。
        /// </summary>
        public bool LoadFailed { get; set; }

        /// <summary>
        /// 表示主相机配置不可用时已从 <c>.bak</c> 或 <c>.lastgood</c> 恢复。
        /// </summary>
        public bool RestoredFromBackup { get; set; }
    }

    /// <summary>
    /// 相机配置 XML 的安全读写入口。相机初始化和软件关闭保存共用该入口，统一处理主文件、备份恢复、临时文件校验和原子替换。
    /// <para>相机序列号、曝光时间等调试参数的恢复副本集中保存在“配置\备份”，同时支持首装自动生成配置。</para>
    /// </summary>
    internal static class ConfigXmlSaveHelper
    {
        private const string BackupExtension = ".bak";
        private const string LastGoodExtension = ".lastgood";
        private const string RecoveryDirectoryName = "备份";

        /// <summary>
        /// 加载相机配置。主文件损坏时按备份顺序恢复，首装无文件时返回默认相机参数并允许后续保存。
        /// </summary>
        /// <param name="filename">相机配置 XML 完整路径。</param>
        /// <returns>返回相机配置对象及其加载状态。</returns>
        public static ConfigLoadResult<CameraModel> TryLoad(string filename)
        {
            return TryLoad(filename, () => new CameraModel(), null);
        }

        /// <summary>
        /// 按主文件、<c>.bak</c>、<c>.lastgood</c> 的顺序加载配置。该泛型入口便于相机项目后续增加同类 XML 配置。
        /// </summary>
        /// <typeparam name="T">XML 对应的配置模型类型。</typeparam>
        /// <param name="filename">主配置文件完整路径。</param>
        /// <param name="createDefault">无任何候选文件或恢复失败时用于创建内存模板的函数。</param>
        /// <param name="log">可选诊断日志入口。</param>
        /// <returns>返回配置对象以及是否从备份恢复、是否加载失败。</returns>
        public static ConfigLoadResult<T> TryLoad<T>(string filename, Func<T> createDefault, Action<string> log = null) where T : class
        {
            MigrateLegacyRecoveryFiles(filename);

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
                        RestoredFromBackup = true
                    };
                }

                return new ConfigLoadResult<T>
                {
                    Data = data,
                    LoadFailed = false
                };
            }

            return new ConfigLoadResult<T>
            {
                Data = createDefault(),
                LoadFailed = HasAnyPersistedFile(filename)
            };
        }

        /// <summary>
        /// 保存相机配置。启动时确认配置损坏的场景会跳过保存，避免默认参数覆盖现场调机结果。
        /// </summary>
        /// <param name="filename">相机配置 XML 完整路径。</param>
        /// <param name="data">准备保存的相机配置。</param>
        /// <param name="loadFailed">本轮启动时相机配置是否发生无法恢复的加载失败。</param>
        /// <returns>配置完成更新或内容保持一致时返回 true。</returns>
        public static bool TrySave(string filename, CameraModel data, bool loadFailed)
        {
            return TrySave<CameraModel>(filename, data, loadFailed);
        }

        /// <summary>
        /// 安全保存配置 XML。正常场景写入临时文件、校验临时文件并完成原子替换；加载失败场景保留磁盘文件供维护人员恢复。
        /// </summary>
        /// <typeparam name="T">XML 对应的配置模型类型。</typeparam>
        /// <param name="filename">主配置文件完整路径。</param>
        /// <param name="data">准备保存的内存配置对象。</param>
        /// <param name="loadFailed">本轮启动时该配置是否发生无法恢复的加载失败。</param>
        /// <returns>配置完成更新或内容保持一致时返回 true。</returns>
        public static bool TrySave<T>(string filename, T data, bool loadFailed) where T : class
        {
            if (data == null || loadFailed)
            {
                return false;
            }

            try
            {
                AtomicSerialize(filename, data);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断主文件或任一备份文件是否曾经存在。该判断用于区分首装无文件和已有文件损坏。
        /// </summary>
        /// <param name="filename">主配置文件完整路径。</param>
        /// <returns>返回 true 表示存在主文件、<c>.bak</c> 或 <c>.lastgood</c> 中的任一文件。</returns>
        private static bool HasAnyPersistedFile(string filename)
        {
            foreach (string candidate in GetLoadCandidates(filename))
            {
                if (File.Exists(candidate))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 返回相机配置的恢复候选顺序。主文件优先，<c>.bak</c> 和 <c>.lastgood</c> 提供现场恢复兜底。
        /// </summary>
        /// <param name="filename">主配置文件完整路径。</param>
        /// <returns>按加载优先级排列的候选文件路径。</returns>
        private static IEnumerable<string> GetLoadCandidates(string filename)
        {
            yield return filename;
            yield return GetRecoveryFilePath(filename, BackupExtension);
            yield return GetRecoveryFilePath(filename, LastGoodExtension);

            // 兼容升级前紧邻正式文件的恢复副本。
            yield return filename + BackupExtension;
            yield return filename + LastGoodExtension;
        }

        private static string GetRecoveryFilePath(string filename, string extension)
        {
            string directory = Path.GetDirectoryName(filename) ?? string.Empty;
            return Path.Combine(directory, RecoveryDirectoryName, Path.GetFileName(filename) + extension);
        }

        private static void MigrateLegacyRecoveryFiles(string filename)
        {
            MigrateLegacyRecoveryFile(filename, BackupExtension);
            MigrateLegacyRecoveryFile(filename, LastGoodExtension);
        }

        private static void MigrateLegacyRecoveryFile(string filename, string extension)
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
            }
            catch
            {
                // 整理失败时保留原文件，加载候选继续包含旧路径。
            }
        }

        /// <summary>
        /// 尝试反序列化指定 XML 文件。文件缺失、格式损坏或类型不匹配统一返回 null，由上层决定恢复或保护策略。
        /// </summary>
        /// <typeparam name="T">XML 对应的配置模型类型。</typeparam>
        /// <param name="filename">待读取的文件路径。</param>
        /// <returns>返回配置对象或 null。</returns>
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
        /// 将可用备份复制回主文件路径。复制失败只影响磁盘修复结果，已恢复的内存配置仍可用于本轮相机初始化。
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
        /// 将相机配置写入临时文件并校验可读，再通过原子替换更新主文件。成功保存后刷新最近一次可读副本 <c>.lastgood</c>。
        /// </summary>
        /// <typeparam name="T">XML 对应的配置模型类型。</typeparam>
        /// <param name="filename">主配置文件完整路径。</param>
        /// <param name="data">准备写入的配置对象。</param>
        private static void AtomicSerialize<T>(string filename, T data) where T : class
        {
            EnsureDirectory(filename);

            string backupFile = GetRecoveryFilePath(filename, BackupExtension);
            string lastGoodFile = GetRecoveryFilePath(filename, LastGoodExtension);
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
                    TryCopyFile(filename, lastGoodFile);
                    return;
                }

                if (File.Exists(filename))
                {
                    EnsureDirectory(backupFile);
                    File.Replace(tempFile, filename, backupFile);
                }
                else
                {
                    File.Move(tempFile, filename);
                }

                TryCopyFile(filename, lastGoodFile);
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
