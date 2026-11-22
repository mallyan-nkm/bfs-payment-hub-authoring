using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TransactHub.Engines;

namespace TransactHub.Engine.Flowable;

/// <summary>An external worker job as the Flowable REST server hands it out, with the variables its execution sees.</summary>
public sealed record ExternalJob(
    string Id, string ProcessInstanceId, string ProcessDefinitionId, string ExecutionId, string ElementId,
    DateTimeOffset CreateTime, int Retries, IReadOnlyDictionary<string, JsonNode?> Variables)
{
    public static ExternalJob Of(JsonObject o)
    {
        var vars = new Dictionary<string, JsonNode?>();
        foreach (var v in J.A(o, "variables")) if (J.S(v, "name") is string n) vars[n] = J.Clone(J.P(v, "value"));
        return new ExternalJob(J.S(o, "id")!, J.S(o, "processInstanceId") ?? "", J.S(o, "processDefinitionId") ?? "", J.S(o, "executionId") ?? "",
            J.S(o, "elementId") ?? "", FlowableRest.Time(J.S(o, "createTime")), (int)(J.Num(o, "retries") ?? 0), vars);
    }
}

/// <summary>Thrown when the Flowable REST server answers with an error status.</summary>
public sealed class FlowableException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The Flowable REST server (flowable-rest, Flowable 7): its process API under <c>/service</c> and its
/// external worker API under <c>/external-job-api</c>. Everything the binding does to the engine goes
/// through here: deploy, start, acquire and lock jobs, complete them, fail them with retries, throw BPMN
/// errors, run the jobs the async executor would run, move timers, and deliver messages.
/// </summary>
public sealed class FlowableRest : IDisposable
{
    readonly HttpClient http;
    readonly string service, external;
    /// <summary>Every request sent, by kind: what the binding cost the server.</summary>
    public readonly Dictionary<string, int> Requests = new();

    public FlowableRest(string baseUrl, string user, string password, HttpMessageHandler? handler = null)
    {
        var b = baseUrl.TrimEnd('/');
        service = $"{b}/service";
        external = $"{b}/external-job-api";
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    /// <summary>From TH_FLOWABLE_URL (e.g. http://127.0.0.1:8080/flowable-rest), TH_FLOWABLE_USER and TH_FLOWABLE_PASSWORD.</summary>
    public static FlowableRest? FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("TH_FLOWABLE_URL");
        if (string.IsNullOrEmpty(url)) return null;
        return new FlowableRest(url, Environment.GetEnvironmentVariable("TH_FLOWABLE_USER") ?? "rest-admin", Environment.GetEnvironmentVariable("TH_FLOWABLE_PASSWORD") ?? "test");
    }

    public static DateTimeOffset Time(string? s) => s is null ? DateTimeOffset.MinValue : DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    async Task<(HttpStatusCode status, string text)> Send(string kind, HttpMethod method, string url, HttpContent? body = null)
    {
        Requests[kind] = Requests.GetValueOrDefault(kind) + 1;
        using var req = new HttpRequestMessage(method, url) { Content = body };
        using var res = await http.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    async Task<JsonNode?> Json(string kind, HttpMethod method, string url, JsonNode? body = null)
    {
        var (status, text) = await Send(kind, method, url, body is null ? null : new StringContent(J.Str(body), Encoding.UTF8, "application/json"));
        if ((int)status >= 300) throw new FlowableException(status, $"{method} {url}: {(int)status} {Short(text)}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    static string Short(string s) => s.Length > 600 ? s[..600] : s;

    /// <summary>A REST variable: name, type and value, the type taken from the JSON value.</summary>
    public static JsonObject Var(string name, JsonNode? value)
    {
        var type = value switch
        {
            null => "string",
            JsonValue v when v.GetValueKind() == JsonValueKind.True || v.GetValueKind() == JsonValueKind.False => "boolean",
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => J.D(v) is double d && d == Math.Floor(d) && Math.Abs(d) < int.MaxValue ? "integer" : "double",
            JsonValue => "string",
            _ => "json",
        };
        JsonNode? val = type switch
        {
            "integer" => JsonValue.Create((int)J.D(value)!.Value),
            "string" when value is JsonValue sv => JsonValue.Create(sv.GetValueKind() == JsonValueKind.String ? sv.GetValue<string>() : sv.ToJsonString()),
            _ => J.Clone(value),
        };
        return new JsonObject { ["name"] = name, ["type"] = type, ["value"] = val };
    }

    static JsonArray Vars(IEnumerable<KeyValuePair<string, JsonNode?>>? vars) =>
        new((vars ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (JsonNode?)Var(kv.Key, kv.Value)).ToArray());

    async Task<List<JsonObject>> List(string kind, string url)
    {
        var all = new List<JsonObject>();
        for (var startAt = 0; ; )
        {
            var sep = url.Contains('?') ? '&' : '?';
            var page = (JsonObject?)await Json(kind, HttpMethod.Get, $"{url}{sep}start={startAt}&size=1000") ?? new JsonObject();
            var data = J.A(page, "data").OfType<JsonObject>().ToList();
            all.AddRange(data);
            var total = (int)(J.Num(page, "total") ?? data.Count);
            startAt += data.Count;
            if (data.Count == 0 || startAt >= total) return all;
        }
    }

    static string Q(string? tenant) => string.IsNullOrEmpty(tenant) ? "" : $"tenantId={Uri.EscapeDataString(tenant)}";

    // ------------------------------------------------------------------ repository

    public async Task<JsonObject> Engine() => (JsonObject)(await Json("engine", HttpMethod.Get, $"{service}/management/engine"))!;

    /// <summary>Deploys BPMN documents as one deployment (a zip archive, the one multipart file the server takes).</summary>
    public async Task<string> Deploy(string name, IReadOnlyDictionary<string, string> files, string? tenant)
    {
        using var zipped = new MemoryStream();
        using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, true))
            foreach (var (file, xml) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                await using var w = new StreamWriter(zip.CreateEntry(file).Open(), new UTF8Encoding(false));
                await w.WriteAsync(xml);
            }
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(zipped.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(part, "file", $"{name}.zip");
        var q = $"deploymentName={Uri.EscapeDataString(name)}" + (string.IsNullOrEmpty(tenant) ? "" : $"&tenantId={Uri.EscapeDataString(tenant)}");
        var (status, text) = await Send("deploy", HttpMethod.Post, $"{service}/repository/deployments?{q}", form);
        if ((int)status >= 300) throw new FlowableException(status, $"deploy {name}: {(int)status} {Short(text)}");
        return J.S(JsonNode.Parse(text), "id")!;
    }

    public Task<List<JsonObject>> ProcessDefinitions(string deploymentId) => List("definitions", $"{service}/repository/process-definitions?deploymentId={deploymentId}");

    public async Task DeleteDeployment(string id) => await Send("undeploy", HttpMethod.Delete, $"{service}/repository/deployments/{id}?cascade=true");

    // ------------------------------------------------------------------ runtime

    public async Task<JsonObject> StartProcess(string key, string? tenant, IEnumerable<KeyValuePair<string, JsonNode?>>? vars = null, string? businessKey = null)
    {
        var body = new JsonObject { ["processDefinitionKey"] = key, ["variables"] = Vars(vars) };
        if (!string.IsNullOrEmpty(tenant)) body["tenantId"] = tenant;
        if (businessKey is not null) body["businessKey"] = businessKey;
        return (JsonObject)(await Json("start", HttpMethod.Post, $"{service}/runtime/process-instances", body))!;
    }

    public Task<List<JsonObject>> Executions(string processInstanceId) => List("executions", $"{service}/runtime/executions?processInstanceId={processInstanceId}");

    public Task<List<JsonObject>> EventSubscriptions(string? tenant, string eventType, string? eventName = null, string? processInstanceId = null)
    {
        var q = new List<string> { $"eventType={eventType}" };
        if (eventName is not null) q.Add($"eventName={Uri.EscapeDataString(eventName)}");
        if (processInstanceId is not null) q.Add($"processInstanceId={processInstanceId}");
        if (!string.IsNullOrEmpty(tenant)) q.Add(Q(tenant));
        return List("subscriptions", $"{service}/runtime/event-subscriptions?{string.Join('&', q)}");
    }

    /// <summary>Delivers a message to the execution whose catch (or event sub-process start) waits for it.</summary>
    public async Task MessageReceived(string executionId, string messageName) =>
        await Json("message", HttpMethod.Put, $"{service}/runtime/executions/{executionId}", new JsonObject { ["action"] = "messageEventReceived", ["messageName"] = messageName });

    // ------------------------------------------------------------------ jobs the async executor would run

    public Task<List<JsonObject>> Jobs(string? tenant, string? processInstanceId = null) =>
        List("jobs", $"{service}/management/jobs?{Q(tenant)}{(processInstanceId is null ? "" : $"&processInstanceId={processInstanceId}")}");
    public Task<List<JsonObject>> TimerJobs(string? tenant) => List("timers", $"{service}/management/timer-jobs?{Q(tenant)}");
    public Task<List<JsonObject>> TimerJobsOf(string processInstanceId) => List("timers", $"{service}/management/timer-jobs?processInstanceId={Uri.EscapeDataString(processInstanceId)}");
    public Task<List<JsonObject>> DeadLetterJobs(string? tenant) => List("dead-letters", $"{service}/management/deadletter-jobs?{Q(tenant)}");

    /// <summary>Runs an executable job; the server's message when the job fails, else null.</summary>
    public async Task<string?> ExecuteJob(string id)
    {
        var (status, text) = await Send("execute", HttpMethod.Post, $"{service}/management/jobs/{id}", new StringContent("{\"action\":\"execute\"}", Encoding.UTF8, "application/json"));
        return (int)status < 300 ? null : $"{(int)status} {Short(text)}";
    }

    public async Task DeleteJob(string id)
    {
        await Send("delete-job", HttpMethod.Delete, $"{service}/management/jobs/{id}");
        await Send("delete-job", HttpMethod.Delete, $"{service}/management/timer-jobs/{id}");
    }

    /// <summary>A timer is due on the binding's clock: it becomes an executable job.</summary>
    public async Task MoveTimer(string id) =>
        await Json("move-timer", HttpMethod.Post, $"{service}/management/timer-jobs/{id}", new JsonObject { ["action"] = "move" });

    /// <summary>
    /// Gives a timer the due date the binding computed (production mode: the business calendar and a
    /// duration from a parameter). The server replaces the timer job by a new one, with a new id; false when
    /// the timer is gone (it fired, or its scope ended).
    /// </summary>
    public async Task<bool> RescheduleTimer(string id, DateTimeOffset due)
    {
        var body = new JsonObject { ["action"] = "reschedule", ["dueDate"] = due.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) };
        var (status, text) = await Send("reschedule-timer", HttpMethod.Post, $"{service}/management/timer-jobs/{id}", new StringContent(J.Str(body), Encoding.UTF8, "application/json"));
        if (status == HttpStatusCode.NotFound) return false;
        if ((int)status >= 300) throw new FlowableException(status, $"reschedule {id}: {(int)status} {Short(text)}");
        return true;
    }

    // ------------------------------------------------------------------ the external worker API

    public Task<List<JsonObject>> ExternalJobs(string? tenant) => List("external-jobs", $"{external}/jobs?{Q(tenant)}");

    /// <summary>Acquires and locks up to <paramref name="n"/> jobs of a topic for this worker.</summary>
    public async Task<List<ExternalJob>> Acquire(string topic, int n, TimeSpan lockFor, string workerId, string? tenant)
    {
        var body = new JsonObject { ["topic"] = topic, ["lockDuration"] = System.Xml.XmlConvert.ToString(lockFor), ["numberOfTasks"] = n, ["workerId"] = workerId };
        if (!string.IsNullOrEmpty(tenant)) body["tenantId"] = tenant;
        var res = await Json("acquire", HttpMethod.Post, $"{external}/acquire/jobs", body) as JsonArray ?? new JsonArray();
        return res.OfType<JsonObject>().Select(ExternalJob.Of).ToList();
    }

    async Task<bool> Finish(string kind, string jobId, JsonObject body)
    {
        var (status, text) = await Send(kind, HttpMethod.Post, $"{external}/acquire/jobs/{jobId}/{kind}", new StringContent(J.Str(body), Encoding.UTF8, "application/json"));
        if (status == HttpStatusCode.NotFound) return false; // the job is gone: a boundary event ended it
        if ((int)status >= 300) throw new FlowableException(status, $"{kind} {jobId}: {(int)status} {Short(text)}");
        return true;
    }

    /// <summary>Completes a locked job with variables; false when the job no longer exists.</summary>
    public Task<bool> Complete(string jobId, string workerId, IEnumerable<KeyValuePair<string, JsonNode?>>? vars = null) =>
        Finish("complete", jobId, new JsonObject { ["workerId"] = workerId, ["variables"] = Vars(vars) });

    /// <summary>Ends a locked job with a BPMN error, caught by an error boundary on its task.</summary>
    public Task<bool> BpmnError(string jobId, string workerId, string errorCode, IEnumerable<KeyValuePair<string, JsonNode?>>? vars = null) =>
        Finish("bpmnError", jobId, new JsonObject { ["workerId"] = workerId, ["errorCode"] = errorCode, ["variables"] = Vars(vars) });

    /// <summary>
    /// Fails a locked job after a technical failure. With retries left the server hands it out again once
    /// its async executor resets the lock after the retry timeout (production mode); with none left it
    /// becomes a dead letter.
    /// </summary>
    public Task<bool> Fail(string jobId, string workerId, string message, int retries, TimeSpan retryTimeout) =>
        Finish("fail", jobId, new JsonObject { ["workerId"] = workerId, ["errorMessage"] = message, ["retries"] = retries, ["retryTimeout"] = System.Xml.XmlConvert.ToString(retryTimeout) });

    public void Dispose() => http.Dispose();
}
