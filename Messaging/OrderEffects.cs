using System.Net.Http.Headers;
using System.Net.Http.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Npgsql;
using RnxInternetShop.Data;

namespace RnxInternetShop.Messaging;

public sealed class MailOptions
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 11025;
    public SecureSocketOptions Security { get; init; }
    public string From { get; init; } = "orders@shop.example";
    public string Manager { get; init; } = "manager@shop.example";
    public string? Username { get; init; }
    public string Password { get; init; } = "";
}

public sealed class SmsOptions
{
    public Uri? Webhook { get; init; }
    public string? ApiToken { get; init; }
    public bool DemoInbox { get; init; }
    public bool Available => Webhook is not null || DemoInbox;
}

public enum OrderEffect { CustomerEmail, ManagerMessage, Notification, Journal, Sms }

public sealed class OrderEffects(NpgsqlDataSource dataSource, IOptions<MailOptions> mail,
    IOptions<SmsOptions> sms, HttpClient http)
{
    public async Task ApplyAsync(OrderEffect effect, OrderPlaced placed, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // This insert serializes redeliveries for this effect. It commits only after the effect succeeds.
        await using var claim = connection.Command(transaction, """
            insert into processed_effects (event_id, effect)
            select id, @effect from outbox where id = @event and order_id = @order and sms_requested = @sms
            on conflict do nothing
            """, ("event", placed.EventId), ("effect", effect.ToString()), ("order", placed.OrderId),
            ("sms", placed.SmsRequested));
        if (await claim.ExecuteNonQueryAsync(ct) == 0)
            return;

        switch (effect)
        {
            case OrderEffect.Notification:
                await using (var command = connection.Command(transaction, """
                    insert into notifications (order_id, message)
                    select id, 'Заказ №' || number || ' принят и передан в обработку.'
                    from orders where id = @order
                    """, ("order", placed.OrderId)))
                    await command.ExecuteNonQueryAsync(ct);
                break;
            case OrderEffect.Journal:
                await using (var command = connection.Command(transaction, """
                    insert into order_journal (event_id, order_id, event_type) values (@event, @order, 'order.placed')
                    """, ("event", placed.EventId), ("order", placed.OrderId)))
                    await command.ExecuteNonQueryAsync(ct);
                break;
            case OrderEffect.CustomerEmail or OrderEffect.ManagerMessage:
                var emailOrder = await ReadOrderAsync(connection, transaction, placed.OrderId, ct);
                await SendMailAsync(effect, placed.EventId, emailOrder, ct);
                break;
            case OrderEffect.Sms when placed.SmsRequested:
                var smsOrder = await ReadOrderAsync(connection, transaction, placed.OrderId, ct);
                await SendSmsAsync(connection, transaction, placed.EventId, smsOrder, ct);
                break;
            default:
                throw new InvalidOperationException($"Unexpected order effect: {effect}");
        }

        await transaction.CommitAsync(ct);
    }

    private async Task SendMailAsync(OrderEffect effect, Guid eventId, OrderContact order, CancellationToken ct)
    {
        var settings = mail.Value;
        var sender = MailboxAddress.Parse(settings.From);
        using var message = new MimeMessage { MessageId = $"{eventId:N}.{effect}@{sender.Domain}" };
        message.From.Add(new MailboxAddress("Тихий дом", sender.Address));
        var total = ShopDisplay.Money(order.Total);

        if (effect == OrderEffect.CustomerEmail)
        {
            message.To.Add(new MailboxAddress(order.Name, order.Email));
            message.Subject = $"Тихий дом · Заказ №{order.Number} принят";
            message.Body = new TextPart("plain")
            {
                Text = $"""
                    {order.Name}, спасибо за заказ!

                    Номер заказа: {order.Number}
                    Сумма с доставкой: {total}
                    Адрес доставки: {order.Address}

                    Тихий дом
                    """
            };
        }
        else
        {
            message.To.Add(MailboxAddress.Parse(settings.Manager));
            message.Subject = $"Новый заказ №{order.Number}";
            message.Body = new TextPart("plain")
            {
                Text = $"""
                    Заказ №{order.Number} готов к сборке.
                    Покупатель: {order.Name}
                    Почта: {order.Email}
                    Адрес доставки: {order.Address}
                    Сумма с доставкой: {total}
                    """
            };
        }

        using var client = new SmtpClient { Timeout = 10000 };
        await client.ConnectAsync(settings.Host, settings.Port, settings.Security, ct);
        if (!string.IsNullOrEmpty(settings.Username))
            await client.AuthenticateAsync(settings.Username, settings.Password, ct);
        await client.SendAsync(message, ct);
    }

    private async Task SendSmsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid eventId, OrderContact order, CancellationToken ct)
    {
        var settings = sms.Value;
        var phone = order.Phone ?? throw new InvalidOperationException("SMS order is missing its recipient.");
        var text = $"Тихий дом: заказ №{order.Number} принят.";
        if (settings.Webhook is { } target)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = JsonContent.Create(new { to = phone, text })
            };
            request.Headers.Add("Idempotency-Key", eventId.ToString("N"));
            if (!string.IsNullOrEmpty(settings.ApiToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            return;
        }

        if (!settings.DemoInbox)
            throw new InvalidOperationException("No SMS transport is configured.");

        await using var command = connection.Command(transaction, """
            insert into demo_sms_inbox (event_id, recipient, message) values (@event, @recipient, @message)
            """, ("event", eventId), ("recipient", phone), ("message", text));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<OrderContact> ReadOrderAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid orderId, CancellationToken ct)
    {
        await using var command = connection.Command(transaction, """
            select number, customer_name, email, address, phone, subtotal + shipping
            from orders where id = @order
            """, ("order", orderId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"Order {orderId} is missing.");

        return new OrderContact(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetDecimal(5));
    }

    private sealed record OrderContact(long Number, string Name, string Email, string Address,
        string? Phone, decimal Total);
}
