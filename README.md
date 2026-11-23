# cst — Creatio Schema Transfer

Standalone command-line tool that transfers selected Creatio schemas, SQL scripts and data bindings
(including system settings and translations) from one stand to another, into mapped packages,
in dependency order. It uses only standard Creatio HTTP services (no clio, no cliogate).

It also moves **rows of tables** between stands (full or partial, with references kept consistent) —
see [Table data](#table-data). Only that mode needs CstGate, cst's own SQL gateway package.

Run without arguments in a console window for an interactive UI (English or Ukrainian).

Tested between core 10.0.0.801 (MSSQL) and 8.3.1.4481 (PostgreSQL).

## Build

```bash
dotnet build src/Cst
dotnet publish src/Cst -c Release -o publish
```
`publish/cst.exe` is self-contained (no .NET needed on the target machine). Copy
`src/Cst/appsettings.example.json` next to it as `appsettings.json` and describe the stands
(`Url`, `Login`, `DbEngine`; passwords preferably via `CST_PASSWORD_<ENV>`).

## Use

```bash
cst export --env dev --config list.txt --out lv.zip
cst inspect --archive lv.zip
cst import --env test --archive lv.zip --dry-run
cst import --env test --archive lv.zip
cst restore --env test --backup lv_import_<date>_<time>
```

List file:
```text
# comment
package LVBase -> UsrBase:
 - SchemaName
 - SomeDataBinding [data]
files:
 - Terrasoft.WebApp\Terrasoft.Configuration\bin\Terrasoft.Configuration.dll
```

### Files

The `files:` section carries plain files of the Creatio installation folder. It needs the root folder of
both stands as seen from the machine running cst: `"Path"` in the environment (like clio's
`EnvironmentPath`) or `--root`. Entries are relative to the root: a file, a folder (all files below it) or
a file-name mask (`Terrasoft.WebApp\bin\Utk*.dll`); paths outside the root are rejected.
On import the files are written last, after compilation (which rewrites `Terrasoft.Configuration\bin`):
identical files (SHA-256) are skipped, replaced ones are backed up, and locked files stop the import before
anything is changed. `restore` puts replaced files back and deletes files the import created.
Files in a `bin` folder or `*.config` take effect after the Creatio application restarts.

- Target packages must already exist; missing packages, package dependencies or schemas are listed in
  `<archive>.missing.md` and nothing is installed (exit code 3).
- `DbEngine` of the target stand (MSSql | PostgreSql | Oracle) decides which SQL scripts run.
- Import order: SQL BeforePackage → schemas → DB structure → compile → SQL AfterPackage → data → SQL AfterSchemaData.
- Exit codes: 0 ok, 1 usage/config, 2 validation, 3 missing dependencies, 4 install failed, 5 compile errors, 10 unexpected.

## Table data

Copies rows of tables from one stand to another: whole tables, the first N rows, rows matching an SQL
condition or given Ids. Rows they reference can be taken along, so the copy stays consistent. Table structure
is not touched: the tables must already exist on the target (install the packages first).

### CstGate

Table data is read and written through **CstGate**, a package with one assembly and no schemas
(`Files\Bin\CstGate.dll` for .NET Framework and `Files\Bin\netstandard\CstGate.dll` for .NET builds of Creatio).
cst carries it and installs it only when you ask:

```bash
cst gate-status  --env dev      # installed? version, DB engine, may this user use it
cst gate-install --env dev      # zip install, then restarts the Creatio application (about a minute)
```
Its service `rest/CstGateService` runs SQL for users with the **CanManageSolution** operation only. It needs
no connection string on the cst side. Nothing else in cst uses it. The source is in `gate/CstGate`.

### Plan

What to copy is a text file:

```text
db packages:                     # without tables: every table created in these packages, in full
 - UtkContracts
tables:
 - UtkContract      full
 - UtkPayment       top 500 by CreatedOn desc
 - UtkLog           where "CreatedOn" > '2026-11-01'    # SQL of the source DB
 - UtkTariff        ids 1b2c…, 3d4e…
 - UtkArchive       skip
children:                        # also take rows that point at the chosen rows
 - UtkPayment.Contract
refs:                            # what to do with each lookup column (Table may be *)
 - UtkPayment.Contact   expect
 - Account.Type         pull
options:
 - rights: yes                   # Sys<Table>Right rows of the copied records
 - translations: yes             # Sys<Table>Lcz rows
```

Reference policies:

| Policy | Meaning |
|---|---|
| `pull` | take the referenced rows into the archive (default when the referenced table is copied too) |
| `expect` | they must already exist on the target (default otherwise); checked before anything is written |
| `author` | missing on the target → the importing user's contact (default for `CreatedBy` / `ModifiedBy`) |
| `null` | missing on the target → empty |
| `drop` | missing on the target → the row is not written |

If one reference would pull more than `MaxPullRows` rows, the export stops and names it. Thresholds
(`LargeTableMb`, `DefaultTop`, `MaxPullRows`, `PageSize`) are set in the `DbData` section of `appsettings.json`.

### Commands

```bash
cst db-tables  --env dev --packages "UtkContracts"          # read-only: tables, rows, ~size, references
cst db-export  --env dev --plan plan.txt --out data.zip
cst db-inspect --archive data.zip
cst db-import  --env test --archive data.zip --dry-run      # everything is done and rolled back
cst db-import  --env test --archive data.zip [--mode insert|upsert] [--on-unique stop|skip]
cst db-restore --env test --backup data_dbimport_<date>_<time>
cst db-sql     --env dev --sql "SELECT TOP 5 * FROM Account"  # ad-hoc SQL through the gate
```

- The archive is engine-neutral: MSSQL ↔ PostgreSQL works in both directions.
- The import is **one transaction**. If any reference is missing (`expect`, `pull`) or a new row collides
  with a unique index of the target, nothing is written (exit code 3); `--on-unique skip` leaves such rows out.
- `insert` (default) adds only rows whose Id is new; `upsert` also overwrites existing rows. Translation and
  rights rows are matched by record + culture / role + operation, not by Id.
- Reference cycles are broken at a nullable column, which is filled in after the inserts.
- A lookup that is a view on the target (e.g. `VwSysSchemaInfo`, keyed by the schema UId) is checked there:
  rows it already shows count as present. Views are never written to.
- `db-restore` deletes the inserted rows and puts back the previous versions of updated rows. Rows deleted by
  FK cascades on the target do not come back. Both restores (`restore`, `db-restore`) refuse a backup made
  on another stand.
- Creatio caches lookups and settings: clear Redis or restart the application after importing cached tables.

Design notes and test results (in Russian): `docs/db-transfer.md`.

`cst --help` prints the full reference. `tests/postgresql/` holds read-only check scripts for a PostgreSQL stand.
