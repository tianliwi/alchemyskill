using AlchemyProxy.Infrastructure;
using AlchemyProxy.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AlchemyProxy.Storage;

public sealed class SqliteSessionStore
{
    private readonly string _connectionString;

    public SqliteSessionStore(
        IOptions<LocalStorageOptions> options,
        IHostEnvironment environment)
    {
        var rootPath = Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath);
        Directory.CreateDirectory(rootPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(rootPath, "troubleshooting.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                query TEXT NOT NULL,
                locale TEXT NOT NULL,
                solution_id TEXT NOT NULL,
                solution_version TEXT NOT NULL,
                current_graph_id TEXT NOT NULL,
                current_node_id TEXT NOT NULL,
                status TEXT NOT NULL,
                revision INTEGER NOT NULL,
                context_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_sessions_owner
                ON sessions (tenant_id, user_id, updated_at);

            CREATE INDEX IF NOT EXISTS ix_sessions_expiration
                ON sessions (expires_at);

            CREATE TABLE IF NOT EXISTS session_events (
                session_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                graph_id TEXT,
                node_id TEXT,
                choice_id TEXT,
                choice_source TEXT,
                confidence REAL,
                evidence TEXT,
                user_message TEXT,
                created_at TEXT NOT NULL,
                PRIMARY KEY (session_id, sequence),
                FOREIGN KEY (session_id) REFERENCES sessions(session_id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateAsync(
        TroubleshootingSession session,
        SessionEvent createdEvent,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await InsertSessionAsync(connection, transaction, session, cancellationToken);
        await InsertEventAsync(connection, transaction, createdEvent, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<TroubleshootingSession?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM sessions WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
    }

    public async Task<IReadOnlyList<SessionEvent>> GetEventsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence, event_type, graph_id, node_id, choice_id, choice_source,
                   confidence, evidence, user_message, created_at
            FROM session_events
            WHERE session_id = $sessionId
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        var events = new List<SessionEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new SessionEvent
            {
                SessionId = sessionId,
                Sequence = reader.GetInt32(0),
                EventType = reader.GetString(1),
                GraphId = reader.IsDBNull(2) ? null : reader.GetString(2),
                NodeId = reader.IsDBNull(3) ? null : reader.GetString(3),
                ChoiceId = reader.IsDBNull(4) ? null : reader.GetString(4),
                ChoiceSource = reader.IsDBNull(5) ? null : reader.GetString(5),
                Confidence = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                Evidence = reader.IsDBNull(7) ? null : reader.GetString(7),
                UserMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(9))
            });
        }

        return events;
    }

    public async Task UpdateAsync(
        TroubleshootingSession session,
        int expectedRevision,
        SessionEvent sessionEvent,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE sessions
            SET current_graph_id = $currentGraphId,
                current_node_id = $currentNodeId,
                status = $status,
                revision = $revision,
                context_json = $contextJson,
                updated_at = $updatedAt
            WHERE session_id = $sessionId
              AND revision = $expectedRevision;
            """;
        command.Parameters.AddWithValue("$currentGraphId", session.CurrentGraphId);
        command.Parameters.AddWithValue("$currentNodeId", session.CurrentNodeId);
        command.Parameters.AddWithValue("$status", session.Status);
        command.Parameters.AddWithValue("$revision", session.Revision);
        command.Parameters.AddWithValue("$contextJson", session.ContextJson);
        command.Parameters.AddWithValue("$updatedAt", session.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$sessionId", session.SessionId);
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ApiException(409, "stale_session_revision", "The troubleshooting session has changed.");
        }

        await InsertEventAsync(connection, transaction, sessionEvent, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task InsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TroubleshootingSession session,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO sessions (
                session_id, tenant_id, user_id, query, locale, solution_id, solution_version,
                current_graph_id, current_node_id, status, revision, context_json,
                created_at, updated_at, expires_at)
            VALUES (
                $sessionId, $tenantId, $userId, $query, $locale, $solutionId, $solutionVersion,
                $currentGraphId, $currentNodeId, $status, $revision, $contextJson,
                $createdAt, $updatedAt, $expiresAt);
            """;
        command.Parameters.AddWithValue("$sessionId", session.SessionId);
        command.Parameters.AddWithValue("$tenantId", session.TenantId);
        command.Parameters.AddWithValue("$userId", session.UserId);
        command.Parameters.AddWithValue("$query", session.Query);
        command.Parameters.AddWithValue("$locale", session.Locale);
        command.Parameters.AddWithValue("$solutionId", session.SolutionId);
        command.Parameters.AddWithValue("$solutionVersion", session.SolutionVersion);
        command.Parameters.AddWithValue("$currentGraphId", session.CurrentGraphId);
        command.Parameters.AddWithValue("$currentNodeId", session.CurrentNodeId);
        command.Parameters.AddWithValue("$status", session.Status);
        command.Parameters.AddWithValue("$revision", session.Revision);
        command.Parameters.AddWithValue("$contextJson", session.ContextJson);
        command.Parameters.AddWithValue("$createdAt", session.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", session.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$expiresAt", session.ExpiresAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionEvent sessionEvent,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO session_events (
                session_id, sequence, event_type, graph_id, node_id, choice_id,
                choice_source, confidence, evidence, user_message, created_at)
            VALUES (
                $sessionId, $sequence, $eventType, $graphId, $nodeId, $choiceId,
                $choiceSource, $confidence, $evidence, $userMessage, $createdAt);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionEvent.SessionId);
        command.Parameters.AddWithValue("$sequence", sessionEvent.Sequence);
        command.Parameters.AddWithValue("$eventType", sessionEvent.EventType);
        command.Parameters.AddWithValue("$graphId", (object?)sessionEvent.GraphId ?? DBNull.Value);
        command.Parameters.AddWithValue("$nodeId", (object?)sessionEvent.NodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$choiceId", (object?)sessionEvent.ChoiceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$choiceSource", (object?)sessionEvent.ChoiceSource ?? DBNull.Value);
        command.Parameters.AddWithValue("$confidence", (object?)sessionEvent.Confidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidence", (object?)sessionEvent.Evidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$userMessage", (object?)sessionEvent.UserMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", sessionEvent.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TroubleshootingSession ReadSession(SqliteDataReader reader) => new()
    {
        SessionId = reader.GetString(reader.GetOrdinal("session_id")),
        TenantId = reader.GetString(reader.GetOrdinal("tenant_id")),
        UserId = reader.GetString(reader.GetOrdinal("user_id")),
        Query = reader.GetString(reader.GetOrdinal("query")),
        Locale = reader.GetString(reader.GetOrdinal("locale")),
        SolutionId = reader.GetString(reader.GetOrdinal("solution_id")),
        SolutionVersion = reader.GetString(reader.GetOrdinal("solution_version")),
        CurrentGraphId = reader.GetString(reader.GetOrdinal("current_graph_id")),
        CurrentNodeId = reader.GetString(reader.GetOrdinal("current_node_id")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        Revision = reader.GetInt32(reader.GetOrdinal("revision")),
        ContextJson = reader.GetString(reader.GetOrdinal("context_json")),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
        ExpiresAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("expires_at")))
    };
}
