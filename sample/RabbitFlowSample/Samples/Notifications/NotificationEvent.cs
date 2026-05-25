using System.Text.Json.Serialization;

namespace RabbitFlowSample.Samples.Notifications;

// Composite payload published to the "notifications" fanout exchange.
// Each subscriber inspects its own slice (email or whatsapp) and ignores the rest,
// so producers can broadcast a multi-channel event without coordinating per-channel publishes.
public sealed class NotificationEvent
{
    [JsonPropertyName("emailData")]
    public EmailNotification? EmailNotificationData { get; set; }

    [JsonPropertyName("whatsappData")]
    public WhatsAppNotification? WhatsAppNotificationData { get; set; }
}

public sealed class EmailNotification
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}

public sealed class WhatsAppNotification
{
    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
