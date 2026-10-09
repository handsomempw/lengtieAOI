using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace F7_L12_Position_Detect.Service
{
    /// <summary>
    /// 一次母排扫码固定的路径B身份。组装工单、工序、工位和母排码来自同一份扫码会话，
    /// 防止报工期间界面切换工单或路由造成写入参数漂移。
    /// </summary>
    public sealed class BusbarBindingRequest
    {
        public string Barcode { get; set; }
        public string WorkOrder { get; set; }
        public string Procedure { get; set; }
        public string Station { get; set; }
    }

    /// <summary>
    /// 路径B完成后由 MES 分配的最终成品身份。调用方使用该 SN 继续 AOI 工序校验。
    /// </summary>
    public sealed class BusbarBindingResult
    {
        public string SN { get; set; }
        public bool Reused { get; set; }
    }

    /// <summary>
    /// 执行现场验证过的路径B：
    /// SerializedProductAssemble(工单, 空产品SN, 母排码, 组装工序, 组装工位, 上岗工号)，
    /// 读取返回的 ProductSn，再调用 ReportWorkByProduct 完成组装 OK 报工。
    /// 本机 pending/.completed 仅作断点恢复；空 SN 或本地 SN 已失效的 pending，在 MES 查不到成品时自动清除，允许重新取号，避免现场卡死。
    /// 组装工序和工位只服务路径B；AOI 工序由调用方继续使用报工配置。
    /// </summary>
    public sealed class BusbarBindingService
    {
        private readonly IBusbarMesGateway _mes;
        private readonly string _pendingDirectory;
        private readonly Action<string> _log;
        private readonly Action _ensureActive;

        public BusbarBindingService(IBusbarMesGateway mes, string pendingDirectory, Action<string> log, Action ensureActive)
        {
            _mes = mes ?? throw new ArgumentNullException(nameof(mes));
            _pendingDirectory = pendingDirectory;
            _log = log ?? (_ => { });
            _ensureActive = ensureActive ?? (() => { });
        }

        /// <summary>
        /// 按路径B完成单母排组装取号和组装 OK 报工。
        /// 写入前保存待确认记录；取号成功后保存 MES 返回的最终成品 SN；重扫时先复核 MES 实际状态再决定复用、补报工或重新取号。
        /// </summary>
        /// <param name="request">母排码、目标工单、组装工序码和组装工位号。</param>
        /// <returns>最终成品 SN；Reused 表示复用了已有成品或本机已完成记录。</returns>
        /// <exception cref="InvalidOperationException">参数不完整、上岗无效或 MES 拒绝且无法自动恢复。</exception>
        public BusbarBindingResult Resolve(BusbarBindingRequest request)
        {
            ValidateRequest(request);

            string pendingPath = PendingPath(request.Barcode);
            string completedPath = pendingPath + ".completed";
            PendingState pending = ReadPending(pendingPath);
            PendingState completed = ReadPending(completedPath);
            string employee = QueryEmployee(request.Station);

            _log($"节点=状态盘点 母排={request.Barcode} 工单={request.WorkOrder} 工序={request.Procedure} 工位={request.Station} " +
                 $"pending={(pending == null ? "无" : (string.IsNullOrWhiteSpace(pending.SN) ? "空SN" : pending.SN))} " +
                 $"completed={(completed == null ? "无" : (completed.SN ?? ""))} 记录目录={_pendingDirectory}");

            if (completed != null && !string.IsNullOrWhiteSpace(completed.SN))
            {
                try
                {
                    object completedProduct = ConfirmFinishedProduct(completed.SN, request.WorkOrder);
                    EnsureAssemblyReported(completed.SN, request, employee, completedProduct, true);
                    _log($"节点=复用本机已完成记录 成品SN={completed.SN} 路径={completedPath}");
                    return new BusbarBindingResult { SN = completed.SN, Reused = true };
                }
                catch (Exception ex)
                {
                    // 工单切换或本机完成标记过期时，清掉后按当前扫码身份继续，避免卡住产线。
                    _log($"节点=本机完成记录不可用 成品SN={completed.SN} 原因={ex.Message}；已清除 {completedPath}");
                    ClearPending(completedPath);
                }
            }

            string finalSn = pending == null ? string.Empty : (pending.SN ?? string.Empty).Trim();
            object productData = null;
            bool reused = !string.IsNullOrWhiteSpace(finalSn);
            bool hadEmptyPending = pending != null && string.IsNullOrWhiteSpace(finalSn);
            bool pendingSnUnusable = false;

            if (!string.IsNullOrWhiteSpace(finalSn))
            {
                try
                {
                    productData = ConfirmFinishedProduct(finalSn, request.WorkOrder);
                    _log($"节点=复用pending成品SN 成品SN={finalSn} 路径={pendingPath}");
                }
                catch (Exception ex)
                {
                    _log($"节点=pending成品SN不可用 成品SN={finalSn} 原因={ex.Message}；改为向MES关系查询");
                    finalSn = string.Empty;
                    reused = false;
                    productData = null;
                    pendingSnUnusable = true;
                }
            }

            if (string.IsNullOrWhiteSpace(finalSn))
            {
                finalSn = TryFindExistingFinishedSn(request.Barcode, request.WorkOrder, out productData);
                reused = !string.IsNullOrWhiteSpace(finalSn);
                if (reused)
                {
                    _log($"节点=MES关系恢复成品 成品SN={finalSn}");
                    if (hadEmptyPending || pendingSnUnusable)
                        _log($"节点=脏pending已由MES关系恢复 原路径={pendingPath}");
                }
            }

            // 脏 pending（空 SN，或本地 SN 已与当前工单/MES 不符）：MES 也查不到成品时清除，允许重新取号。
            if (string.IsNullOrWhiteSpace(finalSn) && (hadEmptyPending || pendingSnUnusable))
            {
                _log($"节点=清除脏pending 原因={(hadEmptyPending ? "空SN" : "本地SN不可用")}且MES无目标工单成品 路径={pendingPath}；允许重新组装取号");
                ClearPending(pendingPath);
                pending = null;
            }

            if (string.IsNullOrWhiteSpace(finalSn))
            {
                Directory.CreateDirectory(_pendingDirectory);
                WritePending(pendingPath, request, employee, null, "路径B组装取号待确认");
                _log($"节点=写入取号断点 路径={pendingPath} 阶段=路径B组装取号待确认");

                var assemble = Call("路径B组装取号", "SerializedProductAssemble",
                    request.WorkOrder,
                    string.Empty,
                    request.Barcode,
                    request.Procedure,
                    request.Station,
                    employee);
                RequireSuccess(assemble);

                finalSn = ExtractProductSn(assemble.Data);
                _log($"节点=路径B取号返回 ProductSn={finalSn}");
                if (string.IsNullOrWhiteSpace(finalSn))
                {
                    finalSn = TryFindExistingFinishedSn(request.Barcode, request.WorkOrder, out productData);
                    if (!string.IsNullOrWhiteSpace(finalSn))
                        _log($"节点=取号后关系回查补全 成品SN={finalSn}");
                }
                if (string.IsNullOrWhiteSpace(finalSn))
                {
                    throw new InvalidOperationException(
                        $"MES 已响应路径B组装，但未返回 ProductSn；断点已保留，请核实 MES 后重扫。断点文件={pendingPath}");
                }

                productData = ConfirmFinishedProduct(finalSn, request.WorkOrder);
                WritePending(pendingPath, request, employee, finalSn, "路径B组装报工待确认");
                _log($"节点=写入报工断点 成品SN={finalSn} 路径={pendingPath}");
            }
            else if (productData == null)
            {
                productData = ConfirmFinishedProduct(finalSn, request.WorkOrder);
            }

            WritePending(pendingPath, request, employee, finalSn, "路径B组装报工待确认");
            EnsureAssemblyReported(finalSn, request, employee, productData, reused);
            _ensureActive();
            ClearPending(pendingPath);
            WritePending(completedPath, request, employee, finalSn, "路径B组装报工已完成");
            _log($"节点=路径B组装报工完成 成品SN={finalSn} 工序={request.Procedure} 工位={request.Station} 是否复用={reused} 完成记录={completedPath}");
            return new BusbarBindingResult { SN = finalSn, Reused = reused };
        }

        private void ValidateRequest(BusbarBindingRequest request)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.Barcode) ||
                string.IsNullOrWhiteSpace(request.WorkOrder) ||
                string.IsNullOrWhiteSpace(request.Procedure) ||
                string.IsNullOrWhiteSpace(request.Station))
            {
                throw new InvalidOperationException("母排码、当前工单、组装工序和组装工位必须完整");
            }
        }

        /// <summary>
        /// 关系查询只作为恢复线索。母排未组装时可能返回母排自身 SN，
        /// 因此必须通过 GetSerializedProduct 过滤母排物料并核对目标工单。
        /// </summary>
        /// <param name="barcode">现场扫到的母排原始码。</param>
        /// <param name="targetWorkOrder">主界面当前目标成品工单。</param>
        /// <param name="productData">匹配成功时的序列件对象，供报工策略判断当前工序。</param>
        /// <returns>目标工单上的成品 SN；找不到时返回空字符串。</returns>
        private string TryFindExistingFinishedSn(string barcode, string targetWorkOrder, out object productData)
        {
            productData = null;
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string method in new[] { "GetLastBindingProductSerialNumberOfPartBarcode", "GetProductSerialNumberByBusbar" })
            {
                try
                {
                    var reply = Call("路径B关系查询", method, barcode);
                    if (!reply.Success)
                    {
                        string message = reply.Message ?? string.Empty;
                        if (!ContainsAny(message, "未绑定", "不存在", "未找到", "找不到", "JValue"))
                        {
                            _log($"节点=关系查询失败 接口={method} MES消息={message}");
                        }
                        continue;
                    }

                    string sn = ExtractProductSn(reply.Data);
                    if (!string.IsNullOrWhiteSpace(sn)) candidates.Add(sn);
                }
                catch (Exception ex)
                {
                    _log($"节点=关系查询异常 接口={method} 原因={ex.Message}");
                }
            }

            foreach (string sn in candidates)
            {
                try
                {
                    var product = Call("路径B成品恢复确认", "GetSerializedProduct", sn);
                    if (!product.Success || product.Data == null || IsBusbarMaterial(product.Data))
                    {
                        if (product.Success && product.Data != null && IsBusbarMaterial(product.Data))
                            _log($"节点=跳过母排自身SN SN={sn}");
                        continue;
                    }

                    string actualSn = BusbarMesReply.Text(product.Data, "Sn");
                    string actualWorkOrder = BusbarMesReply.Text(product.Data, "WorkOrderNo");
                    if (!Same(actualSn, sn) || !Same(actualWorkOrder, targetWorkOrder))
                    {
                        _log($"节点=跳过非目标工单候选 SN={sn} MES工单={actualWorkOrder} 目标工单={targetWorkOrder}");
                        continue;
                    }

                    productData = product.Data;
                    _log($"节点=复用MES已有路径B成品 成品SN={sn} 工单={actualWorkOrder}");
                    return sn;
                }
                catch (Exception ex)
                {
                    _log($"节点=候选成品确认失败 SN={sn} 原因={ex.Message}");
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 确认序列件存在、SN 一致、工单匹配目标成品工单，且物料不是母排本身。
        /// </summary>
        private object ConfirmFinishedProduct(string sn, string targetWorkOrder)
        {
            var product = Call("路径B成品身份确认", "GetSerializedProduct", sn);
            RequireSuccess(product);
            string actualSn = BusbarMesReply.Text(product.Data, "Sn");
            string actualWorkOrder = BusbarMesReply.Text(product.Data, "WorkOrderNo");
            if (!Same(actualSn, sn) || !Same(actualWorkOrder, targetWorkOrder))
            {
                throw new InvalidOperationException(
                    $"路径B成品身份不一致：期望SN={sn}，MES_SN={actualSn}，当前工单={targetWorkOrder}，MES工单={actualWorkOrder}");
            }
            if (IsBusbarMaterial(product.Data))
            {
                throw new InvalidOperationException($"SN={sn} 仍是母排物料，不能作为路径B组装成品报工");
            }
            return product.Data;
        }

        /// <summary>
        /// 对成品执行组装工序 OK 报工；复用场景下若已离开组装工序或 MES 表示已报工，则跳过重复写入。
        /// </summary>
        private void EnsureAssemblyReported(
            string sn,
            BusbarBindingRequest request,
            string employee,
            object productData,
            bool reused)
        {
            if (reused && !NeedsAssemblyReport(productData, request.Procedure))
            {
                _log($"节点=跳过重复组装报工 成品SN={sn} 当前工序已离开={request.Procedure}");
                return;
            }

            var report = Call("路径B组装报工", "ReportWorkByProduct",
                sn,
                request.Station,
                employee,
                request.Procedure,
                "OK",
                string.Empty,
                string.Empty);
            if (report.Success) return;
            if (reused && IsAlreadyReportedMessage(report.Message))
            {
                _log($"节点=复用成品已完成组装报工 成品SN={sn} MES消息={report.Message}");
                return;
            }
            throw new InvalidOperationException("MES 拒绝路径B组装报工：" + report.Message);
        }

        private string QueryEmployee(string station)
        {
            var workers = Call("路径B上岗确认", "QueryCurrentOnWorkListByStationCode", station);
            RequireSuccess(workers);

            var records = ToRecords(workers.Data);
            var worker = records?.OrderByDescending(OnWorkDate).FirstOrDefault();
            string employee = BusbarMesReply.Text(worker, "EmployeeNo");
            if (string.IsNullOrWhiteSpace(employee))
            {
                throw new InvalidOperationException($"路径B组装工位 {station} 尚无有效上岗人员，请先上岗");
            }
            _log($"节点=路径B上岗确认 工位={station} 工号={employee}");
            return employee;
        }

        private static object[] ToRecords(object data)
        {
            if (data == null || data is string) return new object[0];
            var sequence = data as IEnumerable;
            return sequence == null ? new[] { data } : sequence.Cast<object>().ToArray();
        }

        private static DateTime OnWorkDate(object worker)
        {
            object value = BusbarMesReply.Property(worker, "OnWorkDate");
            DateTime date;
            return DateTime.TryParse(Convert.ToString(value), out date) ? date : DateTime.MinValue;
        }

        private static string ExtractProductSn(object data)
        {
            if (data == null) return string.Empty;
            if (data is string text) return text.Trim();
            string productSn = BusbarMesReply.Text(data, "ProductSn");
            return string.IsNullOrWhiteSpace(productSn) ? BusbarMesReply.Text(data, "Sn") : productSn;
        }

        private static bool NeedsAssemblyReport(object productData, string procedure)
        {
            if (productData == null) return true;
            string current = BusbarMesReply.Text(productData, "CurrentGroupStandardCode");
            string next = BusbarMesReply.Text(productData, "NextGroupStandardCode");
            if (string.IsNullOrWhiteSpace(current) && string.IsNullOrWhiteSpace(next)) return true;
            return Same(current, procedure) || Same(next, procedure);
        }

        private static bool IsAlreadyReportedMessage(string message)
        {
            return ContainsAny(message, "已报工", "重复报工", "已完工", "已过站", "不能上传");
        }

        private static bool IsBusbarMaterial(object productData)
        {
            object material = BusbarMesReply.Property(productData, "Material");
            string className = BusbarMesReply.Text(material, "ClassName");
            string classCode = BusbarMesReply.Text(material, "ClassCode");
            return className.IndexOf("母排", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   string.Equals(classCode, "12007201", StringComparison.OrdinalIgnoreCase);
        }

        private PendingState ReadPending(string path)
        {
            if (!File.Exists(path)) return null;
            const string snPrefix = "成品SN=";
            var state = new PendingState();
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.StartsWith(snPrefix, StringComparison.Ordinal))
                {
                    // 前缀含中文，必须按前缀长度截取；写死 Substring(4) 会把 '=' 带进 SN，导致断点复用失败。
                    state.SN = line.Substring(snPrefix.Length).Trim();
                }
            }
            return state;
        }

        private void WritePending(
            string path,
            BusbarBindingRequest request,
            string employee,
            string sn,
            string stage)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _pendingDirectory);
            string tempPath = path + ".tmp";
            File.WriteAllText(
                tempPath,
                $"时间={DateTime.Now:O}\r\n阶段={stage}\r\n路径=SerializedProductAssemble\r\n母排={request.Barcode}\r\n工单={request.WorkOrder}\r\n工序={request.Procedure}\r\n工位={request.Station}\r\n工号={employee}\r\n成品SN={sn ?? string.Empty}\r\n",
                Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tempPath, path, null);
            else File.Move(tempPath, path);
        }

        private BusbarMesReply Call(string node, string method, params object[] args)
        {
            _ensureActive();
            var timer = Stopwatch.StartNew();
            _log($"节点={node} 接口={method} 开始 参数={string.Join("|", args.Select(a => Convert.ToString(a)))}");
            try
            {
                var reply = _mes.Call(method, args);
                _log($"节点={node} 接口={method} 耗时ms={timer.ElapsedMilliseconds} 成功={reply?.Success} MES消息={reply?.Message}");
                _ensureActive();
                if (reply == null) throw new InvalidOperationException(method + " 返回为空");
                return reply;
            }
            catch (Exception ex)
            {
                _log($"节点={node} 接口={method} 耗时ms={timer.ElapsedMilliseconds} 异常={ex.GetType().Name}:{ex.Message}");
                throw;
            }
        }

        private static void RequireSuccess(BusbarMesReply reply)
        {
            if (reply == null || !reply.Success)
            {
                throw new InvalidOperationException("MES 拒绝或查询失败：" + (reply?.Message ?? "返回为空"));
            }
        }

        private string PendingPath(string barcode)
        {
            using (var sha = SHA256.Create())
            {
                string key = BitConverter.ToString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(barcode.ToUpperInvariant()))).Replace("-", "");
                return Path.Combine(_pendingDirectory, key + ".pending");
            }
        }

        private static void ClearPending(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static bool Same(string first, string second)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   terms.Any(term => value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private sealed class PendingState
        {
            public string SN { get; set; }
        }
    }
}
