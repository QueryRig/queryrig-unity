using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace QueryRigSdk
{
    /// <summary>
    /// Headless conformance test (spec §7) — drives the SDK against the
    /// QueryRig SDK harness (queryrig repo: `npm run sdk-harness`, port 8788).
    ///
    /// Run from any project with the package installed:
    ///   Unity.exe -batchmode -nographics -projectPath &lt;proj&gt; -executeMethod QueryRigSdk.QueryRigConformance.Run -logFile -
    /// Env: QUERYRIG_TEST_HARNESS to point at a non-default harness.
    ///
    /// Exit code 0 on pass, 1 on assertion failures, 2 if the harness is down.
    /// </summary>
    public static class QueryRigConformance
    {
        private static string _harness = "http://127.0.0.1:8788";
        private static int _failures;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        public static void Run()
        {
            try
            {
                EditorApplication.Exit(RunAll());
            }
            catch (Exception e)
            {
                Debug.LogError($"conformance: unhandled exception: {e}");
                EditorApplication.Exit(3);
            }
        }

        private static int RunAll()
        {
            string env = Environment.GetEnvironmentVariable("QUERYRIG_TEST_HARNESS");
            if (!string.IsNullOrEmpty(env))
            {
                _harness = env;
            }

            if (Req("GET", "/v1/health") == null)
            {
                Debug.LogError($"conformance: harness not reachable at {_harness} — start it with `npm run sdk-harness` in the queryrig repo");
                return 2;
            }
            Req("POST", "/__harness/reset", "{}");
            string dataDir = Path.Combine(Application.persistentDataPath, "queryrig");
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, true);
            }

            TestDeliveryAndStamping();
            TestSlowFallback();
            TestOutageNoLoss();
            TestEdgeForbiddenIsPermanent();
            TestUnresolvableHostBacksOff();
            TestRemoteKillSwitch();
            TestHardOffIsSilent();
            TestBadEndpointIsInert();
            TestBlockedHostColdStart();

            QueryRig.Shutdown();
            if (_failures == 0)
            {
                Debug.Log("conformance: ALL PASS");
            }
            return _failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------- tests

        private static QueryRigSettings Settings(object enabled) => new QueryRigSettings
        {
            ApiKey = "test-key",
            Endpoint = _harness + "/v1/events",
            Enabled = enabled,
            FlushIntervalS = 999, // flushes are driven explicitly
            GatePollIntervalS = 999,
        };

        private static void TestDeliveryAndStamping()
        {
            QueryRig.Init(Settings(true));
            QueryRig.Track("level_complete", new Dictionary<string, object> { { "level", 3 }, { "duration_s", 82.5 } });
            QueryRig.Track("item_pickup", new Dictionary<string, object> { { "item", "sword" } });
            QueryRig.Track("enemy_killed", null);
            // No event_name at all: nothing is reserved, so an event is any set
            // of fields. Proves the server accepts the studio's own vocabulary.
            QueryRig.TrackFields(new Dictionary<string, object> { { "kind", "boss_defeated" }, { "boss", "warden" } });
            QueryRig.Flush();

            // 4 tracked + the automatic session_start.
            Check(WaitUntil(() => HarnessRows() >= 5, 5.0), "delivery: 5 rows arrive after flush");

            var rows = ReqArray("/__harness/rows");
            var names = new HashSet<string>();
            var sessions = new HashSet<string>();
            var ids = new HashSet<string>();
            bool stamped = true;
            Dictionary<string, object> levelProps = null;
            Dictionary<string, object> unnamed = null;
            foreach (var row in rows.OfType<Dictionary<string, object>>())
            {
                // Only timestamp/received_at/event_id (+ project_id) are
                // columns; every other field lands in `properties` by name.
                var p = (Dictionary<string, object>)row["properties"];
                string name = p.TryGetValue("event_name", out object n) ? (string)n : null;
                names.Add(name);
                sessions.Add((string)p["session_id"]);
                ids.Add((string)row["event_id"]);
                stamped = stamped && !string.IsNullOrEmpty((string)p["player_id"])
                    && !string.IsNullOrEmpty((string)p["platform"]);
                if (name == "level_complete")
                {
                    levelProps = p;
                }
                else if (name == null)
                {
                    unnamed = p;
                }
            }
            Check(names.Contains("session_start") && names.Contains("level_complete"),
                "delivery: session_start + tracked events present");
            Check(stamped, "stamping: player_id + platform set on every row");
            string player = rows.OfType<Dictionary<string, object>>()
                .Select(r => (string)((Dictionary<string, object>)r["properties"])["player_id"]).FirstOrDefault() ?? "";
            Check(player.Length == 32 && player != SystemInfo.deviceUniqueIdentifier,
                "stamping: default player_id is a hash, not the raw device id");
            Check(sessions.Count == 1, "stamping: one session_id across the batch");
            Check(ids.Count == rows.Count, "stamping: event_ids unique");
            Check(unnamed != null && (string)unnamed["kind"] == "boss_defeated"
                && (string)unnamed["boss"] == "warden",
                "fields: an event with no event_name is accepted and keeps its own fields");
            // Every property scalar is stored as a string, whatever the client
            // sent — one type per path, so equality in a query always works.
            Check(levelProps != null && (string)levelProps["level"] == "3"
                && (string)levelProps["duration_s"] == "82.5",
                "stamping: property values arrive as strings");

            bool userAgent = ReqArray("/__harness/requests").OfType<Dictionary<string, object>>()
                .Any(r => ((string)r["userAgent"]).Contains("queryrig-unity/"));
            Check(userAgent, "wire: User-Agent identifies the SDK");
        }

        private static void TestSlowFallback()
        {
            Thread.Sleep(200);
            int before = HarnessRows(), requestsBefore = EventsRequestCount();
            Req("POST", "/__harness/mode", "{\"events\":\"slow-fallback\"}");
            QueryRig.Track("slow_fallback", null);
            QueryRig.Flush();
            Check(WaitUntil(() => HarnessRows() >= before + 1, 22.0),
                "fallback: a 16-second R2 acknowledgement arrives");
            Thread.Sleep(300);
            QueryRig.Flush();
            Thread.Sleep(500);
            Check(EventsRequestCount() == requestsBefore + 1, "fallback: exactly one POST, no premature retry");
            Req("POST", "/__harness/mode", "{\"events\":\"ok\"}");
        }

        private static void TestOutageNoLoss()
        {
            int before = HarnessRows();
            Req("POST", "/__harness/mode", "{\"events\":\"unavailable\"}");
            QueryRig.Track("during_outage_1", null);
            QueryRig.Track("during_outage_2", null);
            QueryRig.Track("during_outage_3", null);
            QueryRig.Flush();
            Thread.Sleep(1500);
            Check(HarnessRows() == before, "outage: nothing stored while backend is down");

            Req("POST", "/__harness/mode", "{\"events\":\"ok\"}");
            Thread.Sleep(7000); // ride out the first retry backoff (~5s + jitter)
            QueryRig.Flush(); // empty queue -> drains one disk batch
            Check(WaitUntil(() => HarnessRows() >= before + 3, 10.0),
                "outage: disk queue drains after recovery — zero loss");
            var state = ReqObject("/__harness/state");
            Check(state != null && (double)state["duplicates"] == 0,
                "outage: replay produced no duplicates");
        }

        private static void TestEdgeForbiddenIsPermanent()
        {
            // Edge 403 (plain HTTP or a path outside the ingest API): stop
            // sending, keep the batch, no retry until restart.
            QueryRig.Init(Settings(true));
            QueryRig.Flush();
            Thread.Sleep(300);
            int rowsBefore = HarnessRows();
            Req("POST", "/__harness/mode", "{\"events\":\"edge-forbidden\"}");
            int before = EventsRequestCount();
            QueryRig.Track("edge_refused", null);
            QueryRig.Flush();
            Check(WaitUntil(() => EventsRequestCount() == before + 1, 5.0), "edge 403: the batch is attempted once");
            Check(WaitUntil(() => !QueryRig.IsEnabled, 5.0), "edge 403: the SDK reports itself off");
            Thread.Sleep(6000); // longer than the first retry backoff would be
            QueryRig.Flush();
            Thread.Sleep(500);
            Check(EventsRequestCount() == before + 1, "edge 403: no retry, even after the backoff window");
            Check(QueueFileCount() > 0, "edge 403: the refused batch is kept on disk");

            // A restart with the corrected endpoint (same here) delivers it.
            Req("POST", "/__harness/mode", "{\"events\":\"ok\"}");
            QueryRig.Init(Settings(true));
            for (int i = 0; i < 4; i++)
            {
                QueryRig.Flush();
                Thread.Sleep(300);
            }
            Check(WaitUntil(() => HarnessRows() >= rowsBefore + 1, 10.0), "edge 403: kept events arrive after a restart");
            var state = ReqObject("/__harness/state");
            Check(state != null && (double)state["duplicates"] == 0, "edge 403: no duplicates after the restart");
        }

        private static void TestUnresolvableHostBacksOff()
        {
            // A DNS-blocked hostname is a network error: back off, keep, recover.
            QueryRig.Init(Settings(true));
            QueryRig.Flush();
            Thread.Sleep(300);
            int rowsBefore = HarnessRows();
            int attemptsBefore = UnreachableAttempts();
            Req("POST", "/__harness/mode", "{\"events\":\"unresolvable\"}");
            QueryRig.Track("while_unreachable", null);
            QueryRig.Flush();
            Check(WaitUntil(() => UnreachableAttempts() > attemptsBefore, 5.0), "unreachable: the batch is attempted");
            QueryRig.Flush();
            QueryRig.Flush();
            Thread.Sleep(1000);
            Check(UnreachableAttempts() == attemptsBefore + 1, "unreachable: backoff holds further attempts");
            Check((int)ClientField("_consecutiveFailures") >= 1 && QueueFileCount() > 0, "unreachable: counted as a failure, batch kept");
            Check(QueryRig.IsEnabled, "unreachable: not treated as a permanent refusal");

            Req("POST", "/__harness/mode", "{\"events\":\"ok\"}");
            Thread.Sleep(7000); // ride out the first retry backoff (~5s + jitter)
            QueryRig.Flush();
            Check(WaitUntil(() => HarnessRows() >= rowsBefore + 1, 10.0), "unreachable: recovers by itself and delivers");
        }

        private static void TestRemoteKillSwitch()
        {
            QueryRig.Init(Settings("remote"));
            Check(WaitUntil(() => QueryRig.IsEnabled, 5.0), "gate: starts enabled with a live key");

            Req("POST", "/__harness/keys", "{\"key\":\"test-key\",\"disabled\":true}");
            QueryRig.RefreshGate();
            Check(WaitUntil(() => !QueryRig.IsEnabled, 5.0), "gate: dashboard disable turns the SDK off");

            int eventsBefore = EventsRequestCount();
            QueryRig.Track("should_be_dropped", null);
            QueryRig.Flush();
            Thread.Sleep(1000);
            Check(EventsRequestCount() == eventsBefore, "gate: dormant SDK sends nothing");

            Req("POST", "/__harness/keys", "{\"key\":\"test-key\",\"disabled\":false}");
            QueryRig.RefreshGate();
            Check(WaitUntil(() => QueryRig.IsEnabled, 5.0), "gate: re-enable turns the SDK back on");
        }

        private static void TestHardOffIsSilent()
        {
            // Init first: it shuts down the PREVIOUS instance (flushing its
            // session_end) before hard-off takes effect. Reset after, so the
            // request log starts clean.
            QueryRig.Init(Settings(false));
            Req("POST", "/__harness/reset", "{}");
            QueryRig.Track("never_sent", null);
            QueryRig.Flush();
            Thread.Sleep(1000);
            var state = ReqObject("/__harness/state");
            Check(state != null && (double)state["requests"] == 0,
                "hard-off: zero network calls of any kind");
        }

        private static void TestBadEndpointIsInert()
        {
            // A key with no endpoint, or a non-HTTPS one off loopback, must be
            // inert: no requests, no retries (spec §2.2).
            foreach (string endpoint in new[] { "", "http://ingest.example.com/v1/events", "ingest.example.com/v1/events" })
            {
                QueryRig.Init(new QueryRigSettings { ApiKey = "test-key", Endpoint = endpoint, Enabled = true });
                Req("POST", "/__harness/reset", "{}");
                QueryRig.Track("never_sent", null);
                QueryRig.Flush();
                Thread.Sleep(500);
                var state = ReqObject("/__harness/state");
                Check((bool)ClientField("_inert") && state != null && (double)state["requests"] == 0,
                    $"bad endpoint: inert with {(endpoint.Length == 0 ? "no endpoint" : endpoint)}");
            }
        }

        private static void TestBlockedHostColdStart()
        {
            // Starting on a DNS-blocked hostname: the gate fetch fails, the
            // default answer holds, and events queue on disk instead of being lost.
            string blocked = Environment.GetEnvironmentVariable("QUERYRIG_TEST_UNRESOLVABLE_ENDPOINT");
            if (string.IsNullOrEmpty(blocked))
            {
                blocked = "https://blocked.invalid/v1/events";
            }
            QueryRig.Init(new QueryRigSettings
            {
                ApiKey = "test-key", Endpoint = blocked, Enabled = "remote", FlushIntervalS = 999, GatePollIntervalS = 999,
            });
            QueryRig.Track("cold_start_blocked", null);
            QueryRig.Flush();
            Check(WaitUntil(() => QueueFileCount() > 0, 5.0), "blocked host: events queue on disk");
            Thread.Sleep(1000);
            Check(!(bool)ClientField("_inert") && QueryRig.IsEnabled, "blocked host: still enabled, waiting to retry");
            Check(WaitUntil(() => (int)ClientField("_consecutiveFailures") >= 1, 10.0), "blocked host: the failure backs off");
            string queueDir = (string)ClientField("_queueDir");
            QueryRig.Shutdown();
            Directory.Delete(Path.GetDirectoryName(queueDir), true);
        }

        // ----------------------------------------------------------- helpers

        private static void Check(bool cond, string label)
        {
            if (cond)
            {
                Debug.Log($"  PASS  {label}");
            }
            else
            {
                Debug.LogError($"  FAIL  {label}");
                _failures++;
            }
        }

        private static bool WaitUntil(Func<bool> cond, double timeoutS)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutS);
            while (DateTime.UtcNow < deadline)
            {
                if (cond())
                {
                    return true;
                }
                Thread.Sleep(100);
            }
            return cond();
        }

        private static int HarnessRows()
        {
            var state = ReqObject("/__harness/state");
            return state != null ? (int)(double)state["rows"] : -1;
        }

        private static int UnreachableAttempts()
        {
            var state = ReqObject("/__harness/state");
            return state != null ? (int)(double)state["unreachableAttempts"] : -1;
        }

        // The SDK's live client and its internals, for assertions only.
        private static object ClientField(string name)
        {
            object client = typeof(QueryRig).GetField("_client", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            return client.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(client);
        }

        private static int QueueFileCount()
        {
            string dir = (string)ClientField("_queueDir");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ndjson").Length : 0;
        }

        private static int EventsRequestCount()
            => ReqArray("/__harness/requests").OfType<Dictionary<string, object>>()
                .Count(r => (string)r["path"] == "/v1/events");

        private static Dictionary<string, object> ReqObject(string path)
            => QueryRigJson.Parse(Req("GET", path) ?? "") as Dictionary<string, object>;

        private static List<object> ReqArray(string path)
            => QueryRigJson.Parse(Req("GET", path) ?? "") as List<object> ?? new List<object>();

        private static string Req(string method, string path, string body = null)
        {
            try
            {
                using (var request = new HttpRequestMessage(new HttpMethod(method), _harness + path))
                {
                    if (body != null)
                    {
                        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    }
                    using (var response = Http.SendAsync(request).GetAwaiter().GetResult())
                    {
                        if ((int)response.StatusCode >= 400)
                        {
                            return null;
                        }
                        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
