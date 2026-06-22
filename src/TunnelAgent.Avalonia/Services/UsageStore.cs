using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

/// <summary>
/// SQLite persistence for per-request usage events drained from the proxy's
/// destructive <c>/usage-queue</c>. The queue only retains ~60s of data and is
/// emptied on read, so the collector accumulates events here (deduped by
/// <c>event_hash</c>) to build durable history for the dashboard's time ranges.
/// Mirrors quotio-desktop's usage_store design.
/// </summary>
public sealed class UsageStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _conn;

    public UsageStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        _conn.Open();
        Initialize();
    }

    private void Initialize()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS usage_events (
                event_hash            TEXT PRIMARY KEY,
                timestamp_ms          INTEGER NOT NULL,
                request_id            TEXT,
                provider              TEXT,
                model                 TEXT,
                source                TEXT,
                path                  TEXT,
                input_tokens          INTEGER NOT NULL DEFAULT 0,
                output_tokens         INTEGER NOT NULL DEFAULT 0,
                reasoning_tokens      INTEGER NOT NULL DEFAULT 0,
                cached_tokens         INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens     INTEGER NOT NULL DEFAULT 0,
                total_tokens          INTEGER NOT NULL DEFAULT 0,
                latency_ms            INTEGER NOT NULL DEFAULT 0,
                failed                INTEGER NOT NULL DEFAULT 0,
                status_code           INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_usage_ts ON usage_events (timestamp_ms);
            """;
        cmd.ExecuteNonQuery();

        // Best-effort migration for databases created before request_id was stored.
        try
        {
            using var migrate = _conn.CreateCommand();
            migrate.CommandText = "ALTER TABLE usage_events ADD COLUMN request_id TEXT";
            migrate.ExecuteNonQuery();
        }
        catch { /* column already exists */ }
    }

    /// <summary>Insert a batch, ignoring duplicates. Returns the number of NEW rows persisted.</summary>
    public int InsertEvents(IReadOnlyList<UsageEvent> events)
    {
        if (events.Count == 0) return 0;

        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO usage_events
                    (event_hash, timestamp_ms, request_id, provider, model, source, path,
                     input_tokens, output_tokens, reasoning_tokens, cached_tokens,
                     cache_creation_tokens, cache_read_tokens, total_tokens,
                     latency_ms, failed, status_code)
                VALUES ($h,$ts,$rid,$prov,$model,$src,$path,$in,$out,$rsn,$cached,$cc,$cr,$total,$lat,$failed,$status)
                """;

            var pH = cmd.CreateParameter(); pH.ParameterName = "$h"; cmd.Parameters.Add(pH);
            var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
            var pRid = cmd.CreateParameter(); pRid.ParameterName = "$rid"; cmd.Parameters.Add(pRid);
            var pProv = cmd.CreateParameter(); pProv.ParameterName = "$prov"; cmd.Parameters.Add(pProv);
            var pModel = cmd.CreateParameter(); pModel.ParameterName = "$model"; cmd.Parameters.Add(pModel);
            var pSrc = cmd.CreateParameter(); pSrc.ParameterName = "$src"; cmd.Parameters.Add(pSrc);
            var pPath = cmd.CreateParameter(); pPath.ParameterName = "$path"; cmd.Parameters.Add(pPath);
            var pIn = cmd.CreateParameter(); pIn.ParameterName = "$in"; cmd.Parameters.Add(pIn);
            var pOut = cmd.CreateParameter(); pOut.ParameterName = "$out"; cmd.Parameters.Add(pOut);
            var pRsn = cmd.CreateParameter(); pRsn.ParameterName = "$rsn"; cmd.Parameters.Add(pRsn);
            var pCached = cmd.CreateParameter(); pCached.ParameterName = "$cached"; cmd.Parameters.Add(pCached);
            var pCc = cmd.CreateParameter(); pCc.ParameterName = "$cc"; cmd.Parameters.Add(pCc);
            var pCr = cmd.CreateParameter(); pCr.ParameterName = "$cr"; cmd.Parameters.Add(pCr);
            var pTotal = cmd.CreateParameter(); pTotal.ParameterName = "$total"; cmd.Parameters.Add(pTotal);
            var pLat = cmd.CreateParameter(); pLat.ParameterName = "$lat"; cmd.Parameters.Add(pLat);
            var pFailed = cmd.CreateParameter(); pFailed.ParameterName = "$failed"; cmd.Parameters.Add(pFailed);
            var pStatus = cmd.CreateParameter(); pStatus.ParameterName = "$status"; cmd.Parameters.Add(pStatus);

            var inserted = 0;
            foreach (var e in events)
            {
                pH.Value = e.EventHash;
                pTs.Value = new DateTimeOffset(e.Timestamp.ToUniversalTime()).ToUnixTimeMilliseconds();
                pRid.Value = string.IsNullOrWhiteSpace(e.RequestId) ? DBNull.Value : e.RequestId;
                pProv.Value = (object?)e.Provider ?? DBNull.Value;
                pModel.Value = e.Model;
                pSrc.Value = (object?)e.Source ?? DBNull.Value;
                pPath.Value = (object?)e.Path ?? DBNull.Value;
                pIn.Value = e.InputTokens;
                pOut.Value = e.OutputTokens;
                pRsn.Value = e.ReasoningTokens;
                pCached.Value = e.CachedTokens;
                pCc.Value = e.CacheCreationTokens;
                pCr.Value = e.CacheReadTokens;
                pTotal.Value = e.TotalTokens;
                pLat.Value = e.LatencyMs;
                pFailed.Value = e.Failed ? 1 : 0;
                pStatus.Value = (object?)e.StatusCode ?? DBNull.Value;
                inserted += cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return inserted;
        }
    }

    /// <summary>Load the most recent <paramref name="limit"/> events (newest first by timestamp).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM usage_events";
            cmd.ExecuteNonQuery();
        }
    }

    public List<UsageEvent> LoadRecent(int limit)
    {
        var list = new List<UsageEvent>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT event_hash, timestamp_ms, request_id, provider, model, source, path,
                       input_tokens, output_tokens, reasoning_tokens, cached_tokens,
                       cache_creation_tokens, cache_read_tokens, total_tokens,
                       latency_ms, failed, status_code
                FROM usage_events ORDER BY timestamp_ms DESC LIMIT $limit
                """;
            var p = cmd.CreateParameter(); p.ParameterName = "$limit"; p.Value = limit; cmd.Parameters.Add(p);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new UsageEvent
                {
                    EventHash = reader.GetString(0),
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).LocalDateTime,
                    RequestId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Provider = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Model = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    Source = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Path = reader.IsDBNull(6) ? null : reader.GetString(6),
                    InputTokens = reader.GetInt64(7),
                    OutputTokens = reader.GetInt64(8),
                    ReasoningTokens = reader.GetInt64(9),
                    CachedTokens = reader.GetInt64(10),
                    CacheCreationTokens = reader.GetInt64(11),
                    CacheReadTokens = reader.GetInt64(12),
                    TotalTokens = reader.GetInt64(13),
                    LatencyMs = reader.GetInt64(14),
                    Failed = reader.GetInt64(15) != 0,
                    StatusCode = reader.IsDBNull(16) ? null : (int)reader.GetInt64(16),
                });
            }
        }
        return list;
    }

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }
}
