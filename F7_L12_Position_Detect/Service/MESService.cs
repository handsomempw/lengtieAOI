using System;
using System.Reflection;
using System.Threading.Tasks;
using MES_ORACLE_DATABASE;

namespace F7_L12_Position_Detect.Service
{
    /// <summary>
    /// MES 接口服务，封装扫码解析、SN 获取与工序校验能力。
    /// </summary>
    public class MESService
    {
        /// <summary>
        /// 读取 MES_ORACLE_DATABASE 已初始化的最新客户端。绑定服务只通过反射调用稳定的字符串接口，
        /// 这样部署 DLL 返回值从旧版 Message 迁移到新版 Data 时不影响流程代码。
        /// </summary>
        public static object GetInitializedAutoLineClient()
        {
            var field = typeof(MES_ORACLE_DATABASE.MES_ORACLE_DATABASE)
                .GetField("client", BindingFlags.NonPublic | BindingFlags.Static);
            object client = field?.GetValue(null);
            if (client != null) return client;

            // 兼容旧版数据库封装未暴露静态客户端的部署包，仍优先使用项目当前引用的 DLL。
            Type clientType = Type.GetType("MESAutoLineClient.AutoLineClient, MESAutoLineClient");
            MethodInfo create = clientType?.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            return create?.Invoke(null, new object[] { "http://mes-equip.efara.cn" });
        }
        /// <summary>
        /// 扫码处理结果。
        /// </summary>
        public class ScanCodeResult
        {
            public bool Success { get; set; }
            public string SN { get; set; }
            public string ErrorMessage { get; set; }
            public string StatusMessage { get; set; }
        }

        /// <summary>
        /// 工序校验结果。
        /// </summary>
        public class ProcessValidationResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public string StatusMessage { get; set; }
        }

        /// <summary>
        /// 处理扫码并获取产品 SN。
        /// 优先获取最外层（最终绑定）SN，失败后回退 DecodeSN。
        /// </summary>
        /// <param name="scanCode">扫码原始内容</param>
        /// <returns>扫码处理结果</returns>
        public async Task<ScanCodeResult> ProcessScanCodeAsync(string scanCode)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(scanCode))
                    {
                        return new ScanCodeResult
                        {
                            Success = false,
                            ErrorMessage = "扫码内容为空",
                            StatusMessage = "扫码失败"
                        };
                    }

                    // 优先获取最外层（最终绑定）SN。
                    string resolvedSN = TryGetLastBindingProductSN(scanCode, out string outerSnError);

                    if (string.IsNullOrEmpty(resolvedSN))
                    {
                        // 兼容旧链路：最外层 SN 获取失败时回退到 DecodeSN。
                        resolvedSN = MES_ORACLE_DATABASE.MES_ORACLE_DATABASE.DecodeSN(scanCode);
                    }

                    if (!string.IsNullOrEmpty(resolvedSN))
                    {
                        return new ScanCodeResult
                        {
                            Success = true,
                            SN = resolvedSN,
                            StatusMessage = "SN获取成功"
                        };
                    }

                    return new ScanCodeResult
                    {
                        Success = false,
                        ErrorMessage = string.IsNullOrWhiteSpace(outerSnError)
                            ? "无法解析扫码内容获取SN"
                            : $"无法解析扫码内容获取SN；最外层SN错误：{outerSnError}",
                        StatusMessage = "SN获取失败"
                    };
                }
                catch (Exception ex)
                {
                    return new ScanCodeResult
                    {
                        Success = false,
                        ErrorMessage = $"处理扫码时发生异常: {ex.Message}",
                        StatusMessage = "SN获取失败"
                    };
                }
            });
        }

        /// <summary>
        /// 通过条码获取最外层（最终绑定）产品 SN。
        /// </summary>
        /// <param name="barcode">零件条码或中间序列号</param>
        /// <param name="errorMessage">失败原因（用于日志）</param>
        /// <returns>最终绑定 SN；失败时返回空字符串</returns>
        private static string TryGetLastBindingProductSN(string barcode, out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                var clientField = typeof(MES_ORACLE_DATABASE.MES_ORACLE_DATABASE)
                    .GetField("client", BindingFlags.NonPublic | BindingFlags.Static);
                var client = clientField?.GetValue(null);
                if (client == null)
                {
                    errorMessage = "AutoLineClient 未初始化";
                    return string.Empty;
                }

                var method = client.GetType().GetMethod("GetLastBindingProductSerialNumberOfPartBarcode", new[] { typeof(string) });
                if (method == null)
                {
                    errorMessage = "AutoLineClient 未提供最终绑定 SN 接口";
                    return string.Empty;
                }
                var result = method.Invoke(client, new object[] { barcode });
                bool success = result?.GetType().GetProperty("Success")?.GetValue(result, null) is bool ok && ok;
                string data = Convert.ToString(result?.GetType().GetProperty("Data")?.GetValue(result, null));
                if (success && !string.IsNullOrWhiteSpace(data))
                {
                    return data.Trim();
                }

                errorMessage = Convert.ToString(result?.GetType().GetProperty("Message")?.GetValue(result, null)) ?? "最外层 SN 为空";
                return string.Empty;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return string.Empty;
            }
        }

        /// <summary>
        /// 执行工序校验。
        /// </summary>
        /// <param name="sn">产品 SN</param>
        /// <param name="procedureName">工序名称</param>
        /// <returns>校验结果</returns>
        public async Task<ProcessValidationResult> ValidateProcessAsync(string sn, string procedureName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(sn))
                    {
                        return new ProcessValidationResult
                        {
                            Success = false,
                            ErrorMessage = "产品SN不能为空",
                            StatusMessage = "工序校验失败"
                        };
                    }

                    if (string.IsNullOrEmpty(procedureName))
                    {
                        return new ProcessValidationResult
                        {
                            Success = false,
                            ErrorMessage = "工序名称不能为空",
                            StatusMessage = "工序校验失败"
                        };
                    }

                    // 调用 MES_ORACLE_DATABASE 的 check_productNO 方法进行工序校验。
                    bool validationResult = MES_ORACLE_DATABASE.MES_ORACLE_DATABASE.check_productNO(sn, procedureName);

                    if (validationResult)
                    {
                        return new ProcessValidationResult
                        {
                            Success = true,
                            StatusMessage = "工序校验通过"
                        };
                    }

                    return new ProcessValidationResult
                    {
                        Success = false,
                        ErrorMessage = $"产品 {sn} 不符合工序 {procedureName} 的要求",
                        StatusMessage = "工序校验失败"
                    };
                }
                catch (Exception ex)
                {
                    return new ProcessValidationResult
                    {
                        Success = false,
                        ErrorMessage = $"工序校验时发生异常: {ex.Message}",
                        StatusMessage = "工序校验失败"
                    };
                }
            });
        }

        /// <summary>
        /// 完整扫码处理流程：先解析 SN，再执行工序校验。
        /// </summary>
        /// <param name="scanCode">扫码内容</param>
        /// <param name="procedureName">工序名称</param>
        /// <returns>完整处理结果</returns>
        public async Task<(ScanCodeResult scanResult, ProcessValidationResult validationResult)> ProcessScanCodeWithValidationAsync(string scanCode, string procedureName)
        {
            // 第一步：处理扫码并获取 SN。
            var scanResult = await ProcessScanCodeAsync(scanCode);

            ProcessValidationResult validationResult;

            // 第二步：SN 获取成功才进行工序校验。
            if (scanResult.Success && !string.IsNullOrEmpty(scanResult.SN))
            {
                validationResult = await ValidateProcessAsync(scanResult.SN, procedureName);
            }
            else
            {
                validationResult = new ProcessValidationResult
                {
                    Success = false,
                    ErrorMessage = "由于SN获取失败，跳过工序校验",
                    StatusMessage = "工序校验跳过"
                };
            }

            return (scanResult, validationResult);
        }
    }
}
