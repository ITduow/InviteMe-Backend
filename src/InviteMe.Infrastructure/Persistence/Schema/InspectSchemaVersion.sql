-- Read-only initial classification. Column signatures do not prove full schema equivalence.
SELECT current_database() AS database_name,
    CASE
        WHEN to_regclass('inviteme.guests') IS NULL THEN 'EMPTY_OR_UNKNOWN'
        WHEN EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='inviteme' AND table_name='guests' AND column_name='status')
         AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='inviteme' AND table_name='rsvps' AND column_name='party_size')
         AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='inviteme' AND table_name='gifts' AND column_name='participant_id') THEN 'V1_CANDIDATE'
        WHEN EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='inviteme' AND table_name='guests' AND column_name='record_status')
         AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='inviteme' AND table_name='rsvps' AND column_name='confirmed_party_size')
         AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='inviteme' AND table_name='gifts' AND column_name='guest_id') THEN 'V2_CANDIDATE'
        ELSE 'MIXED_OR_CUSTOM'
    END AS schema_signature,
    to_regclass('inviteme."__EFMigrationsHistory"') IS NOT NULL AS has_ef_history;

SELECT table_name, column_name, data_type, is_nullable, column_default
FROM information_schema.columns
WHERE table_schema='inviteme'
  AND table_name IN ('guest_groups','guests','invitations','rsvps','rsvp_history','tables','gifts','audit_logs')
ORDER BY table_name, ordinal_position;
