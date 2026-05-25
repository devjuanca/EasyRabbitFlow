using System.Text.Json.Serialization;

namespace RabbitFlowSample.Samples.Thumbnails;

// One image-resize request handled by a temporary worker pool (IRabbitFlowTemporary).
// The whole batch lives only for the duration of a single RunAsync call; no queue
// declaration or consumer registration survives the request.
public sealed class ThumbnailJob
{
    [JsonPropertyName("imageUrl")]
    public string ImageUrl { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; } = 128;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 128;
}

// Per-image result returned by the await-completion endpoint.
public sealed class ThumbnailResult
{
    public string ImageUrl { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long DurationMs { get; set; }
    public string OutputPath { get; set; } = string.Empty;
}
