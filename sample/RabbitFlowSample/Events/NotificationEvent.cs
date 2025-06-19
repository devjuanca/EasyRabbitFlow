using System.Text.Json.Serialization;

namespace RabbitFlowSample.Events;

public class NotificationEvent
{
    [JsonPropertyName("emailData")]
    public EmailNotification? EmailNotificationData { get; set; }


    [JsonPropertyName("whatsappData")]
    public WhatsAppNotification? WhatsAppNotificationData { get; set; }
}


public class EmailNotification
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}

public class WhatsAppNotification
{
    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}