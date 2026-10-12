using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace TPMRouter.Api.Usage;

public sealed class UsageHistoryStore
{
    private readonly string _connectionString;

    public UsageHistoryStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        string path = configuration["UsageHistory:DatabasePath"] ?? "data/usage-history.db";
        path = Path.GetFullPath(path, environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    public void Initialize()
    {
        using SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS usage_history (
                id INTEGER PRIMARY KEY,
                datetime_utc INTEGER NOT NULL,
                source_ip TEXT NOT NULL,
                source_text TEXT NOT NULL,
                tools_list TEXT NOT NULL,
                invoked_tool TEXT NOT NULL,
                input_token_size INTEGER,
                output_token_size INTEGER,
                output_text TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_usage_history_datetime ON usage_history(datetime_utc DESC, id DESC);
            """;
        command.ExecuteNonQuery();
    }

    public void Save(UsageRecord record)
    {
        using SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO usage_history
                (datetime_utc, source_ip, source_text, tools_list, invoked_tool,
                 input_token_size, output_token_size, output_text)
            VALUES ($datetime, $ip, $source, $tools, $invoked, $input, $output, $text);
            """;
        command.Parameters.AddWithValue("$datetime", record.DateTime.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$ip", record.SourceIp);
        command.Parameters.AddWithValue("$source", record.SourceText);
        command.Parameters.AddWithValue("$tools", record.ToolsList.ToJsonString());
        command.Parameters.AddWithValue("$invoked", record.InvokedTool.ToJsonString());
        command.Parameters.AddWithValue("$input", (object?)record.InputTokenSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", (object?)record.OutputTokenSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", record.OutputText);
        command.ExecuteNonQuery();
    }

    public UsageHistoryPage GetHistory(DateTimeOffset start, DateTimeOffset end, int page, int pageSize)
    {
        using SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM usage_history WHERE datetime_utc >= $start AND datetime_utc < $end;
            """;
        command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
        long totalCount = (long)command.ExecuteScalar()!;
        command.CommandText = """
            SELECT datetime_utc, source_ip, source_text, tools_list, invoked_tool,
                   input_token_size, output_token_size, output_text
            FROM usage_history WHERE datetime_utc >= $start AND datetime_utc < $end
            ORDER BY datetime_utc DESC, id DESC LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)(page - 1) * pageSize);
        List<UsageRecord> items = [];
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(new UsageRecord(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                                          reader.GetString(1),
                                          reader.GetString(2),
                                          (JsonArray)JsonNode.Parse(reader.GetString(3))!,
                                          (JsonArray)JsonNode.Parse(reader.GetString(4))!,
                                          reader.IsDBNull(5) ? null : reader.GetInt64(5),
                                          reader.IsDBNull(6) ? null : reader.GetInt64(6),
                                          reader.GetString(7)));
            }
        }

        transaction.Commit();
        return new UsageHistoryPage(items, totalCount, page, pageSize);
    }
}
