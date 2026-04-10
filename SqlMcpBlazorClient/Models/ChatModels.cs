namespace SqlMcpBlazorClient.Models;

public class ChatThread
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "New Conversation";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastMessageAt { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = new();
    public int MessageCount => Messages.Count;
}

public class ChatMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class ChatResponse
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}
