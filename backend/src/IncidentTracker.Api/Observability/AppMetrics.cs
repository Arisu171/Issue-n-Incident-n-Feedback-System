using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IncidentTracker.Api.Observability;

/// <summary>
/// Mục 6.9 — các signal nghiệp vụ mà instrumentation sẵn có của ASP.NET Core không cung cấp:
/// số lần đăng nhập thất bại, số lần chuyển trạng thái, và số sự cố đang vượt ngưỡng SLA.
///
/// Request rate, tỷ lệ 4xx/5xx và p95 latency đã có sẵn từ
/// <c>OpenTelemetry.Instrumentation.AspNetCore</c> nên không tự đếm lại ở đây.
/// </summary>
public sealed class AppMetrics : IDisposable
{
    public const string MeterName = "IncidentTracker.Api";
    public const string ActivitySourceName = "IncidentTracker.Api";

    private readonly Meter _meter;
    private readonly Counter<long> _loginFailures;
    private readonly Counter<long> _loginSuccesses;
    private readonly Counter<long> _statusTransitions;
    private readonly Counter<long> _authorizationDenials;
    private readonly Counter<long> _registrations;
    private readonly Counter<long> _registrationRejections;

    private int _slaBreached;

    public AppMetrics()
    {
        _meter = new Meter(MeterName);

        _loginFailures = _meter.CreateCounter<long>(
            "incident_login_failures_total", description: "Số lần đăng nhập thất bại.");
        _loginSuccesses = _meter.CreateCounter<long>(
            "incident_login_success_total", description: "Số lần đăng nhập thành công.");
        _statusTransitions = _meter.CreateCounter<long>(
            "incident_status_transitions_total", description: "Số lần chuyển trạng thái thành công.");
        _authorizationDenials = _meter.CreateCounter<long>(
            "incident_authorization_denials_total", description: "Số request bị chặn bằng 401 hoặc 403.");

        _registrations = _meter.CreateCounter<long>(
            "incident_registrations_total", description: "Số tài khoản tự đăng ký thành công.");
        _registrationRejections = _meter.CreateCounter<long>(
            "incident_registration_rejections_total", description: "Số lần đăng ký bị từ chối.");

        _meter.CreateObservableGauge(
            "incident_sla_breached",
            () => Volatile.Read(ref _slaBreached),
            description: "Số sự cố đang vượt ngưỡng SLA tại lần quét gần nhất.");
    }

    /// <summary>ActivitySource cho span nghiệp vụ, bổ sung cho span HTTP tự động.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public void LoginFailed(string reason)
        => _loginFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void LoginSucceeded() => _loginSuccesses.Add(1);

    public void StatusTransitioned(string from, string to) => _statusTransitions.Add(1,
        new KeyValuePair<string, object?>("from", from),
        new KeyValuePair<string, object?>("to", to));

    public void AuthorizationDenied(int statusCode)
        => _authorizationDenials.Add(1, new KeyValuePair<string, object?>("status", statusCode));

    public void RegistrationSucceeded(bool pendingApproval) => _registrations.Add(1,
        new KeyValuePair<string, object?>("pendingApproval", pendingApproval));

    public void RegistrationRejected(string reason)
        => _registrationRejections.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void SetSlaBreached(int count) => Volatile.Write(ref _slaBreached, count);

    // ---- Architecture v3.1 (module Tickets, mục 7) ----
    private Counter<long>? _ticketEvents;
    private Counter<long>? _webhookDeliveries;
    private Counter<long>? _consumerFaults;
    private int _ticketSlaBreached;

    private void EnsureTicketInstruments()
    {
        if (_ticketEvents is not null) return;
        _ticketEvents = _meter.CreateCounter<long>("ticket_events_appended_total", description: "Số event ghi vào Event Store.");
        _webhookDeliveries = _meter.CreateCounter<long>("webhook_deliveries_total", description: "Số lần gửi webhook theo kết quả.");
        _consumerFaults = _meter.CreateCounter<long>("ticket_consumer_faults_total", description: "Số message consumer xử lý thất bại (đi vào retry/DLQ).");
        _meter.CreateObservableGauge("ticket_sla_breached", () => Volatile.Read(ref _ticketSlaBreached), description: "Số ticket đang quá hạn SLA phản hồi.");
    }

    public void TicketEventAppended(string eventType)
    {
        EnsureTicketInstruments();
        _ticketEvents!.Add(1, new KeyValuePair<string, object?>("type", eventType));
    }

    public void WebhookDelivered(bool success)
    {
        EnsureTicketInstruments();
        _webhookDeliveries!.Add(1, new KeyValuePair<string, object?>("status", success ? "success" : "failure"));
    }

    public void ConsumerFaulted(string consumer)
    {
        EnsureTicketInstruments();
        _consumerFaults!.Add(1, new KeyValuePair<string, object?>("consumer", consumer));
    }

    public void SetTicketSlaBreached(int count)
    {
        EnsureTicketInstruments();
        Volatile.Write(ref _ticketSlaBreached, count);
    }

    public void Dispose() => _meter.Dispose();
}
