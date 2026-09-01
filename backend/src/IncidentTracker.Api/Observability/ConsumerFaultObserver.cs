using MassTransit;

namespace IncidentTracker.Api.Observability;

/// <summary>
/// Mục 7 (Architecture v3.1): đếm message consumer xử lý thất bại — sau khi hết retry chúng vào queue
/// <c>*_error</c> (DLQ) của MassTransit. Alert dựa trên <c>ticket_consumer_faults_total</c>.
/// </summary>
public sealed class ConsumerFaultObserver : IConsumeObserver
{
    private readonly AppMetrics _metrics;
    private readonly ILogger<ConsumerFaultObserver> _logger;

    public ConsumerFaultObserver(AppMetrics metrics, ILogger<ConsumerFaultObserver> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    public Task PreConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;
    public Task PostConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;

    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
    {
        var consumer = typeof(T).Name;
        _metrics.ConsumerFaulted(consumer);
        _logger.LogWarning(exception, "Consumer fault message={Message} correlation={Correlation}", consumer, context.CorrelationId);
        return Task.CompletedTask;
    }
}
