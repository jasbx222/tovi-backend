using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Tovi.Api.Data;

/// <summary>
/// EnsureCreated only builds a schema on an empty database. This adds the tables and
/// columns that a newer model introduced to a database created by an older build.
/// It only ever creates; it never drops or alters existing columns.
/// </summary>
public static class SchemaUpgrade
{
    public static async Task ApplyAsync(ToviDb db, CancellationToken cancellationToken = default)
    {
        var existing = await ColumnsAsync(db, cancellationToken);
        if (existing.Count == 0) return;
        var differ = db.GetService<IMigrationsModelDiffer>();
        var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var operations = differ.GetDifferences(null, model);
        var wanted = new List<MigrationOperation>();
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            if (operation is CreateTableOperation table)
            {
                if (!existing.ContainsKey(table.Name))
                {
                    table.ForeignKeys.Clear();
                    wanted.Add(table);
                    created.Add(table.Name);
                    continue;
                }
                foreach (var column in table.Columns)
                {
                    if (existing[table.Name].Contains(column.Name)) continue;
                    wanted.Add(new AddColumnOperation
                    {
                        Table = column.Table,
                        Schema = column.Schema,
                        Name = column.Name,
                        ClrType = column.ClrType,
                        ColumnType = column.ColumnType,
                        IsNullable = true,
                        MaxLength = column.MaxLength,
                        IsUnicode = column.IsUnicode,
                    });
                }
            }
            else if (operation is CreateIndexOperation index && created.Contains(index.Table))
            {
                wanted.Add(index);
            }
        }
        if (wanted.Count == 0) return;
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(wanted, db.GetService<IDesignTimeModel>().Model);
        foreach (var command in commands)
            await db.Database.ExecuteSqlRawAsync(command.CommandText, cancellationToken);
    }

    static async Task<Dictionary<string, HashSet<string>>> ColumnsAsync(ToviDb db, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            var sqlite = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
            await using var command = connection.CreateCommand();
            command.CommandText = sqlite
                ? "SELECT m.name, p.name FROM sqlite_master m JOIN pragma_table_info(m.name) p WHERE m.type = 'table'"
                : "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS";
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(0);
                if (!result.TryGetValue(table, out var columns)) result[table] = columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                columns.Add(reader.GetString(1));
            }
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
        return result;
    }
}
