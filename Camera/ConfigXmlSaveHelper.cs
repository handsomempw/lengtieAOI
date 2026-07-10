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
    /// <para>该组件保护现场相机序列号、曝光时间等调试参数，避免配置损坏后由默认参数覆盖磁盘文件，同时支持首装自动生成配置。</para>
    /// </summary>
    internal static class ConfigXmlSaveHelper
    {
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
        /// <returns>返回 true 表示配置已成功落盘。</returns>
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
        /// <returns>返回 true 表示配置已成功落盘。</returns>
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
            return File.Exists(filename)
                || File.Exists(filename + ".bak")
                || File.Exists(filename + ".lastgood");
        }

        /// <summary>
        /// 返回相机配置的恢复候选顺序。主文件优先，<c>.bak</c> 和 <c>.lastgood</c> 提供现场恢复兜底。
        /// </summary>
        /// <param name="filename">主配置文件完整路径。</param>
        /// <returns>按加载优先级排列的候选文件路径。</returns>
        private static IEnumerable<string> GetLoadCandidates(string filename)
        {
            yield return filename;
            yield return filename + ".bak";
            yield return filename + ".lastgood";
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
            try
            {
                string dir = Path.GetDirectoryName(destinationFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Copy(sourceFile, destinationFile, true);
            }
            catch
            {
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
            string dir = Path.GetDirectoryName(filename);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string backupFile = filename + ".bak";
            string lastGoodFile = filename + ".lastgood";
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

                if (File.Exists(filename))
                {
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
                if (File.Exists(tempFile))
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
