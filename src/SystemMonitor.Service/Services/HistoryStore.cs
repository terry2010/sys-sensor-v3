using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SystemMonitor.Service.Services
{
    public sealed class HistoryStore
    {
        private readonly ILogger<HistoryStore> _logger;
        private string _dbPath = string.Empty;

        public HistoryStore(ILogger<HistoryStore> logger)
        {
            _logger = logger;
        }

        public async Task InitAsync(string? baseDir = null, CancellationToken ct = default)
        {
            try
            {
                var dir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "data");
                Directory.CreateDirectory(dir);
                _dbPath = Path.Combine(dir, "metrics.db");
                var cs = new SqliteConnectionStringBuilder
                {
                    DataSource = _dbPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Shared
                }.ToString();
                await using var conn = new SqliteConnection(cs);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                var cmdText = @"
CREATE TABLE IF NOT EXISTS metrics (
  ts INTEGER NOT NULL,
  cpu REAL NULL,
  mem_total INTEGER NULL,
  mem_used INTEGER NULL
);
CREATE INDEX IF NOT EXISTS idx_metrics_ts ON metrics(ts);

-- 10s aggregation table
CREATE TABLE IF NOT EXISTS metrics_10s (
  ts INTEGER NOT NULL PRIMARY KEY, -- bucket end timestamp (ms)
  cpu REAL NULL,
  mem_total INTEGER NULL,
  mem_used INTEGER NULL
);
CREATE INDEX IF NOT EXISTS idx_metrics_10s_ts ON metrics_10s(ts);

-- 1m aggregation table
CREATE TABLE IF NOT EXISTS metrics_1m (
  ts INTEGER NOT NULL PRIMARY KEY, -- bucket end timestamp (ms)
  cpu REAL NULL,
  mem_total INTEGER NULL,
  mem_used INTEGER NULL
);
CREATE INDEX IF NOT EXISTS idx_metrics_1m_ts ON metrics_1m(ts);
";
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = cmdText;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("HistoryStore initialized at {Path}", _dbPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HistoryStore init failed");
                throw;
            }
        }

        public async Task AppendAsync(long ts, double? cpu, (long total, long used)? mem, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_dbPath)) return;
            try
            {
                var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Shared }.ToString();
                await using var conn = new SqliteConnection(cs);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO metrics(ts, cpu, mem_total, mem_used) VALUES($ts, $cpu, $mt, $mu)";
                cmd.Parameters.AddWithValue("$ts", ts);
                cmd.Parameters.AddWithValue("$cpu", (object?)cpu ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$mt", (object?)mem?.total ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$mu", (object?)mem?.used ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                // also write aggregation buckets (last-value wins semantics)
                static long BucketEnd(long t, long bucketMs) => ((t - 1) / bucketMs + 1) * bucketMs;
                var ts10 = BucketEnd(ts, 10_000);
                var ts60 = BucketEnd(ts, 60_000);

                // 10s upsert
                await using (var up10 = conn.CreateCommand())
                {
                    up10.CommandText = "INSERT INTO metrics_10s(ts, cpu, mem_total, mem_used) VALUES($ts, $cpu, $mt, $mu) " +
                        "ON CONFLICT(ts) DO UPDATE SET cpu=excluded.cpu, mem_total=excluded.mem_total, mem_used=excluded.mem_used";
                    up10.Parameters.AddWithValue("$ts", ts10);
                    up10.Parameters.AddWithValue("$cpu", (object?)cpu ?? DBNull.Value);
                    up10.Parameters.AddWithValue("$mt", (object?)mem?.total ?? DBNull.Value);
                    up10.Parameters.AddWithValue("$mu", (object?)mem?.used ?? DBNull.Value);
                    await up10.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                // 1m upsert
                await using (var up60 = conn.CreateCommand())
                {
                    up60.CommandText = "INSERT INTO metrics_1m(ts, cpu, mem_total, mem_used) VALUES($ts, $cpu, $mt, $mu) " +
                        "ON CONFLICT(ts) DO UPDATE SET cpu=excluded.cpu, mem_total=excluded.mem_total, mem_used=excluded.mem_used";
                    up60.Parameters.AddWithValue("$ts", ts60);
                    up60.Parameters.AddWithValue("$cpu", (object?)cpu ?? DBNull.Value);
                    up60.Parameters.AddWithValue("$mt", (object?)mem?.total ?? DBNull.Value);
                    up60.Parameters.AddWithValue("$mu", (object?)mem?.used ?? DBNull.Value);
                    await up60.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "append history failed (ignored)");
            }
        }

        public sealed class MetricRow
        {
            public long Ts { get; set; }
            public double? Cpu { get; set; }
            public long? MemTotal { get; set; }
            public long? MemUsed { get; set; }
        }

        public async Task<List<MetricRow>> QueryAsync(long fromTs, long toTs, CancellationToken ct = default)
        {
            var result = new List<MetricRow>();
            if (string.IsNullOrEmpty(_dbPath)) return result;
            try
            {
                var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString();
                await using var conn = new SqliteConnection(cs);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ts, cpu, mem_total, mem_used FROM metrics WHERE ts >= $from AND ts <= $to ORDER BY ts ASC";
                cmd.Parameters.AddWithValue("$from", fromTs);
                cmd.Parameters.AddWithValue("$to", toTs);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var row = new MetricRow
                    {
                        Ts = reader.GetInt64(0),
                        Cpu = reader.IsDBNull(1) ? null : reader.GetDouble(1),
                        MemTotal = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        MemUsed = reader.IsDBNull(3) ? null : reader.GetInt64(3)
                    };
                    result.Add(row);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "query history failed");
            }
            return result;
        }

        public async Task<List<MetricRow>> QueryAggAsync(string agg, long fromTs, long toTs, CancellationToken ct = default)
        {
            var result = new List<MetricRow>();
            if (string.IsNullOrEmpty(_dbPath)) return result;
            var table = string.Equals(agg, "10s", StringComparison.OrdinalIgnoreCase) ? "metrics_10s"
                : string.Equals(agg, "1m", StringComparison.OrdinalIgnoreCase) ? "metrics_1m" : null;
            if (table == null) return await QueryAsync(fromTs, toTs, ct);
            try
            {
                var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString();
                await using var conn = new SqliteConnection(cs);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT ts, cpu, mem_total, mem_used FROM {table} WHERE ts >= $from AND ts <= $to ORDER BY ts ASC";
                cmd.Parameters.AddWithValue("$from", fromTs);
                cmd.Parameters.AddWithValue("$to", toTs);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var row = new MetricRow
                    {
                        Ts = reader.GetInt64(0),
                        Cpu = reader.IsDBNull(1) ? null : reader.GetDouble(1),
                        MemTotal = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        MemUsed = reader.IsDBNull(3) ? null : reader.GetInt64(3)
                    };
                    result.Add(row);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "query agg history failed");
            }
            return result;
        }
    }
}
