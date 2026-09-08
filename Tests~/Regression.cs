using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QueryRigSdk;

// Production C# sources with a minimal engine shim and isolated loopback server.
namespace UnityEngine {
 public enum RuntimeInitializeLoadType { AfterAssembliesLoaded }
 public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType t) {} }
 public static class Application {
  public static string persistentDataPath, streamingAssetsPath, dataPath;
  public static string platform="test", version="test";
  public static event Action quitting; public static event Action<bool> focusChanged;
  public static void FireEvents() { quitting?.Invoke(); focusChanged?.Invoke(true); }
 }
 public static class SystemInfo { public static string deviceUniqueIdentifier="test", unsupportedIdentifier="unsupported"; }
 public static class Debug { public static void LogWarning(object message) { Console.WriteLine(message); } }
}

class Regression {
 static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
 static void Set(object c, string field, object value) => c.GetType().GetField(field, Flags).SetValue(c, value);
 static object Get(object c, string field) => c.GetType().GetField(field, Flags).GetValue(c);
 static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
 static void Wait(Func<bool> predicate) { if (!SpinWait.SpinUntil(predicate, 6000)) throw new Exception("Timed out"); }
 static void Invalid(object value) { try { QueryRigJson.Serialize(value); } catch (FormatException) { return; } throw new Exception("Invalid value serialized"); }
 static void Main() {
  string root = Path.Combine(Path.GetTempPath(), "queryrig-regression-" + Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(root);
  UnityEngine.Application.persistentDataPath = root; UnityEngine.Application.streamingAssetsPath = root;
  UnityEngine.Application.dataPath = Path.Combine(root, "Assets");
  try { Run(); } finally { Directory.Delete(root, true); }
 }
 static void Run() {
  Invalid(double.NaN); Invalid(double.PositiveInfinity);
  var cyclic = new Dictionary<string,object>(); cyclic["self"] = cyclic; Invalid(cyclic);
  Check(true, "non-finite and cyclic properties rejected safely");
  var depth = new Dictionary<string,object>();
  var current = depth;
  for (int i=1;i<8;i++) { var next=new Dictionary<string,object>(); current["child"]=next; current=next; }
  current["scalar"]=1;
  QueryRigJson.Serialize(depth);
  current["child"]=new Dictionary<string,object>(); Invalid(depth);
  Check(true,"eight container levels accepted; ninth empty container rejected");
  using var server = new Server();
  QueryRigSettings Settings(string key="first") => new QueryRigSettings { ApiKey=key, Endpoint=server.Url+"/v1/events", Enabled=true, FlushIntervalS=9999, FlushMaxEvents=9999, GatePollIntervalS=1 };
  var client = new QueryRigClient(Settings());
  try {
   var nested = new Dictionary<string,object>{{"score",1}};
   client.TrackFields(new Dictionary<string,object>{{"nested",nested}}); nested["score"]=99;
   var queue = (List<string>)Get(client,"_queue");
   Check(queue.Last().Contains("\"score\":1"), "nested fields are immutable after Track returns");
   int count = queue.Count;
   client.TrackFields(new Dictionary<string,object>{{"number",double.NaN}});
   Check(queue.Count==count, "bad event does not remove valid neighbours");
   Set(client,"_sampledIn",false); Set(client,"_sampleRate",0.0); Set(client,"_sessionId","old");
   Set(client,"_lastEventMs",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-31*60*1000L);
   client.Track("new-session",null);
   Check((string)Get(client,"_sessionId")!="old" && queue.Count==count, "sampled-out session rolls over without leaking an event");
   Set(client,"_sampledIn",true);
   server.Status=500; client.Flush(); Wait(()=>server.Events>0); Wait(()=>(long)Get(client,"_nextDrainMs")>0);
   int requests=server.Events;
   for(int i=0;i<4;i++) { client.Track("new-during-outage",null); client.Flush(); Thread.Sleep(100); }
   Check(server.Events==requests, "new events respect outage backoff");
   Check(Directory.GetFiles((string)Get(client,"_queueDir"),"*.ndjson").Length>0, "unacknowledged batches persist before send");
   Set(client,"_nextDrainMs",0L); server.Status=401; client.Flush(); Wait(()=>!client.IsEnabled);
   server.Status=200; client.RefreshGate(); Wait(()=>client.IsEnabled);
   Check(true,"hard-on recovers from temporary 401");
   server.Malformed=true; Set(client,"_nextDrainMs",0L); client.Flush(); Wait(()=>(long)Get(client,"_nextDrainMs")>0);
   Check(Directory.GetFiles((string)Get(client,"_queueDir"),"*.ndjson").Length>0,"invalid 200 acknowledgement retains batch");
  } finally { client.Shutdown(); }
  var a = new QueryRigClient(Settings("namespace-a")); string pathA=(string)Get(a,"_queueDir"); a.Shutdown();
  var b = new QueryRigClient(Settings("namespace-b")); string pathB=(string)Get(b,"_queueDir"); b.Shutdown();
  Check(pathA!=pathB && Directory.GetFiles(pathA,"*.ndjson").Length>0,"key change preserves but isolates the old queue");
  var pending = new QueryRigClient(Settings("shutdown"));
  server.Malformed=false; server.Stall=true;
  pending.Track("shutdown-in-flight",null); int before=server.Events; pending.Flush(); Wait(()=>server.Events>before);
  string pendingDir=(string)Get(pending,"_queueDir"); var timer=System.Diagnostics.Stopwatch.StartNew(); pending.Shutdown();
  Check(timer.ElapsedMilliseconds<4000 && Directory.GetFiles(pendingDir,"*.ndjson").Length>0,"shutdown cancels HTTP and retains its durable batch");
 }
 sealed class Server : IDisposable {
  readonly HttpListener listener=new HttpListener(); public readonly string Url;
  public volatile int Status=200, Events; public volatile bool Malformed, Stall;
  public Server() {
   var socket=new TcpListener(IPAddress.Loopback,0); socket.Start(); int port=((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
   Url="http://127.0.0.1:"+port; listener.Prefixes.Add(Url+"/"); listener.Start(); _=Loop();
  }
  async Task Loop() { try { while(listener.IsListening) { var c=await listener.GetContextAsync(); _=Handle(c); } } catch(HttpListenerException) {} catch(ObjectDisposedException) {} }
  async Task Handle(HttpListenerContext c) {
   try {
    string response;
    if(c.Request.Url.AbsolutePath=="/v1/config") response="{\"enabled\":true}";
    else {
     string body=await new StreamReader(c.Request.InputStream).ReadToEndAsync(); Interlocked.Increment(ref Events);
     if(Stall) { await Task.Delay(7000); c.Response.Close(); return; }
     c.Response.StatusCode=Status; int n=((List<object>)QueryRigJson.Parse(body)).Count;
     response=Malformed ? "not-json" : "{\"accepted\":"+n+",\"rejected\":0}";
    }
    byte[] bytes=Encoding.UTF8.GetBytes(response); c.Response.ContentLength64=bytes.Length; await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
   } catch(Exception) { /* shutdown can cancel requests */ }
  }
  public void Dispose() { listener.Close(); }
 }
}
