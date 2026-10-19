using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;

namespace SLSKDONET.Services;

/// <summary>What one <see cref="SchemaReconciler.Reconcile"/> pass changed (or failed to change).</summary>
public sealed class SchemaReconciliationReport
{
    public List<string> CreatedTables { get; } = new();
    public List<string> AddedColumns { get; } = new();
    public List<string> CreatedIndexes { get; } = new();
    public List<string> Failures { get; } = new();

    public bool HasChanges => CreatedTables.Count + AddedColumns.Count + CreatedIndexes.Count > 0;

    public override string ToString() =>
        $"{CreatedTables.Count} table(s), {AddedColumns.Count} column(s), {CreatedIndexes.Count} index(es) added; {Failures.Count} failure(s)";
}

/// <summary>
/// Last step of database initialisation: makes the SQLite schema a superset of the EF model.
/// <para>
/// The schema is built from two sources that drift apart — the EF migrations (what a brand-new
/// database gets from <c>MigrateAsync</c>) and the hand-written raw-SQL patches in
/// <see cref="SchemaMigratorService"/> (what existing databases picked up over time). 2026-10-19:
/// a fresh install (<c>--setup-preview</c>) was missing 13 mapped columns, including
/// <c>audio_features.VectorEmbedding</c>, so every library query failed. This pass is the safety
/// net behind both: driven by the EF model, it creates missing tables, adds missing columns and
/// creates missing unique indexes.
/// </para>
/// <para>
/// Additive only — never drops, renames or rewrites anything — and cheap when nothing is
/// missing: one <c>sqlite_master</c> read plus one <c>pragma_table_info</c> (and, for tables with
/// unique keys, one <c>pragma_index_list</c>) per mapped table. Each statement runs on its own, so
/// one failure (for example a unique index that existing duplicate rows prevent) is logged and
/// does not block the rest. It never throws: startup continues and the failures are reported.
/// </para>
/// </summary>
public static class SchemaReconciler
{
    public static SchemaReconciliationReport Reconcile(AppDbContext context, DbConnection connection, ILogger logger)
    {
        var report = new SchemaReconciliationReport();
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
                connection.Open();

            // The design-time model carries the full relational metadata (store types, defaults,
            // indexes) that the differ and SQL generator need; the runtime model is trimmed.
            var model = context.GetService<IDesignTimeModel>().Model;
            var relationalModel = model.GetRelationalModel();
            var existingTables = ReadTableNames(connection);

            var missingTables = relationalModel.Tables
                .Where(t => !existingTables.Contains(t.Name))
                .Select(t => t.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (missingTables.Count > 0)
                CreateMissingTables(context, model, relationalModel, missingTables, connection, logger, report);

            foreach (var table in relationalModel.Tables)
            {
                if (missingTables.Contains(table.Name))
                    continue; // just created from the model, complete by construction

                AddMissingColumns(table, connection, logger, report);
                CreateMissingUniqueIndexes(table, connection, logger, report);
            }
        }
        catch (Exception ex)
        {
            report.Failures.Add($"reconciliation aborted: {ex.Message}");
            logger.LogError(ex, "Schema reconciliation aborted");
        }

        if (report.HasChanges || report.Failures.Count > 0)
        {
            logger.LogWarning(
                "Schema reconciliation: {Summary}. Tables: [{Tables}] Columns: [{Columns}] Indexes: [{Indexes}] Failures: [{Failures}]",
                report,
                string.Join(", ", report.CreatedTables),
                string.Join(", ", report.AddedColumns),
                string.Join(", ", report.CreatedIndexes),
                string.Join("; ", report.Failures));
        }
        else
        {
            logger.LogInformation("Schema reconciliation: schema matches the EF model, nothing to patch.");
        }

        return report;
    }

    private static HashSet<string> ReadTableNames(DbConnection connection)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static void CreateMissingTables(
        AppDbContext context,
        IModel model,
        IRelationalModel relationalModel,
        HashSet<string> missingTables,
        DbConnection connection,
        ILogger logger,
        SchemaReconciliationReport report)
    {
        // Diffing "nothing" against the model yields exactly the CREATE TABLE / CREATE INDEX
        // operations EnsureCreated would run; keep only the ones for the missing tables.
        var differ = context.GetService<IMigrationsModelDiffer>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var operations = differ.GetDifferences(null, relationalModel);

        foreach (var create in operations.OfType<CreateTableOperation>().Where(op => missingTables.Contains(op.Name)))
        {
            var indexOps = operations.OfType<CreateIndexOperation>()
                .Where(op => string.Equals(op.Table, create.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!TryExecute(connection, generator.Generate(new MigrationOperation[] { create }, model), out var error))
            {
                report.Failures.Add($"create table {create.Name}: {error}");
                logger.LogError("Schema reconciliation: could not create table {Table}: {Error}", create.Name, error);
                continue;
            }
            report.CreatedTables.Add(create.Name);
            logger.LogWarning("Schema reconciliation: created missing table {Table}", create.Name);

            foreach (var index in indexOps)
            {
                if (TryExecute(connection, generator.Generate(new MigrationOperation[] { index }, model), out error))
                {
                    report.CreatedIndexes.Add($"{create.Name}.{index.Name}");
                }
                else
                {
                    report.Failures.Add($"create index {index.Name}: {error}");
                    logger.LogWarning("Schema reconciliation: could not create index {Index} on new table {Table}: {Error}", index.Name, create.Name, error);
                }
            }
        }
    }

    private static void AddMissingColumns(ITable table, DbConnection connection, ILogger logger, SchemaReconciliationReport report)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info(@t)";
            AddParameter(cmd, "@t", table.Name);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(0));
        }

        foreach (var column in table.Columns)
        {
            if (existing.Contains(column.Name))
                continue;

            var sql = BuildAddColumnSql(table.Name, column);
            if (TryExecute(connection, sql, out var error))
            {
                report.AddedColumns.Add($"{table.Name}.{column.Name}");
                logger.LogWarning("Schema reconciliation: added missing column {Table}.{Column} ({Sql})", table.Name, column.Name, sql);
            }
            else
            {
                report.Failures.Add($"add column {table.Name}.{column.Name}: {error}");
                logger.LogError("Schema reconciliation: could not add column {Table}.{Column}: {Error}", table.Name, column.Name, error);
            }
        }
    }

    /// <summary>
    /// <c>ALTER TABLE … ADD COLUMN</c> for a model column. SQLite rejects NOT NULL without a
    /// default on ADD COLUMN, and existing rows need a value anyway, so NOT NULL columns always get
    /// one: the model's own default if it has one, else the CLR default of the stored type (0,
    /// '' , zero Guid, …) — the same value EF would materialise for an unset property.
    /// </summary>
    internal static string BuildAddColumnSql(string tableName, IColumn column)
    {
        var sql = $"ALTER TABLE {Quote(tableName)} ADD COLUMN {Quote(column.Name)} {column.StoreType}";
        if (column.IsNullable)
        {
            var nullableDefault = ModelDefaultLiteral(column);
            return nullableDefault == null ? sql + " NULL;" : $"{sql} NULL DEFAULT {nullableDefault};";
        }

        return $"{sql} NOT NULL DEFAULT {ModelDefaultLiteral(column) ?? SafeDefaultLiteral(column)};";
    }

    private static string? ModelDefaultLiteral(IColumn column)
    {
        if (!string.IsNullOrWhiteSpace(column.DefaultValueSql))
            return $"({column.DefaultValueSql})";
        if (column.DefaultValue is { } value and not DBNull)
            return column.StoreTypeMapping.GenerateSqlLiteral(value);
        return null;
    }

    private static string SafeDefaultLiteral(IColumn column)
    {
        var providerType = Nullable.GetUnderlyingType(column.ProviderClrType) ?? column.ProviderClrType;
        if (providerType == typeof(string))
            return "''";
        if (providerType == typeof(byte[]))
            return "X''";
        if (providerType == typeof(bool) || providerType.IsEnum || IsNumeric(providerType))
            return "0";
        if (providerType.IsValueType)
            return column.StoreTypeMapping.GenerateProviderValueSqlLiteral(Activator.CreateInstance(providerType));
        return "''";
    }

    private static bool IsNumeric(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)
        || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort)
        || t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    /// <summary>
    /// Unique indexes and alternate keys the code relies on for upserts and lookups (for example
    /// audio_features.TrackUniqueHash). A table created by an old raw-SQL patch can lack them;
    /// matched by column set, not by name, so an equivalent index under another name counts.
    /// </summary>
    private static void CreateMissingUniqueIndexes(ITable table, DbConnection connection, ILogger logger, SchemaReconciliationReport report)
    {
        var wanted = new List<(string Name, IReadOnlyList<string> Columns, string? Filter)>();
        foreach (var index in table.Indexes.Where(i => i.IsUnique))
            wanted.Add((index.Name, index.Columns.Select(c => c.Name).ToList(), index.Filter));
        foreach (var key in table.UniqueConstraints.Where(k => !k.GetIsPrimaryKey()))
            wanted.Add((key.Name, key.Columns.Select(c => c.Name).ToList(), null));

        if (wanted.Count == 0)
            return;

        var existingUnique = ReadUniqueIndexColumnSets(table.Name, connection);
        foreach (var (name, columns, filter) in wanted)
        {
            var set = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
            if (existingUnique.Any(e => e.SetEquals(set)))
                continue;

            // The model's name can already be taken by a NON-unique index (the fresh-install
            // migrations create IX_audio_features_TrackUniqueHash without UNIQUE), and
            // "IF NOT EXISTS" would then silently do nothing. Pick a free name instead.
            var indexName = name;
            for (var n = 2; IndexNameExists(connection, indexName); n++)
                indexName = $"UX_{table.Name}_{string.Join("_", columns)}" + (n > 2 ? $"_{n}" : string.Empty);

            var sql = $"CREATE UNIQUE INDEX {Quote(indexName)} ON {Quote(table.Name)} ({string.Join(", ", columns.Select(Quote))})"
                      + (string.IsNullOrWhiteSpace(filter) ? ";" : $" WHERE {filter};");
            if (TryExecute(connection, sql, out var error))
            {
                existingUnique.Add(set);
                report.CreatedIndexes.Add($"{table.Name}.{indexName}");
                logger.LogWarning("Schema reconciliation: created missing unique index {Index} on {Table}", indexName, table.Name);
            }
            else
            {
                // Typically duplicate rows already in the table. Never delete data to make it fit.
                report.Failures.Add($"create unique index {name}: {error}");
                logger.LogWarning("Schema reconciliation: could not create unique index {Index} on {Table}: {Error}", name, table.Name, error);
            }
        }
    }

    private static bool IndexNameExists(DbConnection connection, string indexName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name = @n COLLATE NOCASE";
        AddParameter(cmd, "@n", indexName);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static List<HashSet<string>> ReadUniqueIndexColumnSets(string tableName, DbConnection connection)
    {
        var indexNames = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_index_list(@t) WHERE \"unique\" = 1";
            AddParameter(cmd, "@t", tableName);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                indexNames.Add(reader.GetString(0));
        }

        var sets = new List<HashSet<string>>();
        foreach (var indexName in indexNames)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_index_info(@i)";
            AddParameter(cmd, "@i", indexName);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                    set.Add(reader.GetString(0));
            }
            sets.Add(set);
        }
        return sets;
    }

    private static bool TryExecute(DbConnection connection, IEnumerable<MigrationCommand> commands, out string? error)
    {
        foreach (var command in commands)
        {
            if (!TryExecute(connection, command.CommandText, out error))
                return false;
        }
        error = null;
        return true;
    }

    private static bool TryExecute(DbConnection connection, string sql, out string? error)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
            error = null;
            return true;
        }
        catch (DbException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
