using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace Document.Api.Services;

public record DocumentEventMessage(
    string EventType,
    int DocumentId,
    string Title,
    string Creator,
    string Approver,
    string Status,
    string Actor,
    string Note,
    DateTime Timestamp
);

public class RabbitMqPublisher
{
    private readonly IConfiguration _config;
    private readonly ILogger<RabbitMqPublisher> _logger;

    public RabbitMqPublisher(IConfiguration config, ILogger<RabbitMqPublisher> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task PublishAsync(DocumentEventMessage message)
    {
        var factory = new ConnectionFactory
        {
            HostName = _config["RabbitMQ:HostName"] ?? "localhost",
            Port = int.Parse(_config["RabbitMQ:Port"] ?? "5672"),
            UserName = _config["RabbitMQ:UserName"] ?? "guest",
            Password = _config["RabbitMQ:Password"] ?? "guest"
        };

        var queueName = _config["RabbitMQ:QueueName"] ?? "document_events_queue";

        try
        {
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();

            await channel.QueueDeclareAsync(
                queue: queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            var json = JsonSerializer.Serialize(message);
            var body = Encoding.UTF8.GetBytes(json);

            var properties = new BasicProperties { Persistent = true };

            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: queueName,
                mandatory: false,
                basicProperties: properties,
                body: body);

            _logger.LogInformation("[RabbitMQ Published] Event={EventType}, DocId={DocId}", message.EventType, message.DocumentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RabbitMQ Error] Failed to publish event {EventType} for DocId={DocId}", message.EventType, message.DocumentId);
            throw;
        }
    }
}