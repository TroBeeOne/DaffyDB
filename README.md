<img src="icon.png" alt="DaffyDB" width="128">

# DaffyDB

Query Parquet, CSV, JSON and Excel files with any SQL Server client.

[![NuGet](https://img.shields.io/nuget/v/DaffyDB.svg)](https://www.nuget.org/packages/DaffyDB)

DaffyDB is a TCP service that speaks enough of the SQL Server wire protocol (MS-TDS) for a real
client to connect to it as if it were SQL Server, hand it T-SQL, and read back rows - with the
query translated and executed against DuckDB over parquet, CSV, JSON and Excel files.

```
client (SqlClient / sqlcmd) ──T-SQL over TDS──> proxy :1433
                                                   │  decode to (sql, parameters)
                                                   ▼
                                            TSqlTranslator
                                                   │  T-SQL dialect -> DuckDB
                                                   ▼
                                          DuckDbQueryHandler
                                                   │
                                     DuckDB views over customers.parquet
                                                    and orders.parquet
```

Both clients work: `Microsoft.Data.SqlClient` and `sqlcmd` (ODBC).

| Document | What is in it |
|---|---|
| **README.md** (this file) | Running it, the flags, connecting, what it can translate |
| [NUGET.md](NUGET.md) | The package page: what someone installing it from NuGet reads |
| [src/DaffySQL/README.md](src/DaffySQL/README.md) | DaffySQL, the SQL Server client behind `daffydb.sqlserver` |
| [LICENSE](LICENSE) | MIT |
| [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) | The licences of what the package bundles |

## Requirements

To run the installed tool, the .NET 10 runtime. To build from source, the .NET 10 SDK. The
server pulls in `DuckDB.NET.Data.Full`; the test clients use `Microsoft.Data.SqlClient`.

## Install

```sh
dotnet tool install -g DaffyDB
daffydb --version
```

To upgrade an existing install, stop any running `daffydb` — on Windows a running copy holds its
files open — and run:

```sh
dotnet tool update -g DaffyDB
```

From 0.4 or earlier, one thing moves: your tables are now in a database named `daffydb`, not in
`master`, which holds only the system catalog as it does on SQL Server. A client that names no
database lands in `daffydb`, so most need no change; anything that names `master` should name
`daffydb`. See [Databases](#databases).

From 0.7, a folder of part files (`part-00000.parquet` and on, or DuckDB's `data_0.parquet`) is
one table named after the folder, where it was a table per part. See
[Folders of parts](#folders-of-parts).

From 0.9, T-SQL gives SQL Server's answers in more places, and a few answers change because they
were wrong: `DATEPART(weekday)` counts Sunday as 1, weeks start on Sunday in `DATEPART(week)` and
`DATEDIFF(week)`, `CONVERT` honours its style codes, `varchar(n)` cuts text to n characters, and
`LIKE` is case-insensitive and understands `[a-z]` classes. A query that relied on the old answer
gets SQL Server's now. A cancelled query also stops on the server, and SSMS 22 connects: it asks
for `SERVERPROPERTY('EditionID')` while connecting, which 0.9 and earlier answered with NULL. See
[T-SQL translation](#t-sql-translation).

From 0.8, nothing moves. What is new is opt-in: `EXEC daffydb.status` and `daffydb.tables` for
everyone, and, with `--allow-admin`, commands that mount folders, files and databases while the
server runs. The log, `--trace` and error messages now mask passwords. See
[DaffyDB's own commands](#daffydbs-own-commands).

From 1.0.0, nothing moves. The binaries in the package are now signed by TroBeeONE LLC:
`daffydb.exe`, `daffydb.dll` and the .NET libraries that ship beside them carry its Authenticode
signature, and DuckDB's `duckdb.dll` keeps the DuckDB Foundation's own. Signing happens to the
finished package at release, so a build from source gives unsigned binaries, as before.

From 1.0.1, nothing moves. SSMS releases before 22 connect now: while connecting they ask whether
the connection is a dedicated admin connection, by reading `sys.endpoints`, which DaffyDB did not
have. See [The sys catalog](#the-sys-catalog).

From 1.0.2, one answer changes. SQL Server can read DaffyDB as a linked server now, and for that a
text column, which has no length of its own, is `nvarchar(4000)` wherever DaffyDB describes it and
in every result. It was `nvarchar(max)` in the catalog, and as wide as its widest value in a
result. A longer value still arrives whole, as `nvarchar(max)`. Prepared statements and
`SET ROWCOUNT` work too. See [From SQL Server, as a linked server](#from-sql-server-as-a-linked-server).
And a `SELECT` straight after a `SET ... ON`, such as `SET NOCOUNT ON`, runs: it used to be read as
part of the `SET`, and returned nothing.

From 1.1.0, nothing moves. With `--allow-admin`, `daffydb.save_parquet` writes a table, view or
temp table out as a parquet file, and `daffydb.save_parquet_all` writes every one. See
[Saving tables as parquet](#saving-tables-as-parquet).

From 1.2.0, one answer changes. `OBJECT_ID` tells schemas apart: `OBJECT_ID('sales.orders')` is
`sales.orders`, and a name without a schema is `dbo`'s, as SQL Server resolves it. It used to
match the name in any schema, and failed when two schemas both had one. With `--allow-admin`,
`daffydb.sqlserver` mounts a SQL Server or Azure SQL database's tables, read live, and sends SQL
Server the conditions a query puts on them. See [SQL Server](#sql-server).

From 1.3.0, a few catalog answers change, and VS Code's Object Explorer lists your tables: it
showed nothing under a database before. One answer changes because it was wrong: `CONVERT` with a
two-digit-year style, 1 to 12, reads 50 to 68 as 1950 to 1968, as SQL Server does, where it read
them as 2050 to 2068. `DATABASEPROPERTYEX` answers NULL for a property it has
nothing for, as SQL Server does, where it answered an empty string. `sys.configurations` holds the
five settings SMO reads with a database's properties beside "SMO and DMO XPs", and `sys.databases`'
`is_published`, `is_subscribed` and `is_merge_published` are `int` where every other flag is `bit`.
`OBJECTPROPERTY`, `SESSIONPROPERTY` and `CONTEXT_INFO()` answer now. See [VS Code](#vs-code) and
[The sys catalog](#the-sys-catalog).

From 1.3.1, nothing moves. With `--allow-admin`, `daffydb.run_sqlserver` runs T-SQL on a mounted SQL
Server, a stored procedure among them, and returns what it returns or keeps it in a temp table. See
[Running T-SQL on it](#running-t-sql-on-it). And a password written `PASSWORD = N'...'` is masked in
the log now.

The command is `daffydb`. On Windows the shim is case-insensitive, so `DaffyDB` works too; on
Linux and macOS type it exactly.

## Run it

Stand in a folder that has data files in it:

```sh
daffydb
```

Every `.parquet`, `.csv`, `.json` and `.xlsx` file underneath becomes a table, the server listens on 1433, and
you get a prompt for trying T-SQL against what was mounted - see [The prompt](#the-prompt).
Connect any SQL Server client to `tcp:127.0.0.1,1433` with any username and password.
[T-SQL translation](#t-sql-translation) says what is translated, and [Limits](#limits) what is
not.

Stop it with Ctrl-C.

### From source

```sh
dotnet build TdsProxy.slnx

# Write the sample data. Once, ever - see below.
dotnet run --project src/TdsProxy -- --seed

# Listen as a SQL Server on 1433, backed by DuckDB
dotnet run --project src/TdsProxy -- --data-dir data
```

Everywhere this README says `daffydb <flags>`, the equivalent from a source checkout is
`dotnet run --project src/TdsProxy -- <flags>`. The leading `--` belongs to `dotnet run`, not
to the CLI, which is why the installed tool does not need it.

While a source build is running it holds `daffydb.exe` open, so a `dotnet build` in another
window will fail on a file lock until you stop it.

### About `--seed`

`--seed` writes two real parquet files and exits immediately — it does not start a server:

```
[seed] wrote D:/.../data/customers.parquet (8 rows)
[seed] wrote D:/.../data/orders.parquet (24 rows)
```

**They stay on disk.** It is a one-off setup step, not something that happens at each start. Run it
once after cloning, and never again unless you delete `data/`. The files are the sample data the
examples here are written against: 8 customers, 24 orders, with one NULL city and one
customer who never ordered, so NULLs and outer joins have something to chew on.

`data/` is gitignored, which is why the files are not in the repo and you have to seed once.

If you are pointing the proxy at your own parquet instead, you can skip `--seed` entirely.

## Flags, with examples

Everything below takes either a relative or a **full path** — `C:\temp\whatever` is fine
everywhere a path is accepted.

### Serving your own data files

With no flags the working directory is served. Point `--data-dir` somewhere else to serve
that instead. Either way, every `.parquet`, `.csv`, `.json` and `.xlsx` underneath becomes a table:

```sh
daffydb --data-dir C:\data\exports
```

```
[duckdb] mounted dbo.customers        <- C:/data/exports/customers.parquet
[duckdb] mounted Sales.Customer       <- C:/data/exports/Sales.Customer.parquet
[duckdb] mounted Person.Address       <- C:/data/exports/Person/Address.parquet
[duckdb] mounted dbo.inventory        <- C:/data/exports/inventory/, 3 part(s)
[duckdb] mounted dbo.products         <- C:/data/exports/products.csv
[duckdb] 7 data file(s) mounted from C:\data\exports
```

The file name decides the schema — see [Loading your own data](#loading-your-own-data) for the
rules. Nothing else is needed: the tables appear in Object Explorer on their own.

### Running a DuckDB script at startup

`--init` points at a `.sql` file of **DuckDB** SQL (not T-SQL), run once as the server starts:

```sh
daffydb --init C:\temp\startup.sql
```

### Both together

This is the usual shape: a folder of files, plus a script for everything a folder cannot express.
The mounts happen first, so the script can build views on top of them.

```sh
daffydb --data-dir C:\data\exports --init C:\temp\startup.sql
```

With `C:\temp\startup.sql` containing, say:

```sql
-- Reach S3 for the things that are not local files
INSTALL httpfs;
LOAD httpfs;
CREATE SECRET (TYPE s3, PROVIDER credential_chain);

CREATE VIEW events AS
SELECT * FROM read_parquet('s3://my-bucket/events/**/*.parquet');

-- A view over the files --data-dir already mounted
CREATE VIEW busy_customers AS
SELECT c.name, COUNT(*) AS orders
FROM daffydb.dbo.orders o
JOIN daffydb.dbo.customers c ON c.customer_id = o.customer_id
GROUP BY c.name;
```

Both `events` and `busy_customers` are then ordinary tables to any client, and both show up in the
catalog. If any statement in the script fails, the proxy prints which one and refuses to start.

### Requiring credentials

```sh
daffydb --user alice --password "s3cret pass!"
```

Either flag works alone. With neither, any login is accepted. Read
[Who is connected](#who-is-connected) before relying on this: the protocol obfuscates the password
rather than encrypting it, so it is only really protected on an encrypted connection — which is
the default, but not under `--no-cert`.

### Letting clients reach DuckDB directly

```sh
daffydb --allow-passthrough
```

Enables `EXEC sp_duckdb N'...'`. Off by default, because it is arbitrary DuckDB execution for
anyone who can connect. See [Reaching DuckDB directly](#reaching-duckdb-directly).

### Letting clients mount more while it runs

```sh
daffydb --allow-admin
```

Enables `EXEC daffydb.mount`, which adds a folder or a file to what is served without a restart;
`daffydb.postgres`, `daffydb.mysql`, `daffydb.sqlserver`, `daffydb.sqlite` and `daffydb.duckdb`,
which mount a database's tables, read-only and read live; `daffydb.unmount`, which takes a mount
away again; and
`daffydb.save_parquet` and `daffydb.save_parquet_all`, which write tables out as parquet files; and
`daffydb.run_sqlserver`, which runs T-SQL on a mounted SQL Server.
Off by default, because a mount reads whatever the account running daffydb can reach, a save
writes wherever it can, and T-SQL does whatever a mounted login may, for anyone who can connect. `daffydb.help`, `daffydb.status` and `daffydb.tables` need no flag, and daffydb's
own prompt can always mount. See [DaffyDB's own commands](#daffydbs-own-commands).

### Encryption, which is already on

```sh
daffydb
```

There is no flag to remember. Encryption is the default because every current client asks for it
by default — `Encrypt=Mandatory` is what `Microsoft.Data.SqlClient` 4.0+, mssql-jdbc 10+,
sqlcmd 18+ and SSMS 20+ send unless told otherwise — and a server that had to be restarted with a
flag before it would answer them failed its very first connection for no good reason.

`--cert` is still accepted and still means what it said; there is simply nothing left for it to
turn on. `--no-cert` goes the other way and refuses encryption outright.

One certificate serves both encrypted transports:

```
Server=tcp:127.0.0.1,1433;Encrypt=Mandatory;TrustServerCertificate=True;...   # TDS 7.4, no file needed
Server=tcp:127.0.0.1,1433;Encrypt=Strict;ServerCertificate=...daffydb.cer;... # TDS 8.0, file needed
```

Mandatory is the easier one to connect with and the one most clients default to; Strict is the
stronger. [Encryption](#encryption) explains the difference and why only Strict needs the `.cer`.

What happens on the **first** run: it finds no certificate in its per-user store, spends about
50ms generating a self-signed one, and writes both halves. That store is `%LOCALAPPDATA%\DaffyDB\certs`
on Windows and `~/.local/share/DaffyDB/certs` elsewhere - deliberately not the working
directory, because a tool you can run from anywhere should not scatter private keys behind it.

```
[tls]    generated a self-signed certificate for MYPC
[tls]    private: C:\Users\you\AppData\Local\DaffyDB\certs\daffydb.pfx
[tls]    public : C:\Users\you\AppData\Local\DaffyDB\certs\daffydb.cer   <- trust this one
```

On **every run after that** it finds the file and reuses it, for the five years it is good for:

```
[tls]    certificate C:\Users\you\AppData\Local\DaffyDB\certs\daffydb.pfx (expires 2031-09-19)
```

If it cannot write one — a read-only profile, a confined container — the server says so and
serves plaintext rather than refusing to start:

```
[tls]    could not provision a certificate: UnauthorizedAccessException: Access to the path ...
```

Related flags:

```sh
# Keep the certificate somewhere specific
daffydb --cert-file C:\temp\tdsproxy.pfx

# Throw the old one away and make a new one
daffydb --regen-cert

# Plaintext only: no TLS offered, no certificate written
daffydb --no-cert
```

`--cert-file` writes the `.cer` beside the `.pfx`, so `C:\temp\tdsproxy.pfx` gives you
`C:\temp\tdsproxy.cer` to point clients at. `--cert-password` sets the PFX password.

**Some clients have to trust it.** Most can be told to accept a self-signed certificate, which is
all `TrustServerCertificate=True` is. Power BI's SQL Server connector cannot: it checks the
certificate against what Windows trusts, and has no setting to skip that. For those, on Windows:

```sh
# Add the certificate to your own Trusted Root store; Windows asks you to confirm
daffydb --trust-cert

# Take every DaffyDB certificate back out
daffydb --untrust-cert
```

The first time the prompt starts on Windows with an untrusted certificate, it offers to do this
before the first command, and remembers a no - beside the certificate, as `daffydb.trust-declined`
- so it asks once per certificate. It asks at the prompt rather than on the way up, because the
server is already listening by then: a copy started in a window nobody is watching must not sit
with its port closed waiting for an answer. `--no-repl`, scripts and redirected input are never
asked. It only ever adds the public
half, to the current user's store, and Windows puts up its own security warning before anything
changes. The certificate is not a certificate authority - its basic constraints say so, and its key
usage allows no certificate signing - so trusting it covers DaffyDB's own server on `localhost`,
`127.0.0.1` and the machine name, and nothing it could sign. Installing the `.cer` by hand works as
well: Install Certificate, Current User, *Place all certificates in the following store*, Trusted
Root Certification Authorities.

**Yes, an encrypting server still accepts unencrypted connections.** All three work on the same port at the
same time — the first byte of a TLS record is `0x16`, which is not a valid TDS packet type, so one
peeked byte separates a Strict client from the rest, and a Mandatory client announces itself a
moment later in PRELOGIN. Turning encryption on takes nothing away.

### Everything else

| Flag | Default | What it does |
|---|---|---|
| `--port N` | 1433 | Listen somewhere else |
| `--data-dir DIR` | the working directory | Mount every `.parquet`, `.csv`, `.json` and `.xlsx` under `DIR`, two levels deep |
| `--init FILE.sql` | none | Run a DuckDB script at startup |
| `--allow-passthrough` | off | Enable `EXEC sp_duckdb N'...'` |
| `--no-cert` | off | Refuse `Encrypt=Mandatory` and `Encrypt=Strict`; write no certificate |
| `--cert` | — | Accepted and does nothing; encryption is already on |
| `--cert-file FILE.pfx` | per-user store | Where the certificate lives |
| `--cert-password PW` | built-in | PFX password |
| `--regen-cert` | off | Replace the certificate |
| `--trust-cert` | — | Windows: add the certificate to your Trusted Root store, then exit |
| `--untrust-cert` | — | Windows: remove every DaffyDB certificate from it, then exit |
| `--user NAME` | any | Require this login |
| `--password VALUE` | any | Require this password |
| `--seed` | — | Write the sample parquet files and exit |
| `--no-repl` | off | Just listen, no prompt; automatic when output is redirected |
| `--repl` | auto | Force the prompt on even with output redirected |
| `--handler duckdb\|echo` | `duckdb` | Swap DuckDB for the phase-1 echo handler |
| `--quiet` | off | Log startup only, not each query (plain listener only) |
| `--trace` | off | Print every decoded TDS message |
| `--version` | - | Print the DaffyDB, DuckDB and .NET versions |
| `--help` | - | Print usage |

## The prompt

With no flags you get a prompt as well as a listener, for trying T-SQL against what was mounted
without opening another tool:

```
daffydb> :tables

  schema  name       type   cols  sample
  ------  ---------  -----  ----  --------------------------------------
  dbo     customers  table     5  SELECT TOP 5 * FROM [dbo].[customers];
  dbo     orders     table     6  SELECT TOP 5 * FROM [dbo].[orders];
  2 row(s)
```

| Command | What it does |
|---|---|
| `:tables` | Mounted tables and views, with a sample query for each |
| `:columns NAME` | Columns and types for one object; accepts `schema.name` |
| `:log [n\|follow]` | The last n log lines (20 by default), or stream them until a key |
| `:wide` | One row per record, for tables too wide for a grid |
| `:rows N` | Rows per page; `0` fits the window |
| `:pbi` | Open Power BI Desktop, connected to this server (Windows); see [Connecting](#connecting) |
| `:help` | The list |
| `:quit` | Stop the server and exit |

A statement runs when a line ends with `;`, or on a blank line if you forgot one. The listener
keeps serving throughout — clients connect, query and disconnect while you sit at the prompt.

### Why the log goes quiet

A plain listener prints every session and every translated statement as it happens, which is the
best thing about watching it run. At a prompt it is the worst: a line from another client arrives
mid-keystroke and moves your cursor. So the prompt takes `Console.Out` and buffers it, and `:log`
hands it back:

```
daffydb> :log 4

  [duckdb] T-SQL : SELECT name FROM customers ORDER BY name
  [duckdb] DuckDB: SELECT name FROM customers ORDER BY name
  [s1] -> 1 result set(s): 8x1
  [serve] session 1 closed
  4 of 12 line(s)
```

Nothing is lost, only deferred — and startup output still prints immediately, because the
connection string and the mount list are what tell you the server is usable. `--quiet` therefore
means nothing at a prompt and applies only to the plain listener.

### Paging

Results are capped at a screenful and then wait:

```
  rows 1-38 of 1,247       [space] next page       [q] stop, back to prompt
```

There is deliberately no "show everything" and no export. A fifty-thousand row dump helps nobody,
and anything that genuinely wants the whole set should connect a real client to the listener,
which is what the listener is for. Note that paging protects the screen and not memory: a result
set is fully materialised before the first page prints, exactly as it is for a client, so `TOP` is
the real answer to a huge table.

Columns are fitted to the terminal and the overflow is reported rather than wrapped; `:wide`
shows everything one field per line. There is no command history.

### Turning it off

```sh
daffydb --no-repl        # just listen
```

This is also automatic whenever output is redirected, so `daffydb > server.log`, a pipe, a
container or a CI step behaves exactly as it always has. `--repl` forces the prompt on anyway,
which is how the verification script drives it.

One thing worth knowing: the prompt runs your SQL **in process**, straight through
`IQueryHandler`, rather than opening a TDS connection to itself. Same translation, same DuckDB,
same log lines — but it does not exercise the wire encoding, so the probes remain the thing that
proves the protocol.

## Connecting

Use `tcp:127.0.0.1` rather than `localhost` or `.`, so the client does not quietly pick shared
memory or named pipes and never reach the listener. `Pooling=False` avoids a reused connection
sending `sp_reset_connection`, which is not implemented.

**Any credentials** — the default, and what most of this README assumes. Encryption is on and
this needs no setup at either end:

```
Server=tcp:127.0.0.1,1433;Encrypt=Mandatory;TrustServerCertificate=True;Pooling=False;User Id=any;Password=any
```

**With `--user alice --password "s3cret pass!"`:**

```
Server=tcp:127.0.0.1,1433;Encrypt=Mandatory;TrustServerCertificate=True;Pooling=False;User Id=alice;Password=s3cret pass!
```

**From JDBC**, which is what DBeaver sends. `encrypt=true` is the JDBC spelling of Mandatory:

```
jdbc:sqlserver://127.0.0.1:1433;encrypt=true;trustServerCertificate=true;user=any;password=any
```

**With `Encrypt=Strict`**, where `ServerCertificate` points at the `.cer` the proxy wrote next to
its `.pfx`:

```
Server=tcp:127.0.0.1,1433;Encrypt=Strict;ServerCertificate=certs\tds-proxy.cer;Pooling=False;User Id=any;Password=any
```

**Plaintext**, which only applies under `--no-cert` or when the certificate could not be written:

```
Server=tcp:127.0.0.1,1433;Encrypt=False;TrustServerCertificate=True;Pooling=False;User Id=any;Password=any
```

**Encrypted, with a certificate kept elsewhere** (`--cert-file C:\temp\tdsproxy.pfx`):

```
Server=tcp:127.0.0.1,1433;Encrypt=Strict;ServerCertificate=C:\temp\tdsproxy.cer;Pooling=False;User Id=any;Password=any
```

**A different port** (`--port 14450`):

```
Server=tcp:127.0.0.1,14450;Encrypt=Mandatory;TrustServerCertificate=True;Pooling=False;User Id=any;Password=any
```

**From Power BI**, with Get Data → SQL Server: server `127.0.0.1` (`127.0.0.1,14450` on another
port), database empty or `daffydb`. Not `localhost`, which does not reach it. The certificate has to be trusted first, because this
connector checks it; see [Encryption, which is already on](#encryption-which-is-already-on).
Power BI's ODBC and OLE DB connectors work too, through ODBC Driver 17/18 and MSOLEDBSQL19, and
need nothing trusted.

**Or have DaffyDB open Power BI**, on Windows. At startup it writes a `.pbids` file, Power BI's own
connection shortcut, to `%LOCALAPPDATA%\DaffyDB\daffydb.pbids` (`daffydb-PORT.pbids` on another
port, so two servers never share one), and prints its path in the banner. Double-click it, or type
`:pbi` at the prompt, and Power BI Desktop opens with the SQL Server connection filled in, in
Import mode, straight to the navigator. `:pbi` writes the file again first and checks the
certificate: if Windows does not trust it, it says so and offers to, because the connector would
refuse it. The file carries no credentials, so Power BI asks the first time. Choose **Database**,
then any username and password, or the `--user` and `--password` values.

**From `sqlcmd`**, plaintext:

```sh
sqlcmd -S tcp:127.0.0.1,1433 -U any -P any -C -Q "SELECT TOP 3 name FROM customers ORDER BY name"
```

One that looks reasonable and does **not** work:

```
Encrypt=Strict;TrustServerCertificate=True             # ignored under Strict — see Encryption
```

That is the one that will catch you out; [Encryption](#encryption) explains why, and why the same
string works perfectly with `Encrypt=Mandatory`.

### From SQL Server, as a linked server

SQL Server reads DaffyDB through MSDASQL, its OLE DB provider for ODBC, and ODBC Driver 18 for SQL
Server, which has to be installed on the SQL Server's machine. DaffyDB listens on 127.0.0.1 only,
so that is also DaffyDB's machine. On the SQL Server:

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

SELECT TOP 5 * FROM DaffyDB.daffydb.dbo.customers;
SELECT * FROM OPENQUERY(DaffyDB, 'select city, count(*) as n from customers group by city');
```

`@rmtuser` and `@rmtpassword` are the `--user` and `--password` DaffyDB was started with, or
anything when it was started without. A four-part name and `OPENQUERY` both read, and they can be
joined to local tables; writing through the linked server is not supported.

What SQL Server does with a four-part name explains the rest. It looks the table up with
`sp_tables`, reads its columns from `sp_columns_100`, opens a cursor on it only to see the same
columns again, asks `sp_statistics_100` about indexes, and reads one row under `SET ROWCOUNT 1`.
Then it runs its own version of the query, prepared, with `sp_prepexec`. `OPENQUERY` describes the
query with `sp_prepare` first. Either way, the columns it is sent when the query runs must be what
it was told beforehand, down to each column's length, or it stops with "supplied inconsistent
metadata for a column". That is why a text column is always `nvarchar(4000)`, in the catalog and in
results alike, rather than as wide as the longest value in hand.

Two kinds of value cannot come through, because MSDASQL cannot read them: text longer than 4000
characters, which goes out as `nvarchar(max)`, and `time`. Either fails the query, with "returned
data that does not match expected data length". SQL Server cannot read them from another SQL
Server through this provider either. In an `OPENQUERY`, `LEFT(note, 4000)` and
`CAST(opens_at AS varchar(16))` read them as shorter text.

The `MSOLEDBSQL` provider does not work. Through it, SQL Server asks for Multiple Active Result
Sets, which DaffyDB does not support, and the connection fails with "Client unable to establish
connection" and "MARS was specified on the connection but is not supported by the server".

## Checking it works

The probe projects are under `src/` and the verification scripts, one per feature, under
`scripts/`; each script's header says what it checks and what it needs. The quickest smoke test:

```sh
dotnet run --project src/TdsProbe -- --sql "SELECT TOP 3 name FROM customers ORDER BY name"
```

## Where your code goes

Everything the proxy extracts arrives at one interface, in `src/TdsProxy/Query/Seam.cs`:

```csharp
public interface IQueryHandler
{
    Task<QueryResult> ExecuteAsync(SqlRequest request, CancellationToken cancellationToken);
}
```

`SqlRequest` carries the SQL text and, for parameterized commands, each parameter's name, declared
type (`int`, `nvarchar(50)`, ...) and CLR value. Return a `QueryResult` — zero, one, or several
`ResultSet`s — and it gets encoded back to the client. `DuckDbQueryHandler` and `EchoQueryHandler`
both implement it; picking one is a single line in `Program.cs`.

Supported column types are `Int`, `BigInt`, `TinyInt`, `SmallInt`, `Real`, `Double`, `Decimal`,
`Bit`, `Date`, `Time`, `DateTime2`, `DateTime`, `NVarChar`, `VarChar`, `VarBinary` and
`UniqueIdentifier`, including NULLs. Adding more means extending `SqlColumnType` and the two
switches in `ResponseWriter` — read finding 9 in the recipe first, it is the one that silently
corrupts the stream.

Each DuckDB type goes out as the nearest SQL Server type, and the catalog reports the same one, so
a client that reads a table's columns and then its rows sees one type, not two:

| DuckDB | Sent as |
|---|---|
| `BOOLEAN` | `bit` |
| `UTINYINT` | `tinyint` (SQL Server's is unsigned, so a signed `TINYINT` cannot use it) |
| `TINYINT`, `USMALLINT`, `INTEGER` | `int` |
| `SMALLINT` | `smallint` |
| `UINTEGER`, `BIGINT` | `bigint` |
| `UBIGINT`, `HUGEINT` | `decimal(20,0)`, `decimal(38,0)` |
| `FLOAT`, `DOUBLE` | `real`, `float` |
| `DECIMAL(p,s)` | `decimal(p,s)` |
| `DATE`, `TIME`, `TIMESTAMP` of any precision | `date`, `time`, `datetime2` |
| `VARCHAR` | `nvarchar(4000)`, becoming `nvarchar(max)` when a value passes 4000 characters |
| `BLOB` | `varbinary(8000)`, becoming `varbinary(max)` when a value passes 8000 bytes |
| `UUID` | `uniqueidentifier` |
| anything else - lists, structs, intervals | `nvarchar(4000)`, spelled out as text |

A DuckDB `VARCHAR` has no length, so DaffyDB gives every text column the widest one short of
`(max)`, the same in the catalog and in every result, whatever the rows hold. A client that is told
a column's length and then sent its rows - a linked server is one - must see the same length both
times. A column only becomes `(max)` when a value needs it, so a long value still arrives whole.
`VarChar` and `DateTime` are never chosen for data; they exist so that a system procedure can
answer in precisely the types SQL Server uses, which ODBC and OLE DB check.

## The data

`--seed` writes two parquet files that DuckDB mounts as views (plus a `dbo` schema, so `dbo.orders`
resolves too):

| customers (8 rows) | orders (24 rows) |
|---|---|
| `customer_id` INTEGER | `order_id` INTEGER |
| `name` VARCHAR | `customer_id` INTEGER |
| `city` VARCHAR (one NULL) | `product` VARCHAR |
| `is_active` BOOLEAN | `quantity` INTEGER |
| `signup_date` DATE | `unit_price` DECIMAL(10,2) |
| | `order_ts` TIMESTAMP |

Contents are fixed rather than random, so examples can quote exact numbers. One customer
deliberately has no orders, and one has no city.

## Batches, variables and multiple result sets

A batch is run one statement at a time, and every statement that returns columns contributes a
result set:

```sql
DECLARE @city nvarchar(50);
SET @city = 'London';
SELECT [name], signup_date FROM customers WHERE city = @city;
SELECT COUNT(*) AS total FROM orders
```

`DECLARE` and `SET` of a variable are handled by the proxy rather than sent to DuckDB, which has no
notion of `@`-prefixed batch variables. A declared type is applied as a CAST, so
`DECLARE @n int = '42'` yields the integer 42. Variable references resolve through the same path as
RPC parameters — the translator rewrites `@name` to `$1`, `$2`, ... and the value is bound, so
nothing is ever spliced into SQL text as a literal.

Statements are separated by semicolons. Because T-SQL does not require them, a newline also ends a
statement when what came before it is a `DECLARE` or a `SET` of a variable and the next line opens a
new statement — enough for the usual `DECLARE` / `SET` / `SELECT` shape without breaking a
multi-line `SELECT` or a `UNION` whose second arm starts a line.

`IF`/`ELSE`, `BEGIN...END` blocks, `SELECT @a = ... FROM ...` and `EXEC('some sql')` all work. Only
the taken branch of an `IF` runs, so only its result sets come back.

A `SELECT` assignment keeps its select-list prefix: `SELECT TOP 1 @best = name FROM ... ORDER BY ...`
is an assignment, not a comparison, and the `TOP 1` is carried into the query that runs. T-SQL
assigns once per row, so the value that survives is the one from the last row — which is why that
`TOP 1` with an `ORDER BY` is how you ask for a particular one. A query returning no rows leaves the
variable as it was.

`EXEC` executes its argument as a batch of its own, sharing the same variables, nested up to 16
deep:

```sql
DECLARE @city nvarchar(50), @sql nvarchar(200);
SELECT @city = city FROM customers WHERE customer_id = 1;
SET @sql = 'SELECT ''' + @city + ''' AS chosen_city';
EXEC (@sql)
```

Temp tables work: `#name` becomes a quoted DuckDB temp table, `CREATE`/`DROP` are made idempotent,
and `INSERT #t EXEC some_proc` is supported for the system procedures below. `WHILE` is not
implemented.

## Errors, TRY and CATCH

`BEGIN TRY` / `BEGIN CATCH` behaves as it does in SQL Server: the first error abandons the rest of
the `TRY` block and runs the handler, while result sets the `TRY` block already produced are kept.
`THROW` and `RAISERROR('message', severity, state)` raise one deliberately, a bare `THROW` inside a
`CATCH` rethrows, and anything uncaught reaches the client as an ERROR token — a real `SqlException`
with the number, severity and message the batch chose.

```sql
BEGIN TRY
    SELECT COUNT(*) AS ran_first FROM orders;
    SELECT * FROM no_such_table;
END TRY
BEGIN CATCH
    SELECT ERROR_NUMBER() AS number, ERROR_LINE() AS line, ERROR_MESSAGE() AS message;
END CATCH
```

The whole `ERROR_*` family — `ERROR_NUMBER`, `ERROR_MESSAGE`, `ERROR_SEVERITY`, `ERROR_STATE`,
`ERROR_LINE`, `ERROR_PROCEDURE` — reads the error being handled, and answers NULL outside a `CATCH`
block. `ERROR_LINE` is real: the parser records the line each statement started on.

Two honest limits. There is no equivalent of SQL Server's message catalogue, so everything DuckDB
raises is reported as error 50000 with severity 16 — only a number you chose yourself survives. And
`1 / 0` answers NULL in DuckDB rather than raising 8134, so divide-by-zero is not a way to reach a
`CATCH` block.

Two places where T-SQL is looser than DuckDB are smoothed over, because client tools rely on it:
a `VALUES` column mixing string literals with anything else is unified as text, and an
`INSERT ... SELECT` is converted into the target's own column types with `TRY_CAST` (T-SQL would
land `''` in an int column as 0; here it becomes NULL).

## Loading your own data

Point `--data-dir` at a folder and every `.parquet`, `.csv` and `.json` file under it becomes a
table, and every sheet of every `.xlsx` (see [Excel workbooks](#excel-workbooks)). The file name
decides where it lands, which means an export straight out of another tool usually needs no
configuration at all:

| File | Becomes |
|---|---|
| `orders.parquet` | `daffydb.dbo.orders` |
| `Sales.Customer.parquet` | `daffydb.Sales.Customer` |
| `Sales/Customer.parquet` | `daffydb.Sales.Customer` |
| `products.csv` | `daffydb.dbo.products` |
| `Sales/regions.csv` | `daffydb.Sales.regions` |
| `events.json` | `daffydb.dbo.events` |
| `archive.csv.gz` | `daffydb.dbo.archive` |
| `inventory/part-00000.parquet`, `part-00001.parquet`, ... | `daffydb.dbo.inventory` |
| `Sales/Orders/data_0.parquet`, `data_1.parquet`, ... | `daffydb.Sales.Orders` |
| `Budget.xlsx`, sheet `Q1` | `daffydb.Budget.Q1` |

A CSV mounts by exactly the same rules, with DuckDB sniffing its column types from the file, and
so does JSON. JSON can be one array of objects or one object per line, under any of `.json`,
`.jsonl` and `.ndjson`; a nested object or array comes back as text, since TDS has no type for it.
Any of these can be gzipped - `.csv.gz`, `.json.gz`, `.jsonl.gz`, `.ndjson.gz` - and the whole
extension comes off the name. All of it is built into DuckDB, so a folder scan never downloads an
extension.

Where a folder holds both `orders.parquet` and `orders.csv` the two claim the same name and the
parquet wins, because it carries its own schema rather than a guess at one; the CSV is named in a
warning and skipped. A CSV wins over a JSON file the same way. The loser is skipped rather than
mounted as `orders2`: a numbered name would depend on the order files happen to be found in, and a
report bound to one would quietly change when a file was added.

A file that cannot be read at all — junk with a `.parquet` extension, a CSV whose dialect DuckDB
cannot work out, JSON that does not parse, a `.gz` that is not gzip — costs that one table and
nothing else. It is reported and skipped, and the server still comes up with everything that did
load. That is the opposite of how `--init` treats a failure, on purpose: a script was written for
this server, while a folder is just a folder.

### Folders of parts

A table too big for one file is usually written as a folder of pieces, and the scan reads such a
folder as one table, named after the folder by the same rules as a file: `inventory/` is
`dbo.inventory`, `Sales/Orders/` is `Sales.Orders`, and `Sales.Orders/` is too. A folder counts as
parts when every data file in it is parquet and either:

- every one is named as a part: `part-00000.parquet` and on, as sqlcarbon, Spark, Hadoop and
  pyarrow write them (Spark's longer `part-00000-<id>-c000.snappy.parquet` and Hadoop's
  `part-r-00000.parquet` included), or `data_0.parquet` and on, as DuckDB's own `COPY` writes them;
  `part-` has to be followed by a number, so `part-numbers.parquet` is still a table of its own, or
- the folder holds a `_SUCCESS` file, the empty marker Spark, Hadoop and sqlcarbon write once an
  export has finished.

A folder that mixes parts with other files is read file by file, as it always was, because
nothing says which reading is meant. The view reads the folder through a glob, so a re-export that
writes more parts or fewer is picked up by the next query without a restart, just as a single
file is read as it now stands. (A folder whose name holds a glob character - `[`, `]`, `*` or `?` -
is read from the list of parts found at startup instead, because DuckDB cannot escape one.)

A table exported once as a single file and again as a folder, or the other way round, leaves both
behind under one name. The newer of the two is served - a folder is as new as its newest part -
and the other is named in a warning and skipped.

[sqlcarbon](https://pypi.org/project/sqlcarbon/), also from TroBeeOne, writes exactly this: it
copies SQL Server tables, to another SQL Server or to parquet, as a YAML file sets out, writing a
small table as one file and a large one as a folder of parts. Point `--data-dir` at its output
folder and every table is served under the name it was exported with.

### What the scan will not look at

- **Anything more than two folders down.** That is deep enough for a folder of parts inside a
  schema folder, `Sales/Orders/part-00000.parquet`, and a deeper tree is usually partitioned data
  (`year=2024/month=01/part-0.parquet`) that one glob in an `--init` script mounts as a single
  table, which is what it is.
- **`bin`, `obj`, `node_modules`**, and folders that are hidden or start with a dot (`.git`,
  `.vs`, `.vscode`). Build output holds copies of whatever a project ships, which would otherwise
  mount twice. This applies below `--data-dir` only; pointed straight at a folder named `bin`, it
  is read.
- **`appsettings.json`, `appsettings.*.json`, `launchSettings.json`, `package.json` and
  `package-lock.json`.** JSON is a configuration format as much as a data one, and these are the
  files most likely to sit beside data.
- **Folders it is not allowed to open.** They are passed over instead of stopping the server.

And a folder with **more than 500 data files is not mounted at all**, a folder of parts counting
as one however many parts it has. The log says how many it found and why nothing was mounted:

```
[duckdb] warning: found 4,312 data files under C:\Users\me, more than the 500 a folder scan will mount; nothing was mounted from it
[duckdb]          point --data-dir at a narrower folder, or mount what you need with --init
```

That many files almost always means the server was started in the wrong folder, and mounting the
first 500 would be worse than mounting none: a server quietly missing tables, chosen by nothing
more than how the names sort.

### Excel workbooks

An `.xlsx` or `.xlsm` workbook becomes a schema, and each of its sheets a table:

```
[duckdb] loaded  Budget.Q1               <- C:/data/Budget.xlsx [Q1], 600 row(s)
[duckdb] loaded  Budget.Summary Q1       <- C:/data/Budget.xlsx [Summary Q1], 2 row(s)
[duckdb] note: C:/data/Budget.xlsx [Report] has values to the right of its header row, so it does not look like a table; skipped
```

The workbook is always the schema, even with one sheet, so adding a sheet never renames the ones
already there. Folders and dots play no part: `Finance/Plan.2024.xlsx` is `[Plan.2024]`. Sheet
names need no cleaning up, because Excel already forbids the characters that would break a
bracketed name (`[ ] : * ? / \`), and they are at most 31 characters long.

Unlike every other kind of file, a workbook is **copied**, not read in place. Its sheets are
loaded into DuckDB tables at startup and queried from memory, so:

- a workbook saved while the server runs shows its changes after a restart;
- the file is never written to, and the copy is gone when the server stops;
- a workbook that is open in Excel still loads - the file is opened sharing writes, which is what
  Excel's own handle demands of anyone else.

The copy is the price of typing columns properly. DuckDB can read a workbook, but only through its
`excel` extension - a download of about 23 MB for each DuckDB version - and `read_xlsx` types every
column from the first data row alone. A column that turns to text at row 301 fails the whole read,
and one loaded into a table typed from a 500-row sample has its row-560 `12.5` rounded to `13`
and its row-590 `13:30` dropped, silently. So the workbook is read with
[Sylvan.Data.Excel](https://www.nuget.org/packages/Sylvan.Data.Excel) (MIT) instead, twice per sheet: once
to see the type of every cell, once to append the rows through DuckDB's appender into a table typed
from all of them.

| Every value in the column is | Column type |
|---|---|
| a whole number (within the 2^53 a double holds exactly) | `bigint` |
| a number, at least one fractional | `float` |
| a date with no time of day | `date` |
| a date, at least one with a time | `datetime2` |
| a time of day (a time format, under one day) | `time` |
| TRUE or FALSE | `bit` |
| anything else, or a mixture | `nvarchar`, each value as it reads in Excel |

Excel keeps dates as numbers, so the cell's format is what says a number is a date. Error values
such as `#N/A` are NULL, whitespace-only text is NULL, and a formula gives the value Excel last
saved with it - a workbook written by a tool that never calculated it has none, and reads NULL.

What makes a sheet a table:

- **The first row with anything in it is the header**, if it is all text. Blank rows elsewhere
  are skipped, and rows hidden by a filter are loaded - they are still data.
- **If that row is not all text, it is data**, and the columns are named `column_A`,
  `column_B`, and so on, by their letter in Excel.
- **A blank header cell** is named the same way, `column_D`; **a repeated one** gets its column
  added, `amount_column_E`; **a wrapped one** (Alt+Enter) has its line break made a space.
- **A column with neither header nor values** - the space to the left of a table that starts in
  column C - is not a column.

Left out, and why:

- **Hidden sheets**, as hidden folders are. **Chart sheets** are not worksheets at all.
- **Empty sheets**, silently: new workbooks come with them.
- **Sheets with values to the right of the header row** - a title above the table, notes beside
  it, a report laid out on a grid. The header would be the title and the table would be garbage,
  so the sheet is named in a note and skipped instead.
- **A sheet that fails partway through** is dropped whole rather than left half-loaded, and named
  in a warning. **A workbook that cannot be opened** - a password-protected one, or a file that
  is not a workbook at all - costs itself and nothing else.
- **Excel's lock file**, `~$Budget.xlsx`, which sits beside an open workbook and is not one.
- **Legacy `.xls`**.

If a sheet needs something these rules do not do - a header on row 5, one range out of a larger
grid - DuckDB's own reader is still available from an `--init` script, where `read_xlsx` takes a
`range`:

```sql
INSTALL excel;
LOAD excel;
CREATE VIEW sales AS SELECT * FROM read_xlsx('C:/data/report.xlsx', sheet = 'Sheet1', range = 'A5:F200');
```

Schemas are created as needed, and nothing else has to know: `sys.schemas` and `sys.objects` are
views over DuckDB's own metadata, so whatever is mounted shows up in a client's object tree on its
own.

This is a flag rather than a line of SQL because plain SQL cannot express it. DuckDB's `glob()`
can list the files, but there is no loop to turn N paths into N views, and
`read_parquet('dir/*.parquet')` unions them into one table instead of keeping them apart. That
union is right for a folder of parts, which is why the scan uses it there, and wrong for a folder
of different tables.

For anything beyond a folder of files, `--init` runs a DuckDB SQL script at startup:

```sql
INSTALL httpfs;
LOAD httpfs;
CREATE SECRET (TYPE s3, PROVIDER credential_chain);

CREATE VIEW events AS SELECT * FROM read_parquet('s3://bucket/events/**/*.parquet');
```

That is DuckDB's dialect, not T-SQL, deliberately: anything in DuckDB's documentation pastes in
unchanged, and you can test the file by feeding it to the DuckDB CLI. A statement that fails stops
the server and names itself — a server silently missing half its tables is worse than one that did
not come up. Prefer `PROVIDER credential_chain` over literal keys, so credentials stay out of a
file that ends up in git.

**Keep a database password out of `ATTACH`.** DuckDB remembers an attached database's connection
string as written, and any client can read it back with `SELECT path FROM duckdb_databases()`. Put
the password in a secret, which DuckDB never shows, and leave it out of the string:

```sql
CREATE SECRET shop_pw (TYPE postgres, PASSWORD 'p@ss');
ATTACH 'host=localhost port=5432 dbname=shop user=reader' AS shop (TYPE postgres, SECRET shop_pw, READ_ONLY);
```

The two are combined when DuckDB connects, and `duckdb_secrets()` shows `password=redacted`.

## DaffyDB's own commands

Some questions are about DaffyDB rather than the data: what is mounted, from where, and how the
server was started. DaffyDB answers these itself, as stored procedures in a `daffydb` schema:

```sql
EXEC daffydb.help                    -- every command; daffydb.help 'mount' for one in full
EXEC daffydb.status                  -- version, port, encryption, logins, sessions, mounts, flags
EXEC daffydb.tables                  -- every table and view, and where each one comes from
```

Those three are for everyone. The rest change what is served, write files or run T-SQL elsewhere, and
a client may run them only when the server was started with `--allow-admin`:

```sql
EXEC daffydb.mount 'D:\exports\2025'                      -- another folder, added to what is served
EXEC daffydb.mount 'D:\exports\orders.parquet', 'staging' -- one file, in a schema of your choosing
EXEC daffydb.postgres 'host=localhost dbname=shop user=reader password=...'   -- a PostgreSQL database
EXEC daffydb.mysql 'host=127.0.0.1 database=shop user=reader password=...'    -- a MySQL or MariaDB one
EXEC daffydb.sqlserver 'Server=sql01;Database=shop;User Id=reader;Password=...' -- a SQL Server or Azure SQL one
EXEC daffydb.sqlite 'D:\data\chinook.db'                  -- a SQLite file
EXEC daffydb.duckdb 'D:\data\warehouse.duckdb'            -- a DuckDB file
EXEC daffydb.unmount 'staging'                            -- take a mount away again
EXEC daffydb.save_parquet 'sales.orders', 'D:\exports'    -- one table, to sales.orders.parquet
EXEC daffydb.save_parquet_all 'D:\exports\2025-10-20'     -- every table, view and temp table
EXEC daffydb.run_sqlserver 'shop', N'EXEC dbo.GetOrders'  -- T-SQL on a mounted SQL Server
```

Everything they mount is read-only, and lasts until the server stops; to have it at every start,
put it in `--data-dir` or an `--init` script. The saves write new files and never replace one.
`daffydb.run_sqlserver` does whatever its T-SQL does, as far as the mount's login may.

### Folders and files

**`daffydb.mount`** reads a folder exactly as `--data-dir` reads one, by the rules in
[Loading your own data](#loading-your-own-data): every data file two folders deep, a dotted name or
a folder for a schema, a folder of parts as one table, a workbook as a schema of sheets. A single
file becomes a single table — a `part-00000.parquet` named on its own is that one file — and the
path is on the machine DaffyDB runs on. Tables are views over the files, as at startup, and
workbook sheets are copies.

Mounting the same folder into the same schema again reads it again: files that appeared are
mounted, files that went are taken away. The `--data-dir` folder counts, so mounting it again picks
up a file added since startup.

### Databases

`daffydb.postgres`, `daffydb.mysql`, `daffydb.sqlite` and `daffydb.duckdb` attach a database
read-only and make a view over each of its tables and views. Nothing is copied: every query reads
the database as it is at that moment, through DuckDB's own scanner for it, and nothing is ever
written to it. The T-SQL you write is translated just as it is for
files.

```sql
EXEC daffydb.postgres 'host=localhost port=5432 dbname=shop user=reader password=...'
EXEC daffydb.postgres 'postgresql://reader@localhost/shop', 'sales.orders'   -- just one table
EXEC daffydb.mysql 'host=127.0.0.1 database=shop user=reader', @schema = 'shop'
EXEC daffydb.duckdb 'D:\data\2024.duckdb', @schema = 'y2024'
```

- **The connection** is libpq's `key=value` form for PostgreSQL (`dbname` is required), and the same
  form with `database=` for MySQL; either also takes a `postgresql://` or `mysql://` URL. Leave the
  password out and PostgreSQL's `PGPASSWORD` or `pgpass.conf`, or MySQL's `MYSQL_PWD`, on this
  machine is used instead. SQLite and DuckDB take a file path, which must exist.
- **Schemas** stay as the database has them, and its default one becomes `dbo`: PostgreSQL's
  `public`, SQLite's and DuckDB's `main`, MySQL's database itself. So a PostgreSQL `sales.orders`
  is `sales.orders` here, and `public.customers` is `dbo.customers`.
- **All or one.** Without `@table`, every table and view is mounted, replacing what that mount
  held before. `@table` names one — `orders`, or `sales.orders` — and adds it to the mount. A table
  DuckDB cannot read, such as a view of the database's own that refers to something gone, is left
  out with a message rather than costing the rest; named with `@table`, it is an error.
- **A DuckDB file** is opened read-only, and while it is mounted no other program can open it to
  write. Unmounting lets it go. Mount as many as you like, each with its own command.
- **Passwords go in a DuckDB secret,** never in the attached connection string. DuckDB keeps that
  string as written, and any client can read it back with `SELECT path FROM duckdb_databases()`;
  the secret it never shows.

**Extensions download on first use.** PostgreSQL, MySQL and SQLite each need one of DuckDB's
extensions, 24 to 28 MB apiece on Windows, which are not part of DaffyDB's package. The first
`daffydb.postgres` downloads DuckDB's postgres extension from DuckDB's own repository, signed, into
`%USERPROFILE%\.duckdb\extensions\v1.5.5\windows_amd64` (`~/.duckdb/extensions/...` elsewhere), and
says so in its messages; every later one, and every later run, loads it from there. Nothing is
downloaded until a command needs it, and DuckDB files need nothing at all. On a machine without
internet access the command fails with error 50305, naming the file to put in that folder by hand.
A new DuckDB version, when DaffyDB moves to one, downloads each extension once more.

One thing is set on DaffyDB's behalf: the MySQL scanner is told not to hand `COUNT` and `SUM` to
MySQL, because through a view — which every mounted table is — DuckDB 1.5.5 fails with an internal
error when it does. Filters and `ORDER BY` still go to MySQL; aggregates are done by DuckDB.

### SQL Server

`daffydb.sqlserver` mounts a SQL Server or Azure SQL database the same way: a view over each of its
tables and views, read live, nothing copied and nothing written. DuckDB has no SQL Server scanner
of its own, so DaffyDB brings one, DaffySQL (`src/DaffySQL`): its own TDS client, built into
DaffyDB, so nothing downloads and nothing native is loaded.

```sql
EXEC daffydb.sqlserver 'Server=tcp:sql01;Database=shop;User Id=reader;Password=...'
EXEC daffydb.sqlserver 'Server=sql01,1433;Database=shop;User Id=reader;Password=...', 'sales.orders'   -- just one table
EXEC daffydb.sqlserver 'Server=tcp:myserver.database.windows.net;Database=shop;Authentication=ActiveDirectoryServicePrincipal;User Id=<app id>;Password=<client secret>', @schema = 'azure'
```

- **The connection** is a SQL Server connection string, as SqlClient takes one, and `Database` is
  required. A keyword DaffyDB has no use for is refused by name rather than ignored.
  - `Encrypt` is `Mandatory` (TDS 7.4) unless it says `Optional`, or `Strict` (TDS 8.0, SQL Server
    2022 and later). The server's certificate is checked against this machine's trusted roots
    unless the string says `TrustServerCertificate=True`; `HostNameInCertificate` names the one to
    expect.
  - A SQL login and its password, or `Authentication=ActiveDirectoryServicePrincipal` for an Entra
    ID application: its client id as `User Id` (`id@tenant` where the server does not name its
    tenant, which Azure SQL always does), a client secret as `Password`. The secret goes only to
    the Microsoft sign-in address the server names, for a token, and never to the server itself.
  - `Lock Timeout` is how many seconds a read waits behind another session's locks before it fails
    with SQL Server's error 1222: 30 unless given, `0` for no limit, and as long as you like
    otherwise (`Lock Timeout=300`). Below.
  - `Connect Timeout`, `Application Name` and `Packet Size` mean what they do to SqlClient.
  - Not yet: Windows logins, a named instance (give its port instead: `Server=sql01,50123`), and
    Entra ID's other ways of logging in.
- **Schemas** stay as SQL Server has them: `dbo.orders` is `dbo.orders` here, and `sales.regions`
  is `sales.regions`. `@schema` puts them all in one.
- **Only the columns and rows a query wants.** A query asks SQL Server for only the columns it
  uses, and sends it the conditions it can apply as DuckDB would, so only the rows they keep cross
  the network: `SELECT name FROM dbo.customers WHERE id = 3` asks for `[name]` and `[id]` - DuckDB
  still checks `id` itself - `FROM [dbo].[customers] WHERE ([id] = 3)`. Below, what is sent and
  what is not.
- **The catalog never asks SQL Server.** Object Explorer, `sys.columns` and the rest are answered
  from the columns recorded when the table was mounted. A query asks twice: once for the table's
  columns as it is planned, and once for its rows. A column that changed on SQL Server shows in
  the catalog after the table's next query; a table that came or went, after mounting again.
- **Types** arrive as DaffyDB sends them, and the catalog says the same: `money` and `smallmoney` as
  `decimal(19,4)` and `decimal(10,4)`; `datetime` and `smalldatetime` as `datetime2`;
  `datetimeoffset` as `datetime2` in UTC, the instant without its offset; `time(7)` and
  `datetime2(7)` to the microsecond; `xml` and `sql_variant` as text; `geography`, `geometry`,
  `hierarchyid` and `rowversion` as `varbinary`.
- **A stuck read stops.** Under `READ COMMITTED` a read waits behind a writer's locks, and DaffyDB
  answers one query at a time, so a read waiting on a lock holds up every client. `Lock Timeout`
  ends the wait. A cancel - SSMS's Cancel button, a client's command timeout - reaches SQL Server
  at once, whatever the read is waiting on, and `SET ROWCOUNT` stops SQL Server as soon as the rows
  wanted have arrived, rather than leaving it sending the rest.
- **Sessions.** A mount keeps up to eight logged-in sessions for its queries to take turns on, reset
  between one query and the next so that temp tables and `SET` options never carry over.
  Unmounting closes them.
- **The password** stays in DaffyDB's memory. It never reaches DuckDB, so there is no secret to
  make, and the log masks it as it masks any other.
- **Mount with a login that can only read.** A mounted table only ever reads, but a client that can query
  DaffyDB can reach whatever the mount's login can: `daffy_scan`, the function every mounted table
  reads through, takes any table on that server, mounted or not, and a T-SQL condition of the
  client's own as its `filter`. That is held to one condition - one that closes the parenthesis it
  is sent in, ends the statement or comments the rest away is refused - but a condition can still
  hold a subquery of any table the login can read. A login with `db_datareader`, or `SELECT` on
  just what you mean to share, is the limit. What a client cannot do is name servers of its own,
  or point a mount somewhere else: DaffyDB gives DuckDB only the DaffySQL functions that read.
  With `--allow-admin` a client can also send T-SQL of its own, as that login: [below](#running-t-sql-on-it).

**Which conditions go to SQL Server.** The query keeps every condition it had, and DuckDB applies
them all to what arrives; what goes to SQL Server is a copy, which can only save rows on the wire.
So a condition is sent only where SQL Server keeps at least every row DuckDB would, and DaffyDB's
log says what was sent, query by query (`[sqlserver] sent to SQL Server, dbo.orders o: ...`).

- **Its shape:** a column of one mounted table against constants - `=`, `<>`, `<`, `>`, `<=`, `>=`,
  `BETWEEN`, `IN`, `NOT IN` - `IS [NOT] NULL`, and `AND` and `OR` of those, in the `WHERE` of a
  plain `SELECT`, or the `ON` of a join. A function, a subquery, a parameter, another table's column,
  a CTE: that condition stays with DuckDB, and the others still go.
- **Joins:** a `WHERE` condition goes to a table only if no outer join can bring it back as NULLs.
  In `customers c LEFT JOIN orders o ... WHERE o.id IS NULL` - customers with no orders - sending
  `o.id IS NULL` to SQL Server would leave every customer without a match, so it is not. A
  `LEFT JOIN`'s `ON` conditions go to its right-hand table, an inner join's to either side. A table
  joined to itself gets one condition for each time it is named.
- **Types:** numbers, `bit`, `date`, `datetime2`, `smalldatetime` and `uniqueidentifier` compare
  exactly. `datetime` holds 1/300ths of a second, so its conditions go 4 ms wider and DuckDB cuts
  the rest. Text goes only with `=` and `IN`, an ASCII constant, and a case-insensitive collation:
  SQL Server then keeps every row DuckDB's case-insensitive comparison does, and more (trailing
  spaces, accents under `_AI`). Text under a case-sensitive or binary collation, `<>` on text,
  `float`, `real`, `time` and `datetimeoffset` are never sent.
- **Constants** are written so SQL Server cannot misread them, whatever its language or date
  format: `CAST('20260101' AS date)`, `CAST('2026-01-02T00:00:00.0000000' AS datetime2(7))`.
- **A table changed on SQL Server** since it was mounted is left alone where a column's type no
  longer matches; mount it again to send its conditions again.

### Running T-SQL on it

`daffydb.run_sqlserver` runs T-SQL on a mounted SQL Server, a stored procedure among them, and
returns what SQL Server returns, through DaffySQL and with no DuckDB in the way:

```sql
EXEC daffydb.run_sqlserver 'shop', N'EXEC dbo.GetOrders @min = 2, @name = N''Ann'''
EXEC daffydb.run_sqlserver 'shop', N'SELECT TOP (5) * FROM dbo.orders; PRINT ''done'''
EXEC daffydb.run_sqlserver @sql = N'DECLARE @o int; EXEC dbo.Totals @o OUTPUT; SELECT @o AS total', @mount = 'shop'
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

It is a command rather than DaffySQL's `daffy_query` table function, which DaffyDB does not register,
for three reasons. A client's SQL reaches every table function DuckDB has, so a registered function
would be open to every client, where a command needs `--allow-admin`. `daffy_query` runs its T-SQL
when DuckDB binds the query, so a driver that prepares a statement would run a procedure once to
learn its columns and again for its rows, where preparing a command does not run it. And DuckDB takes
one table from a function, where a batch may return several, or none, or `PRINT` output and an error
after its rows.

### Another DuckDB, over Quack

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

### Mounts and their names

Every mount has a name, which `daffydb.tables` shows for each of its tables and `daffydb.unmount`
takes:

- a folder's or a file's name, without its extension, or a database's name;
- or, when the command was given `@schema`, that schema — so `daffydb.mount 'D:\exports', 'carbon'`
  is the mount `carbon`, and `daffydb.unmount 'carbon'` takes it away.

A number is added if another mount already has the name (`carbon_2`). The `--data-dir` folder is a
mount like the rest, named after its folder. `daffydb.unmount` goes by the mount, never by a
schema's name, so other mounts' tables in the same schema stay where they are; it drops the
mount's tables, and any schema it made that is left empty, and lets go of a database once nothing
uses it. What was mounted and where it went identify a mount: the same thing into the same schema
again is a refresh, and into another schema is a second mount.

**Whole or not at all.** Every name a mount would use is checked before anything is made. If one
is taken — by another mount, or by a view an `--init` script or a client made — nothing is
mounted, and error 50304 names each clash. `@schema` is usually the way round it. It cannot be
`sys`, `main`, `information_schema` or `pg_catalog`, which DaffyDB and DuckDB keep for themselves.

### Saving tables as parquet

**`daffydb.save_parquet`** writes one table, view or temp table to a parquet file, and
**`daffydb.save_parquet_all`** writes every one, each to a file of its own. Both write through
DuckDB's own parquet writer, one `COPY` per table, from the table as it is at that moment.

```sql
EXEC daffydb.save_parquet 'sales.orders', 'D:\exports'               -- D:\exports\sales.orders.parquet
EXEC daffydb.save_parquet orders, 'D:\exports'                        -- dbo.orders.parquet
EXEC daffydb.save_parquet #totals, 'D:\exports'                       -- a temp table: totals.parquet
EXEC daffydb.save_parquet '#totals', 'D:\exports', 'totals_2025'      -- @file: totals_2025.parquet
EXEC daffydb.save_parquet_all 'D:\exports\2025-10-20'                 -- everything
EXEC daffydb.save_parquet_all 'D:\exports\shop', @mount = 'shop'      -- one mount's tables
```

- **Names.** A file is named `schema.table.parquet`, which is how a folder scan reads a name, so
  `daffydb.mount` on the folder, or `--data-dir` pointed at it, serves every table back under its
  own name. A temp table's file is its name without the `#`, or the `##` of a global one, and it
  comes back in `dbo`. A character Windows does not allow in a file name (`< > : " / \ | ? *`)
  becomes `_`, and a device name such as `CON` gets a `_` added; the result says so. A schema with
  a dot in its own name, as a workbook's can have (`Budget.2024`), comes back split at that dot
  instead. `@file` names the file yourself: a name, not a path, with `.parquet` added if you leave
  it off.
- **What `save_parquet_all` saves** is every table and view `daffydb.tables` lists - mounted, or
  made by `--init`, `sp_duckdb` or a client - and every temp table. Temp tables live on the one
  DuckDB connection every session shares, so they are shared too, and every session's is saved.
  `@mount` saves just that mount's tables, and no temp tables.
- **Nothing is ever replaced.** DuckDB's `COPY` would overwrite a file without a word, so every file
  name is checked before anything is written. If one is already there, or two tables would share one
  (`#totals` and `##totals` would both be `totals.parquet`), the command fails with error 50307 and
  writes nothing. Each file is written under a temporary name ending `.saving` and renamed once it
  is whole, by a rename that will not replace anything, so a failure or a cancel never leaves half
  a file where a table's should be.
- **The folder** is on the machine DaffyDB runs on, and is made if it is not there.
- **A table that cannot be read** - a mounted file that has gone, a database that is down - is left
  out and said so in the messages, and the rest are saved. Only if none can be saved does the
  command fail.
- **Other clients wait less.** Sessions take turns on one connection, so a long save would hold
  everyone else up. `save_parquet_all` lets the connection go between one table and the next, so
  queries from other clients run in between; only the table being written holds them.
- **Timeouts.** A client's command timeout covers the whole save. ADO.NET's default is 30 seconds,
  which a big save can outlast, and running out cancels it; SSMS has no timeout.

Each answers with a row per table: `schema` (NULL for a temp table), `name`, `file`, `rows`,
`bytes`, and a `status` of `saved`, `saved, renamed because ...` or `left out: it could not be
read: ...`. DuckDB's own `EXPORT DATABASE` is not used: it writes data only for real tables, and
nearly everything mounted is a view.

### Calling them

They are procedure calls, so valid T-SQL that SSMS and every client send as written. Arguments go
by position or by name (`@schema = 'staging'`), and a one-word argument may go unquoted
(`EXEC daffydb.help mount`), as T-SQL allows. `EXEC` may be left out where a batch starts with the
call. They can share a batch with other statements, and take parameters in a parameterised command
(`EXEC daffydb.mount @p`). From code, a command is an ordinary stored procedure:

```csharp
using var command = new SqlCommand("daffydb.mount", connection) { CommandType = CommandType.StoredProcedure };
command.Parameters.AddWithValue("@path", @"D:\exports\2025");
command.ExecuteNonQuery();
```

A mount answers with the tables it made, and puts what it passed over, and why, in messages — the
Messages tab in SSMS, `SqlConnection.InfoMessage` in code. SSMS's Object Explorer shows new tables
once its Tables folder is refreshed (F5), and its IntelliSense underlines `daffydb.*` names it
cannot find in the catalog; they run.

**Errors** are ordinary SQL Server errors at severity 16, with messages that start `DaffyDB:`, so
code can catch them by number. The numbers follow on from DaffyDuo's (501xx) and DaffyTee's (502xx):

| # | Meaning |
|---|---|
| 50301 | An admin command from a client, on a server started without `--allow-admin` |
| 50302 | No such command, or arguments it does not take — the message shows how to call it |
| 50303 | The file, folder or database could not be opened, or nothing in it could be read, or a SQL Server could not finish T-SQL |
| 50304 | A name the mount would use is taken, so nothing was mounted |
| 50305 | A DuckDB extension the command needs could not be downloaded or loaded |
| 50306 | No such mount, or no such table in the database |
| 50307 | A file a save would write is already there, or two tables would share one, so nothing was written |
| 50308 | The folder or file a save writes could not be made |
| 50309 | T-SQL ran, but what it returned could not go in the temp table or table it was asked to |

### Who may run them

`--allow-admin` opens the admin commands to every client, as `--allow-passthrough` opens
`sp_duckdb`. A mount reads whatever the account running daffydb can read, and connects wherever it
is told, a save writes parquet files wherever that account can write, though never over a file
that is there, and `daffydb.run_sqlserver` runs any T-SQL a mounted login may, so the flag is off by
default. DaffyDB listens on 127.0.0.1 only, so that is every
program on this machine, and the banner says as much when there is no `--user`/`--password`. The
prompt needs no flag: whoever is at it started the server, and could have mounted anything with
`--data-dir` or `--init` already. Every admin command is logged with the session that ran it,
whatever `--quiet` says, and so is every refused attempt.

**Passwords are never logged.** The log, `:log`, `--trace` and the error messages clients are sent
all mask anything that looks like one — `password=`, `pwd=`, a `PASSWORD '...'` in a
`CREATE SECRET`, the password in a `postgresql://user:password@host` URL — in commands, in
`--init` statements and in what `sp_duckdb` runs. A password typed into SSMS can still stay in its
query history, as with `CREATE LOGIN ... WITH PASSWORD`; leaving it out of the connection string,
for `PGPASSWORD` or `MYSQL_PWD` to supply, keeps it out of there too.

## Reaching DuckDB directly

Some things T-SQL has no way to say. `EXEC sp_duckdb N'...'` hands its argument to DuckDB
untranslated:

```sql
EXEC sp_duckdb N'INSTALL httpfs; LOAD httpfs';
EXEC sp_duckdb N'CREATE VIEW events AS SELECT * FROM read_parquet(''s3://bucket/*.parquet'')';
EXEC sp_duckdb N'SELECT name, type FROM duckdb_secrets()';
```

It is a procedure call rather than a new keyword because that is valid T-SQL, so SSMS's editor and
every client parser accept it — and because it cannot be reached by accident. The tempting
alternative, trying T-SQL and retrying raw on failure, was rejected: it is silent, it is not
deterministic, and it would hide exactly the translation bugs the console log exists to show.

Three things to know. It needs `--allow-passthrough`, because an init script is chosen by whoever
started the process while this is chosen by whoever connected. The argument may be a literal or a
variable built earlier in the batch. And what it creates belongs to the *process*, not the
connection — one DuckDB connection serves every session, so a view made this way is visible to
everyone until the proxy stops.

A bare `SET` typed as ordinary T-SQL is swallowed as a session option; send it through `sp_duckdb`
and it reaches DuckDB.

## Encryption

How to switch it on is under [Turning on encryption](#turning-on-encryption). This is why it works
the way it does.

**Both `Encrypt=Strict` and `Encrypt=Mandatory` work.** They are different protocols, not settings,
and they are implemented differently.

**TDS 8.0** is what `Strict` means: plain TLS from byte zero with TDS carried inside, so the entire
protocol loop runs unchanged over an `SslStream`. That is why it was done first — the only new code
was choosing the stream.

**TDS 7.4** is what `Mandatory` means, and it inverts the layers. PRELOGIN is exchanged in the
clear, and if both ends agree on encryption the TLS handshake is tunnelled *inside* PRELOGIN
packets — each handshake record wrapped in an 8-byte TDS header of type `0x12`. Only when the
handshake finishes does TDS start riding inside TLS instead. `PreLoginTlsStream` is the shim that
makes this possible: `SslStream` is handed it rather than the socket, it adds and strips the
headers during the handshake, and then it becomes a pass-through for the life of the connection.
The client does the same thing on its side — `Microsoft.Data.SqlClient` calls its version
`SslOverTdsStream` — so the two are mirrors.

Three consequences worth knowing:

- **TLS 1.2 is pinned for 7.4.** In 1.3 the handshake no longer completes symmetrically, so the two
  ends would stop framing at different moments and neither could tell which packet was the last one
  that needed a header. Strict does not have this problem and is not restricted.
- **The session's transport is replaced mid-connection.** `TdsSession` reads and writes through a
  field rather than its constructor parameter for exactly this reason, and its `PacketWriter` is
  rebuilt over the new stream — the old one still points at the bare socket.
- **A fourth mode is deliberately absent.** TDS 7.4 also offers encrypting only the login packet
  and then reverting to plaintext mid-stream. Reverting means abandoning the `SslStream` and
  resuming on the bare socket with bytes in flight, for a mode no current client asks for. A client
  requesting it is answered `ENCRYPT_NOT_SUP` and falls back to the plaintext path.

Plaintext and both encrypted transports share a port. The first byte of a TLS record is `0x16`,
which is not a valid TDS packet type, so one peeked byte separates Strict from everything else; and
7.4 announces itself later, in PRELOGIN. Enabling encryption takes nothing away from clients that
do not use it.

The certificate is generated in the .NET base class library alone — no OpenSSL, no certificate
authority, nothing to install. Its SAN covers `localhost`, `127.0.0.1` and the machine name, which
is what lets you copy the folder to another machine and still have local connections validate even
though the host name baked into it now belongs to someone else.

### The one that will catch you out

**`TrustServerCertificate=True` does not work under `Encrypt=Strict`.** It is the obvious thing to
reach for and it is silently ignored: `Microsoft.Data.SqlClient` validates the chain regardless and
fails with

> The certificate chain was issued by an authority that is not trusted.

Point `ServerCertificate` at the `.cer` the proxy wrote instead. That is not a guess — it was
measured, and a check asserts both halves of it so it cannot quietly stop being true.

**Under `Encrypt=Mandatory` the opposite holds**: `TrustServerCertificate=True` *is* honoured, and
no `.cer` is needed at all.

```
Server=tcp:127.0.0.1,1433;Encrypt=Mandatory;TrustServerCertificate=True;User Id=any;Password=any
```

That is the practical argument for 7.4 over 8.0 here. Strict is the stronger mode with the fussier
setup — a file to reference, a path to get right, something to copy to another machine. Mandatory
is the connection string most tools already default to, with one checkbox. JDBC behaves the same
way with `encrypt=true;trustServerCertificate=true`, which is what DBeaver uses.

If encryption is requested and the proxy has no certificate, the client is told so plainly —
*"The instance of SQL Server you attempted to connect to does not support encryption"* — and the
server log names the switch that fixes it. It does not hang and it does not half-connect.

Verified against `Microsoft.Data.SqlClient` 6.1.1, which is what SSMS 20+ and modern .NET apps use.
The `sqlcmd` on this machine is version 15 and predates `-N strict`, so it can only exercise the
plaintext path.

## Who is connected

Every request carries a `SqlSession`: the login name, password, database, host name, application
name, client address, and whether the connection was encrypted. `IQueryHandler.OnLoginAsync` runs
after LOGIN7 is decoded and *before* LOGINACK is sent, so a handler can turn a caller away — a
refusal has to arrive in place of the acknowledgement rather than after it, or the client believes
it is connected.

`--user` and `--password` wrap any handler in a fixed credential check (`CredentialGate`), which
exists mostly as a worked example of the hook. With neither flag, every login is accepted.

The password is worth one blunt sentence: TDS scrambles it (nibble swap, XOR `0xA5`) rather than
encrypting it, so on a plaintext connection it is readable by anyone on the wire. Encryption is
what makes a credential check mean anything, which is a large part of why it is now on by default
and why `--no-cert` and `--password` are a poor pairing.

The identity functions answer from the live connection — `SUSER_SNAME()`, `ORIGINAL_LOGIN()`,
`HOST_NAME()` and `APP_NAME()` report the real caller. `USER_NAME()` and `CURRENT_USER` stay `dbo`,
because those are the *database user* rather than the login, and SQL Server distinguishes the two.

## Databases

There are three, laid out as SQL Server lays them out:

| Database | id | Holds |
|---|---|---|
| `master` | 1 | the system catalog, and nothing else |
| `msdb` | 4 | just enough for SSMS's policy and SQL Server Agent checks |
| `daffydb` | 5 | every mounted file, and whatever `--init` creates |

A login that names no database lands in `daffydb`, the way a SQL Server login with its default
database set does, and the login response says so. One that names `master` or `msdb` gets it; one
that names anything else is refused with SQL Server's error 4060, "Cannot open database ... requested
by the login", rather than landing somewhere it did not ask to be. `USE` moves a session between
them and tells the client, so `SqlConnection.Database` and `DB_NAME()` follow; `USE nosuch` is error
911. `DB_ID()` and `DB_NAME()` know all three, with or without an argument.

It was all one database, `master`, until Power BI's SQL Server connector turned up. It lists
databases with `where name not in ('master', 'model', 'msdb', 'tempdb')`, and with `master` the only
one there was nothing left to show. ODBC and OLE DB never noticed, because they ask for the tables
of whichever database they connected to.

Each database is a DuckDB catalog of the same name, with its own copy of the `sys` views, so a
three-part name resolves as written: `daffydb.dbo.orders` and `master.sys.databases` work from
anywhere. `dbo` is the current schema, so `orders`, `dbo.orders` and `daffydb.dbo.orders` are one
object, and the catalog reports each table once rather than once per alias. Sessions share one
DuckDB connection but not a current database: each request moves the connection to its session's
database first, which costs nothing in the usual case of there being one session.

A system procedure named with a database runs in it, as on SQL Server: `EXEC daffydb.sys.sp_tables`
lists `daffydb`'s tables from wherever it is called, and the caller is put back afterwards.

`daffydb` describes itself, too, as its `MS_Description` - the database-level extended property
tools show as a description: *DaffyDB: a TDS listener in front of a local DuckDB engine. This is not
SQL Server.* `@@VERSION` says the same on its second line. Its first line is
`DaffyDB - 17.0.4025.3 (X64)`: SQL Server's layout and version number, because clients take the
version apart from it, under DaffyDB's own name. `xp_msver`'s `ProductName` and `sp_server_info`'s
`DBMS_NAME` say DaffyDB too.

The one place the name is still SQL Server's is the login acknowledgement, a field of the protocol
rather than anything a query returns. Microsoft's OLE DB driver checks it: given any other name, the login succeeds and then every command
and schema rowset fails with "The data source can not be used, because it is not a Microsoft SQL
Server". SqlClient and the ODBC driver accept either. `verify-drivers.ps1` is what found this, and
is what would notice if it changed.

## The sys catalog

Client tools do not ask "what tables are there" — they ask `sys.objects` joined to `sys.schemas`
filtered on `sys.columns`. `SystemCatalog` projects enough of that from DuckDB's own metadata for a
tool to describe the database: `sys.databases`, `sys.schemas`, `sys.objects`, `sys.tables`,
`sys.views`, `sys.columns`, `sys.types`, `sys.indexes`, `sys.index_columns`, `sys.data_spaces`,
`sys.extended_properties`, `sys.all_objects`, `sys.all_views`, `sys.all_columns`,
`sys.database_files`, `sys.dm_os_host_info`, `sys.dm_exec_connections`, `sys.dm_exec_sessions`,
`sys.endpoints` (its TCP endpoint alone), `sys.configurations` (the six settings clients read:
"SMO and DMO XPs", and the five SMO reads with a database's properties), `sys.server_principals`,
`sys.database_principals` (dbo and public), `sys.syslanguages` (us_english), `sys.xp_msver`, and -
empty, because nothing mounted from a file has any - `sys.default_constraints`,
`sys.computed_columns`, `sys.identity_columns` and `sys.foreign_keys`, which Power BI and SSMS join
to when they describe a table, and the Always On and mirroring views (`sys.availability_replicas`,
`sys.availability_groups`, `sys.dm_hadr_database_replica_states`, `sys.database_mirroring`) that
Object Explorer reads before it will list a user database. VS Code's loads every property of a
database and of a table, and reads `sys.database_recovery_status`,
`sys.database_filestream_options` and `sys.database_scoped_configurations` for them, each with
what SQL Server says of a database that has none of it, and `sys.change_tracking_databases`,
`sys.change_tracking_tables`, `sys.filetables`, `sys.periods` and `sys.fulltext_languages`,
empty.

`INFORMATION_SCHEMA.TABLES`, `COLUMNS`, `VIEWS`, `SCHEMATA`, `ROUTINES` and `PARAMETERS` answer in
SQL Server's shape, as projections of those views. DuckDB has an `information_schema` of its own,
and a schema by that name cannot be created alongside it, so the translator points those six names
at views in each database's `sys` schema. The rest are DuckDB's, which describe the same objects in
DuckDB's terms. Its answers for the six were right for DuckDB and wrong for a SQL Server client:
they spanned every database at once, called every mounted file a `VIEW`, named types `VARCHAR` and
`INTEGER`, and had no `ROUTINES` at all - the view Power BI's navigator reads straight after the
table list.

All of them are live views over `information_schema`, so anything created after startup — by an
`--init` script, or by a client through `EXEC sp_duckdb` — shows up without being registered
anywhere. Five details in there are not obvious:

- **A mounted file is reported as a table, a view as a view.** Everything mounted from `--data-dir`
  is a DuckDB view over `read_parquet` or `read_csv_auto`, but it stands for the table you came to
  find, so the mount loop records it in `sys.mounted_objects` and `sys.objects` calls it a
  `USER_TABLE`. A view written in an `--init` script is not in that list and stays a `VIEW`.
- **`sys.objects.type` is `'U'`, not `'U '`.** SQL Server types that column `char(2)` and returns
  the padded form, but its comparisons ignore trailing blanks and DuckDB's do not. A client sends
  the unpadded value as a parameter, so the unpadded value is what is stored — otherwise every
  `WHERE type = @p` matches nothing.
- **`sys.schemas` is scoped to its own database.** DuckDB always has `memory`, `system` and
  `temp` attached alongside the databases, each with a `main` schema, and since `schema_id` is a
  row number those ties made every id unstable from one query to the next.
- **Every table has a heap row in `sys.indexes`.** A parquet or CSV file has no indexes, so the
  obvious thing is an empty view — but SQL Server gives every heap a row there with `index_id` 0
  and `type` 0, and Object Explorer lists tables with an `INNER JOIN` from `sys.tables` to
  `sys.indexes`. An empty view does not report "no indexes", it reports no tables.
- **A `bit` column is a 0/1 `DECIMAL(1,0)` in DuckDB, and goes out as `bit`.** Clients use these
  flags two ways at once. Object Explorer does arithmetic on them - it decides whether an index
  backs a primary key with `is_primary_key + 2*is_unique_constraint = 1`, and DuckDB has no
  `*(INTEGER, BOOLEAN)` for that to bind against - and SSMS casts a flag it selects directly
  straight to `bool`, so an integer fails with "Specified cast is not valid". A `DECIMAL(1,0)` adds
  up like the number it is, and no parquet or CSV file produces one, so a `DECIMAL(1,0)` holding
  only 0 and 1 is sent as `bit`. A real one-digit decimal with any other value keeps its type.
  Three flags are `int` instead: `sys.databases`' `is_published`, `is_subscribed` and
  `is_merge_published`, which SMO only ever reads summed, as `is_published*1 + is_subscribed*2 +
  is_merge_published*4`. SQL Server answers that with an `int`, SMO turns it into an enum, and a
  decimal fails that.
- **`msdb` has just enough for SSMS's Policy-Based Management checks** - policy management
  off, as on a fresh server - which SSMS runs while connecting and again before it will script
  "Select Top 1000 Rows". SSMS also says `USE [msdb]` before asking about SQL Server Agent, and
  runs what it asks next from there, which is why `msdb` is a database a session can be in.

There is no `sys.partitions`, so a client asking for a table's row count gets an error rather than
a wrong number. That is the only known gap in the object tree.

System procedures resolve to those views, so `EXEC xp_msver` and `INSERT #t EXEC master.dbo.xp_msver`
both work: `xp_msver`, `sp_databases`, and `sp_server_info`, which lists LocalDB's 29 attributes in
its own three columns and takes `@attribute_id` to return one.

The catalog procedures behind ODBC's catalog functions answer too, in SQL Server's exact column
layout, whether a driver calls them by RPC or someone types `EXEC`: `sp_tables` (SQLTables),
`sp_columns_100` (SQLColumns), `sp_datatype_info_100` (SQLGetTypeInfo), `sp_pkeys` and `sp_fkeys`,
which are always empty because nothing mounted from a file has keys. Their arguments are T-SQL
`LIKE` patterns, `[_]` classes included.

The same goes for OLE DB's schema rowsets, which MSOLEDBSQL builds from `sp_catalogs_rowset2`,
`sp_schemata_rowset`, `sp_tables_rowset2`, `sp_columns_100_rowset`, `sp_foreign_keys_rowset` and
`sp_indexes_100_rowset`, and their variants. Each follows SQL Server's own definition, read from
LocalDB, and answers with OLE DB's type codes and column flags. A procedure DaffyDB does not have
fails with SQL Server's error 2812, "Could not find stored procedure", which drivers recognise.

Catalog functions read the same views — `OBJECT_ID` answers NULL for something that does not exist,
which is how a client asks whether a table is there, and `OBJECT_NAME`, `SCHEMA_ID`,
`OBJECTPROPERTY` and `DATABASEPROPERTYEX` round out the set. `OBJECTPROPERTY` knows what a table
here is - no triggers, no indexes, owned by dbo - and both answer NULL for a property they have
nothing for, as SQL Server does. `SESSIONPROPERTY` reports `ANSI_NULLS` and `QUOTED_IDENTIFIER` on,
which is how DuckDB always behaves, since DaffyDB accepts `SET` and ignores it, and `CONTEXT_INFO()`
is NULL. `COLLATIONPROPERTY` answers for the one collation this
server reports and ignores the collation name it is handed, which is how it copes with SSMS
passing a subquery over `sys.databases` as that argument. Permission checks (`HAS_DBACCESS`, `IS_SRVROLEMEMBER`,
`IS_MEMBER`, `HAS_PERMS_BY_NAME`, `PERMISSIONS`) all answer yes: there is one user, with access to
everything, so a client asking whether it may see something is never told no.

## T-SQL translation

`TSqlTranslator` rewrites the dialect. It is a rewriter, not a parser: it handles a documented list
and leaves everything else alone, so anything unsupported reaches DuckDB and comes back as a real
error message instead of being silently mistranslated. All rewriting happens on a masked form of
the statement where string literals, comments and quoted identifiers are opaque tokens, so a regex
can never corrupt a literal.

**Query shape**

| T-SQL | DuckDB |
|---|---|
| `SELECT TOP 3 ...` / `TOP (3)` / `TOP (@n)` | `LIMIT 3` at the end of that query |
| `OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY` | `LIMIT 3 OFFSET 2` |
| `FROM t WITH (NOLOCK)`, `WITH (INDEX(1))` | hints dropped |
| `... OPTION (RECOMPILE)`, `OPTION (MAXDOP 2)` | dropped |
| `CROSS APPLY (...) a` | `CROSS JOIN LATERAL (...) a` |
| `OUTER APPLY (...) a` | `LEFT JOIN LATERAL (...) a ON TRUE` |
| `WITH cte AS (... FROM cte ...)` | `WITH RECURSIVE cte AS (...)`, when a CTE names itself |
| `SELECT Total = SUM(x)` | `SELECT SUM(x) AS Total` |
| `SELECT YEAR(d) Year` | `SELECT YEAR(d) AS Year`, when the alias is one of DuckDB's keywords |
| `FROM STRING_SPLIT(s, ',')` | `FROM (SELECT unnest(string_split(s, ',')) AS value)`, plus `ordinal` when asked for |
| `[bracketed name]` | `"bracketed name"` |
| `N'literal'` | `'literal'` |
| `@param` | `$1`, `$2`, ... bound in order |

**Strings**

| T-SQL | DuckDB |
|---|---|
| `LEN(x)` | `length(rtrim(x))` — T-SQL ignores trailing spaces |
| `DATALENGTH(x)` | `strlen(CAST(x AS VARCHAR))` |
| `CHARINDEX(needle, hay[, start])` | `strpos(hay, needle)`, offset-adjusted for the 3-arg form |
| `REPLICATE(s, n)` / `SPACE(n)` | `repeat(s, n)` / `repeat(' ', n)` |
| `STUFF(s, at, len, repl)` | `substring(...) \|\| repl \|\| substring(...)` |
| `STR(x)` | `CAST(x AS VARCHAR)` |
| `'a' + x` | `'a' \|\| x` — only when one side is a string literal |
| `x LIKE 'mount%'` | `x ILIKE 'mount%'` — case-insensitive, as the collation DaffyDB reports is |
| `x LIKE '%[0-9]%'` | `x SIMILAR TO '(?is).*[0-9].*'` — a written pattern with `[...]` classes |
| `STRING_AGG(x, ',') WITHIN GROUP (ORDER BY y)` | `string_agg(x, ',' ORDER BY y)` |
| `CAST(x AS varchar(7))` | `left(CAST(x AS VARCHAR), 7)` — DuckDB ignores the length, T-SQL cuts to it |
| `COLLATE Latin1_General_CI_AS` | dropped; `_CI_AI` becomes `COLLATE nocase.noaccent`, and `_CS_` or `_BIN` `COLLATE "binary"` |
| `JSON_VALUE(j, '$.a')` | `json_extract_string(j, '$.a')`, NULL for an object or array; `lax`/`strict` dropped |
| `JSON_QUERY(j[, path])` | `json_extract(j, path)` for an object or an array, NULL otherwise |
| `ISJSON(j[, type])` | `json_valid(j)`, and 1 only for an object or array, or for the type named |

**Numbers, conditionals, casts**

| T-SQL | DuckDB |
|---|---|
| `0xFF` | `255` — DuckDB has no hex literal |
| `QUOTENAME(x[, delim])` | `('[' \|\| replace(x, ']', ']]') \|\| ']')` |
| `LOG(x)` / `LOG10(x)` | `ln(x)` / `log(x)` — T-SQL `LOG` is the natural log |
| `SQUARE(x)` / `RAND()` | `power(x, 2)` / `random()` |
| `ISNULL(a, b)` | `COALESCE(a, b)` |
| `IIF(a, b, c)` | `CASE WHEN a THEN b ELSE c END` |
| `CHOOSE(i, a, b, ...)` | `CASE WHEN i = 1 THEN a ... END` |
| `CONVERT(type, x)` / `CAST(x AS type)` | `CAST(x AS <mapped type>)` |
| `TRY_CONVERT(type, x)` | `TRY_CAST(x AS <mapped type>)` |
| `ROUND(x, d, 1)` | `trunc(x, d)` — a third argument other than 0 truncates |
| `ISNUMERIC(x)` | 1 when SQL Server could read x as any number, money included, so `$`, `1,000` and `.` count, as they do there |

**Dates**

| T-SQL | DuckDB |
|---|---|
| `GETDATE()`, `SYSDATETIME()`, `GETUTCDATE()` | `now()` |
| `DATEPART(year, d)` | `date_part('year', d)`, as an int |
| `DATEPART(weekday, d)` | `dayofweek(d) + 1` — Sunday is 1, as in `us_english` |
| `DATEPART(week, d)` | weeks start on Sunday and 1 January is always in week 1; `iso_week` is the ISO week |
| `DATEPART(millisecond, d)` | the milliseconds within the second, as `microsecond` is the microseconds |
| `DATENAME(month, d)` | `monthname(d)` (weekday → `dayname`, else text of the part) |
| `DATEDIFF(day, a, b)` | `date_diff('day', a, b)`; for weeks, the Sundays crossed |
| `DATEADD(day, n, d)` | `(d + to_days(CAST(n AS INTEGER)))` |
| `EOMONTH(d[, n])` | `last_day(d)` |
| `DATEFROMPARTS(y, m, d)` | `make_date(y, m, d)` |
| `CONVERT(varchar(10), d, 101)` | `strftime(d, '%m/%d/%Y')` — every date and time style, as SQL Server writes a `datetime2`, `date` or `time` |
| `CONVERT(date, '04/10/2026', 103)` | read in the style's order: 4 October |
| `CONVERT(date, '01/01/55', 1)` | a two-digit year by SQL Server's 2049 cutoff: 1955, where DuckDB's `%y` says 2055 |
| `FORMAT(d, 'yyyy-MM')` | `strftime(d, '%Y-%m')` — .NET date patterns and standard formats, in en-US |
| `ISDATE(x)` | 1 for a value SQL Server reads as a datetime, month first as `us_english` reads it |
| `DATEDIFF(day, '2026-01-01', '2026-10-04')` | a string literal standing for a date is cast to one |

Several of these behave differently for a date, a time and a timestamp, and the type of an argument
is not known until DuckDB binds the statement. They are written as
`list_transform([x], lambda v: CASE WHEN typeof(v) = ... END)[1]`, which evaluates `x` once and lets
DuckDB choose the branch from the bound type.

**Server context** — `@@VERSION`, `@@SPID`, `@@SERVERNAME`, `@@ROWCOUNT`, `@@MICROSOFTVERSION`,
`SERVERPROPERTY('...')`, `DB_NAME()`, `SCHEMA_NAME()`, `USER_NAME()`, `SUSER_SNAME()`, `HOST_NAME()`,
`APP_NAME()`, `CURRENT_USER` all answer with plausible constants, because clients ask for them
while connecting.

Known limits, deliberately: `+` is only treated as concatenation when a string literal sits on one
side of it. The general case needs real type resolution, and DuckDB's `||` joins numbers too, so a
wrong guess would quietly turn addition into text. `TOP ... WITH TIES` and `TOP ... PERCENT` are
left for DuckDB to reject rather than answered as a plain row count. `FORMAT` of a number, or in a
culture other than en-US, is not translated. Each logs a note or lets DuckDB report the error.

`TOP` is worth a paragraph of its own, because T-SQL puts the row count at the front of a query
and DuckDB puts it at the back, and the two are only the same statement while there is one query.
Each `TOP` is therefore matched to its own scope by counting parentheses forward from it: the
first unbalanced `)` closes the subquery it sits in, and `LIMIT` goes just inside — which is also
after that subquery's `ORDER BY`, where DuckDB needs it. A `TOP` that meets no unbalanced `)`
belongs to the statement and its `LIMIT` is appended at the end. Object Explorer's table listing
has six of them, five in scalar subqueries, and hoisting the first one found out to the end would
limit the outer query to a single row while still looking like it worked.

Semantics are not identical to SQL Server everywhere:

- `CHARINDEX` is case-insensitive under a typical SQL Server collation, but `strpos` is
  case-sensitive.
- `CONVERT(int, 49.99)` truncates in T-SQL but rounds in DuckDB.
- DaffyDB cannot tell `datetime` from `datetime2`. Both are DuckDB's timestamp, and a mounted
  file's timestamps are reported as `datetime2`, so `CONVERT`'s styles write seven fractional
  digits, as for a `datetime2`, even for a value cast to `datetime`, where SQL Server writes three.
- `SET DATEFORMAT` and `SET LANGUAGE` are accepted and ignored, so `ISDATE` and `CONVERT` read
  dates month first, as `us_english` does.
- A `LIKE` pattern held in a parameter is matched case-insensitively, but its `[...]` classes are
  read as plain brackets. Only a pattern written in the query becomes a regular expression.
- `char(n)` cuts text to n characters but does not pad it with spaces, because SQL Server ignores
  trailing spaces when it compares and DuckDB does not.
- `CAST(x AS varchar)` with no length is not cut to SQL Server's default of 30.

`verify-tsql.ps1` runs every translation above against the answers SQL Server gives, which were
taken from LocalDB and can be checked against it again with `-Oracle`.

**Integer division** is T-SQL's: an integer divided by an integer is an integer, truncated toward
zero, so `7 / 2` is 3 and `-1 / 2` is 0, columns included. DuckDB divides that way when its
`integer_division` setting is on, and DaffyDB turns it on, so nothing is rewritten. A decimal or a
float on either side divides as it does in DuckDB.

**Case sensitivity** — SQL Server compares strings case-insensitively by default and DuckDB does
not, which is the difference a client notices first: `WHERE first_name = 'paTricia'` finds nothing
against a column holding `Patricia`. The DuckDB connection is therefore opened with
`SET GLOBAL default_collation = 'nocase'`, which makes `=`, `IN`, `JOIN`, `DISTINCT`, `GROUP BY`
and `ORDER BY` fold case — matching the `SQL_Latin1_General_CP1_CI_AS` the catalog already reports.

`LIKE` is the exception: DuckDB's `LIKE` ignores collation, so DaffyDB sends it `ILIKE`, the
case-insensitive form, and `LIKE 'pat%'` finds `Patricia` as SQL Server's does. One thing the
collation does not reach: the fold is ASCII only: `'Eleve' = 'ELEVE'` matches, `'Elève' = 'ELÈVE'`
does not, so the accent-insensitive half of a SQL Server `_AS` collation is missing.

## Management Studio

SSMS drives SMO, and SMO's whole connect sequence now succeeds:

```
PASS DatabaseEngineType     Standalone
PASS DatabaseEngineEdition  Enterprise
PASS Version                17.0.4025
PASS Edition                Developer Edition (64-bit)
PASS ProductLevel           RTM
PASS Collation              SQL_Latin1_General_CP1_CI_AS
PASS Databases              daffydb, master, msdb
PASS Platform               NT x64
PASS Processors             4
PASS PhysicalMemory         16384
PASS Language               English (United States)
```

So does the object tree — Databases, Tables with their schemas, Columns with their types, Views:

```
PASS OE Databases           1: daffydb,1,smo,3,0,
PASS db.Tables              dbo.customers, dbo.orders
PASS db.Schemas             dbo
PASS OE Tables              2: customers,dbo,False | orders,dbo,False
PASS OE System Tables       0:
PASS OE Views               0:
PASS OE Columns             5: customer_id,int,True,False | name,nvarchar,True,False | ...
```

Both halves are checked because they are different queries. Object Explorer does not use the SMO
collections; it sends URN requests with an explicit field list, and asking for one more field
(`IsSystemObject`, which is how it splits Tables from System Tables) pulls in catalog views the
collections never touch. `db.Tables` passing did not mean the tree worked, and for a while it did
not.

**System Tables is empty, and that is correct.** That node lists objects with `is_ms_shipped = 1`;
nothing served here is Microsoft-shipped, so nothing qualifies.

That sequence was built by running it without the GUI — connect, read the first failure, implement
what it names, repeat. Object Explorer asks for more than this once you start opening property
pages and scripting objects, so expect gaps there; the loop for closing them is the same, and
`--trace` shows you the exact batch SSMS sent.

Connect SSMS to `127.0.0.1,1433` with SQL Server authentication, any username and password, and
leave the database blank. The Encryption dropdown can be left at **Mandatory**, its default, with
*Trust server certificate* ticked — nothing else is needed. **Strict** also works and is stronger,
but wants the generated `.cer` in the *Server certificate* box. Only `--no-cert` requires you to
change the dropdown, to Optional.

### VS Code

VS Code's SQL Server extension (mssql) drives SMO too, but its Object Explorer asks for more than
SSMS's. Before it lists what is in a database it loads every property of the database, and before
it scripts "Select Top 1000 Rows" every property of the table, from queries that join a dozen
catalog views SSMS never reads. One of them missing, or one value of the wrong type, fails the
whole load, and the database shows nothing under it. SmoProbe loads both the same way:

```
PASS db all properties      None, us_english, 1900-01-01
PASS table all properties   dbo, None, False
```

Connect it to `127.0.0.1,1433` with SQL Login, any user name and password, encryption Mandatory and
*Trust server certificate* on. IntelliSense asks for things DaffyDB does not have - triggers,
modules, synonyms, column encryption keys - and goes without them, as it does in SSMS. Folders for
what DaffyDB has none of, such as stored procedures and security, come up empty or with an error,
as in SSMS too.

## Layout

```
src/TdsProxy/
  Program.cs                   argument parsing, serve and --seed modes
  Tds/PacketFramer.cs          header parsing, EOM reassembly, TLS detection
  Tds/PacketWriter.cs          outbound framing and packet splitting
  Tds/TdsServer.cs             session state machine and message dispatch
  Tds/RequestParser.cs         SQL_BATCH and RPC → SqlRequest
  Tds/TypeInfoReader.cs        RPC parameter TYPE_INFO/TYPE_VARBYTE decoding
  Tds/ResponseWriter.cs        the response token stream
  Tds/Annotator.cs             human-readable decoding, used by --trace
  Tds/ServerCertificate.cs     self-signed certificate generation, used by both TLS modes
  Tds/CertificateTrust.cs      --trust-cert, --untrust-cert and the first-run question
  Tds/PreLoginTlsStream.cs     tunnels the TDS 7.4 handshake through PRELOGIN packets
  Sql/SqlSegments.cs           literal/comment-aware scanner
  Sql/TSqlParser.cs            statement boundaries, BEGIN/END, IF/ELSE, TRY/CATCH
  Sql/TSqlTranslator.cs        T-SQL → DuckDB rewriting: the pipeline, names, casts, catalog functions
  Sql/TSqlTranslator.Clauses.cs  hints, collations, recursive CTEs, APPLY, STRING_SPLIT, column aliases
  Sql/TSqlTranslator.Dates.cs  DATEPART and friends, CONVERT styles, FORMAT, ISDATE
  Sql/TSqlTranslator.Text.cs   LIKE, varchar(n), ISNUMERIC, JSON, ROUND
  Sql/LocalVariables.cs        DECLARE, SET, EXEC and SELECT-assignment parsing
  Sql/SqlErrors.cs             THROW, RAISERROR and what a CATCH block can see
  Sql/SessionContext.cs        what SUSER_SNAME() and friends answer
  Sql/Databases.cs             master, msdb and daffydb, and USE
  Sql/SystemProcedures.cs      xp_msver, sp_duckdb and friends
  Sql/DaffyCommands.cs         the daffydb.* commands: names, arguments, help text, error numbers
  Query/Seam.cs                IQueryHandler, SqlSession and friends
  Query/CredentialGate.cs      a worked example of the login hook
  Query/DuckDbQueryHandler.cs  mounting, execution against DuckDB
  Query/DuckDbQueryHandler.Commands.cs  what each daffydb.* command does
  Query/DuckDbQueryHandler.Attached.cs  the database mounts: postgres, mysql, sqlserver, sqlite, duckdb
  Query/ExcelWorkbook.cs       loading a workbook's sheets as typed tables
  Query/ParquetSeeder.cs       writes the sample parquet files
  Query/SystemCatalog.cs       sys.* views projected from DuckDB metadata
  Repl/ReplLoop.cs             the prompt, its commands, and what they run
  Repl/ResultPrinter.cs        grids, paging, record mode, column fitting
  Repl/LogBuffer.cs            holds the log while the prompt owns the terminal
  Chatter.cs                   the per-request log line, and the --quiet switch
  Redact.cs                    masks passwords in the log, --trace and error messages
  ServerFacts.cs               how the server was started, and who is connected, for daffydb.status
  PowerBiDesktop.cs            the .pbids shortcut, and :pbi
src/DaffySQL/                  SQL Server as DuckDB table functions, for daffydb.sqlserver: a TDS
                               client of its own, and DuckDB's C API (see its README.md)
src/DaffySQL.Tests/            DaffySQL's client against fake servers: redirects, Entra ID, packets
src/TdsProbe/                  protocol-level checks, and the --sql and --proc ad-hoc tools
src/SmoProbe/                  reproduces the SSMS/SMO connect sequence
src/DuckProbe/                 end-to-end T-SQL checks against real data
scripts/                       verification scripts, one per feature
```

Each script's header says what it checks and what it needs.

## Limits

Deliberately unimplemented, each reported as a proper `SqlException` rather than a hang: integrated
authentication, MARS, bulk copy, `WHILE` loops, user stored
procedures, output parameters of your own, and fetching through a server cursor: one is opened only
to say what its columns are, which is all a linked server asks of it. `SET ROWCOUNT` limits queries
and not writes. Login-packet-only encryption is declined in the same spirit; see
[Encryption](#encryption). Transactions are accepted and ignored — everything is
autocommit — and tables mounted from files are views over read-only files, so writes to them fail.
Sheets loaded from a workbook are in-memory tables: a write to one succeeds but only changes
DaffyDB's copy, which is gone at restart. The workbook itself is never written.

Management Studio connects and runs queries; see [Management Studio](#management-studio) for what
is and is not furnished in Object Explorer.

DuckDB runs on one connection behind a semaphore, so concurrent sessions take turns. That is fine
for a spike and would need revisiting for real concurrency. A client that cancels - a command
timeout, or SSMS's Cancel Executing Query - stops its own query on the server, so a runaway one,
such as a recursive CTE with no end, does not hold everyone else up. A cancel only ever stops the
query it was sent for.
## Licence and attribution

DaffyDB is MIT licensed. Copyright (c) 2026 Scott Traube and TroBeeOne LLC. See `LICENSE`.

If you use DaffyDB's code, or ship it, the licence asks one thing in return: keep that copyright
line and the licence text with it, so the credit stays with Scott Traube and TroBeeOne LLC.

It bundles its dependencies, which is redistribution, so their licences travel with it in
`THIRD-PARTY-NOTICES.txt`: DuckDB (MIT), DuckDB.NET (MIT), Sylvan.Data.Excel (MIT) and Apache
Arrow (Apache-2.0).
`Microsoft.SqlServer.SqlManagementObjects`, used by `SmoProbe`, is under a proprietary Microsoft
licence and is deliberately not referenced by the shipped project.

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
