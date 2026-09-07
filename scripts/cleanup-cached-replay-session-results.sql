-- Removes the SessionResults rows, and the lap rows behind them, that hold a replay of the relay's
-- cache rather than a session that ran.
--
-- The timing system announces a scratch run of its own at every run change - Orbits sends
-- $B,95,"<name of the run that is ending>" - and that becomes a session here like any other. The
-- run that lands at the end of an event does not stay empty: its fresh state has no cars, so every
-- car update the feed sends names one it has never heard of, RMonitorDataProcessor answers a run of
-- those with a forced relay reset, and the relay replays its cached data set onto it. The scratch
-- run ends up holding a full copy of the race that just finished, and is listed beside it as a
-- second, complete-looking set of results for the same race.
--
-- cleanup-empty-session-results.sql cleared the rows that were *empty*; these are the ones that are
-- full of somebody else's race, so they need their own rule. SessionMonitor.IsCachedReplayOnly now
-- declines to write them. This applies the same rule to the rows already saved.
--
-- How a replay is told apart from a session that ran: the cache carries lap counts but never lap
-- times (SessionContext.SetLastLapTimeBeforeResetAsync exists to put them back from the lap history
-- after a reset, and a session change clears that history), and the run's own race clock never
-- started. So a replay has cars, not one of them with a lap time, and a zero race clock. Verified
-- against events 384, 385, 387, 388, 376, 297, 334 and 281: zero cars with a lap time in every
-- scratch run, and at least one in every session that ran.
--
-- What it deliberately leaves alone:
--
--   * Rows whose control log is the event's longest copy and that have no sibling results row to
--     hand it to. The log is cached per event rather than per session, so a scratch run picks up a
--     copy of it - usually a duplicate of one a real session already holds, which goes with the row.
--     Where the scratch run's copy is the only one, the log is moved to the session that ran before
--     the row is deleted, the same handover SessionMonitor.CarryControlLogForward makes in code.
--     Only a row with nowhere to hand its log is kept.
--
--     An earlier draft held back every row carrying any control log at all. On production that
--     skipped 20 of the 36 rows - including all four Mid-Ohio events this was written for - whose
--     logs were byte-identical duplicates of the real session's.
--   * Rows whose results live in the legacy Payload column. LoadSessionResults prefers Payload over
--     SessionState (V1 EventsController), so a row with a replayed SessionState can still be
--     serving real results from Payload.
--   * Sessions that are still live. A session in progress has not finished being written.
--   * Lap rows for any session that has even one lap with a time on it. That session saw a car
--     cross the line, whatever else is true of it.
--
-- The lap rows have to go with the results rows. EventsControllerBase.HasSomethingToShow lists a
-- session that has either results or laps, so deleting only the results would leave the session on
-- the list resolving to nothing at all - which is the empty entry this whole line of work set out
-- to remove.
--
-- CarLapLogs."LapData" is machine-written JSON, but it is a varchar rather than a jsonb column, so
-- every read of it goes through NULLIF first: an empty string would fail the cast and take the
-- whole transaction down with it halfway through.
--
-- Note SessionState is serialized with default options, so its own properties are PascalCase, while
-- CarPosition carries JsonPropertyName attributes and its properties are short - the last lap time
-- is 'ltm'. CarLapLogs."LapData" is a text column holding the same CarPosition shape, so it needs a
-- ::jsonb cast.
--
-- The file ends in ROLLBACK. Run it, read the four result sets, and only then change the last
-- statement to COMMIT and run it again. The handover writes to a row that is being kept, so read
-- dry run 0 with care.
--
-- Every row this touches is copied into a zz_backup_replay_* table in the same transaction before
-- it is changed, so a commit can be undone by inserting back from those. Drop them once the results
-- lists have been checked.

BEGIN;

CREATE TEMP TABLE cached_replay_sessions ON COMMIT DROP AS
WITH scored AS (
    SELECT r."EventId",
           r."SessionId",
           CASE WHEN jsonb_typeof(r."SessionState" -> 'CarPositions') = 'array'
                THEN jsonb_array_length(r."SessionState" -> 'CarPositions') ELSE 0 END AS state_cars,
           -- Cars carrying completed laps. A session gridded but never started has none, and is
           -- not a replay however blank it reads - the same third condition SessionMonitor applies.
           (SELECT count(*)
              FROM jsonb_array_elements(
                       CASE WHEN jsonb_typeof(r."SessionState" -> 'CarPositions') = 'array'
                            THEN r."SessionState" -> 'CarPositions' ELSE '[]'::jsonb END) AS car
             WHERE COALESCE((car ->> 'llp')::int, 0) > 0) AS cars_with_laps,
           CASE WHEN jsonb_typeof(r."Payload" -> 'cps') = 'array'
                THEN jsonb_array_length(r."Payload" -> 'cps') ELSE 0 END AS payload_cars,
           CASE WHEN jsonb_typeof(r."Payload" -> 'ee') = 'array'
                THEN jsonb_array_length(r."Payload" -> 'ee') ELSE 0 END AS payload_entries,
           CASE WHEN jsonb_typeof(r."ControlLogs") = 'array'
                THEN jsonb_array_length(r."ControlLogs") ELSE 0 END AS control_log_entries,
           -- The longest control log any other session of the same event has saved. The log is
           -- cached per event, so the scratch run picks up a copy of it - if a sibling holds at
           -- least as many entries this copy is redundant and can go with the row.
           (SELECT COALESCE(max(CASE WHEN jsonb_typeof(o."ControlLogs") = 'array'
                                     THEN jsonb_array_length(o."ControlLogs") ELSE 0 END), 0)
              FROM "SessionResults" o
             WHERE o."EventId" = r."EventId" AND o."SessionId" <> r."SessionId") AS sibling_control_log_entries,
           COALESCE(r."SessionState" ->> 'RunningRaceTime', '') AS running_race_time,
           -- Cars carrying a last lap time. A replayed car has none.
           (SELECT count(*)
              FROM jsonb_array_elements(
                       CASE WHEN jsonb_typeof(r."SessionState" -> 'CarPositions') = 'array'
                            THEN r."SessionState" -> 'CarPositions' ELSE '[]'::jsonb END) AS car
             WHERE COALESCE(car ->> 'ltm', '') <> '') AS cars_with_lap_time
      FROM "SessionResults" r
)
SELECT c."EventId", c."SessionId", c.state_cars,
       c.control_log_entries, c.sibling_control_log_entries
  FROM scored c
  JOIN "Sessions" s ON s."EventId" = c."EventId" AND s."Id" = c."SessionId"
 WHERE c.state_cars > 0
   AND c.cars_with_laps > 0
   AND c.cars_with_lap_time = 0
   -- Any all-zero clock, however it is punctuated: '', '00:00:00', '0:00:00', '00:00:00.000'.
   -- RaceTimeParser reads every one of them as zero, and this has to be the same rule.
   AND c.running_race_time ~ '^[0:]*(\.0*)?$'
   AND c.payload_cars = 0
   AND c.payload_entries = 0
   AND NOT s."IsLive";

-- A scratch run that holds the event's ONLY copy of the control log, or the longest one, cannot
-- just be deleted - the log would go with it. Hand it to the session that ran first, which is what
-- SessionMonitor.CarryControlLogForward does in code: the results row with the latest start, the
-- same one the code picks. Only then is the row free to go. A run whose log is the longest and that
-- has no sibling results row to hand it to gets no target here, and is held back below.
CREATE TEMP TABLE carried_control_logs ON COMMIT DROP AS
SELECT cr."EventId",
       cr."SessionId",
       (SELECT o."SessionId"
          FROM "SessionResults" o
         WHERE o."EventId" = cr."EventId" AND o."SessionId" <> cr."SessionId"
         ORDER BY o."Start" DESC
         LIMIT 1) AS target_session
  FROM cached_replay_sessions cr
 WHERE cr.control_log_entries > cr.sibling_control_log_entries;

-- Dry run 0: control logs about to be moved, and where to. Read this one first - it is the only
-- statement here that writes to a row that is being kept.
SELECT c."EventId", c."SessionId" AS from_session, c.target_session AS to_session,
       cr.control_log_entries AS entries_moved,
       cr.sibling_control_log_entries AS entries_the_target_has_now,
       CASE WHEN c.target_session IS NULL THEN 'HELD BACK - nowhere to put the log' ELSE 'moved' END AS outcome
  FROM carried_control_logs c
  JOIN cached_replay_sessions cr ON cr."EventId" = c."EventId" AND cr."SessionId" = c."SessionId"
 ORDER BY c."EventId";

-- The handover target is a row that is being KEPT, so back it up before overwriting its control
-- log. It is not in deletable_sessions and would not be covered by the backups further down.
CREATE TABLE IF NOT EXISTS "zz_backup_replay_control_log_targets" AS
SELECT t.*, now() AS backed_up_at FROM "SessionResults" t
  JOIN carried_control_logs c ON c."EventId" = t."EventId" AND c.target_session = t."SessionId" WITH NO DATA;
INSERT INTO "zz_backup_replay_control_log_targets"
SELECT t.*, now() FROM "SessionResults" t
  JOIN carried_control_logs c ON c."EventId" = t."EventId" AND c.target_session = t."SessionId";

UPDATE "SessionResults" t
   SET "ControlLogs" = src."ControlLogs"
  FROM carried_control_logs c
  JOIN "SessionResults" src ON src."EventId" = c."EventId" AND src."SessionId" = c."SessionId"
 WHERE t."EventId" = c."EventId"
   AND t."SessionId" = c.target_session;

-- Held back: a session with even one lap that has a time on it saw a car cross the line here, so it
-- keeps both its laps and its results row however the state reads. Decided once, up front, so both
-- deletes below work off the same set rather than off each other's leftovers.
CREATE TEMP TABLE deletable_sessions ON COMMIT DROP AS
SELECT cr."EventId", cr."SessionId", cr.state_cars
  FROM cached_replay_sessions cr
 WHERE (cr.control_log_entries <= cr.sibling_control_log_entries
        OR EXISTS (SELECT 1 FROM carried_control_logs c
                    WHERE c."EventId" = cr."EventId" AND c."SessionId" = cr."SessionId"
                      AND c.target_session IS NOT NULL))
   AND NOT EXISTS (
        SELECT 1 FROM "CarLapLogs" x
         WHERE x."EventId" = cr."EventId" AND x."SessionId" = cr."SessionId"
           AND COALESCE(NULLIF(x."LapData", '')::jsonb ->> 'ltm', '') <> '');

-- Dry run 1: the results rows the first DELETE matches, beside the session that really ran. A row
-- here whose car count and session name match a sibling session is the duplicate being removed.
SELECT d."EventId",
       d."SessionId",
       s."Name"      AS session_name,
       s."StartTime" AS session_start,
       d.state_cars  AS replayed_cars,
       (SELECT count(*) FROM "SessionResults" o
         WHERE o."EventId" = d."EventId" AND o."SessionId" <> d."SessionId") AS other_results_for_event
  FROM deletable_sessions d
  JOIN "Sessions" s ON s."EventId" = d."EventId" AND s."Id" = d."SessionId"
 ORDER BY d."EventId", d."SessionId";

-- Dry run 2: the lap rows the lap DELETE matches. None of these sessions has a lap with a time on
-- it, so nothing here recorded a car crossing the line. laps_per_car should read 1.00 - the replay
-- writes exactly one row per car - and max_lap should be the lap the car finished the real session
-- on. Anything reading well above 1.00 is not the shape this script is meant for; stop and look.
SELECT l."EventId",
       l."SessionId",
       count(*)                                          AS lap_rows,
       count(DISTINCT l."CarNumber")                     AS cars,
       round(count(*)::numeric / NULLIF(count(DISTINCT l."CarNumber"), 0), 2) AS laps_per_car,
       min(l."LapNumber")                                AS min_lap,
       max(l."LapNumber")                                AS max_lap
  FROM "CarLapLogs" l
  JOIN deletable_sessions d
    ON d."EventId" = l."EventId" AND d."SessionId" = l."SessionId"
 GROUP BY l."EventId", l."SessionId"
 ORDER BY l."EventId", l."SessionId";

-- Dry run 3: sessions held back because a lap with a time was found. These keep both their laps and
-- their results row; look at any that turn up here before committing.
SELECT cr."EventId", cr."SessionId",
       (SELECT count(*) FROM "CarLapLogs" l
         WHERE l."EventId" = cr."EventId" AND l."SessionId" = cr."SessionId") AS lap_rows
  FROM cached_replay_sessions cr
 WHERE NOT EXISTS (
        SELECT 1 FROM deletable_sessions d
         WHERE d."EventId" = cr."EventId" AND d."SessionId" = cr."SessionId")
 ORDER BY cr."EventId", cr."SessionId";

-- Copy everything that is about to change into backup tables first. These are ordinary tables, not
-- temp ones: they survive the commit, so the whole cleanup can be put back by inserting from them.
-- Drop them once the results lists have been eyeballed. The control-log handover is included, since
-- it writes to a row that is otherwise being kept.
CREATE TABLE IF NOT EXISTS "zz_backup_replay_session_results" AS
SELECT r.*, now() AS backed_up_at FROM "SessionResults" r
  JOIN deletable_sessions d ON d."EventId" = r."EventId" AND d."SessionId" = r."SessionId" WITH NO DATA;
INSERT INTO "zz_backup_replay_session_results"
SELECT r.*, now() FROM "SessionResults" r
  JOIN deletable_sessions d ON d."EventId" = r."EventId" AND d."SessionId" = r."SessionId";

CREATE TABLE IF NOT EXISTS "zz_backup_replay_car_lap_logs" AS
SELECT l.*, now() AS backed_up_at FROM "CarLapLogs" l
  JOIN deletable_sessions d ON d."EventId" = l."EventId" AND d."SessionId" = l."SessionId" WITH NO DATA;
INSERT INTO "zz_backup_replay_car_lap_logs"
SELECT l.*, now() FROM "CarLapLogs" l
  JOIN deletable_sessions d ON d."EventId" = l."EventId" AND d."SessionId" = l."SessionId";

CREATE TABLE IF NOT EXISTS "zz_backup_replay_car_last_laps" AS
SELECT c.*, now() AS backed_up_at FROM "CarLastLaps" c
  JOIN deletable_sessions d ON d."EventId" = c."EventId" AND d."SessionId" = c."SessionId" WITH NO DATA;
INSERT INTO "zz_backup_replay_car_last_laps"
SELECT c.*, now() FROM "CarLastLaps" c
  JOIN deletable_sessions d ON d."EventId" = c."EventId" AND d."SessionId" = c."SessionId";

DELETE FROM "CarLapLogs" l
 USING deletable_sessions d
 WHERE d."EventId" = l."EventId"
   AND d."SessionId" = l."SessionId";

DELETE FROM "SessionResults" r
 USING deletable_sessions d
 WHERE d."EventId" = r."EventId"
   AND d."SessionId" = r."SessionId";

-- CarLastLaps is bookkeeping for restoring lap tracking on a restart. With the session's laps gone
-- it points at nothing, and leaving it would let a restart baseline cars off the replay's lap
-- numbers all over again.
DELETE FROM "CarLastLaps" c
 USING deletable_sessions d
 WHERE d."EventId" = c."EventId"
   AND d."SessionId" = c."SessionId";

-- StatusApi caches the session list per event (SESSIONS_KEY), so restart the status-api pods after
-- committing or the removed sessions keep being served until the entries expire.

-- Change to COMMIT once the three result sets above look right.
ROLLBACK;
