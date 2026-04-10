using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ModelContextProtocol.Client;

namespace SqlMcpBlazorClient.Services;

public class McpChatService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<McpChatService> _logger;
    private ChatCompletionAgent? _agent;
    private bool _isInitialized;
    private readonly Dictionary<string, ChatHistoryAgentThread> _chatThreads = new();

    public McpChatService(IConfiguration configuration, ILogger<McpChatService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        try
        {
            // Get configuration values
            var modelId = _configuration["AzureOpenAI:ModelId"] ?? throw new InvalidOperationException("ModelId not configured");
            var endpoint = _configuration["AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Endpoint not configured");
            var apiKey = _configuration["AzureOpenAI:ApiKey"] ?? throw new InvalidOperationException("ApiKey not configured");
            var dbConnStr = _configuration["ConnectionStrings:SqlServer"] ?? throw new InvalidOperationException("SqlServer connection string not configured");
            var mcpProjectPath = _configuration["McpServer:ProjectPath"] ?? throw new InvalidOperationException("MCP Project Path not configured");

            // Create an MCPClient
            var mcpClient = await ModelContextProtocol.Client.McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Name = "sql-mcp-server",
                Command = "dotnet",
                Arguments = ["run", "--project", mcpProjectPath],
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    { "SQL_CONNECTION_STRING", dbConnStr }
                }
            }));

            // Retrieve the list of tools available on the server
            var tools = await mcpClient.ListToolsAsync();
            _logger.LogInformation("Loaded {ToolCount} tools from MCP server", tools.Count);

            // Create a kernel with Azure OpenAI chat completion
            var kernelBuilder = Kernel.CreateBuilder()
                .AddAzureOpenAIChatCompletion(modelId, endpoint, apiKey);

            var kernel = kernelBuilder.Build();

            // Add MCP tools as kernel plugins
            kernel.Plugins.AddFromFunctions("SqlMcpServer", tools.Select(aiFunction => aiFunction.AsKernelFunction()));

            OpenAIPromptExecutionSettings executionSettings = new()
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            };

            // Create the chat agent
            _agent = new ChatCompletionAgent
            {
                Name = "SqlAssistant",
                Instructions = @"You are a helpful SQL database assistant with comprehensive database analysis capabilities. You can:

                    1. TABLE ANALYSIS:
                       - List all tables with schema, row counts, and size information (MB)
                       - Show space usage (total, used, and unused space)
                       - Identify largest tables in the database

                    2. INDEX OPTIMIZATION:
                       - Find overlapping and duplicate indexes
                       - List missing index recommendations with impact scores
                       - Provide CREATE INDEX statements for recommended indexes
                       - Calculate potential space savings

                    3. PERFORMANCE ANALYSIS:
                       - List SQL Server wait statistics
                       - Identify performance bottlenecks
                       - Explain wait types (CXPACKET, PAGEIOLATCH, LCK, WRITELOG, etc.)

                    4. QUERY EXECUTION:
                       - Execute ad-hoc SQL queries (SELECT, INSERT, UPDATE, DELETE)
                       - Return formatted results with proper table structure
                       - Display rows affected for modification queries

                    5. DATABASE INSIGHTS:
                       - Provide recommendations for performance improvements
                       - Explain database statistics and metrics
                       - Help users understand their database structure and data

                    TOOL CALLING TRANSPARENCY:
                    - ALWAYS mention which tool(s) you are calling at the beginning of your response
                    - Use this format: '[TOOL] Calling: [ToolName]' or '[TOOLS] Calling: [Tool1], [Tool2]'
                    - Example: '[TOOL] Calling: ListTables' or '[TOOL] Calling: ExecuteSqlQuery'
                    - If calling multiple tools in sequence, mention each one
                    - This helps users understand what operations are being performed on their database

                    IMPORTANT:
                    - Always format results clearly with proper structure
                    - When showing table data, preserve the formatted output
                    - Provide context and explanations for technical metrics
                    - Warn users before executing write operations
                    - Suggest testing recommendations in non-production environments first
                    - Be clear, concise, and helpful in your responses",
                Kernel = kernel,
                Arguments = new KernelArguments(executionSettings)
            };

            _isInitialized = true;
            _logger.LogInformation("MCP Chat Service initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize MCP Chat Service");
            throw;
        }
    }

    public async Task<Models.ChatResponse> SendMessageAsync(string userMessage, string threadId)
    {
        if (!_isInitialized || _agent == null)
        {
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");
        }

        try
        {
            // Get or create thread
            if (!_chatThreads.TryGetValue(threadId, out var agentThread))
            {
                agentThread = new ChatHistoryAgentThread();
                _chatThreads[threadId] = agentThread;
                _logger.LogInformation("Created new chat thread: {ThreadId}", threadId);
            }

            // Set timeout to 3 minutes (180 seconds)
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            var sb = new System.Text.StringBuilder();
            await foreach (var item in _agent.InvokeAsync(userMessage, agentThread, cancellationToken: cts.Token))
            {
                Microsoft.SemanticKernel.ChatMessageContent msg = item;
                foreach (var contentItem in msg.Items)
                {
                    if (contentItem is TextContent text)
                    {
                        sb.AppendLine(text.Text);
                    }
                }

                if (msg.Metadata != null && msg.Metadata.TryGetValue("FinishReason", out var finishReason))
                {
                    var reason = finishReason?.ToString();
                    sb.AppendLine($"Response FinishReason: {finishReason}");
                }
            }

            var fullResponse = sb.ToString();

            return new Models.ChatResponse
            {
                IsSuccess = true,
                Message = string.IsNullOrWhiteSpace(fullResponse) ? "No response received" : fullResponse,
                Role = "Assistant"
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Message processing timed out after 3 minutes: {Message}", userMessage);
            return new Models.ChatResponse
            {
                IsSuccess = false,
                Message = "The operation timed out after 3 minutes. The query might be too complex or the database might be slow to respond. Please try a simpler query or check your database connection.",
                Role = "System"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message: {Message}", userMessage);
            return new Models.ChatResponse
            {
                IsSuccess = false,
                Message = $"Error: {ex.Message}",
                Role = "System"
            };
        }
    }

    public void ClearThread(string threadId)
    {
        if (_chatThreads.TryGetValue(threadId, out var thread))
        {
            _chatThreads.Remove(threadId);
            _logger.LogInformation("Cleared chat thread: {ThreadId}", threadId);
        }
    }

    public void ClearAllThreads()
    {
        _chatThreads.Clear();
        _logger.LogInformation("Cleared all chat threads");
    }

    public int GetThreadMessageCount(string threadId)
    {
        if (_chatThreads.TryGetValue(threadId, out var thread))
        {
            // ChatHistoryAgentThread doesn't expose count directly
            // Return 0 as placeholder - actual count is managed by ChatThreadService
            return 0;
        }
        return 0;
    }
}
