SELECT
    CAST(target_data AS XML)
FROM sys.dm_xe_database_session_targets t
JOIN sys.dm_xe_database_sessions s
    ON t.event_session_address = s.address
WHERE s.name = 'DeadlockCapture';