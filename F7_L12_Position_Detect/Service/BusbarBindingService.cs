using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace F7_L12_Position_Detect.Service
{
    /// <summary>
    /// 一次母排扫码固定的母排组装身份。组装工单、工序、工位和母排码来自同一份扫码会话，
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
    /// 母排组装完成后由 MES 分配的最终成品身份。调用方使用该 SN 继续 AOI 工序校验与复测。
    /// </summary>
    public sealed class BusbarBindingResult
    {
        public string SN { get; set; }
        /// <summary>true 表示母排已在 MES 绑定过，本次为 AOI 复测通行，而非首次取号。</summary>
        public bool Reused { get; set; }
    }

    /// <summary>
    /// 母排组装取号与组装报工，并支持 AOI 复测通行：
    /// 首次：SerializedProductAssemble → 读 ProductSn → ReportWorkByProduct → 交 AOI；
    /// 复测：母排已绑定 → 复用成品 SN → 组装报工若已完成则跳过 → 仍返回成品 SN 进入 AOI。
    /// 不在取号前查询本机 pending/completed；新母排长码直接取号。
    /// </summary>
    public sealed class BusbarBindingService
    {
        private static readonly Regex AlreadyBoundSnRegex = new Regex(
            @"已绑到\s*[【\[]\s*(?<sn>[A-Za-z0-9\-]+)\s*[】\]]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly IBusbarMesGateway _mes;
        private readonly Action<string> _log;
        private readonly Action _ensureActive;

        public BusbarBindingService(IBusbarMesGateway mes, string pendingDirectory, Action<string> log, Action ensureActive)
        {
            // pendingDirectory 保留构造签名，兼容现有调用方；本路径不再读写本地断点文件。
            _mes = mes ?? throw new ArgumentNullException(nameof(mes));
            _log = log ?? (_ => { });
            _ensureActive = ensureActive ?? (() => { });
        }

        /// <summary>
        /// 完成母排组装取号/复用，并保证组装工序已报工（首次写入，复测可跳过），返回可供 AOI 使用的成品 SN。
        /// </summary>
        /// <param name="request">母排码、目标工单、组装工序码和组装工位号。</param>
        /// <returns>成品 SN；Reused 表示本次走 AOI 复测通行。</returns>
        /// <exception cref="InvalidOperationException">参数不完整、上岗无效或 MES 拒绝且无法恢复成品 SN。</exception>
        public BusbarBindingResult Resolve(BusbarBindingRequest request)
        {
            ValidateRequest(request);

            string employee = QueryEmployee(request.Station);
            _log($"节点=母排组装开始 母排={request.Barcode} 工单={request.WorkOrder} 工序={request.Procedure} 工位={request.Station} 工号={employee}");

            string finalSn;
            bool reused;
            if (!TryResolveProductSn(request, employee, out finalSn, out reused))
            {
                throw new InvalidOperationException("母排组装未能得到成品 SN，无法进入 AOI");
            }

            _log($"节点=母排组装成品SN就绪 成品SN={finalSn} 是否复用={(reused ? "是(AOI复测通行)" : "否(首次取号)")}");
            EnsureAssemblyReported(finalSn, request, employee, reused);
            _log($"节点=母排组装段完成 成品SN={finalSn} 工序={request.Procedure} 工位={request.Station} 是否复用={reused}");
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
        /// 首次取号成功则返回 ProductSn；若 MES 表示已绑定，解析或关系查询成品 SN，供 AOI 复测使用。
        /// </summary>
        private bool TryResolveProductSn(BusbarBindingRequest request, string employee, out string finalSn, out bool reused)
        {
            finalSn = string.Empty;
            reused = false;

            var assemble = Call("母排组装取号", "SerializedProductAssemble",
                request.WorkOrder,
                string.Empty,
                request.Barcode,
                request.Procedure,
                request.Station,
                employee);

            if (assemble.Success)
            {
                finalSn = ExtractProductSn(assemble.Data);
                _log($"节点=母排组装取号成功 ProductSn={finalSn} 原始Data类型={(assemble.Data == null ? "null" : assemble.Data.GetType().Name)}");
                if (string.IsNullOrWhiteSpace(finalSn))
                {
                    throw new InvalidOperationException("MES 已响应母排组装，但未返回 ProductSn");
                }
                return true;
            }

            string message = assemble.Message ?? string.Empty;
            if (!IsAlreadyBoundMessage(message))
            {
                throw new InvalidOperationException("MES 拒绝或查询失败：" + message);
            }

            // AOI 复测：母排已绑定，禁止重复取号，但必须拿出成品 SN 继续后续流程。
            finalSn = TryParseAlreadyBoundSn(message);
            if (string.IsNullOrWhiteSpace(finalSn))
            {
                finalSn = TryFindBoundProductSnByRelation(request.Barcode);
            }
            if (string.IsNullOrWhiteSpace(finalSn))
            {
                throw new InvalidOperationException(
                    "母排已在 MES 绑定，但未能解析成品 SN，无法进入 AOI 复测。MES消息：" + message);
            }

            reused = true;
            _log($"节点=母排组装已绑定复用 成品SN={finalSn} 用途=AOI复测通行 MES消息={message}");
            return true;
        }

        /// <summary>
        /// 首次必须组装报工成功；复测时若 MES 表示组装已报工/已过站，则跳过，不阻断进入 AOI。
        /// </summary>
        private void EnsureAssemblyReported(
            string sn,
            BusbarBindingRequest request,
            string employee,
            bool reused)
        {
            var report = Call("母排组装报工", "ReportWorkByProduct",
                sn,
                request.Station,
                employee,
                request.Procedure,
                "OK",
                string.Empty,
                string.Empty);
            if (report.Success)
            {
                _log($"节点=母排组装报工成功 成品SN={sn}");
                return;
            }

            string message = report.Message ?? string.Empty;
            if (reused && IsAssemblyAlreadyDoneMessage(message))
            {
                _log($"节点=AOI复测跳过组装报工 成品SN={sn} MES消息={message}");
                return;
            }

            throw new InvalidOperationException("MES 拒绝母排组装报工：" + message);
        }

        /// <summary>
        /// 消息未能带出成品 SN 时，用母排关系查询补全，仅服务已绑定后的 AOI 复测。
        /// </summary>
        private string TryFindBoundProductSnByRelation(string barcode)
        {
            foreach (string method in new[]
            {
                "GetLastBindingProductSerialNumberOfPartBarcode",
                "GetProductSerialNumberByBusbar"
            })
            {
                try
                {
                    var reply = Call("母排组装关系补查", method, barcode);
                    if (!reply.Success) continue;
                    string sn = ExtractProductSn(reply.Data);
                    if (!string.IsNullOrWhiteSpace(sn))
                    {
                        _log($"节点=母排组装关系补查成功 接口={method} 成品SN={sn}");
                        return sn;
                    }
                }
                catch (Exception ex)
                {
                    _log($"节点=母排组装关系补查异常 接口={method} 原因={ex.Message}");
                }
            }
            return string.Empty;
        }

        private string QueryEmployee(string station)
        {
            var workers = Call("母排组装上岗确认", "QueryCurrentOnWorkListByStationCode", station);
            RequireSuccess(workers);

            var records = ToRecords(workers.Data);
            var worker = records?.OrderByDescending(OnWorkDate).FirstOrDefault();
            string employee = BusbarMesReply.Text(worker, "EmployeeNo");
            if (string.IsNullOrWhiteSpace(employee))
            {
                throw new InvalidOperationException($"母排组装工位 {station} 尚无有效上岗人员，请先上岗");
            }
            _log($"节点=母排组装上岗确认 工位={station} 工号={employee}");
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
            if (!string.IsNullOrWhiteSpace(productSn)) return productSn;
            return BusbarMesReply.Text(data, "Sn");
        }

        private static string TryParseAlreadyBoundSn(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;
            var match = AlreadyBoundSnRegex.Match(message);
            return match.Success ? match.Groups["sn"].Value.Trim() : string.Empty;
        }

        private static bool IsAlreadyBoundMessage(string message)
        {
            return ContainsAny(message, "已绑到", "已经绑定", "已绑定", "重复绑定", "不能重复");
        }

        private static bool IsAssemblyAlreadyDoneMessage(string message)
        {
            return ContainsAny(message, "已报工", "重复报工", "已完工", "已过站", "不能上传", "不允许重复");
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

        private static bool ContainsAny(string value, params string[] terms)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   terms.Any(term => value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
