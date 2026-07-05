# QueryRig SDK for Unity

Game analytics for [QueryRig](https://github.com/prhym/queryrig): batched,
durable event tracking with a runtime on/off switch. Implements the QueryRig
engine SDK spec (`docs/sdk-spec.md` in the queryrig repo).

- **Batched** — events queue in memory and flush every 5 s / 100 events, one
  HTTP request instead of dozens. `Track()` just stamps and enqueues;
  serialization, disk, and HTTP run on a worker thread — nothing ever blocks
  a frame.
- **Durable** — delivery failures spill to a disk queue
  (`persistentDataPath/queryrig/`) and replay with exponential backoff. Events
  carry client-generated `event_id`s, so retries are deduplicated server-side:
  crash-safe, duplicate-free, offline-tolerant. On shutdown the remaining
  queue is persisted and replays next launch.
- **Runtime-switchable** — telemetry on/off is never a rebuild: a
  `queryrig.json` config file, an env var, or the remote gate (flip the ingest
  key in the QueryRig dashboard and the installed fleet obeys within ~5
  minutes).
- **Zero dependencies** — plain C#, no packages beyond the Unity engine.

## Install

Package Manager → *Add package from git URL…*:

```
https://github.com/prhym/queryrig-unity.git
```

(or add `"com.queryrig.sdk": "https://github.com/prhym/queryrig-unity.git"`
to `Packages/manifest.json`.)

## Use

```csharp
using QueryRigSdk;

QueryRig.Track("level_complete", new Dictionary<string, object> {
    { "level", 3 }, { "duration_s", 82.5 },
});
QueryRig.SetPlayerId(steamId); // optional; defaults to a stable device id
```

The SDK self-initializes at startup — no scene objects, no prefabs.
`session_id`, `player_id`, `platform`, `build_version`, `timestamp`, and
`event_id` are stamped automatically (explicit properties win).
`session_start` / `session_end` events are emitted for you, with a 30-minute
idle rollover.

## Configure

Create `queryrig.json` — the key and endpoint come from the Keys tab of your
QueryRig project:

```json
{
  "api_key": "qr_ingest_...",
  "endpoint": "https://ingest.example.com/v1/events",
  "enabled": "remote"
}
```

Resolution order: built-in defaults < `StreamingAssets/queryrig.json` <
`queryrig.json` **next to the executable** (desktop builds) <
`persistentDataPath/queryrig.json` < `QUERYRIG_API_KEY` / `QUERYRIG_ENDPOINT`
/ `QUERYRIG_ENABLED` env vars < `-queryrig-*=` command-line args <
`QueryRig.Init(overrides)`. Promote the same binary from dev to playtest by
dropping a different `queryrig.json` next to the executable — never by
rebuilding.

Note: on Android, `StreamingAssets` is inside the APK and not readable via
`System.IO` — use `persistentDataPath`, env-var, or `Init()` configuration
there.

`enabled` is `true`, `false`, or `"remote"` (default):

| Value | Behavior |
|---|---|
| `false` | Hard off. Zero network calls — verifiable with a packet capture. |
| `true` | Send unconditionally. |
| `"remote"` | The server decides: the SDK polls `/v1/config` and obeys the ingest key's enabled state, so telemetry can be flipped after the build has shipped. |

Optional fields: `flush_interval_s` (5), `flush_max_events` (100),
`disk_queue_max_bytes` (8 MB), `sample_rate` (1.0, sampled per session so
funnels stay coherent).

## Conformance test

The package ships an editor-only conformance runner (never compiled into
builds). With the harness running (`npm run sdk-harness` in the queryrig
repo) and any project that has this package installed:

```
Unity.exe -batchmode -nographics -projectPath <project> ^
  -executeMethod QueryRigSdk.QueryRigConformance.Run -logFile -
```

Covers delivery + stamping, an outage (zero loss, zero duplicates after
disk-queue replay), the remote kill switch both directions, and hard-off
silence. Exit code 0 on pass, 2 if the harness is unreachable.
