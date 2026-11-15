using System.Text.Json;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RnxInternetShop.Data;

namespace RnxInternetShop.Messaging;

public sealed class OrderWorker(NpgsqlDataSource dataSource, IConfiguration configuration,
    OrderEffects effects, ILogger<OrderWorker> logger) : BackgroundService
{
    private const string Exchange = "shop.orders";
    private const string DeadExchange = "shop.orders.dead";
    private const string OrderKey = "order.placed";
    private const string SmsKey = "order.placed.sms";

    private static readonly (OrderEffect Effect, string Queue)[] Subscriptions =
    [
        (OrderEffect.CustomerEmail, "shop.orders.customer-email"),
        (OrderEffect.ManagerMessage, "shop.orders.manager"),
        (OrderEffect.Notification, "shop.orders.notification"),
        (OrderEffect.Journal, "shop.orders.journal"),
        (OrderEffect.Sms, "shop.orders.sms")
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(configuration["RabbitMq:Uri"]!),
            AutomaticRecoveryEnabled = false,
            ContinuationTimeout = TimeSpan.FromSeconds(10),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(factory, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Order pipeline disconnected. Reconnecting in five seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunConnectionAsync(ConnectionFactory factory, CancellationToken stoppingToken)
    {
        await using var connection = await factory.CreateConnectionAsync("rnx-shop-orders", stoppingToken);
        var options = new CreateChannelOptions(publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        await using var publisher = await connection.CreateChannelAsync(options, stoppingToken);
        await DeclareTopologyAsync(publisher, stoppingToken);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var tasks = Subscriptions
            .Select(subscription => ConsumeAsync(connection, subscription.Effect, subscription.Queue, lifetime.Token))
            .Append(PublishAsync(publisher, lifetime.Token))
            .ToArray();

        await Task.WhenAny(tasks);
        await lifetime.CancelAsync();
        await Task.WhenAll(tasks);
    }

    private static async Task DeclareTopologyAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        foreach (var (effect, queue) in Subscriptions)
        {
            var deadQueue = queue + ".dead";
            await channel.QueueDeclareAsync(deadQueue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
            await channel.QueueBindAsync(deadQueue, DeadExchange, queue, cancellationToken: ct);

            var arguments = new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = 8,
                ["x-dead-letter-exchange"] = DeadExchange,
                ["x-dead-letter-routing-key"] = queue,
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish",
                ["x-delayed-retry-type"] = "failed",
                ["x-delayed-retry-min"] = 3_000,
                ["x-delayed-retry-max"] = 30_000
            };
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments, cancellationToken: ct);
            await channel.QueueBindAsync(queue, Exchange, effect == OrderEffect.Sms ? SmsKey : "order.placed.#",
                cancellationToken: ct);
        }
    }

    private async Task PublishAsync(IChannel channel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!channel.IsOpen)
                throw new IOException("RabbitMQ publisher channel closed.");
            if (!await PublishNextAsync(channel, ct))
                await Task.Delay(700, ct);
        }
    }

    private async Task<bool> PublishNextAsync(IChannel channel, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.Command(transaction, """
            select id, order_id, sms_requested from outbox where published_at is null
            order by created_at limit 1 for update skip locked
            """);
        OrderPlaced placed;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return false;
            placed = new OrderPlaced(reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2));
        }

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            MessageId = placed.EventId.ToString("N"),
            Persistent = true,
            Type = OrderKey
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        await channel.BasicPublishAsync(Exchange, placed.SmsRequested ? SmsKey : OrderKey, mandatory: true,
            properties, JsonSerializer.SerializeToUtf8Bytes(placed), deadline.Token);

        await using var sent = connection.Command(transaction,
            "update outbox set published_at = now() where id = @event", ("event", placed.EventId));
        await sent.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task ConsumeAsync(IConnection connection, OrderEffect effect, string queue, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.BasicQosAsync(0, prefetchCount: 1, global: false, cancellationToken: ct);

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, reason) =>
        {
            disconnected.TrySetException(new IOException($"RabbitMQ consumer {queue} closed: {reason.ReplyText}"));
            return Task.CompletedTask;
        };
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.UnregisteredAsync += (_, _) =>
        {
            disconnected.TrySetException(new IOException($"RabbitMQ consumer {queue} was removed."));
            return Task.CompletedTask;
        };
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, delivery.CancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                OrderPlaced placed;
                try
                {
                    placed = JsonSerializer.Deserialize<OrderPlaced>(delivery.Body.Span)
                        ?? throw new JsonException("Missing order event.");
                    if (placed.EventId == Guid.Empty || placed.OrderId == Guid.Empty)
                        throw new JsonException("Missing order or event identifier.");
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "Invalid message in {Queue}.", queue);
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken: ct);
                    return;
                }

                try
                {
                    await effects.ApplyAsync(effect, placed, attempt.Token);
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Order effect {Effect} failed for {EventId}.", effect, placed.EventId);
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: true, cancellationToken: ct);
                    return;
                }

                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Disposing this channel returns its unacknowledged delivery to RabbitMQ.
            }
            catch (Exception exception)
            {
                disconnected.TrySetException(exception);
            }
        };

        if (!channel.IsOpen)
            throw new IOException($"RabbitMQ consumer {queue} closed during setup.");
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
        await disconnected.Task.WaitAsync(ct);
    }
}
