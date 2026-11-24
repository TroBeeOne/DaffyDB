# DaffyDB

Query Parquet, CSV, JSON and Excel files with any SQL Server client.

DaffyDB listens on port 1433 and speaks the Tabular Data Stream wire protocol, so anything that
already knows how to connect to SQL Server can read a folder of data files without an import step,
an ETL job, or a database server. Execution is [DuckDB](https://duckdb.org).

```
dotnet tool install -g DaffyDB
```

Already installed? Upgrade to the latest version with the command below. Stop any running
`daffydb` first: on Windows a running copy holds its files open, and the update fails.

```
dotnet tool update -g DaffyDB
```

Coming from 0.4 or earlier: your tables now live in a database named `daffydb` instead of in
`master`, which holds only the system catalog, as on SQL Server. A client that names no database
lands in `daffydb`, so most need no change. A connection string or query that names `master`
should name `daffydb` instead. The folder scan changed too: it reads JSON now, but stops two
folders down and passes over build and tooling folders, so a file deeper than that needs an
`--init` script. "Where a table comes from", below, has the details.

Coming from 0.6: `@@VERSION`, `xp_msver` and `sp_server_info` now give DaffyDB's name where they
gave SQL Server's. The version numbers beside it are unchanged, so clients that read those see no
difference.

Coming from 0.7: a folder of part files, such as `part-00000.parquet` and on or DuckDB's
`data_0.parquet` and on, is now one table named after the folder, where it was a table per part.
And a `--data-dir` typed with a backslash on the end, as tab completion leaves it, no longer puts
the top-level tables in a schema named after the folder.

Coming from 0.9: T-SQL gives SQL Server's answers in more places, and a few answers change
because they were wrong. `DATEPART(weekday)` counts Sunday as 1, weeks start on Sunday in
`DATEPART(week)` and `DATEDIFF(week)`, `CONVERT` honours its style codes, `varchar(n)` cuts text to
n characters, and `LIKE` is case-insensitive and understands `[a-z]` classes. A cancelled query now
stops on the server instead of running on. SSMS 22 connects; earlier versions fail its connect
query with "Object cannot be cast from DBNull to other types".

Coming from 0.8: nothing you rely on changes. `EXEC daffydb.status` and `EXEC daffydb.tables` are
new, and so, with `--allow-admin`, are commands that mount more folders, files and databases while
the server runs. The log, `--trace` and error messages now mask passwords.

Coming from 1.0.0: nothing you rely on changes. The binaries are now signed by TroBeeONE LLC:
`daffydb.exe`, `daffydb.dll` and the .NET libraries that ship beside them carry its Authenticode
signature, and DuckDB's `duckdb.dll` keeps the DuckDB Foundation's own. The Digital Signatures tab
in a file's Properties shows the signer; the files are under `%USERPROFILE%\.dotnet\tools\.store\daffydb`.

Coming from 1.0.1: nothing you rely on changes. SSMS releases before 22 connect now. While
connecting they ask whether the connection is a dedicated admin connection, by reading
`sys.endpoints`, and stopped with "Table with name endpoints does not exist!" when DaffyDB had no
such view.

Coming from 1.0.2: SQL Server can read DaffyDB as a linked server, through ODBC; "Which clients"
shows how. One answer changes to make that work. A text column has no length of its own, and it
is now `nvarchar(4000)` everywhere DaffyDB describes it - `sys.columns`, `INFORMATION_SCHEMA`,
ODBC and OLE DB - and in every result, where it was `nvarchar(max)` in the catalog and only as
wide as its widest value in a result. A value longer than 4000 characters still arrives whole, as
`nvarchar(max)`. Prepared statements work now, `SqlCommand.Prepare()` among them, and so does
`SET ROWCOUNT`. And a `SELECT` straight after a `SET ... ON`, such as `SET NOCOUNT ON`, runs: it
used to be read as part of the `SET`, and returned nothing.

Coming from 1.1.0: nothing you rely on changes. With `--allow-admin`, two new commands write tables
out as parquet files: `daffydb.save_parquet` saves one, temp tables included, and
`daffydb.save_parquet_all` saves every one. "Saving tables as parquet" shows how.

Coming from 1.2.0: one answer changes. `OBJECT_ID('sales.orders')` is `sales.orders` now, and a
name without a schema is `dbo`'s, as on SQL Server; it used to match the name in any schema. With
`--allow-admin`, `daffydb.sqlserver` mounts a SQL Server or Azure SQL database's tables, read live,
and sends SQL Server the conditions a query puts on them. "SQL Server" shows how.

Coming from 1.3.0: VS Code's SQL Server extension lists your tables in its Object Explorer; it
showed nothing under a database before. A few catalog answers change for it: `DATABASEPROPERTYEX`
answers NULL for a property it has nothing for, as SQL Server does, where it answered an empty
string, and `sys.configurations` holds five more settings, the ones SMO reads with a database's
properties. And one answer changes because it was wrong: `CONVERT` with a two-digit-year style, 1
to 12, reads 50 to 68 as 1950 to 1968, as SQL Server does, where it read them as 2050 to 2068.

Coming from 1.3.1: nothing you rely on changes. With `--allow-admin`, `daffydb.run_sqlserver` runs
T-SQL on a mounted SQL Server, a stored procedure among them, and returns what it returns or keeps it
in a temp table; "SQL Server" shows how. "Another DuckDB, over Quack" shows how an `--init` script can reach another
DuckDB. And a password written `PASSWORD = N'...'` is masked in the log now.

## Use it

Stand in a folder that has data files in it and run:

```
daffydb
```

Every `.parquet`, `.csv`, `.json` and `.xlsx` file underneath becomes a table, and you get a prompt for trying
T-SQL without reaching for another client:

```
daffydb> :tables

  schema  name       type   cols  sample
  ------  ---------  -----  ----  --------------------------------------
  dbo     customers  table     5  SELECT TOP 5 * FROM [dbo].[customers];
  dbo     orders     table     6  SELECT TOP 5 * FROM [dbo].[orders];

  2 row(s)

daffydb> SELECT TOP 3 name, city FROM customers ORDER BY name;

  name            city
  --------------  ----------
  Ada Lovelace    London
  Alan Turing     Manchester
  Barbara Liskov  Boston
  3 row(s)
```

`:help` lists the commands, `:quit` stops the server. Clients stay connected and keep working
while you use it. Long results are paged rather than dumped; `[space]` for the next page, `[q]`
to stop. On Windows, `:pbi` opens Power BI Desktop already connected; see "Which clients" below.

Meanwhile connect from anywhere else. Encryption needs no setup: `Encrypt=Mandatory` is what
most clients send by default, and it works against the self-signed certificate DaffyDB generates
for itself on first run.

**`sqlcmd`:**

```
sqlcmd -S tcp:127.0.0.1,1433 -U any -P any -C -Q "SELECT TOP 5 * FROM customers"
```

**ADO.NET:**

```csharp
using Microsoft.Data.SqlClient;

const string cs = "Server=tcp:127.0.0.1,1433;Encrypt=Mandatory;TrustServerCertificate=True;"
                + "Pooling=False;User Id=any;Password=any";

using var connection = new SqlConnection(cs);
connection.Open();

using var command = new SqlCommand("SELECT TOP 5 name, city FROM customers ORDER BY name", connection);
using var reader = command.ExecuteReader();
while (reader.Read()) Console.WriteLine($"{reader.GetString(0)}  {reader.GetString(1)}");
```

**PowerShell** — no module needed; this is the driver that ships with Windows PowerShell, and
`Encrypt=True` is its spelling of Mandatory:

```powershell
$cs = "Server=tcp:127.0.0.1,1433;Encrypt=True;TrustServerCertificate=True;" +
      "Pooling=False;User Id=any;Password=any"

$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT TOP 5 name, city FROM customers ORDER BY name"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) { "{0}  {1}" -f $reader[0], $reader[1] }
$conn.Close()
```

**JDBC** — confirmed working; this is what DBeaver uses:

```
jdbc:sqlserver://127.0.0.1:1433;encrypt=true;trustServerCertificate=true;user=any;password=any
```

Use `tcp:127.0.0.1` rather than `localhost`, or the client may try shared memory and never reach
the listener. If you started DaffyDB with `--no-cert` there is no encryption to negotiate, and the
string becomes `Server=tcp:127.0.0.1,1433;Encrypt=False;TrustServerCertificate=True;...` instead.

### Which clients

`sqlcmd`, ADO.NET and JDBC work straightforwardly — run a query, read the rows.

Management Studio and DBeaver connect and run queries, with **limited operation**: your tables and
their columns appear and are browsable, and SSMS's "Select Top 1000 Rows" works, but deeper Object
Explorer nodes — keys, indexes, statistics, security, agent — report errors, and some property
dialogs fail. Those errors scroll past in the DaffyDB console; they are non-fatal, and the query
window keeps working throughout.

**VS Code**, with Microsoft's SQL Server extension (mssql), connects the same way, to `127.0.0.1,1433`
with SQL Login and *Trust server certificate*. Its Object Explorer lists databases, tables, views
and columns, and "Select Top 1000 Rows" works. IntelliSense goes without what DaffyDB does not have -
triggers, modules, synonyms - as it does in SSMS.

**ODBC** works through ODBC Driver 17 or 18 for SQL Server. Set the DSN's encryption to Mandatory
and tick "Trust server certificate".

**OLE DB** works through MSOLEDBSQL, the Microsoft OLE DB Driver 19 for SQL Server:

```
Provider=MSOLEDBSQL19;Data Source=127.0.0.1;Use Encryption for Data=Mandatory;Trust Server Certificate=True
```

The drivers Windows ships with — the ODBC driver named just "SQL Server", and the `SQLOLEDB`
provider — are not supported: both speak the protocol version of SQL Server 2000.

**SQL Server, as a linked server**, works through ODBC Driver 18 for SQL Server, installed on the
SQL Server's machine. DaffyDB listens on 127.0.0.1 only, so SQL Server has to run on the same
machine as DaffyDB. Run this on the SQL Server:

```tsql
EXEC sp_addlinkedserver
    @server     = 'DaffyDB',
    @srvproduct = '',
    @provider   = 'MSDASQL',
    @provstr    = 'Driver={ODBC Driver 18 for SQL Server};Server=127.0.0.1,1433;Database=daffydb;Encrypt=yes;TrustServerCertificate=yes;';

EXEC sp_addlinkedsrvlogin
    @rmtsrvname  = 'DaffyDB',
    @useself     = 'false',
    @rmtuser     = 'any',
    @rmtpassword = 'any';
```

Then a table is `DaffyDB.daffydb.dbo.customers`, and `OPENQUERY(DaffyDB, '...')` sends DaffyDB a
query exactly as written. Both read; writing through the linked server is not supported. Text
longer than 4000 characters and `time` values cannot come through - MSDASQL cannot read them from
SQL Server itself either - and fail the query with "returned data that does not match expected
data length". In an `OPENQUERY`, `LEFT(note, 4000)` and `CAST(opens_at AS varchar(16))` read them
as shorter text.

Use MSDASQL as shown, not the `MSOLEDBSQL` provider: through that, SQL Server asks for Multiple
Active Result Sets, which DaffyDB does not support, and the connection fails with "Client unable
to establish connection".

**Power BI** connects three ways:

- **Get Data → SQL Server**, its built-in connector. Enter `127.0.0.1` as the server —
  `127.0.0.1,PORT` if DaffyDB is not on 1433 — and leave the database empty, or enter `daffydb`.
  Not `localhost`, which does not reach it.
  This connector checks DaffyDB's certificate and has no setting to skip that, so **Windows has
  to trust the certificate first**; see below.
- **Get Data → ODBC**, with a DSN on ODBC Driver 17 or 18 set up as described above.
- **Get Data → OLE DB**, with the MSOLEDBSQL19 connection string above.

The navigator lists your tables, and they import.

**Or let DaffyDB open it.** Type `:pbi` at the prompt and Power BI Desktop opens with the SQL
Server connection filled in, in Import mode, straight to the navigator. It opens a `.pbids` file,
Power BI's own connection shortcut, which DaffyDB writes at startup to
`%LOCALAPPDATA%\DaffyDB\daffydb.pbids` (`daffydb-PORT.pbids` on another port) and prints in its
banner, so double-clicking that file works too. The certificate has to be trusted first, and `:pbi`
offers to do that if it is not. The file holds no credentials: the first time, Power BI asks for
them. Choose **Database**, then any username and password, or the ones you gave `--user` and
`--password`.

### Trusting the certificate, for Power BI's SQL Server connector

The first time you run `daffydb` on Windows, its prompt offers to do this for you before your
first command. You can also run `daffydb --trust-cert` at any time. Either way, Windows then shows a security warning about
installing the certificate: answer **Yes**.

To do it by hand instead:

1. Find `daffydb.cer`. DaffyDB prints its path when it creates it; it lives in
   `%LOCALAPPDATA%\DaffyDB\certs`.
2. Right-click it and choose **Install Certificate**.
3. Choose **Current User**, then **Next**. Local Machine works too, but it needs administrator
   rights and makes every account on the PC trust the certificate.
4. Choose **Place all certificates in the following store**. Do not leave it on "Automatically
   select", which does not put the certificate among the trusted roots.
5. Click **Browse**, select **Trusted Root Certification Authorities**, and click **OK**. Then
   click **Next** and **Finish**.
6. If Windows shows a security warning, click **Yes** to approve.

This only affects DaffyDB. Windows will accept DaffyDB's own server under the names on its
certificate: `localhost`, `127.0.0.1` and this machine's name. The certificate is not a certificate
authority, so it cannot vouch for any other site or program. `daffydb --untrust-cert` removes it
again. If the certificate is ever replaced — `--regen-cert`, or when it expires after five years —
trust the new one.

No data is copied. Each file is read in place, on demand — except Excel workbooks, which are
loaded into memory when DaffyDB starts (see below).

## Where a table comes from

The file name decides where it lands:

| File | Table |
|---|---|
| `orders.parquet` | `dbo.orders` |
| `Sales.Customer.parquet` | `Sales.Customer` |
| `Sales/Customer.csv` | `Sales.Customer` |
| `events.json` | `dbo.events` |
| `archive.csv.gz` | `dbo.archive` |
| `inventory/part-00000.parquet`, `part-00001.parquet`, ... | `dbo.inventory` |
| `Budget.xlsx`, sheets `Q1` and `Summary Q1` | `Budget.Q1` and `Budget.[Summary Q1]` |

Every table is in the `daffydb` database, so `orders`, `dbo.orders` and `daffydb.dbo.orders` all
name the same one.

**A folder of parts is one table.** A table too big for one file is usually written as a folder of
pieces, and DaffyDB reads that folder as one table named after it. That covers
`part-00000.parquet` and on, as sqlcarbon, Spark, Hadoop and pyarrow write them, `data_0.parquet`
and on, as DuckDB writes them, and any folder of parquet files holding a `_SUCCESS` marker. A
folder that mixes parts with other files is read file by file. A re-export with more parts or fewer
shows up at the next query, without a restart. If a table was exported both ways, `orders.parquet`
beside an `orders` folder, the newer of the two is served and the other is named in a warning.

**Getting tables out of SQL Server.** [sqlcarbon](https://pypi.org/project/sqlcarbon/), also from
TroBeeOne, copies SQL Server tables to another SQL Server or to parquet, as a YAML file sets out
(`pip install sqlcarbon`). It writes a small table as one file and a large one as a folder of
parts, so its output folder is ready for `daffydb --data-dir` as it stands.

JSON can be one array of objects, or one object per line, and `.jsonl` and `.ndjson` are read
too. Gzipped `.csv.gz` and `.json.gz` files (and `.jsonl.gz`, `.ndjson.gz`) are read without
unpacking them first. All of this is built into DuckDB, so no extension is ever downloaded.

CSV and JSON column types are sniffed by DuckDB. A nested JSON object or array comes back as
text. Where a folder holds both `orders.parquet` and `orders.csv`, the parquet wins — it carries a
real schema instead of a guess at one — and a CSV wins over a JSON file in the same way. A file
that cannot be read, JSON that does not parse included, is named in a warning and skipped, so one
bad file costs its own table and nothing else.

The scan looks two folders down, and leaves out:

- `bin`, `obj`, `node_modules`, and folders that are hidden or start with a dot (`.git`, `.vscode`)
- `appsettings.json` (and `appsettings.*.json`), `launchSettings.json`, `package.json` and
  `package-lock.json`, which are configuration rather than data
- folders it is not allowed to open

A folder with more than 500 data files is not mounted at all, and the log says so; a folder of
parts counts as one, however many parts it has. It is almost always the wrong folder, and half of
it would be worse than none. Point `--data-dir` somewhere
narrower, or mount what you need with an `--init` script, where one `read_parquet` glob can cover
a whole partitioned tree.

## Excel workbooks

An `.xlsx` or `.xlsm` workbook becomes a schema, and each sheet in it a table. That holds for a
workbook with one sheet too, so adding a sheet later never renames the others. The workbook's
folder and any dots in its name make no difference: `Finance/Plan.2024.xlsx` is the schema
`Plan.2024`.

Sheets are loaded into DaffyDB's in-memory DuckDB database when it starts. The workbook itself is never
changed, and the loaded copy goes away when DaffyDB stops. Changes saved to a workbook while
DaffyDB is running show up after a restart. A workbook that is open in Excel still loads.

Each column's type is worked out from every row, not a sample, so a value that appears only far
down a sheet can neither fail the load nor be rounded to fit:

- whole numbers are `bigint`, and one fraction anywhere makes the column `float`
- dates are `date`, or `datetime2` if any of them has a time; times of day are `time`
- TRUE/FALSE is `bit`
- a column that mixes these with text, or with each other, is `nvarchar` and keeps every value
  as it reads in Excel
- errors such as `#N/A` are NULL, and formulas give the value Excel last saved

A sheet becomes a table when its first row is a header. If that first row is not all text, it is
data instead, and the columns are named `column_A`, `column_B` and so on. A blank header cell gets
the same kind of name, and a repeated one gets its column added: `amount_column_E`.

Left out: hidden sheets, chart sheets, empty sheets, and sheets laid out as a report rather than a
table, which DaffyDB recognises by values to the right of the header row, such as a title above
the table. Those are named in the log. So is a workbook that cannot be read, such as one protected
with a password; it costs itself and nothing else. Legacy `.xls` files are not read.

Workbooks are read by [Sylvan.Data.Excel](https://www.nuget.org/packages/Sylvan.Data.Excel), an
MIT-licensed .NET library that ships inside DaffyDB, so no DuckDB extension is involved and nothing
is downloaded. If you would rather use DuckDB's own `read_xlsx`, an `--init` script can still
`INSTALL excel` and create views with it.

## Mounting more while it runs

DaffyDB answers a few commands itself, as stored procedures in a `daffydb` schema. These are for
everyone:

```sql
EXEC daffydb.status;
EXEC daffydb.tables;
```

`daffydb.status` says how the server was started and what it serves; `daffydb.tables` lists every
table and view with where it comes from, and `EXEC daffydb.help` lists the commands. Started with
`--allow-admin`, DaffyDB lets clients add to what it serves, and take it away again, without a
restart:

```
EXEC daffydb.mount 'D:\exports\2025'                              -- a folder, read as --data-dir is
EXEC daffydb.mount 'D:\exports\orders.parquet', 'staging'          -- one file, into a schema
EXEC daffydb.postgres 'host=localhost dbname=shop user=reader password=...'
EXEC daffydb.mysql 'host=127.0.0.1 database=shop user=reader password=...'
EXEC daffydb.sqlserver 'Server=sql01;Database=shop;User Id=reader;Password=...'
EXEC daffydb.sqlite 'D:\data\chinook.db'
EXEC daffydb.duckdb 'D:\data\warehouse.duckdb', @schema = 'wh'
EXEC daffydb.unmount 'staging'
```

A database is attached read-only, and each of its tables becomes a table here that reads it live:
nothing is copied, and nothing is ever written to it. Its schemas carry over, with PostgreSQL's
`public`, and SQLite's and DuckDB's `main`, as `dbo`. A second argument mounts just one table, and
`@schema` puts everything in a schema of your choosing. Every mount has a name, which
`daffydb.tables` shows and `daffydb.unmount` takes: the folder's, file's or database's, or the
`@schema` it was given. If any name a mount would use is already taken, nothing is mounted and the
error says which. Mounts last until the server stops.

PostgreSQL, MySQL and SQLite each need one of DuckDB's extensions, which are not in this package:
the first command that needs one downloads it from DuckDB's own repository and keeps it in your
profile, and nothing is downloaded before that. SQL Server and DuckDB files need nothing. Errors are
numbered from 50301, after DaffyDuo's 501xx and DaffyTee's 502xx, so code can catch them; daffydb's
own prompt can always run these commands, flag or not.

## SQL Server

`daffydb.sqlserver` takes a SQL Server connection string, as SqlClient does, and mounts every table
and view of the database it names, or the one table given as a second argument. DaffyDB speaks SQL
Server's protocol itself, so there is no extension and no driver to install.

```
EXEC daffydb.sqlserver 'Server=tcp:sql01;Database=shop;User Id=reader;Password=...'
EXEC daffydb.sqlserver 'Server=sql01,1433;Database=shop;User Id=reader;Password=...;Lock Timeout=120', 'sales.orders'
EXEC daffydb.sqlserver 'Server=tcp:myserver.database.windows.net;Database=shop;Authentication=ActiveDirectoryServicePrincipal;User Id=<app id>;Password=<client secret>', @schema = 'azure'
```

- `Database` is required, and a keyword DaffyDB has no use for is refused by name.
- `Encrypt` is `Mandatory` unless it says `Optional` or `Strict` (TDS 8.0), and the server's
  certificate is checked unless the string says `TrustServerCertificate=True`.
- A SQL login, or an Entra ID application with `Authentication=ActiveDirectoryServicePrincipal`:
  its client id as `User Id`, a client secret as `Password`. Windows logins and named instances are
  not supported yet; give an instance's port instead.
- `Lock Timeout` is how many seconds a read waits behind another session's locks before it fails:
  30 unless given, `0` for no limit. DaffyDB answers one query at a time, so a read left waiting on
  a lock would hold up every client. A cancel reaches SQL Server at once, too.
- A query asks SQL Server for only the columns it uses, and sends it the conditions it can apply
  exactly as DaffyDB would - numbers, dates, `uniqueidentifier`, and text compared with `=` under a
  case-insensitive collation - so only the rows they keep cross the network. DaffyDB still applies
  every condition itself, so the answer never depends on what was sent. The catalog - Object
  Explorer, `sys.columns` - is answered without asking SQL Server at all.
- `money` arrives as `decimal(19,4)`, `datetime` as `datetime2`, `datetimeoffset` as `datetime2` in
  UTC, and `time(7)` and `datetime2(7)` to the microsecond.
- Mount with a login that can only read. A mounted table only ever reads, but a client that can
  query DaffyDB can reach whatever that login can, on that server.

`daffydb.run_sqlserver` runs T-SQL on a mounted server, a stored procedure among them, and returns
what SQL Server returns. It needs `--allow-admin` too.

```
EXEC daffydb.run_sqlserver 'shop', N'EXEC dbo.GetOrders @min = 2, @name = N''Ann'''
EXEC daffydb.run_sqlserver 'shop', N'SELECT TOP (5) * FROM dbo.orders; PRINT ''done'''
```

- The first argument is the mount's name, as `daffydb.tables` shows it. The T-SQL is SQL Server's
  own and goes as it is, as one batch, so without `GO`.
- Every result set comes back. `PRINT` output comes back as messages, ahead of the rows, and an
  error comes back with SQL Server's own number, after the rows that came before it.
- It runs as the mount's login and can do whatever that login can, writes included, so that login
  decides what is possible. One that can only read needs `GRANT EXECUTE` on the procedures it should
  run.
- Each call is its own batch on its own session: a temp table, a `USE` or a `SET` lasts only as long
  as the call, and a transaction left open is rolled back.
- `Lock Timeout` does not apply: it waits as long as SQL Server takes. DaffyDB goes on answering
  other clients meanwhile, and a cancel reaches SQL Server at once.
- What it returns is held in memory until the last row arrives. `varchar`, `xml` and `sql_variant`
  arrive as `nvarchar`, `datetimeoffset` as `datetime2` in UTC, and a `decimal` too wide for .NET's
  as its exact text.

To keep what it returns, to `JOIN` or save later, put it in a temp table:

```sql
EXEC daffydb.run_sqlserver 'shop', N'EXEC dbo.GetOrders', @into = '#orders'

CREATE TABLE #orders (id int, customer nvarchar(50));
INSERT #orders EXEC daffydb.run_sqlserver 'shop', N'EXEC dbo.GetOrders'
```

- `@into` makes the table from the first result set's own columns, replacing any of that name, and
  returns any other result sets. A column with no name becomes `column0`, and a repeated name gets
  `_1`. T-SQL that returns no result set gets error 50309 and leaves the table as it was, though the
  T-SQL did run.
- `INSERT ... EXEC` adds to a table you made, and may list its columns. Every result set goes in, all
  of them or none. A table or column that is not there fails before the T-SQL runs. A value that does
  not fit fails the insert with error 50309, rather than become NULL.
- Rows that came before an error are kept, and the error is still reported. Rows that could not be
  kept come back to the client with the error instead of being lost, since the T-SQL has run and may
  not be safe to run again.
- A temp table's columns are what a mounted table's would be: `datetime` as `datetime2`, and `time`
  and `datetime2` to the microsecond. Temp tables are shared by every session, and last until the
  server stops or they are dropped.

## Another DuckDB, over Quack

An `--init` script can reach another DuckDB through Quack, DuckDB's own client-server protocol, and
make a view of each table you want. Nothing in DaffyDB needs turning on, and the views read the
other DuckDB live.

**The other DuckDB must be 1.5.5, the version DaffyDB runs on.** Connecting to DuckDB 2.0 fails with
`Failed to deserialize`. Support for 2.0 will come later.

On the other DuckDB, which has to stay running:

```sql
INSTALL quack;
LOAD quack;
FROM quack_serve('quack:localhost:9494', token = 'use-a-long-random-token');
```

The script, in DuckDB's dialect rather than T-SQL:

```sql
-- quack.sql: daffydb --init quack.sql
INSTALL quack;
LOAD quack;
CREATE SECRET remote_token (TYPE quack, TOKEN 'use-a-long-random-token', SCOPE 'quack:localhost:9494');
ATTACH 'quack:localhost:9494' AS remote (TYPE quack, READ_ONLY);

CREATE VIEW dbo.orders AS SELECT * FROM remote.main.orders;
CREATE VIEW dbo.customers AS SELECT * FROM remote.main.customers;
INSERT INTO sys.mounted_objects VALUES ('dbo', 'orders'), ('dbo', 'customers');
```

The last line lists the views as tables, in Object Explorer and `daffydb.tables`. Leave it out and
they are views. Either way `daffydb.tables` names no mount for them, since the script made them, so
`daffydb.unmount` cannot take them away.

- If the other DuckDB is not there when DaffyDB starts, the script fails and DaffyDB stops.
- **The token reaches the log.** DaffyDB logs each `--init` statement and does not mask a Quack
  `TOKEN`, so treat that log as you would the token.
- **Every client can reach the other DuckDB.** `SELECT * FROM remote.query('...')` runs any SQL
  there, needs no flag, and `READ_ONLY` does not stop it, so that SQL can write and can read the
  files that DuckDB can. Protect the other DuckDB itself, as
  [its security page](https://duckdb.org/docs/2.0/quack/security.html) describes.
- DuckDB calls Quack a beta until 2.0. For another machine, its
  [overview](https://duckdb.org/docs/2.0/quack/overview.html) covers listening beyond `localhost`,
  HTTPS and the reverse proxy it recommends.

## Saving tables as parquet

With `--allow-admin`, two more commands write what DaffyDB serves out to parquet files, through
DuckDB's own parquet writer:

```
EXEC daffydb.save_parquet 'sales.orders', 'D:\exports'               -- sales.orders.parquet
EXEC daffydb.save_parquet '#totals', 'D:\exports', 'totals_2025'      -- a temp table, under a name of your own
EXEC daffydb.save_parquet_all 'D:\exports\2025-10-20'                 -- every table, view and temp table
EXEC daffydb.save_parquet_all 'D:\exports\shop', @mount = 'shop'      -- one mount's tables
```

A file is named `schema.table.parquet`, which is how a folder scan reads a name, so mounting the
folder - or serving it with `--data-dir` - gives every table back under its own name. A temp
table's file is its name without the `#`: `#totals` becomes `totals.parquet`, and comes back in
`dbo`. A character Windows does not allow in a file name becomes `_`, and the result says so.
`save_parquet_all` saves every table and view `daffydb.tables` lists, mounted or made by `--init`,
`sp_duckdb` or a client, and every temp table. Temp tables here are shared by every session, so
that is every session's.

Nothing is ever replaced. Every file name is checked before anything is written, and if one is
already there, or two tables would share one, the command fails and writes nothing. Each file is
written under a temporary name and renamed once it is whole, so a failure or a cancel never leaves
half a file behind. The folder is made if it is not there, on the machine DaffyDB runs on. A table
that cannot be read - a mounted file that has gone, a database that is down - is left out and said
so, and the rest are saved. Between one table and the next, other clients' queries get their turn.
A client's command timeout covers the whole save, so give a big one longer: ADO.NET's default is 30
seconds, and SSMS has none.

## Flags

| Flag | What it does |
|---|---|
| `--data-dir PATH` | Serve another folder instead of the current one, two levels deep |
| `--port N` | Listen somewhere other than 1433 |
| `--no-cert` | Refuse encrypted connections. They are accepted by default, with a self-signed certificate generated on first run |
| `--trust-cert` / `--untrust-cert` | Windows only: add the certificate to your Trusted Root store, which Power BI's SQL Server connector needs, or take it back out |
| `--user NAME --password VALUE` | Require these credentials to log in |
| `--init FILE.sql` | Run a DuckDB script at startup: `INSTALL httpfs`, `CREATE SECRET`, `ATTACH`, `CREATE VIEW` |
| `--allow-passthrough` | Let clients run DuckDB SQL directly via `EXEC sp_duckdb N'...'` |
| `--allow-admin` | Let clients mount folders, files and databases with `EXEC daffydb.mount`, `daffydb.postgres`, `daffydb.sqlserver` and the rest, save tables as parquet with `daffydb.save_parquet`, and run T-SQL on a mounted SQL Server with `daffydb.run_sqlserver` |
| `--seed` | Write sample parquet files to play with |
| `--no-repl` | Just listen, no prompt. Automatic when output is redirected |
| `--quiet` / `--trace` | Log startup only / print every decoded TDS message |
| `--version` / `--help` | |

`--init` is the interesting one: it is DuckDB's own dialect, so anything DuckDB can reach becomes
a table here — S3 and Azure through `httpfs`, Postgres and SQLite through `ATTACH`, a JSON or
Excel file through an extension. Keep a password out of an `ATTACH` string, which any client can
read back from `duckdb_databases()`: put it in `CREATE SECRET (TYPE postgres, PASSWORD '...')` and
name that secret in the `ATTACH`.

## Some SQL that works

All of it against the sample files `daffydb --seed` writes. None of this is DuckDB syntax — watch
the console to see what each statement was rewritten into.

**Join and aggregate.**

```sql
SELECT c.name,
       COUNT(*)                       AS order_count,
       SUM(o.quantity * o.unit_price) AS total
FROM orders o JOIN customers c ON c.customer_id = o.customer_id
GROUP BY c.name
ORDER BY total DESC;
```

**Comparison ignores case, the way SQL Server does by default.**

```sql
SELECT name, city FROM customers WHERE name = 'ada lovelace';
```

**`TOP` inside a subquery** — each one becomes a `LIMIT` on the query it belongs to, not on the
statement.

```sql
SELECT TOP 2 c.name,
       (SELECT TOP 1 o.product
        FROM orders o
        WHERE o.customer_id = c.customer_id
        ORDER BY o.order_ts DESC) AS most_recent_order
FROM customers c
ORDER BY c.name;
```

**Paging, and a table hint that is silently dropped.**

```sql
SELECT order_id, product
FROM orders WITH (NOLOCK)
ORDER BY order_id
OFFSET 5 ROWS FETCH NEXT 5 ROWS ONLY;
```

**`CROSS APPLY`, which becomes a lateral join.**

```sql
SELECT c.name, x.units, x.spend
FROM customers c
CROSS APPLY (
    SELECT SUM(o.quantity) AS units, SUM(o.quantity * o.unit_price) AS spend
    FROM orders o WHERE o.customer_id = c.customer_id
) x
ORDER BY c.name;
```

**Window functions and CTEs.**

```sql
WITH ranked AS (
    SELECT c.name,
           SUM(o.quantity * o.unit_price) AS total,
           RANK() OVER (ORDER BY SUM(o.quantity * o.unit_price) DESC) AS position
    FROM orders o JOIN customers c ON c.customer_id = o.customer_id
    GROUP BY c.name
)
SELECT name, total, position FROM ranked WHERE position <= 3 ORDER BY position;
```

**Variables, control flow, and two result sets from one batch.**

```sql
DECLARE @best nvarchar(50);

SELECT TOP 1 @best = c.name
FROM orders o JOIN customers c ON c.customer_id = o.customer_id
GROUP BY c.name
ORDER BY SUM(o.quantity * o.unit_price) DESC;

IF @best IS NOT NULL
BEGIN
    SELECT @best AS best_customer;
    SELECT order_id, product, quantity
    FROM orders o JOIN customers c ON c.customer_id = o.customer_id
    WHERE c.name = @best ORDER BY order_id;
END;
```

**`TRY` / `CATCH`, with the error readable from the `CATCH`.**

```sql
BEGIN TRY
    SELECT * FROM no_such_table;
END TRY
BEGIN CATCH
    SELECT ERROR_NUMBER() AS number, ERROR_MESSAGE() AS message;
END CATCH
```

**A temp table, filled and read back.**

```sql
CREATE TABLE #spend (name nvarchar(50), total decimal(18,2));

INSERT INTO #spend
SELECT c.name, SUM(o.quantity * o.unit_price)
FROM orders o JOIN customers c ON c.customer_id = o.customer_id
GROUP BY c.name;

SELECT name, total, CASE WHEN total > 1000 THEN 'high' ELSE 'normal' END AS band
FROM #spend ORDER BY total DESC;

DROP TABLE #spend;
```

**DuckDB itself**, with `--allow-passthrough`. What you create this way is an ordinary table to
every client afterwards, which is how you reach S3, Postgres, SQLite, JSON or Excel:

```sql
EXEC sp_duckdb N'CREATE SCHEMA IF NOT EXISTS live;
                 CREATE OR REPLACE VIEW live.top_products AS
                 SELECT product, SUM(quantity) AS units
                 FROM daffydb.dbo.orders GROUP BY product';

SELECT TOP 3 product, units FROM live.top_products ORDER BY units DESC;
```

## What works, and what does not

T-SQL is translated to DuckDB, not executed by SQL Server, so the surface is real but finite.
Batches, parameters, local variables, `IF`/`ELSE`, `TRY`/`CATCH`, temp tables, multiple result
sets, `TOP` (including inside subqueries, where it becomes a `LIMIT` on that subquery rather than
on the statement), `USE`, and a `sys` catalog and `INFORMATION_SCHEMA` complete enough for the
Object Explorers of SSMS and VS Code, DBeaver and Power BI to draw your tables and columns all work.

String comparison is case-insensitive, as it is in SQL Server by default, so
`WHERE first_name = 'paTricia'` finds `Patricia`. That covers `=`, `IN`, `JOIN`, `DISTINCT`,
`GROUP BY` and `ORDER BY`, and `LIKE` too. The fold is ASCII only, so accented characters still
compare exactly.

Deliberately unimplemented, each reported as a proper `SqlException` rather than a hang:
integrated authentication, MARS, bulk copy, `WHILE` loops, user stored
procedures, output parameters of your own, and fetching through a server cursor: one is opened
only to say what its columns are, which is all a linked server asks of it. `SET ROWCOUNT` limits
queries and not writes. Transactions are
accepted and ignored — everything is autocommit. Tables mounted from files are views over read-only
files, so writes to them fail. Sheets loaded from a workbook are in-memory tables: a write to one
succeeds but only changes DaffyDB's copy, which is gone at restart. The workbook itself is never
written. Table row counts are not available, because there is no `sys.partitions`.

DuckDB runs behind a single connection, so concurrent sessions take turns. Fine for one person
with a query tool and a script open at once; not yet a multi-user server. Cancelling a query, or a
command timeout, stops it on the server, so a runaway query does not hold up the others.

This is an early release. It is useful today for exploring data files with tools you already have,
and it is not a SQL Server replacement.

## Security

DaffyDB binds to loopback only, and with no `--user`/`--password` it accepts any login.
`--allow-passthrough` is off by default because it is arbitrary DuckDB execution, including file
and network reads, available to anyone who can connect. `--allow-admin` is off by default for a
narrower reason: a mount reads whatever the account running DaffyDB can read, and connects
wherever it is told, though only ever read-only, a save writes parquet files wherever that
account can write, though never over a file that is there, and `daffydb.run_sqlserver` runs any
T-SQL a mounted SQL Server's login may. Every admin command, and every refused one, is logged with
the session that sent it, T-SQL included.

Passwords never reach the log, `:log`, `--trace` or an error message a client is sent: anything
that looks like one, in a command, an `--init` statement or what `sp_duckdb` runs, is masked. A
database mounted with `daffydb.postgres` or `daffydb.mysql` keeps its password in a DuckDB secret,
which DuckDB never shows; one mounted with `daffydb.sqlserver` keeps it in DaffyDB's own memory,
outside DuckDB. Mount SQL Server with a login that can only read: a client that can query DaffyDB
can reach whatever that login can. A Quack `TOKEN` in an `--init` script is the exception, and does
reach the log.

Encrypted and plaintext clients share the port, and encryption is on by default: a self-signed
certificate is generated on first run and kept per-user. `Encrypt=Mandatory` needs nothing but
`TrustServerCertificate=True`; `Encrypt=Strict` is stronger but ignores that setting and needs
`ServerCertificate=` pointed at the `.cer` written beside the key. `--no-cert` turns all of it
off, which also leaves the login password readable on the wire — the protocol obfuscates it
rather than encrypting it, so `--password` and `--no-cert` are a poor pairing.

Trusting the certificate (`--trust-cert`, or the question on first run) is only ever done when you
say yes, only in your own Windows account, and only with the public half. The private key stays
beside it in your profile. Anyone who can read your profile could use that key to pose as
`localhost` to your own programs, so keep the folder as private as the rest of your profile.

## Protocol and trademarks

DaffyDB implements the Tabular Data Stream protocol as published by Microsoft in the open
[MS-TDS] specification. It contains no Microsoft code, and is not a derivative of, replacement
for, or emulator of Microsoft SQL Server. It is not affiliated with, endorsed by, or sponsored by
Microsoft Corporation. Microsoft, SQL Server and Azure are trademarks of Microsoft Corporation.

TDS began at Sybase in the 1980s and has many independent implementations, clients and servers
alike: FreeTDS, jTDS, Tiberius and Babelfish for PostgreSQL among them.

DaffyDB gives its own name wherever a client asks for one, such as `@@VERSION`. The one exception
is the product name in the login reply. Microsoft's OLE DB driver checks that field and refuses
every command unless it holds the value the driver expects, so DaffyDB sends that value there and
nowhere else. The version number it reports is one clients recognise, for the same reason: they
use it to decide which features to use. This is a protocol requirement, not a claim of
equivalence. Some tools may show "SQL Server" for DaffyDB, because they label a connection by the
driver or connector that opened it, not by the server it reaches.

## License

MIT. Copyright (c) 2026 TroBeeOne LLC.

Bundled dependencies and their licenses are listed in `THIRD-PARTY-NOTICES.txt`: DuckDB (MIT),
DuckDB.NET (MIT), Sylvan.Data.Excel (MIT) and Apache Arrow (Apache-2.0).
