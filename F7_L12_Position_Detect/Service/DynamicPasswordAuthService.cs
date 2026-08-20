using Faratronic.EquipUtils.Authentication;
using Faratronic.EquipUtils.Authentication.Models;
using F7_L12_Position_Detect.Model.Setting;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace F7_L12_Position_Detect.Service
{
    /// <summary>
    /// 冷贴 AOI 编辑权限的动态密码认证入口。
    /// 主程序固定连接生产认证服务；XML 保存应用、权限等级、有效期和接收人等现场参数。
    /// </summary>
    public sealed class DynamicPasswordAuthService : IDisposable
    {
        private readonly DynamicPasswordAuthenticationSetting setting;
        private readonly DynamicPassword dynamicPassword;
        private readonly string equipmentNumber;
        private bool disposed;

        /// <summary>
        /// 本次动态密码申请、验证和权限回收共用的审计关联编号。
        /// </summary>
        public string AuditId { get; }

        public event EventHandler PasswordExpired;

        public DynamicPasswordAuthService(DynamicPasswordAuthenticationSetting setting, string equipmentNumber)
        {
            this.setting = setting ?? throw new ArgumentNullException(nameof(setting));
            if (string.IsNullOrWhiteSpace(equipmentNumber))
            {
                throw new ArgumentException("设备编号不能为空（用于动态密码认证）", nameof(equipmentNumber));
            }

            this.equipmentNumber = equipmentNumber;
            AuditId = DynamicPasswordAuditLogger.CreateAuditId();
            dynamicPassword = new DynamicPassword(false);
            dynamicPassword.PasswordExpired += OnPasswordExpired;
        }

        public Task<IReadOnlyList<Receiver>> RequestPasswordAsync(string reason, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateSetting();
                DynamicPasswordAuditLogger.Write(AuditId, "申请开始", $"应用={setting.ApplicationName}, 权限等级={setting.PrivilegeLevel}, 有效期={setting.PeriodMinutes}分钟, 原因={reason}");
                try
                {
                    List<Receiver> receivers = string.IsNullOrWhiteSpace(setting.ReceiverNo)
                        ? dynamicPassword.RequestPassword(equipmentNumber, setting.ApplicationName, setting.PrivilegeLevel, setting.PeriodMinutes, reason)
                        : dynamicPassword.RequestPasswordManualReceiver(setting.ReceiverNo, equipmentNumber, setting.ApplicationName, setting.PrivilegeLevel, setting.PeriodMinutes, reason);

                    receivers = receivers ?? new List<Receiver>();
                    DynamicPasswordAuditLogger.Write(AuditId, "申请结果", $"结果={(receivers.Count > 0 ? "成功" : "未找到接收人")}, 接收人数={receivers.Count}");
                    return (IReadOnlyList<Receiver>)receivers;
                }
                catch (OperationCanceledException)
                {
                    DynamicPasswordAuditLogger.Write(AuditId, "申请取消", "操作已取消");
                    throw;
                }
                catch (Exception ex)
                {
                    DynamicPasswordAuditLogger.Write(AuditId, "申请失败", ex.Message);
                    throw;
                }
            }, cancellationToken);
        }

        public Task<DynamicPasswordVerifyInfo> VerifyPasswordAsync(string password, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateSetting();
                DynamicPasswordAuditLogger.Write(AuditId, "验证开始", $"应用={setting.ApplicationName}");
                try
                {
                    dynamic result = dynamicPassword.VerifyPassword(equipmentNumber, setting.ApplicationName, password);
                    var verifyInfo = new DynamicPasswordVerifyInfo(
                        result != null && result.VerifyStatus,
                        result != null ? (string)(result.VerifyMessage ?? string.Empty) : "验证失败（无返回结果）",
                        result != null ? (string)(result.AuthorizerName ?? string.Empty) : string.Empty,
                        result != null ? (string)(result.AuthorizerNo ?? string.Empty) : string.Empty);
                    DynamicPasswordAuditLogger.Write(AuditId, "验证结果", $"结果={(verifyInfo.Verified ? "成功" : "失败")}, 授权人={verifyInfo.AuthorizerName}({verifyInfo.AuthorizerNo}), 原因={verifyInfo.Message}");
                    return verifyInfo;
                }
                catch (OperationCanceledException)
                {
                    DynamicPasswordAuditLogger.Write(AuditId, "验证取消", "操作已取消");
                    throw;
                }
                catch (Exception ex)
                {
                    DynamicPasswordAuditLogger.Write(AuditId, "验证异常", ex.Message);
                    throw;
                }
            }, cancellationToken);
        }

        private void ValidateSetting()
        {
            if (string.IsNullOrWhiteSpace(setting.ApplicationName))
            {
                throw new InvalidOperationException("动态密码认证配置：应用名称不能为空");
            }
            if (setting.PrivilegeLevel <= 0)
            {
                throw new InvalidOperationException("动态密码认证配置：权限等级必须大于 0");
            }
            if (setting.PeriodMinutes <= 0)
            {
                throw new InvalidOperationException("动态密码认证配置：有效期分钟必须大于 0");
            }
        }

        private void OnPasswordExpired(object sender, EventArgs e)
        {
            DynamicPasswordAuditLogger.Write(AuditId, "密码过期", "认证服务通知授权会话到期");
            PasswordExpired?.Invoke(this, EventArgs.Empty);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(DynamicPasswordAuthService));
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            dynamicPassword.PasswordExpired -= OnPasswordExpired;
        }
    }

    /// <summary>
    /// 动态密码验证的界面结果。
    /// </summary>
    public sealed class DynamicPasswordVerifyInfo
    {
        public DynamicPasswordVerifyInfo(bool verified, string message, string authorizerName, string authorizerNo)
        {
            Verified = verified;
            Message = message ?? string.Empty;
            AuthorizerName = authorizerName ?? string.Empty;
            AuthorizerNo = authorizerNo ?? string.Empty;
        }

        public bool Verified { get; }
        public string Message { get; }
        public string AuthorizerName { get; }
        public string AuthorizerNo { get; }
    }
}
