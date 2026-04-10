# SQL DB Assistant

A .NET-based SQL Server database assistant that combines the **Model Context Protocol (MCP)** and **Microsoft Semantic Kernel** to give you a conversational AI interface for analyzing and querying your SQL Server databases.

---

## Architecture

The solution is composed of two projects:

```
sql-db-assistant/
+-- SqlMcpServer/           # MCP Server - exposes SQL analysis tools over stdio
+-- SqlMcpBlazorClient/     # Blazor Server UI - chat interface powered by Semantic Kernel + Azure OpenAI
```

### How it works

```
User (Browser)
    |
    v
SqlMcpBlazorClient  (Blazor Server - Semantic Kernel + ChatCompletionAgent)
    |   spawns via stdio
    v
SqlMcpServer        (MCP Server - SQL Server tools)
    |
    v
SQL Server Database
```

1. The **Blazor client** launches the MCP server as a child process communicating over `stdin`/`stdout`.
2. **Semantic Kernel** wraps the MCP tools as kernel plugins and routes natural-language requests to them through an Azure OpenAI chat completion model.
3. The **MCP server** executes the actual T-SQL queries against SQL Server and returns structured results.

---

## Available MCP Tools

| Tool | Description |
|---|---|
| `FindOverlappingIndexes` | Detects duplicate and redundant indexes; reports size waste in MB |
| `ListTables` | Lists all user tables with row counts, total/used/unused space in MB |
| `ListWaitStats` | Shows SQL Server wait statistics to identify performance bottlenecks |
| `ListMissingIndexes` | Surfaces index recommendations with impact scores and ready-to-use `CREATE INDEX` statements |
| `ExecuteSqlQuery` | Executes any ad-hoc T-SQL query (SELECT, INSERT, UPDATE, DELETE, EXEC) |

All tools read the SQL Server connection string from the `SQL_CONNECTION_STRING` environment variable and enforce a **120-second command timeout**.

---

## Getting Started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9)
- A SQL Server instance (local or remote)
- An [Azure OpenAI](https://azure.microsoft.com/en-us/products/ai-services/openai-service) resource with a deployed chat model

### 1. Clone the repository

```bash
git clone https://github.com/KarthikeyanKailasam/sql-db-assistant.git
cd sql-db-assistant
```

### 2. Configure the Blazor client

Copy the example settings file and fill in your values:

```bash
cp SqlMcpBlazorClient/appsettings.json.example SqlMcpBlazorClient/appsettings.json
```

Edit `appsettings.json`:

```json
{
  "AzureOpenAI": {
    "ModelId": "your-model-deployment-name",
    "Endpoint": "https://your-resource-name.openai.azure.com/",
    "ApiKey": "YOUR_AZURE_OPENAI_API_KEY_HERE"
  },
  "ConnectionStrings": {
    "SqlServer": "Server=(localdb)\\MSSQLLocalDB;Database=YourDatabaseName;Integrated Security=true;TrustServerCertificate=true;"
  },
  "McpServer": {
    "ProjectPath": "C:\\full\\path\\to\\SqlMcpServer\\SqlMcpServer.csproj"
  }
}
```

| Key | Description |
|---|---|
| `AzureOpenAI:ModelId` | Your Azure OpenAI model deployment name |
| `AzureOpenAI:Endpoint` | Your Azure OpenAI endpoint URL |
| `AzureOpenAI:ApiKey` | Your Azure OpenAI API key |
| `ConnectionStrings:SqlServer` | SQL Server connection string passed to the MCP server |
| `McpServer:ProjectPath` | Absolute path to `SqlMcpServer.csproj` |

### 3. Run the application

```bash
cd SqlMcpBlazorClient
dotnet run
```

Open your browser at `https://localhost:5001` (or the port shown in the terminal). The Blazor app will automatically launch the MCP server as a background process.

> **Note:** You do not need to run `SqlMcpServer` separately - the Blazor client manages its lifecycle.

---

## Using the Chat Interface

- Navigate to the **Chat** page from the navigation menu.
- Type natural-language questions about your database, for example:
  - *"List all tables and their sizes"*
  - *"Are there any overlapping indexes I should clean up?"*
  - *"What are the top wait statistics on this server?"*
  - *"Show me missing index recommendations with their impact scores"*
  - *"Run: SELECT TOP 10 * FROM Orders ORDER BY OrderDate DESC"*
- Use the **Conversations** sidebar to create, switch between, or delete chat threads.

---

## Key Dependencies

### SqlMcpServer

| Package | Version | Purpose |
|---|---|---|
| `ModelContextProtocol` | 1.2.0 | MCP server hosting and tool registration |
| `Microsoft.Data.SqlClient` | 5.2.2 | SQL Server connectivity |
| `Microsoft.Extensions.Hosting` | 9.0.x | .NET Generic Host |

### SqlMcpBlazorClient

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.SemanticKernel` | 1.72.0 | AI orchestration and Azure OpenAI integration |
| `Microsoft.SemanticKernel.Agents.Core` | 1.72.0 | `ChatCompletionAgent` for conversational AI |
| `ModelContextProtocol` | 1.2.0 | MCP client to connect to the MCP server |
| `Microsoft.Extensions.AI` | 10.4.1 | AI abstractions |

---

## Security Notes

- `ExecuteSqlQuery` supports **write operations** (INSERT, UPDATE, DELETE). Use with caution and consider restricting the SQL Server login to read-only access if write access is not needed.
- The `SQL_CONNECTION_STRING` is passed as an environment variable to the MCP server process - avoid committing it to source control.
- `appsettings.json` is listed in `.gitignore` by default. Use `appsettings.json.example` as the template.

---

## Contributing

This project is a proof of concept intended to demonstrate the core approach. It is not actively maintained, and contributions are not expected at this stage.

---

## License

This project is open source. See [LICENSE](LICENSE) for details.
