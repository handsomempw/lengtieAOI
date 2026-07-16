using System;
using System.Reflection;
using System.Threading.Tasks;
using MESAutoLineClient;
using MES_ORACLE_DATABASE;

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

        /// <summary>
        /// 处理扫码并获取最终产品SN。优先通过MES查询条码最终绑定的产品SN，查询失败时保留DecodeSN兼容普通生产扫码。
        /// </summary>
        /// <param name="scanCode">扫码获取的原始编码</param>
        /// <returns>处理结果</returns>
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

                    string resolvedSN = TryGetLastBindingProductSN(scanCode, out string finalSnError);
                    if (string.IsNullOrWhiteSpace(resolvedSN))
                    {
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
                        ErrorMessage = string.IsNullOrWhiteSpace(finalSnError)
                            ? "无法解析扫码内容获取最终产品SN"
                            : $"无法解析扫码内容获取最终产品SN；MES最终绑定查询：{finalSnError}",
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
        /// 查询条码在MES中的最终绑定产品SN。普通生产保留DecodeSN兼容路径，点检还会通过产品查询接口进行第二次实时确认。
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
        /// 复用MES数据库组件已经初始化的客户端，保证最终SN查询与现有工位连接配置一致。
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
        /// <param name="sn">产品SN码</param>
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
                            StatusMessage = "工序校验失败"
                        };
                    }
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
        /// 完整的扫码处理流程
        /// 包括扫码解析、SN获取和工序校验
        /// </summary>
        /// <param name="scanCode">扫码内容</param>
        /// <param name="procedureName">工序名称</param>
        /// <returns>完整处理结果</returns>
        public async Task<(ScanCodeResult scanResult, ProcessValidationResult validationResult)> ProcessScanCodeWithValidationAsync(string scanCode, string procedureName)
        {
            // 第一步：处理扫码获取SN
            var scanResult = await ProcessScanCodeAsync(scanCode);
            
            ProcessValidationResult validationResult = null;
            
            // 第二步：如果SN获取成功，进行工序校验
            if (scanResult.Success && !string.IsNullOrEmpty(scanResult.SN))
            {
                validationResult = await ValidateProcessAsync(scanResult.SN, procedureName);
            }
            else
            {
                // SN获取失败，跳过工序校验
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
