-- cst transfer check on a PostgreSQL Creatio stand (read-only).
-- Run in psql / DBeaver against the stand's database after `cst import`.
-- Default values are for the cst test set (package UtkCstTarget); change the package name in params if needed.

WITH params AS (SELECT 'UtkCstTarget'::text AS pkg)

-- 1. Elements of the target package: schemas, SQL scripts, data bindings, with their "modified locally" flags.
SELECT 'schema' AS kind, s."Name", s."ManagerName" AS detail, s."IsChanged", s."IsLocked", s."ModifiedOn"
FROM "SysSchema" s JOIN "SysPackage" p ON p."Id" = s."SysPackageId", params
WHERE p."Name" = params.pkg
UNION ALL
SELECT 'sql script', q."Name",
       'engine=' || q."DBEngineType" || ' install=' || q."InstallType" || ' needInstall=' || q."NeedInstall"
         || COALESCE(' error=' || left(q."LastError", 80), ''),
       q."IsChanged", q."IsLocked", q."ModifiedOn"
FROM "SysPackageSqlScript" q JOIN "SysPackage" p ON p."Id" = q."SysPackageId", params
WHERE p."Name" = params.pkg
UNION ALL
SELECT 'data binding', d."Name",
       'bytes=' || octet_length(d."Data") || ' cultures=' ||
         (SELECT count(*) FROM "SysPackageDataLcz" l WHERE l."SysPackageSchemaDataId" = d."Id"),
       d."IsChanged", d."IsLocked", d."ModifiedOn"
FROM "SysPackageSchemaData" d JOIN "SysPackage" p ON p."Id" = d."SysPackageId", params
WHERE p."Name" = params.pkg
ORDER BY 1, 2;

-- 2. Tables created for the transferred entities (UpdateDBStructure) and their row counts.
SELECT c.relname AS "Table", c.reltuples::bigint AS "ApproxRows"
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind = 'r'
  AND c.relname IN ('UtkCstClient', 'UtkCstOrder', 'UtkCstStatus', 'SysUtkCstStatusLcz')
ORDER BY 1;

-- 3. Rows written by data bindings and by the PostgreSQL SQL script.
--    Expected: Client A, Client B (binding), Client from PG SQL (UtkCstInitPg);
--    "Client from SQL" must be ABSENT (UtkCstInit is an MSSQL script and must not run on PostgreSQL).
SELECT "Id", "UtkName" FROM "UtkCstClient" ORDER BY "Id";

-- 4. Lookup rows and their translations (binding UtkCstStatus).
SELECT s."Id", s."Name" AS "PrimaryName", c."Name" AS "Culture", l."Name" AS "Translated", l."Description"
FROM "UtkCstStatus" s
LEFT JOIN "SysUtkCstStatusLcz" l ON l."RecordId" = s."Id"
LEFT JOIN "SysCulture" c ON c."Id" = l."SysCultureId"
WHERE COALESCE(l."Name", '') <> '' OR l."RecordId" IS NULL
ORDER BY s."Name", c."Name";
