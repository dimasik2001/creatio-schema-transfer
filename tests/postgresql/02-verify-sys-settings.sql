-- System settings transferred by cst (bindings on SysSettings / SysSettingsValue), PostgreSQL, read-only.
-- Expected for the cst test set:
--   UtkCstSetText = 'hello cst', UtkCstSetInt = 42, UtkCstSetBool = true,
--   UtkCstSetLookup = 5ca7a7e5-0000-4000-8000-000000000002 (UtkCstStatus "Done"), all for "All employees".

SELECT s."Id", s."Code", s."Name", s."ValueTypeName", s."IsCacheable", s."IsPersonal",
       s."ReferenceSchemaUId",
       au."Name" AS "ForAdminUnit",
       v."Id" AS "ValueRowId",
       CASE
         WHEN s."ValueTypeName" = 'Boolean' THEN v."BooleanValue"::text
         WHEN s."ValueTypeName" = 'Integer' THEN v."IntegerValue"::text
         WHEN s."ValueTypeName" = 'Lookup' THEN v."GuidValue"::text
         WHEN s."ValueTypeName" IN ('DateTime', 'Date', 'Time') THEN v."DateTimeValue"::text
         WHEN s."ValueTypeName" LIKE 'Float%' OR s."ValueTypeName" LIKE 'Money%' THEN v."FloatValue"::text
         ELSE v."TextValue"
       END AS "Value"
FROM "SysSettings" s
LEFT JOIN "SysSettingsValue" v ON v."SysSettingsId" = s."Id"
LEFT JOIN "SysAdminUnit" au ON au."Id" = v."SysAdminUnitId"
WHERE s."Code" LIKE 'UtkCst%'
ORDER BY s."Code", au."Name";

-- Duplicate value rows for the same setting and user/role (should return nothing).
SELECT s."Code", v."SysAdminUnitId", count(*) AS "Rows"
FROM "SysSettingsValue" v JOIN "SysSettings" s ON s."Id" = v."SysSettingsId"
WHERE s."Code" LIKE 'UtkCst%'
GROUP BY s."Code", v."SysAdminUnitId"
HAVING count(*) > 1;

-- Bindings that hold these settings, and the row Ids they reference.
SELECT d."Name" AS "Binding", p."Name" AS "Package", octet_length(d."Data") AS "Bytes", d."ModifiedOn"
FROM "SysPackageSchemaData" d JOIN "SysPackage" p ON p."Id" = d."SysPackageId"
WHERE d."Name" IN ('UtkCstSysSettings', 'UtkCstSysSettingsValue');
