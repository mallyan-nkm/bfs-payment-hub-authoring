package th.flowable;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import org.flowable.bpmn.model.BpmnModel;
import org.flowable.bpmn.model.ExtensionAttribute;
import org.flowable.bpmn.model.ExtensionElement;
import org.flowable.bpmn.model.ExternalWorkerServiceTask;
import org.flowable.bpmn.model.FlowElement;
import org.flowable.bpmn.model.FlowElementsContainer;
import org.flowable.common.engine.api.delegate.event.FlowableEngineEventType;
import org.flowable.common.engine.api.delegate.event.FlowableEntityEvent;
import org.flowable.common.engine.api.delegate.event.FlowableEvent;
import org.flowable.common.engine.api.delegate.event.FlowableEventListener;
import org.flowable.common.engine.impl.calendar.CycleBusinessCalendar;
import org.flowable.common.engine.impl.calendar.DueDateBusinessCalendar;
import org.flowable.common.engine.impl.calendar.DurationBusinessCalendar;
import org.flowable.common.engine.impl.calendar.MapBusinessCalendarManager;
import org.flowable.common.engine.impl.history.HistoryLevel;
import org.flowable.engine.ManagementService;
import org.flowable.engine.ProcessEngine;
import org.flowable.engine.ProcessEngineConfiguration;
import org.flowable.engine.RepositoryService;
import org.flowable.engine.RuntimeService;
import org.flowable.engine.delegate.event.FlowableActivityEvent;
import org.flowable.engine.impl.cfg.ProcessEngineConfigurationImpl;
import org.flowable.engine.impl.persistence.entity.ExecutionEntity;
import org.flowable.engine.impl.util.CommandContextUtil;
import org.flowable.engine.repository.Deployment;
import org.flowable.engine.repository.DeploymentBuilder;
import org.flowable.engine.repository.ProcessDefinition;
import org.flowable.engine.runtime.Execution;
import org.flowable.eventsubscription.api.EventSubscription;
import org.flowable.job.api.AcquiredExternalWorkerJob;
import org.flowable.job.api.Job;

import java.io.File;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.IdentityHashMap;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.PriorityQueue;
import java.util.Set;
import java.util.TreeSet;
import java.util.TreeMap;

import static th.flowable.Support.F;
import static th.flowable.Support.M;

/**
 * The Flowable harness for the conformance kit (C22, C23). It deploys the BPMN the studio
 * generated for one scenario into an embedded Flowable engine on H2 in memory, runs it, works the
 * external worker jobs by asking the party service, moves the engine clock to fire timers, and
 * writes the business trace and the detail at the places the generated model marks. It prints
 * one observation as JSON on stdout.
 *
 * The harness is the engine binding: it decides nothing about flow. Flow is in the BPMN. The
 * binding's jobs are the ones Part 9 gives it: worker topics, the front door (correlation, early,
 * late and duplicate messages, R-09-16), signals, hook bindings and their contracts, leg dispatch,
 * supersession, claim-check of the context (R-09-10), and plain request handlers (R-09-14).
 */
public final class Harness {
    static final String TH_NS = "urn:transacthub:flowable:1";
    static final String WORKER = "th-kit-worker";
    static final boolean DEBUG = System.getenv("TH_DEBUG") != null;

    final JsonNode plan;
    final long start;
    final Support.VClock clock;
    final Support.Party party;
    ProcessEngine engine;
    RuntimeService rt;
    ManagementService ms;
    RepositoryService repo;

    /** element annotations by process definition id, element id, tag. */
    final Map<String, Map<String, Map<String, Map<String, String>>>> anns = new HashMap<>();
    final Map<String, Map<String, String>> workflowOf = new HashMap<>(); // procDefId -> th:workflow
    final Map<String, String> keyOf = new HashMap<>(); // procDefId -> key
    final Set<String> topics = new TreeSet<>();
    final Set<String> undoElements = new HashSet<>();
    /** the composite step (block sub-process) an element is in, innermost, by element id (R-05-43). */
    final Map<String, String> blockOf = new HashMap<>();
    /** the value a timer from a duration parameter took (R-05-45), by timer job id; and by instance and timer element. */
    final Map<String, String> timerValue = new HashMap<>(), lastTimerValue = new HashMap<>();

    final Map<String, Inst> insts = new LinkedHashMap<>();
    final List<Inst> roots = new ArrayList<>();
    String pendingLabel;
    int durable = 0;

    // The front door.
    final List<JsonNode> mailbox = new ArrayList<>();
    final Map<JsonNode, Long> receivedAt = new IdentityHashMap<>();
    int unmatched = 0, duplicates = 0, earlyKept = 0;
    final List<String> notes = new ArrayList<>();

    // Harness events on the virtual clock.
    long seq = 0;
    final PriorityQueue<Ev> queue = new PriorityQueue<>(Comparator.<Ev>comparingLong(e -> e.at).thenComparingLong(e -> e.eng).thenComparingLong(e -> e.seq));
    final Set<String> worked = new HashSet<>();

    static final class Ev {
        long at, eng, seq;
        String kind;
        JsonNode data;
        Runnable run;
    }

    static final class Inst {
        String pid, label, name, release, kind, key;
        Inst parent;
        ObjectNode ctx;
        private final List<String> trace = new ArrayList<>(), detail = new ArrayList<>();
        final List<String> releaseTrace = new ArrayList<>(), pending = new ArrayList<>();
        /** What the items of a forEach recorded, by item, while they ran side by side: put into the trace in item order (R-05-53). */
        final TreeMap<Integer, List<String[]>> items = new TreeMap<>();
        void flushItems() {
            for (List<String[]> es : items.values()) for (String[] e : es) ("detail".equals(e[0]) ? detail : trace).add(e[1]);
            items.clear();
        }
        List<String> trace() { flushItems(); return trace; }
        List<String> detail() { flushItems(); return detail; }
        final List<ObjectNode> steps = new ArrayList<>();
        final Map<String, Integer> attempts = new LinkedHashMap<>(), calls = new HashMap<>();
        final Map<String, ObjectNode> openStep = new HashMap<>();
        final Set<String> expired = new HashSet<>(), consumed = new HashSet<>(), superseded = new HashSet<>();
        final Map<String, String> lastChild = new HashMap<>();
        final Map<String, Integer> notifyAt = new HashMap<>();
        final Set<String> stateKeys = new TreeSet<>();
        final Set<String> awaited = new HashSet<>(); // the message of every await this instance has waited for (R-05-43)
        final Map<String, Long> blockStarted = new HashMap<>(); // block element id -> virtual time it last began
        final List<String> ruleTraceSteps = new ArrayList<>();
        int stateSnapshots = 0;
        boolean ended;
        Long startedAt, endedAt;
        String failed, lastFatal;
        List<JsonNode> bindings = new ArrayList<>();
    }

    Harness(JsonNode plan) throws Exception {
        this.plan = plan;
        this.start = plan.get("startMs").asLong();
        this.clock = new Support.VClock(start);
        this.party = new Support.Party(new File(plan.get("studioRoot").asText()));
    }

    public static void main(String[] args) throws Exception {
        if (args.length > 0 && args[0].equals("--validate")) { validate(java.util.Arrays.copyOfRange(args, 1, args.length)); return; }
        JsonNode plan = M.readTree(new File(args[0]));
        Harness h = new Harness(plan);
        ObjectNode obs;
        try {
            obs = h.runScenario();
        } finally {
            h.party.close();
            if (h.engine != null) h.engine.close();
        }
        System.out.println("OBSERVATION " + M.writeValueAsString(obs));
        System.exit(0);
    }

    /** Deploy each BPMN file into a fresh engine and say whether Flowable accepts it (C22-S5). */
    static void validate(String[] files) {
        ProcessEngineConfigurationImpl cfg = (ProcessEngineConfigurationImpl) ProcessEngineConfiguration.createStandaloneInMemProcessEngineConfiguration();
        cfg.setJdbcUrl("jdbc:h2:mem:th-validate;DB_CLOSE_DELAY=-1");
        cfg.setAsyncExecutorActivate(false);
        cfg.setHistoryLevel(HistoryLevel.NONE);
        ProcessEngine e = cfg.buildProcessEngine();
        for (String f : files) {
            try {
                e.getRepositoryService().createDeployment().addString(new File(f).getName(), Files.readString(new File(f).toPath())).deploy();
                System.out.println("DEPLOYED " + f);
            } catch (Exception x) {
                System.out.println("REFUSED " + f + " " + rootMessage(x).replace('\n', ' '));
            }
        }
        e.close();
    }

    // ================================================================ engine

    void boot() throws Exception {
        ProcessEngineConfigurationImpl cfg = (ProcessEngineConfigurationImpl) ProcessEngineConfiguration.createStandaloneInMemProcessEngineConfiguration();
        cfg.setJdbcUrl("jdbc:h2:mem:th-" + Support.safe(plan.get("scenario").asText()) + ";DB_CLOSE_DELAY=-1");
        cfg.setAsyncExecutorActivate(false);
        cfg.setHistoryLevel(HistoryLevel.NONE);
        cfg.setClock(clock);
        cfg.setEngineName("th-" + plan.get("scenario").asText());
        MapBusinessCalendarManager cals = new MapBusinessCalendarManager();
        cals.addBusinessCalendar(DurationBusinessCalendar.NAME, new DurationBusinessCalendar(clock));
        cals.addBusinessCalendar(DueDateBusinessCalendar.NAME, new DueDateBusinessCalendar(clock));
        cals.addBusinessCalendar(CycleBusinessCalendar.NAME, new CycleBusinessCalendar(clock));
        List<Integer> weekend = new ArrayList<>();
        for (JsonNode d : plan.at("/calendar/weekend")) weekend.add(d.asInt());
        cals.addBusinessCalendar("business", new Support.Business(clock, weekend, Support.strings(plan.at("/calendar/holidays"))));
        cfg.setBusinessCalendarManager(cals);
        cfg.setEventListeners(List.of(new Listener()));
        engine = cfg.buildProcessEngine();
        rt = engine.getRuntimeService();
        ms = engine.getManagementService();
        repo = engine.getRepositoryService();
        if (plan.get("processes").size() == 0) return;
        DeploymentBuilder db = repo.createDeployment().name(plan.get("scenario").asText());
        for (JsonNode p : plan.get("processes")) db.addString(p.get("key").asText() + ".bpmn20.xml", Files.readString(new File(p.get("file").asText()).toPath()));
        Deployment d = db.deploy();
        for (ProcessDefinition pd : repo.createProcessDefinitionQuery().deploymentId(d.getId()).list()) {
            keyOf.put(pd.getId(), pd.getKey());
            BpmnModel model = repo.getBpmnModel(pd.getId());
            org.flowable.bpmn.model.Process proc = model.getMainProcess();
            Map<String, Map<String, Map<String, String>>> byEl = new HashMap<>();
            Map<String, Map<String, String>> pa = annotations(proc.getExtensionElements());
            workflowOf.put(pd.getId(), pa.getOrDefault("workflow", Map.of()));
            collect(proc, byEl, null);
            anns.put(pd.getId(), byEl);
        }
    }

    void collect(FlowElementsContainer c, Map<String, Map<String, Map<String, String>>> byEl, String block) {
        for (FlowElement el : c.getFlowElements()) {
            Map<String, Map<String, String>> a = annotations(el.getExtensionElements());
            if (!a.isEmpty()) byEl.put(el.getId(), a);
            if (block != null) blockOf.put(el.getId(), block);
            if (el instanceof ExternalWorkerServiceTask t) topics.add(t.getTopic());
            if (a.containsKey("point") && "undo".equals(a.get("point").get("kind"))) undoElements.add(el.getId());
            if (el instanceof FlowElementsContainer inner) collect(inner, byEl, a.containsKey("block") ? el.getId() : block);
        }
    }

    static Map<String, Map<String, String>> annotations(Map<String, List<ExtensionElement>> ext) {
        Map<String, Map<String, String>> out = new HashMap<>();
        if (ext == null) return out;
        for (Map.Entry<String, List<ExtensionElement>> e : ext.entrySet()) {
            for (ExtensionElement x : e.getValue()) {
                if (!TH_NS.equals(x.getNamespace())) continue;
                Map<String, String> attrs = new HashMap<>();
                for (Map.Entry<String, List<ExtensionAttribute>> a : x.getAttributes().entrySet()) attrs.put(a.getKey(), a.getValue().get(0).getValue());
                out.put(x.getName(), attrs);
            }
        }
        return out;
    }

    Map<String, Map<String, String>> annOf(String procDefId, String elementId) {
        Map<String, Map<String, Map<String, String>>> m = anns.get(procDefId);
        if (m == null) return Map.of();
        return m.getOrDefault(elementId, Map.of());
    }

    long elapsed() { return clock.virtual - start; }

    // ================================================================ the scenario

    ObjectNode runScenario() throws Exception {
        boot();
        ObjectNode open = F.objectNode().put("op", "open").put("scenario", plan.get("scenario").asText());
        party.call(open);
        JsonNode started = party.call(F.objectNode().put("op", "start").put("now", clock.virtual).put("conversation", plan.path("conversation").asText("conv-1")));
        deliveries(started.get("deliveries"));
        if (!plan.path("handler").isMissingNode() && !plan.path("handler").isNull()) {
            // A handler scenario: the inbound message is answered at the interop boundary.
        }
        for (JsonNode i : plan.get("instances")) {
            String label = i.get("label").asText();
            String key = i.get("key").asText();
            schedule(i.get("atMs").asLong() + start, "start", i, () -> {
                pendingLabel = label;
                clock.tick();
                rt.startProcessInstanceByKey(key);
                pendingLabel = null;
            });
        }
        for (JsonNode s : plan.get("signals")) schedule(s.get("atMs").asLong() + start, "signal", s, () -> signal(s.get("signal").asText()));
        loop();
        ObjectNode obs = F.objectNode();
        obs.put("target", "flowable");
        ArrayNode list = obs.putArray("instances");
        for (Inst i : roots) list.add(observe(i));
        JsonNode sent = party.call(F.objectNode().put("op", "sent"));
        obs.set("messages", sent.get("messages"));
        obs.put("unmatched", unmatched);
        obs.put("duplicates", duplicates);
        obs.put("earlyKept", earlyKept);
        obs.put("durableInstances", durable);
        obs.put("startMs", start);
        obs.set("notes", Support.arr(notes));
        return obs;
    }

    ObjectNode observe(Inst i) {
        ObjectNode o = F.objectNode();
        o.put("label", i.label);
        o.set("trace", Support.arr(i.trace()));
        o.set("detail", Support.arr(i.detail()));
        o.set("releaseTrace", Support.arr(i.releaseTrace));
        ArrayNode steps = o.putArray("steps");
        i.steps.forEach(steps::add);
        ObjectNode att = o.putObject("attempts");
        i.attempts.forEach(att::put);
        o.set("ruleTraceSteps", Support.arr(i.ruleTraceSteps));
        o.set("engineStateKeys", Support.arr(new ArrayList<>(i.stateKeys)));
        o.put("startedAt", i.startedAt == null ? 0 : i.startedAt);
        if (i.endedAt != null) o.put("endedAt", i.endedAt);
        if (i.failed != null) o.put("failed", i.failed);
        o.set("ctx", i.ctx);
        return o;
    }

    void schedule(long at, String kind, JsonNode data, Runnable run) {
        Ev e = new Ev();
        e.at = at;
        e.eng = clock.engineNow();
        e.seq = seq++;
        e.kind = kind;
        e.data = data;
        e.run = run;
        queue.add(e);
    }

    void deliveries(JsonNode ds) {
        if (ds == null) return;
        for (JsonNode d : ds) {
            JsonNode m = d.get("message");
            schedule(d.get("at").asLong(), "delivery", m, () -> frontDoor(m));
        }
    }

    // ================================================================ the run loop

    void loop() {
        settle();
        if (DEBUG) for (Execution e : rt.createExecutionQuery().list()) System.err.println("[exec] " + e.getId() + " at " + e.getActivityId() + " parent " + e.getParentId());
        if (DEBUG) System.err.println("[ext] " + ms.createExternalWorkerJobQuery().list().size() + " timers " + ms.createTimerJobQuery().list().size() + " jobs " + ms.createJobQuery().list().size() + " notes " + notes);
        for (int guard = 0; guard < 2_000_000; guard++) {
            Ev next = queue.peek();
            Job timer = null;
            long tAt = Long.MAX_VALUE, tEng = Long.MAX_VALUE;
            for (Job j : ms.createTimerJobQuery().list()) {
                long eng = j.getCreateTime().getTime();
                long vdue = timerDue(j, eng);
                if (vdue < tAt || (vdue == tAt && eng < tEng)) { tAt = vdue; tEng = eng; timer = j; }
            }
            boolean useTimer = timer != null && (next == null || tAt < next.at || (tAt == next.at && tEng <= next.eng));
            if (!useTimer && next == null) break;
            long at = useTimer ? tAt : next.at;
            if (at - start > 400L * 86400000L) { notes.add("stopped at the 400-day limit"); break; }
            if (at > clock.virtual) clock.advance(at);
            if (useTimer) {
                clock.tick();
                Job j = ms.moveTimerToExecutableJob(timer.getId());
                execute(j.getId());
            } else {
                queue.poll();
                next.run.run();
            }
            settle();
            if (DEBUG) for (Execution e : rt.createExecutionQuery().list()) System.err.println("[exec] t=" + elapsed() + " " + e.getId() + " at " + e.getActivityId() + " parent " + e.getParentId());
            if (DEBUG) for (var j : ms.createExternalWorkerJobQuery().list()) System.err.println("[extjob] " + j.getElementId() + " lockexp=" + j.getLockExpirationTime() + " owner=" + j.getLockOwner() + " cfg=" + j.getJobHandlerConfiguration() + " topics=" + topics);
            if (DEBUG) System.err.println("[ext] " + ms.createExternalWorkerJobQuery().list().size() + " timers " + ms.createTimerJobQuery().list().size() + " notes " + notes);
        }
    }

    /**
     * When a timer is due on the virtual clock. A timer from a duration parameter (R-05-45) takes the tenant's
     * value in force on the business date it was created, else the default; the engine wrote it with the default.
     */
    long timerDue(Job j, long eng) {
        long vCreated = clock.virtualOf(eng);
        Map<String, String> t = annOf(j.getProcessDefinitionId(), j.getElementId()).get("timer");
        if (t == null || (t.get("parameter") == null && t.get("calendarParameter") == null)) return vCreated + (j.getDuedate().getTime() - eng);
        // R-05-47: an await's wait a bank may set names its duration and its calendar as parameters, the policy giving the defaults.
        String date = Support.isoDate(vCreated);
        String value = timerValue.computeIfAbsent(j.getId(), k -> t.get("parameter") == null ? t.get("default") : paramValue(t.get("parameter"), t.get("default"), date));
        lastTimerValue.put(j.getProcessInstanceId() + "|" + j.getElementId(), value);
        String calendar = t.get("calendarParameter") == null ? t.get("calendar") : paramValue(t.get("calendarParameter"), t.get("calendar"), date);
        if (!"business".equals(calendar)) return vCreated + Support.durationMs(value);
        List<Integer> weekend = new ArrayList<>();
        for (JsonNode d : plan.at("/calendar/weekend")) weekend.add(d.asInt());
        return Support.addBusiness(vCreated, value, weekend, Support.strings(plan.at("/calendar/holidays")));
    }

    /** A parameter's value in force on a date (the tenant's configuration set), else its default (R-01-17). */
    String paramValue(String parameter, String dflt, String date) {
        JsonNode best = null;
        for (JsonNode v : plan.path("values")) {
            if (!parameter.equals(v.path("parameter").asText())) continue;
            String from = v.path("validFrom").asText("0000-01-01"), to = v.path("validTo").asText("9999-12-31");
            if (from.compareTo(date) > 0 || to.compareTo(date) < 0) continue;
            if (best == null || best.path("validFrom").asText("").compareTo(from) <= 0) best = v;
        }
        return best == null ? dflt : best.path("value").asText();
    }

    void execute(String jobId) {
        clock.tick();
        try {
            ms.executeJob(jobId);
        } catch (RuntimeException e) {
            notes.add("engine error at " + elapsed() + " ms: " + rootMessage(e));
            try { ms.deleteJob(jobId); } catch (RuntimeException ignored) {}
            try { ms.deleteTimerJob(jobId); } catch (RuntimeException ignored) {}
        }
    }

    static String rootMessage(Throwable e) {
        Throwable t = e;
        while (t.getCause() != null && t.getCause() != t) t = t.getCause();
        return t.getClass().getSimpleName() + ": " + t.getMessage();
    }

    void settle() {
        for (int guard = 0; guard < 1_000_000; guard++) {
            boolean p = false;
            List<Job> jobs = ms.createJobQuery().list();
            for (Job j : jobs) { execute(j.getId()); p = true; }
            if (p) continue;
            p |= workExternalJobs();
            p |= deliverHeld();
            p |= releaseCompensation();
            if (!p) return;
        }
        notes.add("settle did not converge");
    }

    // ================================================================ the worker

    boolean workExternalJobs() {
        List<AcquiredExternalWorkerJob> all = new ArrayList<>();
        if (DEBUG) System.err.println("[try] t=" + elapsed() + " unlocked=" + ms.createExternalWorkerJobQuery().list().stream().filter(j -> j.getLockOwner() == null).count());
        for (String t : topics) {
            clock.tick();
            all.addAll(ms.createExternalWorkerJobAcquireBuilder().topic(t, Support.lock()).acquireAndLock(100_000, WORKER));
        }
        all.sort(Comparator.comparing((AcquiredExternalWorkerJob j) -> j.getCreateTime()).thenComparing(j -> j.getId()));
        if (DEBUG && !all.isEmpty()) System.err.println("[acquired] t=" + elapsed() + " " + all.size());
        for (AcquiredExternalWorkerJob j : all) work(j);
        return !all.isEmpty();
    }

    void work(AcquiredExternalWorkerJob job) {
        Map<String, String> task = annOf(job.getProcessDefinitionId(), job.getElementId()).get("task");
        Inst inst = insts.get(job.getProcessInstanceId());
        if (task == null || inst == null) {
            notes.add("no annotation for " + job.getElementId());
            return;
        }
        String step = task.get("step");
        String activity = task.get("activity");
        String release = task.get("release");
        boolean silent = "silent".equals(task.get("record"));
        if (DEBUG) System.err.println("[job] t=" + elapsed() + " " + job.getElementId() + " exec=" + job.getExecutionId() + " vars=" + rt.getVariables(job.getExecutionId()));
        Object av = rt.getVariable(job.getExecutionId(), "th_attempt");
        int attempt = av instanceof Number n ? n.intValue() : 1;
        Integer index = null;
        if ("true".equals(task.get("item"))) {
            Object lc = rt.getVariable(job.getExecutionId(), "th_item");
            if (lc instanceof Number n) index = n.intValue();
        }
        String label = index == null ? step : step + "[" + index + "]";
        if (attempt == 1) inst.calls.merge(step, 1, Integer::sum);
        int call = inst.calls.getOrDefault(step, 1);
        // Supersession: a step not yet started runs the superseding release (CK-SUP-01).
        String runRelease = release;
        for (JsonNode s : plan.get("supersessions")) {
            if (!s.get("artefact").asText().equals(activity) || !s.get("replaces").asText().equals(release)) continue;
            if (s.has("effectiveAtMs") && elapsed() < s.get("effectiveAtMs").asLong()) continue;
            runRelease = s.get("with").asText();
            if (inst.superseded.add(activity + "|" + step)) inst.releaseTrace.add("supersede:" + Support.shortName(activity) + " " + release + " to " + runRelease);
        }
        if (attempt == 1) {
            if (!silent) inst.releaseTrace.add(Support.shortName(activity) + "@" + runRelease);
            ObjectNode rec = F.objectNode();
            rec.put("step", label);
            rec.put("release", runRelease);
            rec.put("startedAt", elapsed());
            try {
                rec.set("policy", M.readTree(task.getOrDefault("policy", "{}")));
                if (task.containsKey("sources")) rec.set("policySources", M.readTree(task.get("sources")));
            } catch (Exception e) { throw new IllegalStateException(e); }
            rec.putArray("delta");
            inst.steps.add(rec);
            inst.openStep.put(label, rec);
        }
        inst.attempts.merge(step, 1, Integer::sum);
        if (inst.stateSnapshots++ < 200) inst.stateKeys.addAll(rt.getVariables(job.getExecutionId()).keySet());
        ObjectNode req = F.objectNode();
        req.put("op", "attempt");
        req.put("instance", inst.pid);
        req.put("activity", activity);
        req.put("release", runRelease);
        req.put("step", step);
        req.put("call", call);
        req.put("attempt", attempt);
        req.put("now", clock.virtual);
        if (index != null && "item".equals(task.get("level"))) {
            ObjectNode c = inst.ctx.deepCopy();
            JsonNode item = inst.ctx.path("items").path(index);
            c.remove("items");
            c.putArray("items").add(item.deepCopy());
            c.set("__item", item.deepCopy());
            req.set("ctx", c);
            req.put("level", "item");
        } else req.set("ctx", inst.ctx);
        try { if (task.containsKey("policy")) req.set("policy", M.readTree(task.get("policy"))); } catch (Exception e) { throw new IllegalStateException(e); }
        JsonNode res = party.call(req);
        deliveries(res.get("deliveries"));
        String kind = res.get("kind").asText();
        if (DEBUG) System.err.println("[party] " + step + " -> " + res);
        if ("hang".equals(kind)) return; // the attempt never answers; the timer boundary ends it
        long latency = latency(step, activity);
        final Integer idx = index;
        Runnable finish = () -> finish(job, inst, step, label, idx, task, res);
        if (latency > 0) schedule(clock.virtual + latency, "complete", res, finish);
        else finish.run();
    }

    long latency(String step, String activity) {
        JsonNode l = plan.path("latency");
        if (l.has("step:" + step)) return l.get("step:" + step).asLong();
        if (l.has(activity)) return l.get(activity).asLong();
        return 0;
    }

    void finish(AcquiredExternalWorkerJob job, Inst inst, String step, String label, Integer index, Map<String, String> task, JsonNode res) {
        if (ms.createExternalWorkerJobQuery().jobId(job.getId()).singleResult() == null) return; // a timer boundary ended the attempt; the late result is ignored
        String kind = res.get("kind").asText();
        clock.tick();
        if ("error".equals(kind)) {
            boolean retryable = res.path("retryable").asBoolean(false);
            String name = res.path("name").asText("Error");
            inst.lastFatal = task.get("activity") + " failed: " + res.path("message").asText(name);
            ms.createExternalWorkerCompletionBuilder(job.getId(), WORKER).bpmnError(retryable ? name : "th:nonRetryable");
            return;
        }
        // The rule trace is stored with the payment record (R-04-20).
        if (res.hasNonNull("ruleTrace") && !inst.ruleTraceSteps.contains(label)) inst.ruleTraceSteps.add(label);
        // A result: apply the writes to the context the binding keeps (claim-check), record the delta.
        ObjectNode rec = inst.openStep.get(label);
        ArrayNode delta = rec == null ? F.arrayNode() : (ArrayNode) rec.get("delta");
        delta.removeAll();
        ObjectNode target = inst.ctx;
        ObjectNode item = null;
        if (index != null) {
            JsonNode it = inst.ctx.path("items").path(index);
            if (it.isObject()) {
                item = (ObjectNode) it;
                target = inst.ctx.deepCopy();
                target.set("payment", item.has("payment") ? item.get("payment") : F.objectNode());
                target.set("ext", item.has("ext") ? item.get("ext") : F.objectNode());
                target.set("facts", item.has("facts") ? item.get("facts") : F.objectNode());
            }
        }
        for (Iterator<Map.Entry<String, JsonNode>> w = Support.fields(res.get("writes")); w.hasNext(); ) {
            Map.Entry<String, JsonNode> e = w.next();
            JsonNode before = Support.getPath(target, e.getKey());
            Support.setPath(target, e.getKey(), e.getValue().deepCopy());
            ObjectNode d = delta.addObject();
            d.put("path", index != null ? "items[" + index + "]." + e.getKey() : e.getKey());
            d.set("before", before == null ? F.nullNode() : before.deepCopy());
            d.set("after", e.getValue().deepCopy());
        }
        if (item != null) {
            item.set("payment", target.get("payment"));
            item.set("ext", target.get("ext"));
            item.set("facts", target.get("facts"));
        }
        Map<String, Object> vars = new HashMap<>();
        if (!"silent".equals(task.get("record"))) vars.put("outcome_" + Support.safe(step), res.get("outcome").asText());
        for (Iterator<Map.Entry<String, JsonNode>> d = Support.fields(res.get("decisions")); d.hasNext(); ) {
            Map.Entry<String, JsonNode> e = d.next();
            vars.put("decision_" + Support.safe(e.getKey()), e.getValue().isValueNode() ? e.getValue().asText() : e.getValue().toString());
        }
        ms.createExternalWorkerCompletionBuilder(job.getId(), WORKER).variables(vars).complete();
    }

    // ================================================================ the front door (R-09-16)

    boolean correlates(Inst i, JsonNode m) {
        JsonNode h = i.ctx.path("header");
        String conv = m.path("conversation").asText(null);
        if (conv != null && conv.equals(h.path("conversation").asText(null))) return true;
        JsonNode c = m.path("correlation");
        for (String k : List.of("paymentId", "endToEndId", "fileId")) {
            String x = c.path(k).asText(null);
            if (x != null && x.equals(h.path(k).asText(null))) return true;
        }
        return false;
    }

    static String dedupeId(JsonNode m) {
        if (m.hasNonNull("inReplyTo")) return m.path("type").asText() + ":" + m.get("inReplyTo").asText() + ":" + m.path("status").asText("");
        return m.path("id").asText();
    }

    /** A catch of this instance waiting for this message: of its type and, where the await names one, its family (R-05-44). */
    EventSubscription waiting(Inst i, JsonNode m) {
        String type = m.path("type").asText();
        for (EventSubscription s : rt.createEventSubscriptionQuery().processInstanceId(i.pid).eventType("message").eventName(type).list()) {
            Map<String, String> a = annOf(s.getProcessDefinitionId(), s.getActivityId()).get("await");
            String family = a == null ? null : a.get("family");
            String got = m.path("family").asText(null);
            if (family == null || got == null || family.equals(got)) return s;
        }
        return null;
    }

    /**
     * R-05-43: once an earlier await of this instance has waited for this message, a held message received
     * before the composite step that holds the waiting await began answered the earlier exchange.
     */
    boolean stale(Inst i, JsonNode m, EventSubscription w) {
        if (!i.awaited.contains(m.path("type").asText())) return false;
        String block = blockOf.get(w.getActivityId());
        Long began = block == null ? null : i.blockStarted.get(block);
        long since = began != null ? began : clock.virtual;
        Long at = receivedAt.get(m);
        return at != null && at < since;
    }

    void frontDoor(JsonNode m) {
        String type = m.path("type").asText();
        JsonNode entry = plan.path("catalogue").path(type);
        if (entry.path("handler").isTextual() || entry.path("start").asBoolean(false)) {
            if (m.path("copy").asBoolean(false)) { duplicates++; return; }
            if (entry.path("handler").isTextual()) { new Handler(this).run(m, entry); return; }
            notes.add("a start message " + type + " is outside the kit's Flowable harness");
            unmatched++;
            return;
        }
        List<Inst> candidates = new ArrayList<>();
        for (Inst i : insts.values()) if (correlates(i, m)) candidates.add(i);
        Inst alive = null;
        for (Inst i : candidates) if (!i.ended && waiting(i, m) != null) { alive = i; break; }
        if (alive == null) for (int k = candidates.size() - 1; k >= 0; k--) if (!candidates.get(k).ended) { alive = candidates.get(k); break; }
        Inst target = alive != null ? alive : candidates.isEmpty() ? null : candidates.get(candidates.size() - 1);
        if (target == null) { unmatched++; return; }
        String id = dedupeId(m);
        boolean seen = candidates.stream().anyMatch(c -> c.consumed.contains(id)) || mailbox.stream().anyMatch(x -> dedupeId(x).equals(id));
        if (seen) { duplicates++; return; }
        if (target.ended) { unmatched++; return; }
        EventSubscription w = waiting(target, m);
        if (w != null) { deliver(target, m, w); return; }
        if (target.expired.contains(type)) { unmatched++; return; }
        // Early: the binding holds the message until a catch is waiting (R-09-16).
        receivedAt.put(m, clock.virtual);
        mailbox.add(m);
        earlyKept++;
    }

    void deliver(Inst target, JsonNode m, EventSubscription w) {
        target.consumed.add(dedupeId(m));
        ObjectNode msgs = target.ctx.has("messages") && target.ctx.get("messages").isObject() ? (ObjectNode) target.ctx.get("messages") : target.ctx.putObject("messages");
        ObjectNode received = msgs.has("received") && msgs.get("received").isObject() ? (ObjectNode) msgs.get("received") : msgs.putObject("received");
        ObjectNode ref = F.objectNode().put("id", m.path("id").asText()).put("type", m.path("type").asText());
        received.set(m.path("type").asText(), ref);
        msgs.set("lastReceived", ref.deepCopy());
        clock.tick();
        rt.messageEventReceived(m.path("type").asText(), w.getExecutionId());
    }

    boolean deliverHeld() {
        boolean p = false;
        for (Iterator<JsonNode> it = mailbox.iterator(); it.hasNext(); ) {
            JsonNode m = it.next();
            for (Inst i : insts.values()) {
                if (i.ended || !correlates(i, m)) continue;
                EventSubscription w = waiting(i, m);
                if (w == null) continue;
                it.remove();
                if (stale(i, m, w)) { unmatched++; notes.add("a held " + m.path("type").asText() + " received before its step began is unmatched (R-05-43)"); p = true; break; }
                deliver(i, m, w);
                p = true;
                break;
            }
            if (p) break;
        }
        return p;
    }

    /** Flowable 7.1 does not wait for compensation handlers; the binding releases the wait when none is left. */
    boolean releaseCompensation() {
        boolean p = false;
        for (EventSubscription s : rt.createEventSubscriptionQuery().eventType("message").eventName("th.compensated").list()) {
            boolean busy = false;
            for (Execution e : rt.createExecutionQuery().processInstanceId(s.getProcessInstanceId()).list()) if (e.getActivityId() != null && undoElements.stream().anyMatch(u -> e.getActivityId().startsWith(u))) busy = true;
            if (busy) continue;
            clock.tick();
            rt.messageEventReceived("th.compensated", s.getExecutionId());
            p = true;
        }
        return p;
    }

    // ================================================================ signals (R-05-12)

    String awaitingStep(Inst i) {
        for (EventSubscription s : rt.createEventSubscriptionQuery().processInstanceId(i.pid).eventType("message").list()) {
            Map<String, String> a = annOf(s.getProcessDefinitionId(), s.getActivityId()).get("await");
            if (a != null) return a.get("step");
        }
        return null;
    }

    void signal(String signal) {
        Inst root = roots.isEmpty() ? null : roots.get(0);
        if (root == null || root.ended) return;
        String aw = awaitingStep(root);
        if ("pause".equals(signal)) {
            if (aw != null) { root.trace().add(aw + ":waiting"); root.trace().add("signal:pause"); } else root.pending.add("signal:pause");
        } else {
            if (aw != null) root.trace().add(aw + ":waiting");
            root.trace().addAll(root.pending);
            root.pending.clear();
            root.trace().add("signal:" + signal);
        }
        String name = plan.path("signalMessages").path(signal).asText();
        List<EventSubscription> subs = rt.createEventSubscriptionQuery().processInstanceId(root.pid).eventType("message").eventName(name).list();
        if (subs.isEmpty()) { notes.add("no handler waits for signal " + signal); return; }
        clock.tick();
        rt.messageEventReceived(name, subs.get(0).getExecutionId());
    }

    // ================================================================ the trace, written where the model marks it

    final class Listener implements FlowableEventListener {
        @Override public void onEvent(FlowableEvent event) {
            if (event.getType() == FlowableEngineEventType.PROCESS_STARTED) processStarted((ExecutionEntity) ((FlowableEntityEvent) event).getEntity());
            else if (event.getType() == FlowableEngineEventType.ACTIVITY_STARTED || event.getType() == FlowableEngineEventType.MULTI_INSTANCE_ACTIVITY_STARTED) activityStarted((FlowableActivityEvent) event);
        }
        @Override public boolean isFailOnException() { return true; }
        @Override public boolean isFireOnTransactionLifecycleEvent() { return false; }
        @Override public String getOnTransaction() { return null; }
    }

    void processStarted(ExecutionEntity started) {
        // For a call activity Flowable sends the child's first execution, not the instance.
        ExecutionEntity pi = started.isProcessInstanceType() ? started : started.getProcessInstance();
        Inst i = new Inst();
        i.pid = pi.getProcessInstanceId();
        Map<String, String> wf = workflowOf.getOrDefault(pi.getProcessDefinitionId(), Map.of());
        i.name = wf.getOrDefault("name", "?");
        i.release = wf.getOrDefault("release", "?");
        i.kind = wf.getOrDefault("kind", "workflow");
        i.key = keyOf.get(pi.getProcessDefinitionId());
        i.startedAt = elapsed();
        durable++;
        ExecutionEntity sup = pi.getSuperExecutionId() == null ? null : CommandContextUtil.getExecutionEntityManager().findById(pi.getSuperExecutionId());
        if (DEBUG) System.err.println("[start] " + pi.getProcessInstanceId() + " super=" + pi.getSuperExecutionId() + " at " + (sup == null ? null : sup.getCurrentActivityId()));
        if (sup != null) {
            Inst parent = insts.get(sup.getProcessInstanceId());
            i.parent = parent;
            i.label = "child";
            i.ctx = parent.ctx.deepCopy();
            Map<String, Map<String, String>> a = annOf(sup.getProcessDefinitionId(), sup.getCurrentActivityId());
            if (a.containsKey("slot")) parent.lastChild.put("slot:" + a.get("slot").get("step"), i.pid);
            if (a.containsKey("hook")) parent.lastChild.put("hook:" + a.get("hook").get("hookPoint"), i.pid);
            i.bindings = parent.bindings;
        } else {
            i.label = pendingLabel == null ? "root" : pendingLabel;
            i.ctx = plan.get("context").deepCopy();
            roots.add(i);
            // Hook bindings are taken when the instance starts (R-06-13).
            String today = Support.isoDate(clock.virtual);
            for (JsonNode b : plan.get("bindings")) {
                String from = b.path("validFrom").asText("0000-01-01");
                String to = b.path("validTo").asText("9999-12-31");
                if (from.compareTo(today) <= 0 && to.compareTo(today) >= 0) i.bindings.add(b);
            }
        }
        for (JsonNode b : i.bindings) pi.setVariable("th_hook_" + Support.safe(b.get("hookPoint").asText()), b.get("key").asText());
        insts.put(i.pid, i);
        i.releaseTrace.add(Support.shortName(i.name) + "@" + i.release);
    }

    void record(Inst i, Map<String, String> a, String entry) { record(i, a, entry, null); }

    /** An item's entry waits with its item's others, so the trace is in item order (R-05-53). */
    void record(Inst i, Map<String, String> a, String entry, ExecutionEntity ex) {
        String r = a.getOrDefault("record", "business");
        if (ex != null && "true".equals(a.get("item")) && ex.getVariable("th_item") instanceof Number n) {
            i.items.computeIfAbsent(n.intValue(), k -> new ArrayList<>()).add(new String[] { r, entry });
            return;
        }
        if ("detail".equals(r)) i.detail().add(entry);
        else i.trace().add(entry);
    }

    String labelOf(ExecutionEntity ex, Map<String, String> a, String step) {
        if ("finally".equals(a.get("record"))) return "finally:" + step;
        if ("true".equals(a.get("item"))) {
            Object lc = ex.getVariable("th_item");
            if (lc instanceof Number n) return step + "[" + n.intValue() + "]";
        }
        return step;
    }

    void activityStarted(FlowableActivityEvent ev) {
        Map<String, Map<String, String>> a = annOf(ev.getProcessDefinitionId(), ev.getActivityId());
        if (a.isEmpty()) return;
        Inst inst = insts.get(ev.getProcessInstanceId());
        if (inst == null) return;
        ExecutionEntity ex = CommandContextUtil.getExecutionEntityManager().findById(ev.getExecutionId());
        if (a.containsKey("block")) inst.blockStarted.put(ev.getActivityId(), clock.virtual);
        if (a.containsKey("forEach")) {
            Map<String, String> fe = a.get("forEach");
            JsonNode items = Support.getPath(inst.ctx, fe.get("in"));
            ex.setVariable(fe.get("count"), items != null && items.isArray() ? items.size() : 0);
        }
        if (a.containsKey("slot")) {
            Map<String, String> s = a.get("slot");
            String slot = s.get("slot");
            JsonNode railN = Support.getPath(inst.ctx, "facts.route.rail");
            if (railN == null || railN.isNull()) railN = Support.getPath(inst.ctx, "header.scheme");
            String rail = railN == null ? "" : railN.asText();
            JsonNode legs = plan.path("legs").path(inst.name).path(slot);
            String leg = legs.path("byRail").path(rail).asText(legs.path("fallback").asText(null));
            JsonNode chosen = null;
            for (JsonNode r : plan.path("releases").path(leg == null ? "" : leg)) if (r.get("availableFromMs").asLong() <= elapsed()) chosen = r;
            if (chosen != null) {
                ex.setVariable("th_leg_" + Support.safe(slot), chosen.get("key").asText());
                inst.releaseTrace.add("leg@" + chosen.get("release").asText());
            } else notes.add("no leg fills slot " + slot);
        }
        if (a.containsKey("hook")) {
            Map<String, String> h = a.get("hook");
            for (JsonNode b : inst.bindings) if (b.get("hookPoint").asText().equals(h.get("hookPoint"))) inst.releaseTrace.add("hook@" + b.get("release").asText());
        }
        Map<String, String> p = a.get("point");
        if (p == null) return;
        switch (p.get("kind")) {
            case "gate" -> {
                // A signal is seen at the next step boundary (the interpreter's gate).
                if (!inst.pending.isEmpty()) { inst.trace().addAll(inst.pending); inst.pending.clear(); }
            }
            case "call" -> {
                String step = p.get("step");
                Object o = ex.getVariable(p.get("var"));
                String label = labelOf(ex, p, step);
                record(inst, p, "finally".equals(p.get("record")) ? label : label + ":" + o, ex);
            }
            case "slot" -> {
                String step = p.get("step");
                String child = inst.lastChild.get("slot:" + step);
                Inst c = child == null ? null : insts.get(child);
                if (c != null) for (String w : Support.csv(p.get("writes"))) {
                    JsonNode val = Support.getPath(c.ctx, w);
                    if (val != null && !val.isNull()) Support.setPath(inst.ctx, w, val.deepCopy());
                }
                record(inst, p, step + ":" + ex.getVariable(p.get("var")));
            }
            case "text" -> {
                // A wait from a duration parameter records the value its timer took (R-05-45).
                String timer = p.get("timer");
                String v = timer == null ? null : lastTimerValue.get(inst.pid + "|" + timer);
                record(inst, p, v == null ? p.get("text") : p.get("prefix") + ":" + v);
            }
            case "awaitReceived" -> {
                inst.awaited.add(p.get("message"));
                record(inst, p, labelOf(ex, p, p.get("step")) + ":received", ex);
            }
            case "awaitTimeout" -> {
                inst.awaited.add(p.get("message"));
                inst.expired.add(p.get("message"));
                record(inst, p, labelOf(ex, p, p.get("step")) + ":timeout", ex);
            }
            case "undo" -> inst.trace().add("undo:" + p.get("step"));
            case "hookStart" -> {
                inst.notifyAt.put(p.get("hookPoint"), inst.trace().size());
                inst.trace().add("hook:pending");
            }
            case "hookResult" -> hookResult(inst, ex, p);
            case "end" -> {
                Object o = ex.getVariable("th_outcome");
                String outcome = o == null ? "done" : o.toString();
                inst.trace().addAll(inst.pending);
                inst.pending.clear();
                inst.trace().add("end:" + outcome);
                inst.releaseTrace.add("end:" + outcome);
                inst.ended = true;
                inst.endedAt = elapsed();
                if ("failed".equals(outcome)) inst.failed = inst.lastFatal != null ? inst.lastFatal : "failed";
            }
            default -> {}
        }
    }

    /** The binding enforces the hook point's contract (R-06-07) and maps a failure by the point's policy. */
    void hookResult(Inst inst, ExecutionEntity ex, Map<String, String> p) {
        String key = p.get("hookPoint");
        String name = p.get("name");
        Object rawV = ex.getVariable("th_hookOutcome_" + Support.safe(name));
        String raw = rawV == null ? "failed" : rawV.toString();
        String child = inst.lastChild.get("hook:" + key);
        Inst c = child == null ? null : insts.get(child);
        if (DEBUG) System.err.println("[hook] " + key + " raw=" + raw + " child=" + child + " lastChild=" + inst.lastChild + " childCtx=" + (c == null ? null : c.ctx));
        if (!"failed".equals(raw) && c != null) {
            if (c.failed != null) raw = "failed";
            else {
                List<String> changes = new ArrayList<>();
                Support.diffPaths(inst.ctx, c.ctx, "", changes);
                List<String> allowed = Support.csv(p.get("writes"));
                // A hook's blocks may derive facts, as check L13 allows (R-06-07).
                boolean outside = changes.stream().anyMatch(x -> !Support.coveredByAny(allowed, x) && !x.startsWith("facts."));
                if (outside) raw = "failed";
                else if ("transform".equals(p.get("type"))) for (String w : allowed) {
                    JsonNode val = Support.getPath(c.ctx, w);
                    if (val != null && !val.isNull()) Support.setPath(inst.ctx, w, val.deepCopy());
                }
            }
        }
        if ("notify".equals(p.get("type"))) {
            Integer at = inst.notifyAt.get(key);
            String entry = "hook:" + raw;
            if (at != null) inst.trace().set(at, entry);
            else inst.trace().add(entry);
            return;
        }
        inst.trace().add("hook:" + raw);
        String eff = raw;
        if ("failed".equals(raw)) eff = "fail-closed".equals(p.get("onFailure")) ? "hold" : "continue";
        ex.setVariable("th_hookEffect_" + Support.safe(name), eff);
    }
}
