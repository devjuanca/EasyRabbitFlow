using System.Text.Json.Serialization;

namespace RabbitFlowSample.Events;

public class WhatsAppEvent
{
    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
