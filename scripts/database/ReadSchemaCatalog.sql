-- Read-only catalog export for comparison with authoritative DDL.
-- Contains definitions only, never business rows, passwords or tokens.
BEGIN READ ONLY;
SET LOCAL search_path TO inviteme, public;
WITH definitions AS (
    SELECT 'relation' AS kind, c.relname::text AS name,
        jsonb_build_object('kind', c.relkind, 'rowSecurity', c.relrowsecurity, 'forceRowSecurity', c.relforcerowsecurity) AS definition
    FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname='inviteme' AND c.relkind IN ('r','p','v','m','S')
    UNION ALL
    SELECT 'column', table_name||'.'||column_name,
        jsonb_build_object('position',ordinal_position,'type',udt_name,'nullable',is_nullable,
            'length',character_maximum_length,'precision',numeric_precision,'scale',numeric_scale,
            'datetimePrecision',datetime_precision,'default',column_default,'identity',is_identity,'generated',is_generated,
            'collation',collation_name)
    FROM information_schema.columns WHERE table_schema='inviteme'
    UNION ALL
    SELECT 'constraint', r.relname||'.'||c.conname, to_jsonb(pg_get_constraintdef(c.oid))
    FROM pg_constraint c JOIN pg_class r ON r.oid=c.conrelid JOIN pg_namespace n ON n.oid=r.relnamespace
    WHERE n.nspname='inviteme'
    UNION ALL
    SELECT 'index', tablename||'.'||indexname, to_jsonb(indexdef)
    FROM pg_indexes WHERE schemaname='inviteme'
    UNION ALL
    SELECT 'view', viewname, to_jsonb(definition) FROM pg_views WHERE schemaname='inviteme'
    UNION ALL
    SELECT 'function', p.proname||'('||pg_get_function_identity_arguments(p.oid)||')',
        jsonb_build_object('definition',pg_get_functiondef(p.oid),'configuration',p.proconfig)
    FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='inviteme' AND p.prokind='f'
    UNION ALL
    SELECT 'trigger', r.relname||'.'||t.tgname,
        jsonb_build_object('definition',pg_get_triggerdef(t.oid),'enabled',t.tgenabled)
    FROM pg_trigger t JOIN pg_class r ON r.oid=t.tgrelid JOIN pg_namespace n ON n.oid=r.relnamespace
    WHERE n.nspname='inviteme' AND NOT t.tgisinternal
    UNION ALL
    SELECT 'policy', tablename||'.'||policyname,
        jsonb_build_object('permissive',permissive,'roles',roles,'command',cmd,'using',qual,'check',with_check)
    FROM pg_policies WHERE schemaname='inviteme'
)
SELECT coalesce(jsonb_agg(jsonb_build_object('kind',kind,'name',name,'definition',definition) ORDER BY kind,name),'[]'::jsonb)
FROM definitions;
COMMIT;
