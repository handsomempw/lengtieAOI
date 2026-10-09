using System;
using System.Linq;
using System.Reflection;

namespace F7_L12_Position_Detect.Service
{
    /// <summary>将不同 SDK 版本的 Result 包装为稳定的业务返回值。</summary>
    public sealed class BusbarMesReply
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }

        public static object Property(object target, string name)
        {
            return target?.GetType().GetProperty(name)?.GetValue(target, null);
        }

        public static string Text(object target, string name)
        {
            return Convert.ToString(Property(target, name)).Trim();
        }
    }

    /// <summary>母排流程使用的 MES 边界，供现场 SDK 适配和离线业务验证共同使用。</summary>
    public interface IBusbarMesGateway
    {
        string Version { get; }
        BusbarMesReply Call(string method, params object[] arguments);
    }

    /// <summary>
    /// 复用现有 MES 客户端的服务地址。按运行时签名读取 Success、Message、Data，
    /// 兼容旧版 DLL 的返回类型，并在能力缺失时明确终止绑定。
    /// </summary>
    public sealed class BusbarMesGateway : IBusbarMesGateway
    {
        private readonly object _client;

        public BusbarMesGateway(object client)
        {
            _client = client ?? throw new InvalidOperationException("MES 客户端尚未初始化");
        }

        public string Version => _client.GetType().Assembly.GetName().Version.ToString();

        public BusbarMesReply Call(string method, params object[] arguments)
        {
            var candidate = _client.GetType().GetMethods().SingleOrDefault(m =>
                m.Name == method && m.GetParameters().Length == arguments.Length &&
                m.GetParameters().All(p => p.ParameterType == typeof(string)));
            if (candidate == null)
                throw new MissingMethodException($"MES SDK {Version} 缺少接口 {method}，请核对部署 DLL");
            try
            {
                object result = candidate.Invoke(_client, arguments);
                if (result == null) throw new InvalidOperationException($"{method} 返回为空");
                return new BusbarMesReply
                {
                    Success = BusbarMesReply.Property(result, "Success") is bool success && success,
                    Message = BusbarMesReply.Text(result, "Message"),
                    Data = BusbarMesReply.Property(result, "Data")
                };
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException($"{method}: {ex.InnerException?.Message ?? ex.Message}", ex.InnerException ?? ex);
            }
        }
    }
}
