using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace Camera
{
    /// <summary>
    /// 相机配置加载结果。相机关闭保存通过该状态避免用默认参数覆盖无法恢复的现场文件。
    /// </summary>
    /// <typeparam name="T">XML 根节点对应的数据模型类型。</typeparam>
    internal sealed class ConfigLoadResult<T> where T : class
    {
        /// <summary>
        /// 本轮进入内存的相机配置。
        /// </summary>
        public T Data { get; set; }

        /// <summary>
        /// 主文件和备份均无法反序列化时为 true。
        /// </summary>
        public bool LoadFailed { get; set; }

        /// <summary>
        /// 主文件不可用时是否已从备份恢复。
        /// </summary>
        public bool RestoredFromBackup { get; set; }
    }

    /// <summary>
    /// 相机配置 XML 的安全读写入口。该入口保护相机序列号和曝光参数，恢复副本集中保存在“配置\备份”目录。
    /// </summary>
    internal static class ConfigXmlSaveHelper
    {
        private const string BackupExtension = ".bak";
        private const string LastGoodExtension = ".lastgood";
        private const string RecoveryDirectoryName = "备份";

        /// <summary>
        /// 加载相机配置，主文件损坏时按备份顺序自动恢复。
        /// </summary>
        /// <param name="filename">相机配置 XML 完整路径。</param>
        /// <returns>相机配置及加载健康状态。</returns>
        public static ConfigLoadResult<CameraModel> TryLoad(string filename)
        {
            MigrateLegacyRecoveryFiles(filename);

            foreach (string sourceFile in GetLoadCandidates(filename))
            {
                CameraModel data;
                if (!TryDeserialize(sourceFile, out data))
                {
                    continue;
                }

                bool restored = !string.Equals(sourceFile, filename, StringComparison.OrdinalIgnoreCase);
                if (restored)
                {
                    TryCopyFile(sourceFile, filename);
                }

                return new ConfigLoadResult<CameraModel>
                {
                    Data = data,
                    LoadFailed = false,
                    RestoredFromBackup = restored
                };
            }

            return new ConfigLoadResult<CameraModel>
            {
                Data = new CameraModel(),
                LoadFailed = HasAnyPersistedFile(filename)
            };
        }

        /// <summary>
        /// 安全保存相机配置。加载失败场景保留磁盘文件，正常场景通过临时文件校验和原子替换落盘。
        /// </summary>
        /// <param name="filename">相机配置 XML 完整路径。</param>
        /// <param name="data">准备落盘的相机配置。</param>
        /// <param name="loadFailed">本轮启动是否发生无法恢复的加载失败。</param>
        /// <returns>正式文件完成更新或内容保持一致时返回 true。</returns>
        public static bool TrySave(string filename, CameraModel data, bool loadFailed)
        {
            if (data == null || loadFailed)
            {
                return false;
            }

            try
            {
                SaveValidatedXml(filename, data);
                return true;
            }
            catch
            {
                return false;
            }
        }

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

        private static IEnumerable<string> GetLoadCandidates(string filename)
        {
            yield return filename;
            yield return GetRecoveryFilePath(filename, BackupExtension);
            yield return GetRecoveryFilePath(filename, LastGoodExtension);

            // 兼容旧版本：升级后仍可读取原先紧邻正式文件的恢复副本。
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
                // 整理失败时保留原文件，后续加载候选仍包含旧路径。
            }
        }

        private static bool TryDeserialize(string filename, out CameraModel data)
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
                    var serializer = new XmlSerializer(typeof(CameraModel));
                    data = serializer.Deserialize(stream) as CameraModel;
                }

                return data != null;
            }
            catch
            {
                data = null;
                return false;
            }
        }

        private static void SaveValidatedXml(string filename, CameraModel data)
        {
            EnsureDirectory(filename);
            string tempFile = filename + ".tmp";
            string backupFile = GetRecoveryFilePath(filename, BackupExtension);
            string lastGoodFile = GetRecoveryFilePath(filename, LastGoodExtension);

            try
            {
                using (var stream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var serializer = new XmlSerializer(typeof(CameraModel));
                    serializer.Serialize(stream, data);
                    stream.Flush();
                }

                CameraModel verified;
                if (!TryDeserialize(tempFile, out verified))
                {
                    throw new InvalidOperationException("相机配置临时文件校验失败");
                }

                if (File.Exists(filename) && FilesHaveSameContent(tempFile, filename))
                {
                    TryCopyFile(filename, lastGoodFile);
                    return;
                }

                if (File.Exists(filename))
                {
                    TryCopyFile(filename, backupFile);
                    ReplaceExistingFile(tempFile, filename);
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
            string dir = Path.GetDirectoryName(filename);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
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
