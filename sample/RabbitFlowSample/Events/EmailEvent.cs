using System.Text.Json.Serialization;

namespace RabbitFlowSample.Events;

public class EmailEvent
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}
