-- Removes the SessionResults rows that hold no results.
--
-- The timing system announces a scratch run of its own at every run change - Orbits sends
-- $B,95,"<name of the run that is ending>" - and that arrives as a session change like any other,
-- so the scratch run becomes a session in its own right. It is normally superseded by the real run
-- seconds later, but one that lands at the end of an event never is: nothing is ever applied to it,
-- and it was still written out as a results row. On the apps' results list that shows as a second,
-- empty entry beside the session that actually ran (event 373 "New run" is the reported case).
--
-- SessionMonitor.HasSomethingToShow now declines to write these rows, and the session lists in
-- EventsControllerBase leave out sessions that have neither a results row nor laps. This applies
-- the same rule to the rows already saved, so past events stop showing the empty entry as well.
--
-- What it deliberately leaves alone:
--
--   * Rows carrying control-log entries. The log is cached per event rather than per session, so a
--     scratch run picks up a copy of it - and for events 87, 141 and 153 that copy is the only one
--     there is. Those events keep their empty entry; the alternative is destroying their log.
--   * Rows whose results live in the legacy Payload column. LoadSessionResults prefers Payload over
--     SessionState (V2 EventsController), so a row with an empty SessionState can still be serving
--     real results. Note the Payload's JSON uses short names - car positions are "cps", entries are
--     "ee" - so it cannot be checked with the same keys as SessionState.
--
-- Every count is taken with a jsonb_typeof guard: a null list serializes as a JSON null, which
-- COALESCE does not catch and jsonb_array_length refuses.
--
-- The file ends in ROLLBACK. Run it, read the two result sets, and only then change the last
-- statement to COMMIT and run it again. The delete is not reversible.

BEGIN;

-- Dry run: the rows the DELETE below matches.
WITH scored AS (
    SELECT r."EventId", r."SessionId",
           CASE WHEN jsonb_typeof(r."SessionState" -> 'CarPositions') = 'array'
                THEN jsonb_array_length(r."SessionState" -> 'CarPositions') ELSE 0 END AS state_cars,
           CASE WHEN jsonb_typeof(r."SessionState" -> 'EventEntries') = 'array'
                THEN jsonb_array_length(r."SessionState" -> 'EventEntries') ELSE 0 END AS state_entries,
           CASE WHEN jsonb_typeof(r."Payload" -> 'cps') = 'array'
                THEN jsonb_array_length(r."Payload" -> 'cps') ELSE 0 END AS payload_cars,
           CASE WHEN jsonb_typeof(r."Payload" -> 'ee') = 'array'
                THEN jsonb_array_length(r."Payload" -> 'ee') ELSE 0 END AS payload_entries,
           CASE WHEN jsonb_typeof(r."ControlLogs") = 'array'
                THEN jsonb_array_length(r."ControlLogs") ELSE 0 END AS control_log_entries
    FROM "SessionResults" r
)
SELECT c."EventId", c."SessionId", s."Name", s."StartTime"
FROM scored c
JOIN "Sessions" s ON s."EventId" = c."EventId" AND s."Id" = c."SessionId"
WHERE c.state_cars = 0 AND c.state_entries = 0
  AND c.payload_cars = 0 AND c.payload_entries = 0
  AND c.control_log_entries = 0
ORDER BY c."EventId", c."SessionId";

WITH scored AS (
    SELECT r."EventId", r."SessionId",
           CASE WHEN jsonb_typeof(r."SessionState" -> 'CarPositions') = 'array'
                THEN jsonb_array_length(r."SessionState" -> 'CarPositions') ELSE 0 END AS state_cars,
           CASE WHEN jsonb_typeof(r."SessionState" -> 'EventEntries') = 'array'
                THEN jsonb_array_length(r."SessionState" -> 'EventEntries') ELSE 0 END AS state_entries,
           CASE WHEN jsonb_typeof(r."Payload" -> 'cps') = 'array'
                THEN jsonb_array_length(r."Payload" -> 'cps') ELSE 0 END AS payload_cars,
           CASE WHEN jsonb_typeof(r."Payload" -> 'ee') = 'array'
                THEN jsonb_array_length(r."Payload" -> 'ee') ELSE 0 END AS payload_entries,
           CASE WHEN jsonb_typeof(r."ControlLogs") = 'array'
                THEN jsonb_array_length(r."ControlLogs") ELSE 0 END AS control_log_entries
    FROM "SessionResults" r
), empty AS (
    SELECT "EventId", "SessionId" FROM scored
    WHERE state_cars = 0 AND state_entries = 0
      AND payload_cars = 0 AND payload_entries = 0
      AND control_log_entries = 0
)
DELETE FROM "SessionResults" r
USING empty e
WHERE r."EventId" = e."EventId" AND r."SessionId" = e."SessionId";

-- Expected: 88 rows as of 2026-08-21.

-- Change to COMMIT once the two result sets above look right.
ROLLBACK;
