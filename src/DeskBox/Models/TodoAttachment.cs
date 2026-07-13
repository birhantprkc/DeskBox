namespace DeskBox.Models;

public sealed class TodoAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string FilePath { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Type { get; set; } = "file";

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
