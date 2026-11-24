# DaffySQL

SQL Server from DuckDB, in .NET: T-SQL sent over TDS by DaffySQL's own client (lifted from
DaffyDuo), and the answer streamed into DuckDB as a table. This is DaffyDB's copy, which
`daffydb.sqlserver` mounts through; DaffySQL began as a project of its own, which keeps its
probes, its comparison with DuckDB's community mssql extension, and the notes for bringing it here.

```sql
SELECT daffy_connect('sales', 'Server=tcp:sql01;Database=Sales;User Id=svc;Password=...');

SELECT c.Name, t.Region
FROM daffy_scan('sales', 'dbo.Customers') c
JOIN daffy_query('reports', 'SELECT Id, Region FROM dbo.Territories') t USING (Id);
```

No ODBC, no SqlClient, no DuckDB extension: the functions are registered in-process through
DuckDB's C API, into the DuckDB that DuckDB.NET loaded (`src/DaffySQL/DuckDb/DuckApi.cs`).

## Functions

| Function | What it does |
|---|---|
| `daffy_connect(name, connection_string)` | Names a SQL Server, after logging in once to prove the connection string. Returns a description (never the password). |
| `daffy_query(name, tsql)` | Runs T-SQL and returns its first result set, streamed 2048 rows at a time. |
| `daffy_scan(name, table [, filter := 'T-SQL'])` | Reads a table or view, asking the server for only the columns the DuckDB query uses, and only the rows the filter keeps. |
| `daffy_exec(name, tsql)` | Runs T-SQL for its effects. One row: `rows_affected`, and `messages` (PRINT output). Use with `CALL` or in a FROM. |
| `daffy_tables(name)` | The tables and views: schema, name, type, approximate row count. |
| `daffy_columns(name, table)` | A table's columns: SQL Server type, the DuckDB type it arrives as, nullable. |
| `daffy_connections()` | The named servers, their security, and their sessions open and idle. |
| `daffy_disconnect(name)` | Forgets a name and closes its idle sessions. |

From C#:

| Call | What it does |
|---|---|
| `DaffySql.Register(connection [, functions])` | Adds the functions to a DuckDB database: all of them, or those `DaffyFunctions` names. |
| `DaffySql.Connect(name, connectionString)` | Names a server without its password passing through SQL. |
| `DaffySql.Disconnect(name)` | Forgets a name and closes its idle sessions. |
| `DaffySql.Cancel(connection)` | Cancels the SQL Server queries running for a DuckDB connection, from any thread (below). |
| `DaffySql.Address(connectionString)` | Where a connection string points - host, port, user, database - without connecting or the password. |
| `DaffySql.Diagnostics` | Receives DaffySQL's log lines: logins, redirects, sessions closed. Never a password. |

A host that names its servers from code, and lets people it does not trust write the SQL, should
register only what reads: `daffy_connect` would let any of them point a name the host made at a
server of their own, and `daffy_disconnect` break it. DaffyDB registers `DaffyFunctions.Scan |
Tables | Columns`.

Connection strings are SQL Server's: `Server`, `Database`, `User Id`, `Password`,
`Encrypt=Mandatory|Optional|Strict`, `TrustServerCertificate`, `HostNameInCertificate`,
`Connect Timeout`, `Application Name`, `Packet Size`, `Lock Timeout`, and
`Authentication=ActiveDirectoryServicePrincipal` for an Entra ID application. An unknown keyword is
refused by name.

`Lock Timeout` is DaffySQL's own: how many seconds the queries DaffySQL writes itself -
`daffy_scan`'s and `daffy_tables`' - wait for a lock before SQL Server gives up on them with Msg
1222. 30 unless given, 0 for no limit. It is sent as `SET LOCK_TIMEOUT` before each of them, because
a session's `SET` options go when it is reset for reuse. `daffy_query` and `daffy_exec` run the
caller's T-SQL as written, with nothing before it, since `CREATE VIEW` and `CREATE PROCEDURE` must
open their batch; T-SQL that should give up sooner can say `SET LOCK_TIMEOUT` itself.

## How they behave

- **daffy_query runs its T-SQL when DuckDB binds the query**, because that is when DuckDB must know
  the columns. So `DESCRIBE` runs it too (and cancels it), and a prepared statement executed twice
  runs it twice. It is for reading. Only the first result set is DuckDB's; anything after it is
  read past, and its statements still run.
- **daffy_scan describes the table at bind** with `SELECT TOP (0) *`, then sends a SELECT naming only
  the columns used: `SELECT Name FROM daffy_scan('sales', 'dbo.Customers')` sends
  `SELECT [Name] FROM [dbo].[Customers]`. `count(*)` reads one column. Table names are parsed and
  re-quoted, never pasted into T-SQL. DuckDB's C API has no filter pushdown, so a WHERE in the
  DuckDB query is applied after the rows arrive; `filter` is how to send one (below).
- **daffy_exec runs when the scan starts**, not at bind, so `DESCRIBE` and an unexecuted `PREPARE`
  never run it. A batch that fails part-way keeps what its earlier statements did, as T-SQL does.
- **Stopping early cancels on the server.** LIMIT, an error elsewhere in the DuckDB query, a closed
  reader: the server gets an attention, and the session goes back to the pool once it acknowledges.
- **Sessions are pooled per name** (8 idle at most) and reset on reuse with RESETCONNECTION, so temp
  tables and SET options never leak from one query to the next. A session that may hold an open
  transaction - one began and was not ended, a request was cancelled, part of an answer was read past
  unparsed - is reset before it goes back to the pool, not when it is next used, so its locks go at
  once. One that cannot be reset is closed, and the server rolls back for it.
- **Errors are SQL Server's**: `sales: Msg 208, Level 16, State 1, Line 1: Invalid object name 'dbo.Nope'.`

## Filters

`filter` gives daffy_scan a T-SQL condition, sent as the SELECT's WHERE, so only the rows it keeps
cross the network:

```sql
SELECT o.Id, t.Region
FROM daffy_scan('sales', 'dbo.Orders', filter := '[Total] > 100 AND [Status] = ''open''') o
JOIN 'territories.parquet' t ON t.Id = o.TerritoryId;
-- sends: SELECT [Id], [TerritoryId] FROM [dbo].[Orders] WHERE ([Total] > 100 AND [Status] = 'open')
```

- **It is T-SQL, passed on as written**, inside parentheses. Its columns need not be ones the DuckDB
  query uses. Building it is the caller's job, as daffy_query's T-SQL is.
- **It is one condition.** A filter that closes the parenthesis it is sent in, ends the statement
  with `;`, or comments the rest away with `--` or `/*` is refused, wherever it does so outside a
  `'string'`, a `[name]` or a `"name"`: inside the parentheses T-SQL has expressions and nothing
  else, so no second statement can start there. A condition can still hold a subquery of any table
  the login can read; the login is what limits that.
- **It fails at bind.** The bind's `SELECT TOP (0)` carries it, so a misnamed column (`Msg 207`) or a
  syntax error (`Msg 102`) is reported before a row is read, by `DESCRIBE` too.
- **It is compared by SQL Server's rules**, and a WHERE in the DuckDB query by DuckDB's. Under a
  case-insensitive collation, `filter := '[Name] = ''ada lovelace'''` finds Ada Lovelace, and SQL
  Server's `=` ignores trailing spaces; DuckDB's `=` does neither. To get DuckDB's answer with SQL
  Server doing the cutting, put the condition in both: the WHERE then re-checks what the filter
  let through. Only push what SQL Server keeps at least as much of: equality and IN on text,
  comparisons of numbers and dates - not a range on text, which a collation sorts differently.
- **NULL or blank** is no filter.

## Cancelling

DuckDB's interrupt (`DuckDBCommand.Cancel()`, what DaffyDB calls when a client cancels) only takes
effect between chunks, so it cannot stop a query SQL Server has not answered yet, or a read blocked
on a lock. `DaffySql.Cancel(connection)` can: every query DaffySQL is running for that DuckDB
connection is sent an attention, whichever thread is waiting on it wakes and fails with
`the query was cancelled`, and its session goes back to the pool as soon as the server acknowledges.
Call both. Call `DaffySql.Cancel` too after closing a streaming reader early: otherwise DuckDB keeps
the query - and the session under it - until the connection's next statement.

## Types

| SQL Server | DuckDB |
|---|---|
| tinyint | UTINYINT |
| smallint, int, bigint, bit, real, float | SMALLINT, INTEGER, BIGINT, BOOLEAN, FLOAT, DOUBLE |
| decimal(p,s), numeric(p,s) | DECIMAL(p,s) |
| money, smallmoney | DECIMAL(19,4), DECIMAL(10,4) |
| date, time | DATE, TIME |
| datetime2, datetime, smalldatetime | TIMESTAMP |
| datetimeoffset | TIMESTAMP WITH TIME ZONE (the instant; not the offset it was written in) |
| uniqueidentifier | UUID |
| char, varchar, nchar, nvarchar, text, ntext, xml, sql_variant | VARCHAR |
| binary, varbinary, image, rowversion, geography, geometry, hierarchyid | BLOB |

DuckDB's TIME and TIMESTAMP are microseconds, so `time(7)` and `datetime2(7)` lose their 100 ns
digit. `TIMESTAMP_NS` would keep it but only spans 1677-2262, which `0001-01-01` and `9999-12-31`
do not fit in. varchar is decoded from its collation's code page, or taken as UTF-8 for a `_UTF8`
collation (DaffySQL asks for UTF8SUPPORT at login).

## Layout

```
src/DaffySQL/
  Tds/        wire format, from DaffyDuo: framing, PRELOGIN, the 7.4 TLS shim, LOGIN7, login
              response, COLMETADATA; plus the pull reader for responses and the request builder
  Client/     connection strings, the connector (TLS, redirects, Entra ID), sessions, the pool,
              one request's life (ServerQuery: reading, cancelling, resetting), table names
  DuckDb/     DuckDB's C API, the type mapping, rows into vectors, the functions
src/DaffySQL.Tests/         redirects and Entra ID against fake servers (from DaffyDuo), the response
                            reader across packet boundaries, table-name parsing, Lock Timeout
```

## Testing it

```sh
dotnet test src/DaffySQL.Tests              # the client against fake servers; no SQL Server needed
pwsh scripts/verify-sqlserver.ps1           # daffydb.sqlserver against a real one
```

`verify-sqlserver.ps1` is DaffyDB's: it mounts a database it makes on a SQL Server it may write to
- by default DaffyDuo's docker test server on `127.0.0.1,14341` - and checks what a client of
DaffyDB sees, cancels and lock timeouts included. Its header says what else it needs.

When DaffyDB moves to a new DuckDB.NET, DaffySQL moves with it, in the same commit:
`DuckApi.Load` looks every C API function up by name, and fails at startup naming any that are
missing, so a break is loud rather than subtle.

## Azure SQL

`Authentication=ActiveDirectoryServicePrincipal` logs in as an Entra ID application: `User Id` is
its client id (`id@tenant` when the server does not name its tenant, which Azure SQL always does),
`Password` one of its client secrets. DaffySQL asks Entra ID for a token - once, shared by every
login until five minutes before it expires - and presents that; the secret never reaches SQL Server,
and is only sent to a Microsoft sign-in address the server names, for an Azure SQL resource. Under
the Redirect connection policy the gateway sends the login on to the node the database is on, and
DaffySQL follows, once, accepting that node's certificate.

## Not yet

- A `Command Timeout`: the keyword is accepted and ignored; a query may take as long as it takes,
  and `DaffySql.Cancel` is how to stop one.
- Named instances (SQL Browser), Windows and Kerberos logins, and the other Entra ID flows
  (managed identity, interactive).
