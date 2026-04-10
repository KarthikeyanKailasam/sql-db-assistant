// ==============================================================================
// SQL MCP Server Tools - Database Analysis and Query Execution Tools
// ==============================================================================
// This file contains all MCP server tools for SQL Server database analysis.
// Each method is decorated with [McpServerTool] to be automatically discovered
// and exposed via the Model Context Protocol.
//
// Available Tools:
// 1. FindOverlappingIndexes - Identifies duplicate/redundant indexes
// 2. ListTables - Shows all tables with sizes and row counts
// 3. ListWaitStats - Displays SQL Server wait statistics
// 4. ListMissingIndexes - Provides index recommendations with impact scores
// 5. ExecuteSqlQuery - Executes ad-hoc SQL queries
//
// Configuration:
// All tools read connection string from SQL_CONNECTION_STRING environment variable
//
// Security:
// - ExecuteSqlQuery supports write operations - use with caution
// - All queries use parameterized commands where applicable
// - Timeout protection on all operations (default: 120 seconds)
// ==============================================================================

using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace SqlMcpServer
{
    /// <summary>
    /// Static class containing all SQL Server analysis tools exposed via Model Context Protocol.
    /// Tools are automatically discovered and registered via [McpServerToolType] & [McpServerTool] attributes.
    /// </summary>
    [McpServerToolType]
    public static class Tool
    {
        /// <summary>
        /// Environment variable name for SQL Server connection string.
        /// </summary>
        private const string ConnectionStringEnvVar = "SQL_CONNECTION_STRING";

        /// <summary>
        /// Retrieves the SQL Server connection string from environment variables.
        /// </summary>
        /// <returns>The connection string.</returns>
        /// <exception cref="InvalidOperationException">Thrown when connection string is not configured.</exception>
        private static string GetConnectionString()
        {
            var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Connection string not found. Please set the '{ConnectionStringEnvVar}' environment variable. " +
                    $"Example: Server=localhost;Database=MyDB;Integrated Security=true;TrustServerCertificate=true;");
            }
            return connectionString;
        }

        /// <summary>
        /// Identifies overlapping SQL indexes in the database that waste space and degrade performance.
        /// Finds both identical key indexes and superset indexes (where one index is a prefix of another).
        /// </summary>
        /// <returns>
        /// A formatted report showing:
        /// - Tables with overlapping indexes
        /// - Key column definitions
        /// - Identical and superset index names
        /// - Total overlapping count and size in MB
        /// Ordered by total overlapping index size (descending).
        /// </returns>
        /// <remarks>
        /// Uses sys.indexes, sys.index_columns, and sys.dm_db_partition_stats system views.
        /// Timeout: 120 seconds.
        /// </remarks>
        [McpServerTool, Description("Identifies potential overlapping SQL indexes in the database. Returns tables with duplicate or overlapping indexes along with their size in MB. Reads connection string from SQL_CONNECTION_STRING environment variable.")]
        public static async Task<string> FindOverlappingIndexes()
        {
            // Query to find overlapping indexes using CTEs for clarity
            // idx CTE: Aggregates key columns for each index
            // idx_size CTE: Calculates index sizes from partition stats
            // Main query: Finds indexes with identical keys or superset relationships
            const string sql = @"
            WITH idx AS (
                SELECT
                    i.object_id,
                    i.index_id,
                    i.name,
                    key_columns =
                        STRING_AGG(CONVERT(nvarchar(4000), c.name)
                            + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE ' ASC' END, ', ')
                        WITHIN GROUP (ORDER BY ic.key_ordinal)
                FROM sys.indexes i
                JOIN sys.index_columns ic
                    ON ic.object_id = i.object_id
                   AND ic.index_id = i.index_id
                JOIN sys.columns c
                    ON c.object_id = ic.object_id
                   AND c.column_id = ic.column_id
                WHERE i.index_id > 0
                  AND ic.key_ordinal > 0
                  AND OBJECTPROPERTY(i.object_id,'IsUserTable') = 1
                GROUP BY i.object_id, i.index_id, i.name
            ),

            idx_size AS (
                SELECT
                    object_id,
                    index_id,
                    SUM(reserved_page_count) * 8.0 / 1024 AS size_mb
                FROM sys.dm_db_partition_stats
                GROUP BY object_id, index_id
            )

            SELECT
                QUOTENAME(OBJECT_SCHEMA_NAME(i.object_id)) + '.'
                    + QUOTENAME(OBJECT_NAME(i.object_id)) AS table_name,

                i.key_columns AS key_column_definition,

                -- Identical key indexes
                STRING_AGG(i.name, '; ') AS indexes_with_identical_keys,

                -- Superset indexes
                super.indexes_with_prefix_superset,

                COUNT(*) 
                  + ISNULL(super.super_count, 0) AS total_overlapping_index_count,

                -- Collective size (identical + supersets)
                ROUND(
                    SUM(ISNULL(s.size_mb, 0))
                    + ISNULL(super.super_size_mb, 0),
                    2
                ) AS total_overlapping_index_size_mb

            FROM idx i

            LEFT JOIN idx_size s
                ON s.object_id = i.object_id
               AND s.index_id = i.index_id

            OUTER APPLY (
                SELECT
                    STRING_AGG(i2.name, '; ') AS indexes_with_prefix_superset,
                    COUNT(*) AS super_count,
                    SUM(ISNULL(s2.size_mb, 0)) AS super_size_mb
                FROM idx i2
                LEFT JOIN idx_size s2
                    ON s2.object_id = i2.object_id
                   AND s2.index_id = i2.index_id
                WHERE i2.object_id = i.object_id
                  AND i2.key_columns LIKE i.key_columns + ', %'
            ) super

            GROUP BY
                i.object_id,
                i.key_columns,
                super.indexes_with_prefix_superset,
                super.super_count,
                super.super_size_mb

            HAVING
                COUNT(*) > 1
                OR ISNULL(super.super_count, 0) > 0

            ORDER BY
                total_overlapping_index_size_mb DESC,
                table_name;
            ";

            try
            {
                var connectionString = GetConnectionString();
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                await using var command = new SqlCommand(sql, connection);
                command.CommandTimeout = 120;

                await using var reader = await command.ExecuteReaderAsync();

                if (!reader.HasRows)
                {
                    return "No overlapping indexes found in the database.";
                }

                var results = new StringBuilder();
                results.AppendLine("Overlapping SQL Indexes Report");
                results.AppendLine("==============================");
                results.AppendLine();

                int rowCount = 0;
                while (await reader.ReadAsync())
                {
                    rowCount++;
                    results.AppendLine($"Table: {reader["table_name"]}");
                    results.AppendLine($"Key Columns: {reader["key_column_definition"]}");
                    results.AppendLine($"Identical Key Indexes: {reader["indexes_with_identical_keys"]}");

                    if (!reader.IsDBNull(reader.GetOrdinal("indexes_with_prefix_superset")))
                    {
                        results.AppendLine($"Superset Indexes: {reader["indexes_with_prefix_superset"]}");
                    }

                    results.AppendLine($"Total Overlapping Count: {reader["total_overlapping_index_count"]}");
                    results.AppendLine($"Total Size (MB): {reader["total_overlapping_index_size_mb"]}");
                    results.AppendLine();
                    results.AppendLine("---");
                    results.AppendLine();
                }

                results.AppendLine($"Total overlapping index groups found: {rowCount}");
                return results.ToString();
            }
            catch (SqlException ex)
            {
                return $"SQL Error: {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool, Description("Lists all tables in the database with schema, row count, and size information (in MB). Ordered by size descending. Reads connection string from SQL_CONNECTION_STRING environment variable.")]
        public static async Task<string> ListTables()
        {
            const string sql = @"
            SELECT
                QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name) AS table_name,
                SCHEMA_NAME(t.schema_id) AS schema_name,
                t.name AS object_name,
                SUM(p.rows) AS row_count,
                CAST(ROUND(
                    SUM(a.total_pages) * 8 / 1024.00, 2
                ) AS DECIMAL(18,2)) AS total_space_mb,
                CAST(ROUND(
                    SUM(a.used_pages) * 8 / 1024.00, 2
                ) AS DECIMAL(18,2)) AS used_space_mb,
                CAST(ROUND(
                    (SUM(a.total_pages) - SUM(a.used_pages)) * 8 / 1024.00, 2
                ) AS DECIMAL(18,2)) AS unused_space_mb
            FROM
                sys.tables t
            INNER JOIN
                sys.indexes i ON t.object_id = i.object_id
            INNER JOIN
                sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
            INNER JOIN
                sys.allocation_units a ON p.partition_id = a.container_id
            WHERE
                t.is_ms_shipped = 0
                AND i.object_id > 255
            GROUP BY
                t.schema_id,
                t.name
            ORDER BY
                total_space_mb DESC;
            ";

            try
            {
                var connectionString = GetConnectionString();
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                await using var command = new SqlCommand(sql, connection);
                command.CommandTimeout = 120;

                await using var reader = await command.ExecuteReaderAsync();

                if (!reader.HasRows)
                {
                    return "No user tables found in the database.";
                }

                var results = new StringBuilder();
                results.AppendLine("Database Tables Report");
                results.AppendLine("======================");
                results.AppendLine();

                int tableCount = 0;
                decimal totalSizeMb = 0;
                long totalRows = 0;

                while (await reader.ReadAsync())
                {
                    tableCount++;
                    var rowCount = reader.GetInt64(reader.GetOrdinal("row_count"));
                    var totalSpaceMb = reader.GetDecimal(reader.GetOrdinal("total_space_mb"));

                    totalRows += rowCount;
                    totalSizeMb += totalSpaceMb;

                    results.AppendLine($"Table: {reader["table_name"]}");
                    results.AppendLine($"  Schema: {reader["schema_name"]}");
                    results.AppendLine($"  Row Count: {rowCount:N0}");
                    results.AppendLine($"  Total Size: {totalSpaceMb:N2} MB");
                    results.AppendLine($"  Used Space: {reader["used_space_mb"]:N2} MB");
                    results.AppendLine($"  Unused Space: {reader["unused_space_mb"]:N2} MB");
                    results.AppendLine();
                }

                results.AppendLine("---");
                results.AppendLine($"Total Tables: {tableCount}");
                results.AppendLine($"Total Rows: {totalRows:N0}");
                results.AppendLine($"Total Size: {totalSizeMb:N2} MB");

                return results.ToString();
            }
            catch (SqlException ex)
            {
                return $"SQL Error: {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool, Description("Lists SQL Server wait statistics showing where the database is spending time waiting. Includes wait time, signal wait time, and percentage of total waits. Excludes common benign waits. Reads connection string from SQL_CONNECTION_STRING environment variable.")]
        public static async Task<string> ListWaitStats()
        {
            const string sql = @"
            WITH Waits AS (
                SELECT
                    wait_type,
                    wait_time_ms / 1000.0 AS wait_time_s,
                    (wait_time_ms - signal_wait_time_ms) / 1000.0 AS resource_wait_time_s,
                    signal_wait_time_ms / 1000.0 AS signal_wait_time_s,
                    waiting_tasks_count,
                    100.0 * wait_time_ms / SUM(wait_time_ms) OVER() AS pct
                FROM sys.dm_os_wait_stats
                WHERE wait_type NOT IN (
                    -- Exclude common benign waits
                    'CLR_SEMAPHORE', 'LAZYWRITER_SLEEP', 'RESOURCE_QUEUE',
                    'SLEEP_TASK', 'SLEEP_SYSTEMTASK', 'SQLTRACE_BUFFER_FLUSH',
                    'WAITFOR', 'LOGMGR_QUEUE', 'CHECKPOINT_QUEUE',
                    'REQUEST_FOR_DEADLOCK_SEARCH', 'XE_TIMER_EVENT', 'BROKER_TO_FLUSH',
                    'BROKER_TASK_STOP', 'CLR_MANUAL_EVENT', 'CLR_AUTO_EVENT',
                    'DISPATCHER_QUEUE_SEMAPHORE', 'FT_IFTS_SCHEDULER_IDLE_WAIT',
                    'XE_DISPATCHER_WAIT', 'XE_DISPATCHER_JOIN', 'SQLTRACE_INCREMENTAL_FLUSH_SLEEP',
                    'ONDEMAND_TASK_QUEUE', 'BROKER_EVENTHANDLER', 'SLEEP_BPOOL_FLUSH',
                    'DIRTY_PAGE_POLL', 'HADR_FILESTREAM_IOMGR_IOCOMPLETION', 'SP_SERVER_DIAGNOSTICS_SLEEP'
                )
                AND wait_time_ms > 0
            )
            SELECT TOP 50
                wait_type,
                CAST(wait_time_s AS DECIMAL(18,2)) AS wait_time_seconds,
                CAST(resource_wait_time_s AS DECIMAL(18,2)) AS resource_wait_seconds,
                CAST(signal_wait_time_s AS DECIMAL(18,2)) AS signal_wait_seconds,
                waiting_tasks_count,
                CAST(pct AS DECIMAL(5,2)) AS wait_percentage
            FROM Waits
            WHERE pct > 0.1
            ORDER BY wait_time_s DESC;
            ";

            try
            {
                var connectionString = GetConnectionString();
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                await using var command = new SqlCommand(sql, connection);
                command.CommandTimeout = 120;

                await using var reader = await command.ExecuteReaderAsync();

                if (!reader.HasRows)
                {
                    return "No significant wait statistics found.";
                }

                var results = new StringBuilder();
                results.AppendLine("SQL Server Wait Statistics Report");
                results.AppendLine("===================================");
                results.AppendLine();
                results.AppendLine("Top waits indicate where SQL Server is spending time:");
                results.AppendLine("- CXPACKET: Parallelism waits");
                results.AppendLine("- PAGEIOLATCH_*: Disk I/O waits");
                results.AppendLine("- LCK_*: Locking waits");
                results.AppendLine("- WRITELOG: Transaction log write waits");
                results.AppendLine("- ASYNC_NETWORK_IO: Client processing waits");
                results.AppendLine();

                int waitCount = 0;
                while (await reader.ReadAsync())
                {
                    waitCount++;
                    results.AppendLine($"{waitCount}. {reader["wait_type"]}");
                    results.AppendLine($"   Wait Time: {reader["wait_time_seconds"]:N2} seconds ({reader["wait_percentage"]:N2}%)");
                    results.AppendLine($"   Resource Wait: {reader["resource_wait_seconds"]:N2} seconds");
                    results.AppendLine($"   Signal Wait: {reader["signal_wait_seconds"]:N2} seconds");
                    results.AppendLine($"   Waiting Tasks: {reader["waiting_tasks_count"]:N0}");
                    results.AppendLine();
                }

                results.AppendLine("---");
                results.AppendLine($"Total wait types displayed: {waitCount}");
                results.AppendLine();
                results.AppendLine("Note: Wait stats are cumulative since last SQL Server restart or manual reset.");

                return results.ToString();
            }
            catch (SqlException ex)
            {
                return $"SQL Error: {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool, Description("Lists missing index recommendations from SQL Server's Database Engine Tuning Advisor. Includes impact score, equality/inequality columns, and included columns. Ordered by impact score descending. Reads connection string from SQL_CONNECTION_STRING environment variable.")]
        public static async Task<string> ListMissingIndexes()
        {
            const string sql = @"
            SELECT TOP 50
                QUOTENAME(DB_NAME(mid.database_id)) AS database_name,
                QUOTENAME(OBJECT_SCHEMA_NAME(mid.object_id, mid.database_id)) + '.'
                    + QUOTENAME(OBJECT_NAME(mid.object_id, mid.database_id)) AS table_name,

                -- Impact score calculation
                CAST(
                    (migs.avg_total_user_cost * migs.avg_user_impact * (migs.user_seeks + migs.user_scans))
                    AS DECIMAL(18,2)
                ) AS impact_score,

                migs.user_seeks,
                migs.user_scans,
                migs.last_user_seek,
                migs.last_user_scan,

                CAST(migs.avg_total_user_cost AS DECIMAL(18,4)) AS avg_query_cost,
                CAST(migs.avg_user_impact AS DECIMAL(5,2)) AS avg_user_impact_pct,

                mid.equality_columns,
                mid.inequality_columns,
                mid.included_columns,

                -- CREATE INDEX statement
                'CREATE NONCLUSTERED INDEX IX_' 
                    + REPLACE(REPLACE(REPLACE(OBJECT_NAME(mid.object_id, mid.database_id), '[', ''), ']', ''), '.', '_')
                    + '_' + CAST(ROW_NUMBER() OVER (ORDER BY (migs.avg_total_user_cost * migs.avg_user_impact * (migs.user_seeks + migs.user_scans)) DESC) AS VARCHAR(5))
                    + ' ON ' + QUOTENAME(OBJECT_SCHEMA_NAME(mid.object_id, mid.database_id)) + '.'
                    + QUOTENAME(OBJECT_NAME(mid.object_id, mid.database_id))
                    + ' (' + ISNULL(mid.equality_columns, '') 
                    + CASE WHEN mid.inequality_columns IS NOT NULL THEN 
                        CASE WHEN mid.equality_columns IS NOT NULL THEN ', ' ELSE '' END 
                        + mid.inequality_columns 
                      ELSE '' END
                    + ')'
                    + CASE WHEN mid.included_columns IS NOT NULL 
                        THEN ' INCLUDE (' + mid.included_columns + ')' 
                        ELSE '' 
                      END
                    + ';' AS create_index_statement

            FROM sys.dm_db_missing_index_details mid
            INNER JOIN sys.dm_db_missing_index_groups mig
                ON mid.index_handle = mig.index_handle
            INNER JOIN sys.dm_db_missing_index_group_stats migs
                ON mig.index_group_handle = migs.group_handle
            WHERE
                mid.database_id = DB_ID()
                AND (migs.user_seeks > 0 OR migs.user_scans > 0)
            ORDER BY
                impact_score DESC;
            ";

            try
            {
                var connectionString = GetConnectionString();
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                await using var command = new SqlCommand(sql, connection);
                command.CommandTimeout = 120;

                await using var reader = await command.ExecuteReaderAsync();

                if (!reader.HasRows)
                {
                    return "No missing index recommendations found. This could mean:\n" +
                           "- The database is well-optimized\n" +
                           "- SQL Server hasn't collected enough query data yet\n" +
                           "- Statistics were recently cleared";
                }

                var results = new StringBuilder();
                results.AppendLine("Missing Index Recommendations Report");
                results.AppendLine("=====================================");
                results.AppendLine();
                results.AppendLine("Impact Score = avg_query_cost × avg_user_impact × (seeks + scans)");
                results.AppendLine("Higher impact scores indicate indexes that would likely provide the most benefit.");
                results.AppendLine();

                int indexCount = 0;
                while (await reader.ReadAsync())
                {
                    indexCount++;
                    results.AppendLine($"--- Recommendation #{indexCount} ---");
                    results.AppendLine($"Table: {reader["table_name"]}");
                    results.AppendLine($"Impact Score: {reader["impact_score"]:N2}");
                    results.AppendLine();

                    results.AppendLine("Usage Statistics:");
                    results.AppendLine($"  User Seeks: {reader["user_seeks"]:N0}");
                    results.AppendLine($"  User Scans: {reader["user_scans"]:N0}");
                    results.AppendLine($"  Avg Query Cost: {reader["avg_query_cost"]:N4}");
                    results.AppendLine($"  Avg User Impact: {reader["avg_user_impact_pct"]:N2}%");
                    results.AppendLine();

                    if (!reader.IsDBNull(reader.GetOrdinal("last_user_seek")))
                    {
                        results.AppendLine($"  Last Seek: {reader["last_user_seek"]}");
                    }
                    if (!reader.IsDBNull(reader.GetOrdinal("last_user_scan")))
                    {
                        results.AppendLine($"  Last Scan: {reader["last_user_scan"]}");
                    }
                    results.AppendLine();

                    results.AppendLine("Recommended Index Columns:");
                    if (!reader.IsDBNull(reader.GetOrdinal("equality_columns")))
                    {
                        results.AppendLine($"  Equality: {reader["equality_columns"]}");
                    }
                    if (!reader.IsDBNull(reader.GetOrdinal("inequality_columns")))
                    {
                        results.AppendLine($"  Inequality: {reader["inequality_columns"]}");
                    }
                    if (!reader.IsDBNull(reader.GetOrdinal("included_columns")))
                    {
                        results.AppendLine($"  Include: {reader["included_columns"]}");
                    }
                    results.AppendLine();

                    results.AppendLine("CREATE INDEX Statement:");
                    results.AppendLine(reader["create_index_statement"].ToString());
                    results.AppendLine();
                }

                results.AppendLine("===================================");
                results.AppendLine($"Total recommendations: {indexCount}");
                results.AppendLine();
                results.AppendLine("⚠️  Important Notes:");
                results.AppendLine("- Review each recommendation carefully before implementation");
                results.AppendLine("- Consider existing indexes and potential overlaps");
                results.AppendLine("- Indexes improve read performance but add overhead to writes");
                results.AppendLine("- Test in a non-production environment first");

                return results.ToString();
            }
            catch (SqlException ex)
            {
                return $"SQL Error: {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool, Description("Executes an ad-hoc SQL query or script on the database. Supports both SELECT queries (returns results) and data modification commands (returns rows affected). Use with caution for write operations. Reads connection string from SQL_CONNECTION_STRING environment variable.")]
        public static async Task<string> ExecuteSqlQuery(
[Description("The SQL query or script to execute. Can be SELECT, INSERT, UPDATE, DELETE, or other valid T-SQL commands.")]
            string sqlQuery,
[Description("Optional timeout in seconds (default: 120 seconds)")]
            int timeoutSeconds = 120)
        {
            if (string.IsNullOrWhiteSpace(sqlQuery))
            {
                return "Error: SQL query cannot be empty.";
            }

            try
            {
                var connectionString = GetConnectionString();
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                await using var command = new SqlCommand(sqlQuery, connection);
                command.CommandTimeout = timeoutSeconds;

                var trimmedQuery = sqlQuery.TrimStart().ToUpperInvariant();
                bool isSelectQuery = trimmedQuery.StartsWith("SELECT") ||
                                    trimmedQuery.StartsWith("WITH") ||
                                    trimmedQuery.StartsWith("EXEC") ||
                                    trimmedQuery.StartsWith("EXECUTE");

                if (isSelectQuery)
                {
                    await using var reader = await command.ExecuteReaderAsync();

                    if (!reader.HasRows)
                    {
                        return "Query executed successfully. No rows returned.";
                    }

                    var results = new StringBuilder();
                    results.AppendLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
                    results.AppendLine("║                         SQL QUERY RESULTS                                     ║");
                    results.AppendLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
                    results.AppendLine();

                    var columnNames = new List<string>();
                    var columnWidths = new List<int>();

                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var columnName = reader.GetName(i);
                        columnNames.Add(columnName);
                        columnWidths.Add(Math.Max(columnName.Length, 10));
                    }

                    var headerLine = new StringBuilder("│ ");
                    foreach (var (name, width) in columnNames.Zip(columnWidths))
                    {
                        headerLine.Append(name.PadRight(width)).Append(" │ ");
                    }
                    results.AppendLine(headerLine.ToString());

                    var separatorLine = new StringBuilder("├─");
                    foreach (var width in columnWidths)
                    {
                        separatorLine.Append(new string('─', width)).Append("─┼─");
                    }
                    separatorLine.Length -= 2;
                    separatorLine.Append("┤");
                    results.AppendLine(separatorLine.ToString());

                    int rowCount = 0;
                    const int maxRows = 1000;

                    while (await reader.ReadAsync() && rowCount < maxRows)
                    {
                        rowCount++;
                        var rowLine = new StringBuilder("│ ");

                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            var value = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString();
                            if (value!.Length > columnWidths[i])
                            {
                                value = value.Substring(0, columnWidths[i] - 3) + "...";
                            }
                            rowLine.Append(value.PadRight(columnWidths[i])).Append(" │ ");
                        }

                        results.AppendLine(rowLine.ToString());
                    }

                    results.AppendLine("└" + new string('─', headerLine.Length - 2) + "┘");
                    results.AppendLine();

                    if (rowCount >= maxRows)
                    {
                        results.AppendLine($"⚠️  Showing first {maxRows} rows. Query returned more rows than the display limit.");
                        results.AppendLine();
                    }

                    results.AppendLine($"📊 Total rows displayed: {rowCount}");
                    results.AppendLine($"⏱️  Query executed successfully.");

                    return results.ToString();
                }
                else
                {
                    int rowsAffected = await command.ExecuteNonQueryAsync();

                    var results = new StringBuilder();
                    results.AppendLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
                    results.AppendLine("║                    SQL COMMAND EXECUTION RESULT                               ║");
                    results.AppendLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
                    results.AppendLine();
                    results.AppendLine($"✅ Command executed successfully.");
                    results.AppendLine($"📝 Rows affected: {rowsAffected}");
                    results.AppendLine();

                    return results.ToString();
                }
            }
            catch (SqlException ex)
            {
                var error = new StringBuilder();
                error.AppendLine("❌ SQL Error occurred:");
                error.AppendLine($"   Message: {ex.Message}");
                error.AppendLine($"   Error Number: {ex.Number}");
                error.AppendLine($"   Line Number: {ex.LineNumber}");
                error.AppendLine($"   Severity: {ex.Class}");
                if (!string.IsNullOrEmpty(ex.Procedure))
                {
                    error.AppendLine($"   Procedure: {ex.Procedure}");
                }
                return error.ToString();
            }
            catch (Exception ex)
            {
                return $"❌ Error: {ex.Message}";
            }
        }
    }
}
