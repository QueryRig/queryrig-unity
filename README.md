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

1. In Unity, open **Window → Package Manager**.
2. Click the **+** button in the top-left of that window, choose
   **Add package from git URL…**, and paste:

   ```
   https://github.com/prhym/queryrig-unity.git
   ```
3. Click **Add** and wait for Unity to finish importing.

(Equivalent, if you'd rather edit the file directly: add
`"com.queryrig.sdk": "https://github.com/prhym/queryrig-unity.git"` to
`Packages/manifest.json`.)

The SDK self-initializes at startup — no scene objects, no prefabs to drag
in, nothing further to configure here.

## Use

Unity doesn't ship a visual-scripting system that maps cleanly onto this
SDK's API, so tracking an event means writing one call in a C# script —
everything else (batching, retries, sessions) happens automatically. The
smallest way to add that call so it can be wired up from the Inspector
afterward, with no further code:

1. In the **Project** window, right-click → **Create → C# Script**, name it
   e.g. `TrackLevelComplete`, and double-click to open it in your editor.
2. Replace its contents with:

   ```csharp
   using UnityEngine;
   using System.Collections.Generic;
   using QueryRigSdk;

   public class TrackLevelComplete : MonoBehaviour
   {
       public void Fire()
       {
           QueryRig.Track("level_complete", new Dictionary<string, object> {
               { "level", 3 }, { "duration_s", 82.5 },
           });
       }
   }
   ```
3. Save, drag the script onto any GameObject (a Button works well), then in
   that Button's **On Click ()** list in the Inspector, click **+**, drag the
   same GameObject in, and pick `TrackLevelComplete → Fire()` from the
   function dropdown. Clicking the button now tracks the event — no more
   code needed for that hookup.

Everything inside `Fire()` is yours to edit — swap `"level_complete"` for any
event name, and the dictionary for whatever details matter (or drop the
dictionary entirely: `QueryRig.Track("item_pickup")`). Duplicate the script
per event, or add more public methods to one script for a small set of
related events.

Called directly from your own code instead, it's simpler still:

```csharp
using QueryRigSdk;

QueryRig.Track("level_complete", new Dictionary<string, object> {
    { "level", 3 }, { "duration_s", 82.5 },
});
QueryRig.SetPlayerId(steamId); // optional; defaults to a stable device id
```

`session_id`, `player_id`, `platform`, `build_version`, `timestamp`, and
`event_id` are stamped automatically (explicit properties win).
`session_start` / `session_end` events are emitted for you, with a 30-minute
idle rollover.

### Your fields, your names

Nothing is reserved. Apart from `timestamp` and `event_id`, every field you
send is stored under the name you gave it — including `event_name`, which is
just the name the dashboard's query templates look for by default. If your
game already has its own vocabulary, send it and skip the event name entirely:

```csharp
QueryRig.TrackFields(new Dictionary<string, object> {
    { "kind", "boss_defeated" }, { "boss", "warden" }, { "attempts", 4 },
});
```

Then point the templates at `kind` instead of `event_name` (every template
takes the field name). Use `Track()` when you have no reason not to — matching
the convention means charts work with no configuration.

Values are stored as text, so query them quoted (`properties.attempts = '4'`)
and cast when you need arithmetic (`toInt64OrNull(toString(properties.attempts))`).
Pass natural types here — the conversion happens server-side.

## Configure

1. In the **Project** window, make sure a `StreamingAssets` folder exists
   directly under `Assets` — if it doesn't, right-click `Assets` →
   **Create → Folder**, name it `StreamingAssets`.
2. Inside it, create a text file named `queryrig.json` (any text editor
   works — Unity doesn't need to know about it as an asset type).
3. Fill in `api_key` and `endpoint` — both are shown on the **Keys** tab of
   your QueryRig project (the dashboard at your QueryRig site). Leave the
   quotes in place; only change what's between them:

```json
{
  "api_key": "qr_ingest_...",
  "endpoint": "https://ingest.example.com/v1/events",
  "enabled": "remote"
}
```

This file works for testing in the Editor's Play mode too — no build
required to try it.

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
