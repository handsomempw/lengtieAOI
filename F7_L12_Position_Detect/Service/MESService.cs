using System;
using System.Reflection;
using System.Threading.Tasks;
using MESAutoLineClient;
using MES_ORACLE_DATABASE;
using F7_L12_Position_Detect.Model;

namespace F7_L12_Position_Detect.Service
{
    /// <summary>
    /// MES系统接口服务类
    /// 封装扫码解析、SN获取和工序校验功能
    /// </summary>
    public class MESService
    {
        /// <summary>
        /// 扫码结果数据结构
        /// </summary>
        public class ScanCodeResult
        {
            public bool Success { get; set; }
            public string SN { get; set; }
            public string ErrorMessage { get; set; }
            public string StatusMessage { get; set; }
        }

        /// <summary>
        /// 工序校验结果数据结构
        /// </summary>
        public class ProcessValidationResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public string StatusMessage { get; set; }
        }

        /// <summary>
        /// 点检样件最终产品SN的MES确认结果。该结果只表达身份确认，不承载工序校验和产品报工结论。
        /// </summary>
        public class InspectionSnValidationResult
        {
            /// <summary>
            /// MES返回最终产品SN且与配置的点检SN一致时为true。
            /// </summary>
            public bool Success { get; set; }

            /// <summary>
            /// MES确认得到的最终产品SN，用于进入点检模式和日志追溯。
            /// </summary>
            public string SN { get; set; }

            /// <summary>
            /// 身份确认失败时提供现场可见且可定位的原因。
            /// </summary>
            public string ErrorMessage { get; set; }
        }

        private static bool IsTimeoutException(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                string typeName = current.GetType().Name;
                string message = current.Message ?? string.Empty;
                if (current is TimeoutException
                    || typeName.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("超时", StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 处理扫码并获取产品SN。解码策略由配置 <see cref="ReportWorkConfig.SnDecodeMode"/> 决定。
        /// </summary>
        /// <param name="scanCode">扫码获取的原始编码</param>
        /// <param name="snDecodeMode">
        /// DecodeSN：仅 DecodeSN；最终绑定：先查MES最终绑定产品SN，失败再回退 DecodeSN。
        /// 空或非法值按 DecodeSN 处理。
        /// </param>
        /// <returns>处理结果</returns>
        public async Task<ScanCodeResult> ProcessScanCodeAsync(
            string scanCode,
            string snDecodeMode = ReportWorkConfig.SnDecodeModeDecodeSN)
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

                    string mode = NormalizeSnDecodeMode(snDecodeMode);
                    string resolvedSN;
                    string finalSnError = string.Empty;

                    if (mode == ReportWorkConfig.SnDecodeModeLastBinding)
                    {
                        // 最终绑定：优先 MES 零件码最终绑定产品SN，查不到再 DecodeSN
                        resolvedSN = TryGetLastBindingProductSN(scanCode, out finalSnError);
                        if (string.IsNullOrWhiteSpace(resolvedSN))
                        {
                            resolvedSN = MES_ORACLE_DATABASE.MES_ORACLE_DATABASE.DecodeSN(scanCode);
                        }
                    }
                    else
                    {
                        // DecodeSN：仅按码内容解析
                        resolvedSN = MES_ORACLE_DATABASE.MES_ORACLE_DATABASE.DecodeSN(scanCode);
                    }

                    if (!string.IsNullOrWhiteSpace(resolvedSN))
                    {
                        return new ScanCodeResult
                        {
                            Success = true,
                            SN = resolvedSN.Trim(),
                            StatusMessage = "SN获取成功"
                        };
                    }

                    return new ScanCodeResult
                    {
                        Success = false,
                        ErrorMessage = mode == ReportWorkConfig.SnDecodeModeLastBinding
                            && !string.IsNullOrWhiteSpace(finalSnError)
                            ? $"无法解析扫码内容获取最终产品SN；MES最终绑定查询：{finalSnError}"
                            : "无法解析扫码内容获取SN",
                        StatusMessage = "SN获取失败"
                    };
                }
                catch (Exception ex)
                {
                    return new ScanCodeResult
                    {
                        Success = false,
                        ErrorMessage = $"处理扫码时发生异常: {ex.Message}",
                        StatusMessage = IsTimeoutException(ex) ? "MES超时" : "SN获取失败"
                    };
                }
            });
        }

        /// <summary>
        /// 将配置中的SN解码方式规范为受支持取值；非法或空值回退 DecodeSN。
        /// </summary>
        public static string NormalizeSnDecodeMode(string snDecodeMode)
        {
            string normalized = (snDecodeMode ?? string.Empty).Trim();
            if (string.Equals(normalized, ReportWorkConfig.SnDecodeModeLastBinding, StringComparison.OrdinalIgnoreCase)
                || normalized == "LastBinding"
                || normalized == "MES最终绑定")
            {
                return ReportWorkConfig.SnDecodeModeLastBinding;
            }

            return ReportWorkConfig.SnDecodeModeDecodeSN;
        }

        /// <summary>
        /// 通过MES实时确认点检样件最终产品SN，并与XML中对应的固定点检SN匹配。
        /// </summary>
        /// <param name="resolvedSN">扫码解析得到的最终产品SN。</param>
        /// <param name="expectedInspectionSN">XML中配置的OK或NG点检最终产品SN。</param>
        /// <returns>MES确认成功且SN匹配时返回成功结果；产品编码和工序状态不参与此校验。</returns>
        public async Task<InspectionSnValidationResult> ValidateInspectionSNAsync(
            string resolvedSN,
            string expectedInspectionSN)
        {
            return await Task.Run(() =>
            {
                string normalizedResolvedSN = (resolvedSN ?? string.Empty).Trim();
                string normalizedExpectedSN = (expectedInspectionSN ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(normalizedExpectedSN))
                {
                    return new InspectionSnValidationResult
                    {
                        Success = false,
                        ErrorMessage = "点检最终产品SN未配置"
                    };
                }

                if (string.IsNullOrEmpty(normalizedResolvedSN))
                {
                    return new InspectionSnValidationResult
                    {
                        Success = false,
                        ErrorMessage = "MES解析得到的最终产品SN为空"
                    };
                }

                AutoLineClient client = TryGetAutoLineClient(out string clientError);
                if (client == null)
                {
                    return new InspectionSnValidationResult
                    {
                        Success = false,
                        ErrorMessage = $"MES客户端不可用：{clientError}"
                    };
                }

                try
                {
                    var result = client.GetSerializedProduct(normalizedResolvedSN);
                    if (result == null || !result.Success || result.Data == null || string.IsNullOrWhiteSpace(result.Data.Sn))
                    {
                        return new InspectionSnValidationResult
                        {
                            Success = false,
                            ErrorMessage = $"MES未确认点检最终产品SN：{result?.Message ?? "返回数据为空"}"
                        };
                    }

                    string mesConfirmedSN = result.Data.Sn.Trim();
                    if (!string.Equals(mesConfirmedSN, normalizedExpectedSN, StringComparison.OrdinalIgnoreCase))
                    {
                        return new InspectionSnValidationResult
                        {
                            Success = false,
                            SN = mesConfirmedSN,
                            ErrorMessage = $"点检SN不匹配，MES最终SN={mesConfirmedSN}，配置SN={normalizedExpectedSN}"
                        };
                    }

                    return new InspectionSnValidationResult
                    {
                        Success = true,
                        SN = mesConfirmedSN
                    };
                }
                catch (Exception ex)
                {
                    return new InspectionSnValidationResult
                    {
                        Success = false,
                        ErrorMessage = $"MES确认点检最终产品SN异常：{ex.Message}"
                    };
                }
            });
        }

        /// <summary>
        /// 查询条码在MES中的最终绑定产品SN，供「最终绑定」解码方式使用。
        /// </summary>
        private static string TryGetLastBindingProductSN(string barcode, out string errorMessage)
        {
            errorMessage = string.Empty;
            AutoLineClient client = TryGetAutoLineClient(out errorMessage);
            if (client == null)
            {
                return string.Empty;
            }

            try
            {
                var result = client.GetLastBindingProductSerialNumberOfPartBarcode(barcode);
                if (result != null && result.Success && !string.IsNullOrWhiteSpace(result.Data))
                {
                    return result.Data.Trim();
                }

                errorMessage = result?.Message ?? "最终绑定产品SN为空";
                return string.Empty;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return string.Empty;
            }
        }

        /// <summary>
        /// 复用MES数据库组件已初始化的客户端，供最终绑定查询与点检样件身份二次确认使用。
        /// </summary>
        private static AutoLineClient TryGetAutoLineClient(out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                FieldInfo clientField = typeof(MES_ORACLE_DATABASE.MES_ORACLE_DATABASE)
                    .GetField("client", BindingFlags.NonPublic | BindingFlags.Static);
                AutoLineClient client = clientField?.GetValue(null) as AutoLineClient;
                if (client == null)
                {
                    errorMessage = "AutoLineClient未初始化";
                }
                return client;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 执行工序校验
        /// </summary>
        /// <param name="sn">产品SN</param>
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

                    // 调用MES_ORACLE_DATABASE的check_productNO方法进行工序校验
                    bool validationResult = MES_ORACLE_DATABASE.MES_ORACLE_DATABASE.check_productNO(sn, procedureName);
                    
                    if (validationResult)
                    {
                        return new ProcessValidationResult
                        {
                            Success = true,
                            StatusMessage = "工序校验通过"
                        };
                    }
                    else
                    {
                        return new ProcessValidationResult
                        {
                            Success = false,
                            ErrorMessage = $"产品 {sn} 不符合工序 {procedureName} 的要求",
                            StatusMessage = "工序不通过"
                        };
                    }
                }
                catch (Exception ex)
                {
                    return new ProcessValidationResult
                    {
                        Success = false,
                        ErrorMessage = $"工序校验时发生异常: {ex.Message}",
                        StatusMessage = IsTimeoutException(ex) ? "MES超时" : "工序校验失败"
                    };
                }
            });
        }

        /// <summary>
        /// 完整的扫码处理流程
        /// 包括扫码解析、SN获取和工序校验
        /// </summary>
        /// <param name="scanCode">扫码内容</param>
        /// <param name="procedureName">工序名称</param>
        /// <param name="snDecodeMode">SN解码方式，见 <see cref="ReportWorkConfig.SnDecodeMode"/></param>
        /// <returns>完整处理结果</returns>
        public async Task<(ScanCodeResult scanResult, ProcessValidationResult validationResult)> ProcessScanCodeWithValidationAsync(
            string scanCode,
            string procedureName,
            string snDecodeMode = ReportWorkConfig.SnDecodeModeDecodeSN)
        {
            // 第一步：处理扫码获取SN
            var scanResult = await ProcessScanCodeAsync(scanCode, snDecodeMode);
            
            ProcessValidationResult validationResult = null;
            
            // 第二步：如果SN获取成功，进行工序校验
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
