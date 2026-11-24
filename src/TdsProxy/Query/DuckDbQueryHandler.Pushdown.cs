using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using TdsProxy.Sql;

namespace TdsProxy.Query;

/// <summary>
/// Conditions on a mounted SQL Server table, sent to SQL Server as the WHERE of the SELECT it is
/// read with, so that only the rows they keep cross the network.
///
/// DuckDB's C API gives a table function no filters: a WHERE on a view over daffy_scan is applied
/// by DuckDB once every row has arrived. So the conditions are found here instead, in the query
/// DuckDB is about to run - parsed by DuckDB itself, with json_serialize_sql - and each table that
/// has some is read with daffy_scan(..., filter := 'T-SQL') in place of its view. The T-SQL is
/// written here, from the parsed conditions and the values DuckDB gives their constants, never
/// copied from what the client sent.
///
/// The query keeps every condition it had: DuckDB still applies them all to what arrives. So a
/// condition sent to SQL Server can only save rows on the wire, never change the answer - as long
/// as SQL Server keeps at least every row DuckDB would. Everything here is about that:
/// <list type="bullet">
/// <item>Only conditions on one table, of one shape: a column against constants (=, &lt;&gt;, &lt;,
/// &gt;, BETWEEN, IN, NOT IN), IS [NOT] NULL, and AND and OR of those. Anything with a function,
/// a subquery, a parameter or another table's column stays in DuckDB.</item>
/// <item>Never to the side of an outer join that can come back as NULLs. Taking a row away there
/// turns its match into a NULL-extended row, which a condition such as o.id IS NULL then keeps.
/// The ON conditions of a LEFT JOIN go to the right-hand table, and of an inner join to either.</item>
/// <item>Only where SQL Server compares as DuckDB does, or keeps more. Numbers, bit, date,
/// datetime2 and uniqueidentifier compare exactly. Text only with = and IN, an ASCII constant, and
/// a case-insensitive collation: SQL Server then keeps what DuckDB's nocase keeps, and more besides
/// (trailing spaces, accents under _AI); &lt;&gt; on text it would not. datetime holds 1/300ths of a
/// second, so its conditions are sent 4 ms wider. float, real, time and datetimeoffset are not
/// sent at all.</item>
/// </list>
/// Constants are written as SQL Server cannot misread them: dates as 'yyyyMMdd', timestamps cast
/// to datetime2(7), names bracketed. Whatever cannot be sent is not, and the query runs as it
/// would have; nothing here ever fails a query that would otherwise have run.
/// </summary>
internal sealed partial class DuckDbQueryHandler
{
    /// <summary>A datetime is a count of 1/300ths of a second, so a condition on one is widened by more than one of them.</summary>
    private static readonly TimeSpan DatetimeSlack = TimeSpan.FromMilliseconds(4);

    /// <summary>What a column's conditions may be: how SQL Server compares it, against how DuckDB does.</summary>
    private enum PushKind { None, Exact, Bit, Date, Stamp, Datetime, Text, Guid }

    /// <summary>A mounted SQL Server table as the query names it: where it is in the parse tree, and what it reads.</summary>
    private sealed class PushTable(JsonObject node, string binding, string catalog, MountedObject mounted, bool whereSafe)
    {
        public JsonObject Node { get; } = node;
        public string Binding { get; } = binding;
        public string Catalog { get; } = catalog;
        public MountedObject Mounted { get; } = mounted;

        /// <summary>Whether a WHERE condition may be sent to it: no outer join can NULL-extend it.</summary>
        public bool WhereSafe { get; } = whereSafe;

        /// <summary>Named with three parts somewhere (ms.orders.id), which the table function in its place could not answer to.</summary>
        public bool Unsafe { get; set; }

        public Dictionary<string, PushKind> Kinds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Cond> Conditions { get; } = [];
    }

    /// <summary>A condition as it will be written for SQL Server, its constants still to be evaluated by DuckDB.</summary>
    private abstract record Cond;
    private sealed record Compare(string Column, PushKind Kind, string Op, int Value) : Cond;
    private sealed record Between(string Column, PushKind Kind, int Low, int High) : Cond;
    private sealed record InList(string Column, PushKind Kind, bool Not, int[] Values) : Cond;
    private sealed record IsNull(string Column, bool Not) : Cond;
    private sealed record Junction(bool And, Cond[] Parts) : Cond;

    /// <summary>A constant to evaluate, cast first to what DuckDB would cast it to against the column.</summary>
    private sealed record Constant(JsonNode Node, string? CastTo);

    private JsonObject? _scanTemplate;

    /// <summary>The query with conditions on mounted SQL Server tables sent to SQL Server, or as it was when there are none.</summary>
    private string WithSqlServerFilters(string sql)
    {
        var mounted = SqlServerTables();
        if (mounted.Count == 0 || !mounted.Keys.Any(k => sql.Contains(k.Item2, StringComparison.OrdinalIgnoreCase))) return sql;

        try
        {
            return Pushed(sql, mounted) ?? sql;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Chatter.Line($"[sqlserver] nothing sent to SQL Server: {FirstLine(ex.Message)}");
            return sql;
        }
    }

    /// <summary>Every mounted SQL Server table whose columns are known, by its schema and name here.</summary>
    private Dictionary<(string, string), (string Catalog, MountedObject Mounted)> SqlServerTables()
    {
        var tables = new Dictionary<(string, string), (string, MountedObject)>(NameComparer.Instance);
        foreach (var mount in _mounts.Where(m => m.Kind == "sqlserver" && m.Catalog is not null))
            foreach (var mounted in mount.Objects.Where(o => o.Columns is not null))
                tables[(mounted.Schema, mounted.Name)] = (mount.Catalog!, mounted);
        return tables;
    }

    private string? Pushed(string sql, Dictionary<(string, string), (string Catalog, MountedObject Mounted)> mounted)
    {
        var tree = JsonNode.Parse(Serialized(sql))!.AsObject();
        if (tree["error"]?.GetValue<bool>() != false || tree["statements"] is not JsonArray { Count: 1 } statements) return null;

        // A plain SELECT. A CTE could take a mounted table's name, and a set operation has more than one FROM.
        if (statements[0]?["node"] is not JsonObject select || (string?)select["type"] != "SELECT_NODE") return null;
        if (select["cte_map"]?["map"] is JsonArray { Count: > 0 }) return null;
        if (select["from_table"] is not JsonObject from) return null;

        var leaves = new List<(JsonObject Node, string Binding, bool WhereSafe)>();
        Leaves(from, whereSafe: true, leaves);
        if (leaves.GroupBy(l => l.Binding, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) return null;

        var tables = new List<PushTable>();
        foreach (var (node, binding, whereSafe) in leaves)
            if (Resolve(node, mounted) is { } found)
                tables.Add(new PushTable(node, binding, found.Catalog, found.Mounted, whereSafe));
        if (tables.Count == 0) return null;

        foreach (var table in tables)
            foreach (var (column, kind) in ColumnKinds(table.Mounted))
                table.Kinds[column] = kind;
        MarkThreePartNames(select, tables);

        var constants = new List<Constant>();
        foreach (var condition in Conjuncts(select["where_clause"]))
            if (OnlyTable(condition, leaves.Count, tables) is { WhereSafe: true, Unsafe: false } table && Build(condition, table, constants) is { } built)
                table.Conditions.Add(built);
        JoinConditions(from, tables, leaves.Count, constants);

        var sending = tables.Where(t => t.Conditions.Count > 0).ToList();
        if (sending.Count == 0) return null;

        var values = Evaluated(constants);
        var sent = new List<string>();
        foreach (var table in sending)
        {
            var written = table.Conditions.Select(c => Written(c, values)).Where(w => w is not null).ToList();
            if (written.Count == 0) continue;

            string filter = string.Join(" AND ", written);
            table.Node.ReplaceWith(ScanNode(table, filter));
            sent.Add($"{table.Mounted.Schema}.{table.Mounted.Name}{(table.Binding.Equals(table.Mounted.Name, StringComparison.OrdinalIgnoreCase) ? "" : $" {table.Binding}")}: {filter}");
        }
        if (sent.Count == 0) return null;

        string rewritten = Deserialized(tree.ToJsonString());
        foreach (string line in sent) Chatter.Line($"[sqlserver] sent to SQL Server, {line}");
        return rewritten;
    }

    // ---------------------------------------------------------------------------------------
    // The FROM clause: which tables there are, and which a condition may be sent to

    /// <summary>
    /// The FROM clause's tables, in order, each with its binding name and whether a WHERE condition
    /// on it may go to it: not when an outer join can NULL-extend it, and not inside a join of a kind
    /// whose rows depend on more than a filter (ASOF, POSITIONAL, LATERAL, SEMI, ANTI).
    /// </summary>
    private static void Leaves(JsonObject node, bool whereSafe, List<(JsonObject, string, bool)> leaves)
    {
        switch ((string?)node["type"])
        {
            case "BASE_TABLE":
                string alias = (string?)node["alias"] ?? "";
                leaves.Add((node, alias.Length > 0 ? alias : (string?)node["table_name"] ?? "", whereSafe));
                return;

            case "JOIN" when node["left"] is JsonObject left && node["right"] is JsonObject right:
                var (leftSafe, rightSafe) = SidesSafe(node);
                Leaves(left, whereSafe && leftSafe, leaves);
                Leaves(right, whereSafe && rightSafe, leaves);
                return;

            default:
                // A subquery, a table function, VALUES: none of DaffyDB's, but its columns could be
                // what an unqualified name means, so it is counted.
                leaves.Add((node, (string?)node["alias"] ?? "", false));
                return;
        }
    }

    /// <summary>Whether each side of a join keeps the rows a filter on it leaves, and only those.</summary>
    private static (bool Left, bool Right) SidesSafe(JsonObject join)
    {
        string refType = (string?)join["ref_type"] ?? "";
        if (refType is not ("REGULAR" or "CROSS")) return (false, false);

        return ((string?)join["join_type"]) switch
        {
            "INNER" => (true, true),
            "LEFT" => (true, false),
            "RIGHT" => (false, true),
            _ => (false, false),
        };
    }

    /// <summary>
    /// The ON conditions that may be sent: an inner join's to a table on either side, a LEFT
    /// join's to one on its right, a RIGHT join's to one on its left - each only as far down as
    /// no outer join below can NULL-extend it. A join with USING or NATURAL keeps its own.
    /// </summary>
    private void JoinConditions(JsonObject node, List<PushTable> tables, int leafCount, List<Constant> constants)
    {
        if ((string?)node["type"] != "JOIN" || node["left"] is not JsonObject left || node["right"] is not JsonObject right) return;

        JoinConditions(left, tables, leafCount, constants);
        JoinConditions(right, tables, leafCount, constants);

        if ((string?)node["ref_type"] != "REGULAR" || node["using_columns"] is JsonArray { Count: > 0 }) return;

        var targets = new List<(JsonObject, string, bool)>();
        string joinType = (string?)node["join_type"] ?? "";
        if (joinType is "INNER" or "RIGHT") Leaves(left, true, targets);
        if (joinType is "INNER" or "LEFT") Leaves(right, true, targets);
        var reachable = targets.Where(t => t.Item3).Select(t => t.Item1).ToHashSet();

        foreach (var condition in Conjuncts(node["condition"]))
            if (OnlyTable(condition, leafCount, tables) is { Unsafe: false } table && reachable.Contains(table.Node) &&
                Build(condition, table, constants) is { } built)
                table.Conditions.Add(built);
    }

    /// <summary>
    /// The mounted SQL Server table a base table names, if it names one: in daffydb - named so, or
    /// current - and in dbo when no schema is given, as DaffyDB's search path has it. A name that
    /// could be a database's as well as a schema's is left alone, and so is one sampled or timed,
    /// and one given column names of the query's own - AS x(a, b) - which its conditions use.
    /// </summary>
    private (string Catalog, MountedObject Mounted)? Resolve(JsonObject node,
        Dictionary<(string, string), (string Catalog, MountedObject Mounted)> mounted)
    {
        if ((string?)node["type"] != "BASE_TABLE" || node["sample"] is not null || node["at_clause"] is not null) return null;
        if (node["column_name_alias"] is JsonArray { Count: > 0 }) return null;

        string catalog = (string?)node["catalog_name"] ?? "";
        string schema = (string?)node["schema_name"] ?? "";
        string name = (string?)node["table_name"] ?? "";

        if (!(catalog.Length == 0 ? _current : catalog).Equals(Databases.Data, StringComparison.OrdinalIgnoreCase)) return null;
        if (catalog.Length == 0 && Databases.All.Any(d => d.Name.Equals(schema, StringComparison.OrdinalIgnoreCase))) return null;

        return mounted.TryGetValue((schema.Length == 0 ? "dbo" : schema, name), out var found) ? found : null;
    }

    /// <summary>How each column's conditions may be sent, from its SQL Server type and collation and the DuckDB type it arrives as now.</summary>
    private Dictionary<string, PushKind> ColumnKinds(MountedObject mounted)
    {
        var kinds = new Dictionary<string, PushKind>(StringComparer.OrdinalIgnoreCase);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT column_name, data_type FROM duckdb_columns() " +
                              $"WHERE database_name = {Literal(Databases.Data)} AND schema_name = {Literal(mounted.Schema)} " +
                              $"AND table_name = {Literal(mounted.Name)}";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(0);
            if (mounted.Columns!.TryGetValue(name, out var column)) kinds[name] = KindOf(column, reader.GetString(1));
        }
        return kinds;
    }

    /// <summary>
    /// How SQL Server compares a column, if DuckDB compares it the same way or more narrowly. The
    /// DuckDB type is checked too: a table changed on SQL Server since it was mounted arrives as
    /// something else, and is then left alone.
    /// </summary>
    private static PushKind KindOf(SourceColumn column, string duckType)
    {
        string duck = duckType.ToUpperInvariant();
        return column.Type.ToLowerInvariant() switch
        {
            "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney"
                when duck is "UTINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" || duck.StartsWith("DECIMAL", StringComparison.Ordinal)
                => PushKind.Exact,
            "bit" when duck == "BOOLEAN" => PushKind.Bit,
            "date" when duck == "DATE" => PushKind.Date,
            "datetime2" or "smalldatetime" when duck == "TIMESTAMP" => PushKind.Stamp,
            "datetime" when duck == "TIMESTAMP" => PushKind.Datetime,
            "uniqueidentifier" when duck == "UUID" => PushKind.Guid,
            "char" or "varchar" or "nchar" or "nvarchar"
                when duck == "VARCHAR" && column.Collation is { } collation &&
                     collation.Contains("_CI", StringComparison.OrdinalIgnoreCase) &&
                     !collation.Contains("_BIN", StringComparison.OrdinalIgnoreCase)
                => PushKind.Text,
            _ => PushKind.None,
        };
    }

    /// <summary>
    /// A table that is named with three parts anywhere in the query - ms.orders.id - and has no
    /// alias: once a table function stands in its place under the name orders, that would not bind.
    /// </summary>
    private static void MarkThreePartNames(JsonNode select, List<PushTable> tables)
    {
        foreach (var reference in Descendants(select).Where(n => (string?)n["class"] == "COLUMN_REF"))
        {
            if (reference["column_names"] is not JsonArray { Count: >= 3 } names) continue;
            string table = (string?)names[^2] ?? "";
            foreach (var match in tables.Where(t => t.Binding.Equals(table, StringComparison.OrdinalIgnoreCase)))
                match.Unsafe = true;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Conditions

    /// <summary>A condition's parts, split on its top-level ANDs.</summary>
    private static IEnumerable<JsonObject> Conjuncts(JsonNode? condition)
    {
        if (condition is not JsonObject node) yield break;

        if ((string?)node["type"] == "CONJUNCTION_AND" && node["children"] is JsonArray children)
        {
            foreach (var child in children)
                foreach (var part in Conjuncts(child))
                    yield return part;
            yield break;
        }

        yield return node;
    }

    /// <summary>
    /// The one mounted table every column in a condition belongs to, or null: when it names
    /// another table, or none, or has a subquery. An unqualified column is a table's only when it
    /// is the FROM clause's only table.
    /// </summary>
    private static PushTable? OnlyTable(JsonObject condition, int leafCount, List<PushTable> tables)
    {
        PushTable? only = null;
        foreach (var node in Descendants(condition))
        {
            string? kind = (string?)node["class"];
            if (kind is "SUBQUERY" or "PARAMETER" or "STAR" or "LAMBDA" or "WINDOW") return null;
            if (kind != "COLUMN_REF") continue;

            if (node["column_names"] is not JsonArray names) return null;
            PushTable? table = names.Count switch
            {
                1 when leafCount == 1 => tables.SingleOrDefault(),
                2 => tables.FirstOrDefault(t => t.Binding.Equals((string?)names[0], StringComparison.OrdinalIgnoreCase)),
                _ => null,
            };

            if (table is null || (only is not null && only != table)) return null;
            only = table;
        }
        return only;
    }

    /// <summary>A condition as it can be written for SQL Server, or null when it cannot be, or should not.</summary>
    private static Cond? Build(JsonObject node, PushTable table, List<Constant> constants)
    {
        string type = (string?)node["type"] ?? "";
        switch ((string?)node["class"])
        {
            case "CONJUNCTION" when node["children"] is JsonArray children && type is "CONJUNCTION_AND" or "CONJUNCTION_OR":
                var parts = children.Select(c => c is JsonObject o ? Build(o, table, constants) : null).ToArray();
                return parts.Any(p => p is null) ? null : new Junction(type == "CONJUNCTION_AND", parts!);

            case "COMPARISON":
                if (Operator(type) is not { } op) return null;
                var (column, constant, flipped) =
                    Column(node["left"], table) is { } l ? (l, node["right"], false) : (Column(node["right"], table), node["left"], true);
                if (column is null) return null;
                if (flipped) op = op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op };

                var kind = table.Kinds[column];
                bool allowed = kind switch
                {
                    PushKind.Exact or PushKind.Date or PushKind.Stamp => true,
                    PushKind.Datetime => op != "<>",
                    PushKind.Bit => op is "=" or "<>",
                    PushKind.Text or PushKind.Guid => op == "=",
                    _ => false,
                };
                return allowed && Register(constant, kind, constants) is { } value ? new Compare(column, kind, op, value) : null;

            case "BETWEEN":
                if (Column(node["input"], table) is not { } input) return null;
                var range = table.Kinds[input];
                if (range is not (PushKind.Exact or PushKind.Date or PushKind.Stamp or PushKind.Datetime)) return null;
                return Register(node["lower"], range, constants) is { } low && Register(node["upper"], range, constants) is { } high
                    ? new Between(input, range, low, high)
                    : null;

            case "OPERATOR" when node["children"] is JsonArray children:
                if (type is "OPERATOR_IS_NULL" or "OPERATOR_IS_NOT_NULL")
                    return children.Count == 1 && Column(children[0], table) is { } tested ? new IsNull(tested, type == "OPERATOR_IS_NOT_NULL") : null;

                if (type is not ("COMPARE_IN" or "COMPARE_NOT_IN") || children.Count < 2 || Column(children[0], table) is not { } listed)
                    return null;
                bool not = type == "COMPARE_NOT_IN";
                var member = table.Kinds[listed];
                bool fits = member switch
                {
                    PushKind.Exact or PushKind.Date or PushKind.Stamp => true,
                    PushKind.Datetime or PushKind.Bit or PushKind.Text or PushKind.Guid => !not,
                    _ => false,
                };
                if (!fits) return null;
                var items = children.Skip(1).Select(c => Register(c, member, constants)).ToArray();
                return items.Any(i => i is null) ? null : new InList(listed, member, not, items.Select(i => i!.Value).ToArray());

            default:
                return null;
        }
    }

    private static string? Operator(string type) => type switch
    {
        "COMPARE_EQUAL" => "=",
        "COMPARE_NOTEQUAL" => "<>",
        "COMPARE_LESSTHAN" => "<",
        "COMPARE_GREATERTHAN" => ">",
        "COMPARE_LESSTHANOREQUALTO" => "<=",
        "COMPARE_GREATERTHANOREQUALTO" => ">=",
        _ => null,
    };

    /// <summary>The column of the table a node names, as SQL Server spells it, or null.</summary>
    private static string? Column(JsonNode? node, PushTable table)
    {
        if ((string?)node?["class"] != "COLUMN_REF" || node["column_names"] is not JsonArray names || names.Count is 0 or > 2) return null;
        string name = (string?)names[^1] ?? "";
        return table.Kinds.ContainsKey(name) && table.Mounted.Columns!.TryGetValue(name, out var column) ? column.Name : null;
    }

    /// <summary>
    /// A constant to evaluate, or null when the node is not one: a literal, or a cast of one. A
    /// string compared with a date, a timestamp or a uuid is cast to it, as DuckDB casts it.
    /// </summary>
    private static int? Register(JsonNode? node, PushKind kind, List<Constant> constants)
    {
        if (node is null) return null;

        string? literalType = (string?)node["class"] switch
        {
            "CONSTANT" when node["value"]?["is_null"]?.GetValue<bool>() == false => (string?)node["value"]?["type"]?["id"],
            "CAST" when node["try_cast"]?.GetValue<bool>() == false && (string?)node["child"]?["class"] == "CONSTANT" &&
                        node["child"]?["value"]?["is_null"]?.GetValue<bool>() == false
                => (string?)node["cast_type"]?["id"],
            _ => null,
        };
        if (literalType is null) return null;

        string? castTo = literalType == "VARCHAR"
            ? kind switch { PushKind.Date => "DATE", PushKind.Stamp or PushKind.Datetime => "TIMESTAMP", PushKind.Guid => "UUID", _ => null }
            : null;

        constants.Add(new Constant(node.DeepClone(), castTo));
        return constants.Count - 1;
    }

    // ---------------------------------------------------------------------------------------
    // Constants, evaluated by DuckDB and written for SQL Server

    /// <summary>
    /// Every constant's value, as DuckDB has it - with the casts it would make against the column -
    /// in one query of DuckDB's own writing, so that a literal means here exactly what it means in
    /// the query. A cast that fails is NULL, and the condition it is in is not sent.
    /// </summary>
    private (object? Value, string Type)[] Evaluated(List<Constant> constants)
    {
        if (constants.Count == 0) return [];

        var statement = JsonNode.Parse(Serialized("SELECT 1 AS k0"))!.AsObject();
        var list = new JsonArray();
        for (int i = 0; i < constants.Count; i++)
        {
            var (node, castTo) = constants[i];
            JsonObject item = castTo is null
                ? node.DeepClone().AsObject()
                : new JsonObject
                {
                    ["class"] = "CAST", ["type"] = "OPERATOR_CAST", ["alias"] = "", ["query_location"] = 0,
                    ["child"] = node.DeepClone(),
                    ["cast_type"] = new JsonObject { ["id"] = castTo, ["type_info"] = null },
                    ["try_cast"] = true,
                };
            item["alias"] = $"k{i}";
            list.Add(item);
        }
        statement["statements"]![0]!["node"]!["select_list"] = list;

        using var command = _connection.CreateCommand();
        command.CommandText = Deserialized(statement.ToJsonString());
        using var reader = command.ExecuteReader();
        reader.Read();

        var values = new (object?, string)[constants.Count];
        for (int i = 0; i < values.Length; i++)
        {
            string type = reader.GetDataTypeName(i);
            try { values[i] = (reader.IsDBNull(i) ? null : reader.GetValue(i), type); }
            catch (Exception ex) when (ex is OverflowException or InvalidCastException or ArgumentOutOfRangeException) { values[i] = (null, type); }
        }
        return values;
    }

    /// <summary>A condition in T-SQL, or null when a constant in it cannot be written for its column.</summary>
    private static string? Written(Cond condition, (object? Value, string Type)[] values)
    {
        switch (condition)
        {
            case Junction junction:
                var parts = junction.Parts.Select(p => Written(p, values)).ToArray();
                return parts.Any(p => p is null) ? null : "(" + string.Join(junction.And ? " AND " : " OR ", parts) + ")";

            case IsNull test:
                return $"{Bracket(test.Column)} IS {(test.Not ? "NOT " : "")}NULL";

            case Compare { Kind: PushKind.Datetime } compare:
                if (Instant(values[compare.Value]) is not { } at) return null;
                return compare.Op switch
                {
                    "=" => Widened(compare.Column, at, at),
                    ">" or ">=" => Stamp(at - DatetimeSlack) is { } low ? $"{Bracket(compare.Column)} >= {low}" : null,
                    _ => Stamp(at + DatetimeSlack) is { } high ? $"{Bracket(compare.Column)} <= {high}" : null,
                };

            case Compare compare:
                return Literal(values[compare.Value], compare.Kind) is { } value ? $"{Bracket(compare.Column)} {compare.Op} {value}" : null;

            case Between { Kind: PushKind.Datetime } between:
                return Instant(values[between.Low]) is { } from && Instant(values[between.High]) is { } to
                    ? Widened(between.Column, from, to)
                    : null;

            case Between between:
                return Literal(values[between.Low], between.Kind) is { } lo && Literal(values[between.High], between.Kind) is { } hi
                    ? $"{Bracket(between.Column)} BETWEEN {lo} AND {hi}"
                    : null;

            case InList { Kind: PushKind.Datetime } list:
                var ranges = list.Values.Select(v => Instant(values[v]) is { } at ? Widened(list.Column, at, at) : null).ToArray();
                return ranges.Any(r => r is null) ? null : "(" + string.Join(" OR ", ranges) + ")";

            case InList list:
                var items = list.Values.Select(v => Literal(values[v], list.Kind)).ToArray();
                return items.Any(i => i is null)
                    ? null
                    : $"{Bracket(list.Column)} {(list.Not ? "NOT IN" : "IN")} ({string.Join(", ", items)})";

            default:
                return null;
        }
    }

    /// <summary>A datetime column from one instant to another, each 4 ms further out.</summary>
    private static string? Widened(string column, DateTime from, DateTime to) =>
        Stamp(from - DatetimeSlack) is { } low && Stamp(to + DatetimeSlack) is { } high
            ? $"{Bracket(column)} BETWEEN {low} AND {high}"
            : null;

    /// <summary>A constant as a T-SQL literal SQL Server reads exactly as DuckDB has it, for a column of this kind; null when it cannot be.</summary>
    private static string? Literal((object? Value, string Type) constant, PushKind kind)
    {
        var (value, type) = constant;
        if (value is null) return null;

        switch (kind)
        {
            case PushKind.Exact:
                return value switch
                {
                    sbyte or byte or short or ushort or int or uint or long or ulong or BigInteger => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
                    decimal d => d.ToString(CultureInfo.InvariantCulture),
                    _ => null,
                };

            case PushKind.Bit:
                return value switch
                {
                    bool b => b ? "1" : "0",
                    sbyte or byte or short or int or long when System.Convert.ToInt64(value, CultureInfo.InvariantCulture) is 0 or 1 =>
                        System.Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                    _ => null,
                };

            case PushKind.Date when type.Equals("Date", StringComparison.OrdinalIgnoreCase):
                return value switch
                {
                    DateOnly day => $"CAST('{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}' AS date)",
                    DateTime day => $"CAST('{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}' AS date)",
                    _ => null,
                };

            case PushKind.Date or PushKind.Stamp:
                return Instant(constant) is { } at ? Stamp(at) : null;

            case PushKind.Text:
                return value is string text && text.All(c => c is >= ' ' and <= '~') ? "'" + text.Replace("'", "''") + "'" : null;

            case PushKind.Guid:
                return value is Guid guid ? $"CAST('{guid:D}' AS uniqueidentifier)" : null;

            default:
                return null;
        }
    }

    /// <summary>A date or timestamp constant as an instant.</summary>
    private static DateTime? Instant((object? Value, string Type) constant) => constant.Value switch
    {
        DateTime at => at,
        DateOnly day => day.ToDateTime(TimeOnly.MinValue),
        _ => null,
    };

    /// <summary>An instant as datetime2(7), which any SQL Server date and time type compares against exactly; null outside its range.</summary>
    private static string? Stamp(DateTime at) =>
        at.Year is < 1 or > 9999
            ? null
            : $"CAST('{at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}' AS datetime2(7))";

    // ---------------------------------------------------------------------------------------
    // The parse tree, through DuckDB

    /// <summary>
    /// daffy_scan in a table's place, under the name the query knows it by, with the filter. Made
    /// from DuckDB's own parse of such a call, so that it is shaped as DuckDB's parser shapes one.
    /// </summary>
    private JsonObject ScanNode(PushTable table, string filter)
    {
        _scanTemplate ??= JsonNode.Parse(Serialized("SELECT * FROM daffy_scan('c', 't', filter := 'f') AS a"))!
            ["statements"]![0]!["node"]!["from_table"]!.AsObject();

        var node = _scanTemplate.DeepClone().AsObject();
        var arguments = node["function"]!["children"]!.AsArray();
        arguments[0]!["value"]!["value"] = table.Catalog;
        arguments[1]!["value"]!["value"] = $"{Bracket(table.Mounted.SourceSchema!)}.{Bracket(table.Mounted.SourceTable!)}";
        arguments[2]!["value"]!["value"] = filter;
        node["alias"] = table.Binding;
        node["column_name_alias"] = table.Node["column_name_alias"]?.DeepClone() ?? new JsonArray();
        node["query_location"] = table.Node["query_location"]?.DeepClone();
        return node;
    }

    // Both take their argument as a literal: json_serialize_sql refuses a parameter.
    private string Serialized(string sql) => Scalar($"SELECT json_serialize_sql({Literal(sql)})");

    private string Deserialized(string json) => Scalar($"SELECT json_deserialize_sql({Literal(json)}::JSON)");

    /// <summary>Every object in a part of the parse tree - expressions, table references - the part itself included.</summary>
    private static IEnumerable<JsonObject> Descendants(JsonNode node)
    {
        if (node is JsonObject self) yield return self;
        IEnumerable<JsonNode?> children = node switch
        {
            JsonObject o => o.Select(p => p.Value),
            JsonArray a => a,
            _ => [],
        };
        foreach (var child in children)
            if (child is not null)
                foreach (var descendant in Descendants(child))
                    yield return descendant;
    }
}
