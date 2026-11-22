package th.flowable;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.node.ObjectNode;

import java.time.Instant;
import java.util.Iterator;
import java.util.Map;

import static th.flowable.Support.F;

/**
 * A plain request handler at the interop boundary (R-09-14). A handler holds no state, so on the
 * Flowable target it does not run in the engine: the binding runs its steps one after another,
 * each through the party service, and no process instance is started.
 */
final class Handler {
    final Harness h;

    Handler(Harness h) { this.h = h; }

    static final class End extends RuntimeException {
        final String outcome;
        End(String o) { super(o, null, false, false); outcome = o; }
    }

    void run(JsonNode m, JsonNode entry) {
        JsonNode doc = h.plan.path("handler").path("doc");
        Harness.Inst inst = new Harness.Inst();
        inst.pid = "handler-" + m.path("id").asText();
        inst.label = "root";
        inst.name = doc.path("name").asText();
        inst.release = doc.path("release").asText();
        inst.startedAt = h.elapsed();
        String type = m.path("type").asText();
        ObjectNode header = F.objectNode();
        header.put("conversation", m.path("conversation").asText());
        header.put("scheme", type.split("\\.").length > 1 ? type.split("\\.")[0] + "." + type.split("\\.")[1] : type);
        header.put("direction", "in".equals(entry.path("direction").asText()) ? "inward" : "outward");
        header.put("receivedAt", Instant.ofEpochMilli(h.clock.virtual).toString());
        inst.ctx = F.objectNode();
        inst.ctx.set("header", header);
        inst.ctx.putObject("messages").putObject("inbound").put("id", m.path("id").asText()).put("type", type);
        h.roots.add(inst);
        String outcome = doc.path("exit").path("outcomes").path(0).asText("done");
        try {
            steps(doc.path("do"), inst);
        } catch (End e) {
            outcome = e.outcome;
        } catch (RuntimeException e) {
            inst.failed = e.getMessage();
            outcome = "failed";
        }
        inst.trace().add("end:" + outcome);
        inst.releaseTrace.add("end:" + outcome);
        inst.ended = true;
        inst.endedAt = h.elapsed();
    }

    void steps(JsonNode steps, Harness.Inst inst) {
        for (JsonNode st : steps) step(st, inst);
    }

    void step(JsonNode st, Harness.Inst inst) {
        if (st.has("call")) { call(st, inst); return; }
        if (st.has("end")) throw new End(st.get("end").asText());
        throw new IllegalStateException("A handler step the binding cannot run: " + st.fieldNames().next());
    }

    void call(JsonNode st, Harness.Inst inst) {
        String activity = st.get("call").asText();
        String step = st.has("id") ? st.get("id").asText() : Support.shortName(activity);
        int call = inst.calls.merge(step, 1, Integer::sum);
        String outcome = null;
        for (int attempt = 1; attempt <= 3 && outcome == null; attempt++) {
            ObjectNode req = F.objectNode();
            req.put("op", "attempt").put("instance", inst.pid).put("activity", activity).put("step", step).put("call", call).put("attempt", attempt).put("now", h.clock.virtual);
            req.set("ctx", inst.ctx);
            JsonNode res = h.party.call(req);
            h.deliveries(res.get("deliveries"));
            inst.attempts.merge(step, 1, Integer::sum);
            String kind = res.get("kind").asText();
            if ("result".equals(kind)) {
                outcome = res.get("outcome").asText();
                for (Iterator<Map.Entry<String, JsonNode>> w = Support.fields(res.get("writes")); w.hasNext(); ) {
                    Map.Entry<String, JsonNode> e = w.next();
                    Support.setPath(inst.ctx, e.getKey(), e.getValue().deepCopy());
                }
            } else if ("error".equals(kind) && !res.path("retryable").asBoolean(false)) {
                throw new IllegalStateException(activity + " failed: " + res.path("message").asText());
            } else if ("hang".equals(kind)) {
                throw new IllegalStateException(activity + " did not answer");
            }
        }
        if (outcome == null) throw new IllegalStateException(activity + " failed after retries");
        inst.trace().add(step + ":" + outcome);
        JsonNode action = st.path("handle").path(outcome);
        if (action.isMissingNode()) return;
        if (action.isArray()) steps(action, inst);
        else if (action.has("end")) throw new End(action.get("end").asText());
    }
}
