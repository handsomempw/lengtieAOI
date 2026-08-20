using Faratronic.EquipUtils.Authentication.Models;
using F7_L12_Position_Detect.Service;
using Panuon.WPF.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace F7_L12_Position_Detect.View
{
    /// <summary>
    /// 完成动态密码申请、接收人提示和验证的 AOI 编辑授权窗口。
    /// </summary>
    public partial class DynamicPasswordAuthWindow : WindowX
    {
        private readonly DynamicPasswordAuthService authService;
        private readonly CancellationToken cancellationToken;
        private readonly string reason;
        private IReadOnlyList<Receiver> requestedReceivers = Array.Empty<Receiver>();

        public IReadOnlyList<Receiver> RequestedReceivers => requestedReceivers;
        public DynamicPasswordVerifyInfo VerifyInfo { get; private set; }

        public DynamicPasswordAuthWindow(DynamicPasswordAuthService authService, string reason, CancellationToken cancellationToken)
        {
            this.authService = authService ?? throw new ArgumentNullException(nameof(authService));
            this.reason = string.IsNullOrWhiteSpace(reason) ? "AOI 工具编辑/参数修改" : reason;
            this.cancellationToken = cancellationToken;

            InitializeComponent();
            reasonTextBlock.Text = this.reason;
        }

        private async void RequestButton_Click(object sender, RoutedEventArgs e)
        {
            requestButton.IsEnabled = false;
            try
            {
                requestStatusTextBlock.Text = "正在申请动态密码...";
                receiverListBox.ItemsSource = null;
                requestedReceivers = Array.Empty<Receiver>();

                requestedReceivers = await authService.RequestPasswordAsync(reason, cancellationToken) ?? Array.Empty<Receiver>();
                if (requestedReceivers.Count == 0)
                {
                    DynamicPasswordAuditLogger.Write(authService.AuditId, "申请结果", "界面未找到可审批管理员");
                    requestStatusTextBlock.Text = "未找到可审批管理员";
                    NoticeBox.Show("未找到可审批管理员，请检查认证服务或接收人工号配置", "提示", MessageBoxIcon.Warning, true, 6000);
                    return;
                }

                var receiverLines = requestedReceivers.Select(receiver => $"{receiver.ReceiverName} ({receiver.ReceiverNo})").ToList();
                receiverListBox.ItemsSource = receiverLines;
                requestStatusTextBlock.Text = $"申请成功，已发送给 {requestedReceivers.Count} 人";
                NoticeBox.Show($"动态密码已发送给：{string.Join("、", receiverLines)}", "提示", MessageBoxIcon.Info, true, 6000);
            }
            catch (OperationCanceledException)
            {
                DynamicPasswordAuditLogger.Write(authService.AuditId, "申请取消", "界面操作已取消");
                requestStatusTextBlock.Text = "申请已取消";
            }
            catch (Exception ex)
            {
                DynamicPasswordAuditLogger.Write(authService.AuditId, "申请界面异常", ex.Message);
                requestStatusTextBlock.Text = "申请失败";
                NoticeBox.Show($"申请动态密码失败：{ex.Message}", "错误", MessageBoxIcon.Error, true, 6000);
            }
            finally
            {
                requestButton.IsEnabled = true;
            }
        }

        private async void VerifyButton_Click(object sender, RoutedEventArgs e)
        {
            verifyButton.IsEnabled = false;
            try
            {
                if (requestedReceivers.Count == 0)
                {
                    DynamicPasswordAuditLogger.Write(authService.AuditId, "验证拒绝", "尚未完成动态密码申请");
                    NoticeBox.Show("请先申请动态密码", "提示", MessageBoxIcon.Warning, true, 5000);
                    return;
                }

                string password = (passwordBox.Password ?? string.Empty).Trim();
                if (password.Length != 6 || password.Any(character => character < '0' || character > '9'))
                {
                    DynamicPasswordAuditLogger.Write(authService.AuditId, "验证拒绝", "输入格式不符合六位数字要求");
                    NoticeBox.Show("动态密码格式应为 6 位数字", "提示", MessageBoxIcon.Warning, true, 5000);
                    return;
                }

                requestStatusTextBlock.Text = "正在验证动态密码...";
                DynamicPasswordVerifyInfo verifyInfo = await authService.VerifyPasswordAsync(password, cancellationToken);
                if (verifyInfo == null || !verifyInfo.Verified)
                {
                    string message = verifyInfo?.Message ?? "验证失败";
                    requestStatusTextBlock.Text = $"验证失败：{message}";
                    NoticeBox.Show($"动态密码验证失败：{message}", "提示", MessageBoxIcon.Warning, true, 6000);
                    return;
                }

                VerifyInfo = verifyInfo;
                requestStatusTextBlock.Text = "验证通过";
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                DynamicPasswordAuditLogger.Write(authService.AuditId, "验证取消", "界面操作已取消");
                requestStatusTextBlock.Text = "验证已取消";
            }
            catch (Exception ex)
            {
                DynamicPasswordAuditLogger.Write(authService.AuditId, "验证界面异常", ex.Message);
                requestStatusTextBlock.Text = "验证异常";
                NoticeBox.Show($"验证动态密码失败：{ex.Message}", "错误", MessageBoxIcon.Error, true, 6000);
            }
            finally
            {
                verifyButton.IsEnabled = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DynamicPasswordAuditLogger.Write(authService.AuditId, "授权窗口关闭", "操作者取消授权窗口");
            DialogResult = false;
        }
    }
}
