using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

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
    /// 执行现场验证过的路径B最简流程（与 MesFlowProbe 成功样本一致）：
    /// 1) 查组装工位上岗工号；
    /// 2) SerializedProductAssemble(工单, 空产品SN, 母排原始码, 组装工序, 组装工位, 工号)；
    /// 3) 读取返回的 ProductSn；
    /// 4) ReportWorkByProduct(成品SN, 组装工位, 工号, 组装工序, OK)。
    /// 不在取号前查询本机 pending/completed，也不做关系预判；新母排长码直接取号。
    /// 若 MES 返回「半成品已绑到[成品SN]」，解析该 SN 后直接补组装报工，避免重复取号。
    /// 组装工序和工位只服务路径B；AOI 工序由调用方继续使用报工配置。
    /// </summary>
    public sealed class BusbarBindingService
    {
        private static readonly Regex AlreadyBoundSnRegex = new Regex(
            @"已绑到[【\[]\s*(?<sn>[A-Za-z0-9\-]+)\s*[】\]]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly IBusbarMesGateway _mes;
        private readonly Action<string> _log;
        private readonly Action _ensureActive;

        public BusbarBindingService(IBusbarMesGateway mes, string pendingDirectory, Action<string> log, Action ensureActive)
        {
            // pendingDirectory 保留构造签名，兼容现有调用方；最简路径不再读写本地断点文件。
            _mes = mes ?? throw new ArgumentNullException(nameof(mes));
            _log = log ?? (_ => { });
            _ensureActive = ensureActive ?? (() => { });
        }

        /// <summary>
        /// 按路径B完成单母排组装取号和组装 OK 报工。
        /// </summary>
        /// <param name="request">母排码、目标工单、组装工序码和组装工位号。</param>
        /// <returns>最终成品 SN；Reused 表示复用了 MES 已绑定关系。</returns>
        /// <exception cref="InvalidOperationException">参数不完整、上岗无效或 MES 拒绝。</exception>
        public BusbarBindingResult Resolve(BusbarBindingRequest request)
        {
            ValidateRequest(request);

            string employee = QueryEmployee(request.Station);
            _log($"节点=路径B开始 母排={request.Barcode} 工单={request.WorkOrder} 工序={request.Procedure} 工位={request.Station} 工号={employee}");

            string finalSn;
            bool reused;
            if (!TryAssemble(request, employee, out finalSn, out reused))
            {
                throw new InvalidOperationException("路径B组装取号失败且未能从 MES 消息恢复成品 SN");
            }

            _log($"节点=路径B成品SN就绪 成品SN={finalSn} 是否复用={reused}");
            EnsureAssemblyReported(finalSn, request, employee, reused);
            _log($"节点=路径B组装报工完成 成品SN={finalSn} 工序={request.Procedure} 工位={request.Station} 是否复用={reused}");
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
        /// 调用 SerializedProductAssemble。成功则读 ProductSn；
        /// 若 MES 表示半成品已绑定，从拒绝消息解析成品 SN 并视为复用。
        /// </summary>
        private bool TryAssemble(BusbarBindingRequest request, string employee, out string finalSn, out bool reused)
        {
            finalSn = string.Empty;
            reused = false;

            var assemble = Call("路径B组装取号", "SerializedProductAssemble",
                request.WorkOrder,
                string.Empty,
                request.Barcode,
                request.Procedure,
                request.Station,
                employee);

            if (assemble.Success)
            {
                finalSn = ExtractProductSn(assemble.Data);
                _log($"节点=路径B取号成功 ProductSn={finalSn} 原始Data类型={(assemble.Data == null ? "null" : assemble.Data.GetType().Name)}");
                if (string.IsNullOrWhiteSpace(finalSn))
                {
                    throw new InvalidOperationException("MES 已响应路径B组装，但未返回 ProductSn");
                }
                return true;
            }

            string message = assemble.Message ?? string.Empty;
            string boundSn = TryParseAlreadyBoundSn(message);
            if (!string.IsNullOrWhiteSpace(boundSn))
            {
                finalSn = boundSn;
                reused = true;
                _log($"节点=路径B取号复用已绑定成品 成品SN={finalSn} MES消息={message}");
                return true;
            }

            throw new InvalidOperationException("MES 拒绝或查询失败：" + message);
        }

        /// <summary>
        /// 对成品执行组装工序 OK 报工。复用场景下若 MES 表示已报工，则跳过重复写入。
        /// </summary>
        private void EnsureAssemblyReported(
            string sn,
            BusbarBindingRequest request,
            string employee,
            bool reused)
        {
            var report = Call("路径B组装报工", "ReportWorkByProduct",
                sn,
                request.Station,
                employee,
                request.Procedure,
                "OK",
                string.Empty,
                string.Empty);
            if (report.Success)
            {
                _log($"节点=路径B组装报工成功 成品SN={sn}");
                return;
            }

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

        /// <summary>
        /// 只认组装返回的 ProductSn；为空时再读 Sn。
        /// 取号成功后的 ProductSn 即成品身份，不再用物料类别二次否决，避免误拦已分配的 MT 成品号。
        /// </summary>
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

        private static bool IsAlreadyReportedMessage(string message)
        {
            return ContainsAny(message, "已报工", "重复报工", "已完工", "已过站");
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
