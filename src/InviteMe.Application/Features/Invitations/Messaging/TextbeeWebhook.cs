using System.Text.Json;

namespace InviteMe.Application.Features.Invitations.Messaging;

public sealed record TextbeeDeliveryEvent(string? SmsBatchId, string? Recipient, string? WebhookEvent, string? ErrorCode,
    DateTimeOffset? SentAt, DateTimeOffset? DeliveredAt, DateTimeOffset? FailedAt);
public sealed record TextbeeWebhookProcessResult(bool Authenticated, bool Accepted, bool RetryLater);

public interface ITextbeeWebhookSignatureVerifier
{
    bool Verify(string rawBody, string? signature);
}

public interface ITextbeeDeliveryStore
{
    Task<bool> ApplyStatusAsync(string smsBatchId, string recipient, string status, string? errorCode,
        DateTimeOffset? providerOccurredAt, DateTimeOffset? sentAt, DateTimeOffset? deliveredAt, CancellationToken ct);
    Task<bool> IsAwaitingReceiptAsync(string recipient, CancellationToken ct);
}

public sealed class TextbeeWebhookHandler(ITextbeeWebhookSignatureVerifier signatures, ITextbeeDeliveryStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TextbeeWebhookProcessResult> ProcessAsync(string rawBody, string? signature, CancellationToken ct)
    {
        if (!signatures.Verify(rawBody, signature)) return new(false, false, false);

        TextbeeDeliveryEvent? deliveryEvent;
        try { deliveryEvent = JsonSerializer.Deserialize<TextbeeDeliveryEvent>(rawBody, JsonOptions); }
        catch (JsonException) { return new(true, false, false); }

        if (deliveryEvent is null || string.IsNullOrWhiteSpace(deliveryEvent.WebhookEvent))
            return new(true, false, false);

        var eventType = deliveryEvent.WebhookEvent.ToUpperInvariant();
        var status = eventType switch
        {
            "MESSAGE_SENT" => "SENT",
            "MESSAGE_DELIVERED" => "DELIVERED",
            "MESSAGE_FAILED" => "FAILED",
            "UNKNOWN_STATE" => "UNKNOWN",
            _ => null
        };

        // InviteMe tracks delivery reports for outbound messages only. Textbee also sends
        // MESSAGE_RECEIVED when the gateway phone receives an SMS; acknowledge that valid,
        // signed event without trying to map it to an outbound job (it has no smsBatchId/recipient).
        if (status is null) return new(true, true, false);

        if (string.IsNullOrWhiteSpace(deliveryEvent.SmsBatchId) || deliveryEvent.SmsBatchId.Length > 128 ||
            string.IsNullOrWhiteSpace(deliveryEvent.Recipient) || deliveryEvent.Recipient.Length > 24)
            return new(true, false, false);

        var errorCode = status == "FAILED" ? SafeProviderError(deliveryEvent.ErrorCode) : null;
        var occurredAt = status switch
        {
            "SENT" => deliveryEvent.SentAt,
            "DELIVERED" => deliveryEvent.DeliveredAt ?? deliveryEvent.SentAt,
            "FAILED" => deliveryEvent.FailedAt,
            _ => null
        };
        if (await store.ApplyStatusAsync(deliveryEvent.SmsBatchId, deliveryEvent.Recipient, status, errorCode,
                occurredAt, deliveryEvent.SentAt, deliveryEvent.DeliveredAt, ct))
            return new(true, true, false);

        // A status callback may arrive before the worker persists smsBatchId. Ask textbee to retry it.
        return await store.IsAwaitingReceiptAsync(deliveryEvent.Recipient, ct)
            ? new(true, false, true)
            : new(true, true, false);
    }

    private static string? SafeProviderError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "TEXTBEE_SEND_FAILED";
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length <= 40 && normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? $"TEXTBEE_{normalized}"
            : "TEXTBEE_SEND_FAILED";
    }
}
