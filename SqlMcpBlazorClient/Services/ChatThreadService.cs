using SqlMcpBlazorClient.Models;

namespace SqlMcpBlazorClient.Services;

public class ChatThreadService
{
    private readonly List<ChatThread> _threads = new();
    private ChatThread? _currentThread;
    private int _nextConversationNumber = 1;

    public event Action? OnThreadsChanged;
    public event Action<ChatThread>? OnCurrentThreadChanged;

    public ChatThreadService()
    {
        // Create initial thread
        CreateNewThread();
    }

    public IReadOnlyList<ChatThread> GetAllThreads() => _threads.AsReadOnly();

    public ChatThread? GetCurrentThread() => _currentThread;

    public ChatThread? GetThreadById(string threadId)
    {
        return _threads.FirstOrDefault(t => t.Id == threadId);
    }

    public ChatThread CreateNewThread(string? title = null)
    {
        var thread = new ChatThread
        {
            Title = title ?? $"Conversation {_nextConversationNumber}",
            CreatedAt = DateTime.Now,
            LastMessageAt = DateTime.Now
        };

        _nextConversationNumber++; // Increment for next thread

        _threads.Insert(0, thread); // Add to beginning for reverse chronological order
        _currentThread = thread;

        OnThreadsChanged?.Invoke();
        OnCurrentThreadChanged?.Invoke(thread);

        return thread;
    }

    public void SwitchToThread(string threadId)
    {
        var thread = GetThreadById(threadId);
        if (thread != null && thread != _currentThread)
        {
            _currentThread = thread;
            OnCurrentThreadChanged?.Invoke(thread);
        }
    }

    public void DeleteThread(string threadId)
    {
        var thread = GetThreadById(threadId);
        if (thread != null)
        {
            _threads.Remove(thread);

            // If deleting current thread, switch to another or create new
            if (_currentThread?.Id == threadId)
            {
                if (_threads.Count > 0)
                {
                    SwitchToThread(_threads[0].Id);
                }
                else
                {
                    CreateNewThread();
                }
            }

            OnThreadsChanged?.Invoke();
        }
    }

    public void RenameThread(string threadId, string newTitle)
    {
        var thread = GetThreadById(threadId);
        if (thread != null)
        {
            thread.Title = newTitle;
            OnThreadsChanged?.Invoke();
        }
    }

    public void AddMessageToCurrentThread(ChatMessage message)
    {
        if (_currentThread != null)
        {
            _currentThread.Messages.Add(message);
            _currentThread.LastMessageAt = DateTime.Now;

            // Auto-title thread based on first user message
            if (_currentThread.MessageCount == 1 && message.Role == "User" && _currentThread.Title.StartsWith("Conversation"))
            {
                _currentThread.Title = TruncateTitle(message.Content);
            }

            OnThreadsChanged?.Invoke();
        }
    }

    public void ClearCurrentThread()
    {
        if (_currentThread != null)
        {
            _currentThread.Messages.Clear();
            _currentThread.LastMessageAt = DateTime.Now;
            OnThreadsChanged?.Invoke();
        }
    }

    private string TruncateTitle(string content, int maxLength = 50)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "New Conversation";

        content = content.Trim();
        if (content.Length <= maxLength)
            return content;

        return content.Substring(0, maxLength - 3) + "...";
    }

    public int GetTotalThreadCount() => _threads.Count;

    public int GetTotalMessageCount() => _threads.Sum(t => t.MessageCount);
}
