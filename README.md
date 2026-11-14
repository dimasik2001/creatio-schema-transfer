# cst — Creatio Schema Transfer

Standalone command-line tool that transfers selected Creatio schemas, SQL scripts and data bindings
(including system settings and translations) from one stand to another, into mapped packages,
in dependency order. It uses only standard Creatio HTTP services (no clio, no cliogate).

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
```

- Target packages must already exist; missing packages, package dependencies or schemas are listed in
  `<archive>.missing.md` and nothing is installed (exit code 3).
- `DbEngine` of the target stand (MSSql | PostgreSql | Oracle) decides which SQL scripts run.
- Import order: SQL BeforePackage → schemas → DB structure → compile → SQL AfterPackage → data → SQL AfterSchemaData.
- Exit codes: 0 ok, 1 usage/config, 2 validation, 3 missing dependencies, 4 install failed, 5 compile errors, 10 unexpected.

`cst --help` prints the full reference. `tests/postgresql/` holds read-only check scripts for a PostgreSQL stand.
