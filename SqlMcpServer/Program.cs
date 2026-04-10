using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SqlMcpServer
{
    /// <summary>
    /// Entry point for the SQL MCP Server application.
    /// Initializes and runs an MCP server that exposes SQL Server database analysis tools.
    /// </summary>
    internal class Program
    {
        /// <summary>
        /// Main entry point for the application.
        /// Configures logging, registers MCP server with stdio transport, and discovers tools from the assembly.
        /// </summary>
        /// <param name="args">Command line arguments (not used).</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        static async Task Main(string[] args)
        {
            // Create application builder with default configuration
            var builder = Host.CreateApplicationBuilder(args);

            // Configure logging to stderr for MCP protocol compatibility
            // MCP uses stdout for protocol messages, so logs must go to stderr
            builder.Logging.AddConsole(consoleLogOptions =>
            {
                consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            // Register MCP server services
            builder.Services
                .AddMcpServer()                    // Core MCP server functionality
                .WithStdioServerTransport()        // Use stdin/stdout for communication
                .WithToolsFromAssembly();          // Auto-discover tools from [McpServerTool] attributes

            // Build and run the host
            await builder.Build().RunAsync();
        }
    }
}
