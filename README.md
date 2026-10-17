# QueryRig SDK for Unity

Game analytics for [QueryRig](https://github.com/prhym/queryrig): batched,
durable event tracking with a runtime on/off switch. Implements the QueryRig
engine SDK spec (`docs/sdk-spec.md` in the queryrig repo).

- **Batched** — events queue in memory and flush every 5 s / 100 events, one
  HTTP request instead of dozens. `Track()` validates, stamps, and serializes
  an immutable snapshot. Disk and HTTP run on a worker thread.
- **Persistent retries** — batches are written to a scoped disk queue before
  sending and retained until acknowledged. Retries preserve event IDs and
  timestamps and honor exponential backoff. See the limits below.
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

The default player id is pseudonymous: a hash of the device id scoped to your
project's endpoint, so the raw hardware id never leaves the device and the
same machine has unrelated ids in different games. If you pass your own id,
prefer an opaque account id over a name or email address, and if your game is
played by children, send only pseudonymous ids (see the QueryRig terms).

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
  "endpoint": "https://3fa91c0e.qrig.net/v1/events",
  "enabled": "remote"
}
```

Every project has its own endpoint, `https://<ingest id>.qrig.net/v1/events`,
and it accepts only that project's key, so always copy the two together. The
endpoint must use `https://` (plain `http://` is allowed only for loopback
addresses, for local development). With a key but no usable endpoint, the SDK
logs one error at startup and stays off for that run.

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
| `true` | Collect locally; authentication failures pause sending, with gate checks allowing recovery. |
| `"remote"` | The server decides: the SDK polls `/v1/config` and obeys the ingest key's enabled state, so telemetry can be flipped after the build has shipped. |

Optional fields: `flush_interval_s` (5), `flush_max_events` (100),
`disk_queue_max_bytes` (8 MB), `sample_rate` (1.0, sampled per session so
funnels stay coherent).

## Delivery limits and upgrading

Event uploads allow 30 seconds for a complete acknowledgement, leaving room
for the server's 15-second ClickHouse timeout and R2 fallback. Config checks
still time out after 5 seconds. A successful R2 acknowledgement completes the
batch; a timeout retains it for backoff/retry with the same IDs and timestamps.
Shutdown cancels outstanding uploads instead of waiting for the deadline.
This does not guarantee a response within 30 seconds on every network.

`Track`, `TrackFields`, and `SetPlayerId` are thread-safe after initialization.
Initialize and shut down on Unity's main thread, without concurrent lifecycle
calls. Do not mutate input collections during a tracking call; changes after
it returns cannot alter the queued event. Only JSON-compatible values are
accepted; cycles, non-finite numbers, excessive depth, and events exceeding
200 KB in UTF-8 are discarded individually with a warning.

Batches are written via temporary files and renamed before HTTP starts.
Shutdown cancels HTTP, joins the worker, and attempts to persist pending events;
local disk operations can still block or fail. Events still in memory can be
lost on a crash before a flush. Memory (10,000 events) and disk (8 MB by default)
are capped and evict oldest entries. This is not an unlimited or exactly-once
delivery guarantee. Server retention still applies to offline events.

Queue and gate-cache paths are now
`persistentDataPath/queryrig/<endpoint-and-key-fingerprint>/`. Changing the
endpoint **or key** isolates the old queue, including rotations within the same
project. Drain before rotating credentials. Old unscoped files are left intact
but are **not automatically replayed**; recover them only after verifying the
destination project. The backend must preserve historical timestamps for
retry-safe deduplication beyond seven days.

How the SDK reacts when it cannot deliver:

| What happens | SDK behavior |
|---|---|
| 401 (key unknown, disabled, or used on another project's endpoint) | Dormant: keeps queued events, re-checks the remote gate on its poll interval. |
| 403 from the edge (plain HTTP, or a path outside the ingest API) | Logs one error naming the endpoint and stops delivery until the game restarts. Queued events are kept. |
| Endpoint host does not resolve (the project is deleted, or paused while clients kept sending) | Treated as a network error: keeps events on disk and backs off (5 s doubling to 5 min). |

### Upgrading to 0.2.0

- `endpoint` is now required with `api_key` and must use `https://` off
  loopback. A config that relied on the old shared ingest host must switch to
  the project's own endpoint from the Keys tab.
- The default `player_id` is now a hash (see Use), so players tracked by
  0.1.0 with the default id get a new id after the upgrade. Ids you set with
  `SetPlayerId` are unchanged.
- Platforms without a device id keep their random fallback id across key
  rotations: it now lives at `persistentDataPath/queryrig/device_id`.

## Conformance test

The package ships an editor-only conformance runner (never compiled into
builds). Use a **disposable test project**: this runner clears its local QueryRig
data. With the harness running (`npm run sdk-harness` in the queryrig repo) and
the package installed in that test project:

```
Unity.exe -batchmode -nographics -projectPath <project> ^
  -executeMethod QueryRigSdk.QueryRigConformance.Run -logFile -
```

Covers delivery + stamping (including the hashed player id), an outage (zero
loss, zero duplicates after disk-queue replay), an edge 403 treated as
permanent without losing the batch, an unresolvable host backed off as a
network error (live and at cold start), inert startup with a missing or
non-HTTPS endpoint, the remote kill switch both directions, and hard-off
silence. Exit code 0 on pass, 2 if the harness is unreachable.

Standalone runtime regressions (actual runtime sources with Unity API shims):
`dotnet run --project Tests~/Regression.csproj`. This does not replace testing
inside Unity or on each supported target platform.

Compile against an installed Unity editor's real API assemblies:
`dotnet build Tests~/UnityApiCompatibility.csproj -p:UnityEditorPath="<editor version directory>"`.
