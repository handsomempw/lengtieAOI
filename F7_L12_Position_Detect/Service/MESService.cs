using System;
using System.Threading.Tasks;
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
        /// 处理扫码并获取SN码
        /// 集成了DecodeSN和GetSNbyMSN的逻辑
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

                    // 调用MES_ORACLE_DATABASE的DecodeSN方法解析SN
                    string decodedSN = MES_ORACLE_DATABASE.MES_ORACLE_DATABASE.DecodeSN(scanCode);
                    
                    if (!string.IsNullOrEmpty(decodedSN))
                    {
                        return new ScanCodeResult
                        {
                            Success = true,
                            SN = decodedSN,
                            StatusMessage = "SN获取成功"
                        };
                    }
                    else
                    {
                        return new ScanCodeResult
                        {
                            Success = false,
                            ErrorMessage = "无法解析扫码内容获取SN",
                            StatusMessage = "SN获取失败"
                        };
                    }
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
