# Track map replay

Replays recorded GPS from a real event through the production track-map and track-position code, and
reports what it produced.

The GPS features — learning a track map, locating start/finish on it, and reporting where each car is
around the lap — are only as good as the data they meet. Unit tests exercise them against a synthetic
circle, which is convex, evenly sampled and never passes near itself. Real feeds are none of those
things. Two defects that a full green unit-test suite did not catch showed up on the first replay of
a real race:

- A learned map of **10,094 m for a 4,088 m circuit**, which would have been persisted for the whole
  event, because one lap buffer had swallowed a start/finish crossing and nothing checked the result.
- A start/finish calibration built on that map, off by 14% of a lap.

Run this before trusting the GPS features at a new event, and after changing anything they depend on.

## Getting the data

Two exports, both from the production database. `EventStatusLogs` holds the raw Flagtronics vehicle
records (`Type = 'ftcar'`); `CarLapLogs` holds lap completions, which are what the map builder keys
its lap boundaries off.

Pick an event and session with a decent volume of GPS:

```sql
SELECT "EventId", "SessionId", COUNT(*) AS n, MIN("Timestamp"), MAX("Timestamp")
FROM "EventStatusLogs" WHERE "Type" = 'ftcar'
GROUP BY "EventId", "SessionId" ORDER BY n DESC LIMIT 15;
```

Then export the samples — one row per car per record, so the JSON array is unnested:

```sql
SELECT e."Id" AS seq,
       EXTRACT(EPOCH FROM e."Timestamp")::bigint AS ts,
       v->>'carNumber' AS car,
       COALESCE((v->>'lat')::float8, 0) AS lat,
       COALESCE((v->>'lon')::float8, 0) AS lon,
       COALESCE((v->>'speed')::int, -1) AS speed,
       COALESCE((v->>'flaggingZone')::int, -1) AS zone
FROM "EventStatusLogs" e, LATERAL jsonb_array_elements(e."Data"::jsonb) v
WHERE e."Type" = 'ftcar' AND e."EventId" = 297 AND e."SessionId" = 64
ORDER BY e."Id";
```

and the lap completions:

```sql
SELECT "CarNumber" AS car, "LapNumber" AS lap, EXTRACT(EPOCH FROM "Timestamp")::bigint AS ts
FROM "CarLapLogs" WHERE "EventId" = 297 AND "SessionId" = 64 ORDER BY "Timestamp";
```

Write each to a file. The reader takes pipe-separated columns and ignores the header and any
trailing row count, which is what the `dbq` helper in the `redmist-debug` skill emits:

```bash
dotnet run --no-build q.sql > ftcar.txt
```

## Running it

```bash
dotnet run -- ftcar.txt laps.txt [declared-track-miles]
```

The declared track length is optional but worth supplying — it is what `TrackMapService` checks a
learned map against, so passing it exercises that path. It comes from the timing system's
`$E,"TRACKLENGTH"` record, or from the circuit's published length.

## Reading the output

**MAP** — where the map came from and what it measured. Compare the length against the real circuit:
a polyline through sampled fixes cuts every corner, so a few percent short is expected and correct.
Much longer means a buffer swallowed a lap boundary. Rejections against the declared length are
printed as they happen.

**START/FINISH** — where calibration put the line, and how tightly the crossings agreed. The offset
should be small: the map's origin and the crossings are both anchored to the same lap-increment
event, so they are measuring nearly the same thing. A large offset, or crossings agreeing only to
within hundreds of meters, means the map underneath is wrong.

**POSITION QUALITY** — one row for the map as learned, then for maps with later laps folded in to
raise the point density. Watch what moves between rows: anything that improves with density was
measuring the map's resolution, and anything that does not is measuring the cars. On the Road Atlanta
8-hour, quadrupling the density moved the median lateral offset from 5.2 m to 3.1 m and left the p90,
the p99 and the rejection rate untouched — which is how we know the 7.2% rejected by the 30 m gate
are cars genuinely off the racing line rather than the gate fighting a coarse map.

**SIGNAL BARS** — the distribution `TelemetrySignalTracker` would have produced, and what the
`>= 4` gate in `GpsLapPositionEnricher` would admit. Fix quality is close to binary in practice, so
the gap between a `>= 4` and a `>= 3` gate is small; if it ever looks large, the devices at that
event are behaving differently from the ones this was tuned against.

## What it does not reproduce

- **Lap timestamps are when the logger wrote the row**, not when the car crossed the line. Live, the
  builder sees `CarPosition.LastLapCompleted` from session state, which moves sooner. Lap boundaries
  here are therefore slightly later and more irregular than in production — which makes this a
  pessimistic test of map learning, not an optimistic one.
- **`EventStatusLogs.Timestamp` has whole-second resolution**, so anything time-scaled (the teleport
  allowance, the anchor window) is evaluated against a coarser clock than live, and rejects a little
  more than production would.
- Only the Flagtronics lane. The external timing source carries its own positions and is not covered.
