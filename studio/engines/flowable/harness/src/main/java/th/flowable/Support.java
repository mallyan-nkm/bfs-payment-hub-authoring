package th.flowable;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.JsonNodeFactory;
import com.fasterxml.jackson.databind.node.ObjectNode;
import org.flowable.common.engine.impl.calendar.BusinessCalendar;
import org.flowable.common.engine.impl.runtime.Clock;

import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.File;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.time.Instant;
import java.time.ZoneOffset;
import java.time.ZonedDateTime;
import java.util.ArrayList;
import java.util.Calendar;
import java.util.Date;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TimeZone;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** Small pieces the harness needs: JSON paths, the virtual clock, the business calendar, the party client. */
final class Support {
    static final ObjectMapper M = new ObjectMapper();
    static final JsonNodeFactory F = JsonNodeFactory.instance;

    private Support() {}

    // ------------------------------------------------------------------ context paths, as core/paths.ts

    static String[] segs(String path) {
        return path.replaceFirst("^items\\[\\]", "items").split("\\.");
    }

    static JsonNode getPath(JsonNode obj, String path) {
        JsonNode cur = obj;
        for (String s : segs(path)) {
            if (cur == null || cur.isNull() || cur.isMissingNode()) return null;
            cur = cur.get(s);
        }
        return cur;
    }

    static void setPath(ObjectNode obj, String path, JsonNode value) {
        String[] parts = segs(path);
        ObjectNode cur = obj;
        for (int i = 0; i < parts.length - 1; i++) {
            JsonNode n = cur.get(parts[i]);
            if (n == null || !n.isObject()) {
                n = F.objectNode();
                cur.set(parts[i], n);
            }
            cur = (ObjectNode) n;
        }
        cur.set(parts[parts.length - 1], value == null ? F.nullNode() : value);
    }

    static boolean covers(String root, String path) {
        String r = root.replace("[]", "");
        String p = path.replace("[]", "");
        return p.equals(r) || p.startsWith(r + ".");
    }

    static boolean coveredByAny(List<String> roots, String path) {
        for (String r : roots) if (covers(r, path)) return true;
        return false;
    }

    /** The paths that differ between two contexts, as the interpreter's diffPaths. */
    static void diffPaths(JsonNode a, JsonNode b, String prefix, List<String> out) {
        Set<String> keys = new java.util.TreeSet<>();
        if (a != null && a.isObject()) a.fieldNames().forEachRemaining(keys::add);
        if (b != null && b.isObject()) b.fieldNames().forEachRemaining(keys::add);
        for (String k : keys) {
            String p = prefix.isEmpty() ? k : prefix + "." + k;
            JsonNode x = a == null ? null : a.get(k);
            JsonNode y = b == null ? null : b.get(k);
            if (x == null && y == null) continue;
            if (x != null && x.equals(y)) continue;
            if (x != null && y != null && x.isObject() && y.isObject()) diffPaths(x, y, p, out);
            else out.add(p);
        }
    }

    static String safe(String s) {
        return s.replaceAll("[^A-Za-z0-9_]", "_");
    }

    static String shortName(String n) {
        String[] p = n.split("\\.");
        return p.length > 2 ? p[p.length - 2] : n;
    }

    // ------------------------------------------------------------------ durations and the calendar

    private static final Pattern DUR = Pattern.compile("^P(?:(\\d+)Y)?(?:(\\d+)M)?(?:(\\d+)W)?(?:(\\d+)D)?(?:T(?:(\\d+)H)?(?:(\\d+)M)?(?:(\\d+(?:\\.\\d+)?)S)?)?$");

    static long[] parse(String s) {
        Matcher m = DUR.matcher(s);
        if (!m.matches()) throw new IllegalArgumentException("Not an ISO 8601 duration: " + s);
        long[] out = new long[7];
        for (int i = 0; i < 6; i++) out[i] = m.group(i + 1) == null ? 0 : Long.parseLong(m.group(i + 1));
        out[6] = m.group(7) == null ? 0 : Math.round(Double.parseDouble(m.group(7)) * 1000);
        return out;
    }

    static long durationMs(String s) {
        long[] d = parse(s);
        return (((d[2] * 7 + d[3]) * 24 + d[4]) * 60 + d[5]) * 60000 + d[6];
    }

    /** addDuration with the business calendar, as core/duration.ts. */
    static long addBusiness(long t, String s, List<Integer> weekend, List<String> holidays) {
        long[] d = parse(s);
        long out = t;
        long days = d[2] * 7 + d[3];
        for (int i = 0; i < days; i++) {
            out += 86400000L;
            while (!isBusinessDay(out, weekend, holidays)) out += 86400000L;
        }
        out += ((d[4] * 60 + d[5]) * 60) * 1000 + d[6];
        return out;
    }

    static boolean isBusinessDay(long t, List<Integer> weekend, List<String> holidays) {
        ZonedDateTime z = Instant.ofEpochMilli(t).atZone(ZoneOffset.UTC);
        int dow = z.getDayOfWeek().getValue() % 7; // 0 = Sunday
        if (weekend.contains(dow)) return false;
        return !holidays.contains(z.toLocalDate().toString());
    }

    static String isoDate(long t) {
        return Instant.ofEpochMilli(t).atZone(ZoneOffset.UTC).toLocalDate().toString();
    }

    /**
     * The engine's clock. Virtual time moves only when the harness says so; within one virtual
     * instant each engine command gets its own millisecond, so that the engine can order what
     * happened in one instant (it orders compensation by creation time). The harness maps engine
     * times back to virtual time.
     */
    static final class VClock implements Clock {
        long virtual;
        long offset;
        final List<long[]> instants = new ArrayList<>(); // {virtual, offset at the start of the instant}

        VClock(long start) {
            virtual = start;
            instants.add(new long[] {start, 0});
        }

        void advance(long v) {
            if (v < virtual) throw new IllegalStateException("time goes backwards");
            if (v == virtual) return;
            virtual = v;
            instants.add(new long[] {v, offset});
        }

        void tick() { offset++; }

        long engineNow() { return virtual + offset; }

        /** The virtual instant an engine time belongs to. */
        long virtualOf(long engine) {
            int lo = 0, hi = instants.size() - 1, ans = 0;
            while (lo <= hi) {
                int mid = (lo + hi) >>> 1;
                long[] x = instants.get(mid);
                if (x[0] + x[1] <= engine) { ans = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return instants.get(ans)[0];
        }

        @Override public void setCurrentTime(Date currentTime) { throw new UnsupportedOperationException("the harness owns the clock"); }
        @Override public void setCurrentCalendar(Calendar currentTime) { throw new UnsupportedOperationException("the harness owns the clock"); }
        @Override public void reset() {}
        @Override public Date getCurrentTime() { return new Date(engineNow()); }
        @Override public Calendar getCurrentCalendar() { return getCurrentCalendar(TimeZone.getTimeZone("UTC")); }
        @Override public Calendar getCurrentCalendar(TimeZone tz) { Calendar c = Calendar.getInstance(tz); c.setTime(getCurrentTime()); return c; }
        @Override public TimeZone getCurrentTimeZone() { return TimeZone.getTimeZone("UTC"); }
    }

    /** The business calendar a generated timer names: whole days count business days (Part 5). */
    static final class Business implements BusinessCalendar {
        final VClock clock;
        final List<Integer> weekend;
        final List<String> holidays;

        Business(VClock clock, List<Integer> weekend, List<String> holidays) {
            this.clock = clock;
            this.weekend = weekend;
            this.holidays = holidays;
        }

        @Override public Date resolveDuedate(String duedate) { return resolveDuedate(duedate, -1); }

        @Override public Date resolveDuedate(String duedate, int maxIterations) {
            long due = addBusiness(clock.virtual, duedate, weekend, holidays);
            return new Date(due + clock.offset);
        }

        @Override public Boolean validateDuedate(String duedateDescription, int maxIterations, Date endDate, Date newTimer) { return true; }
        @Override public Date resolveEndDate(String endDate) { return null; }
    }

    // ------------------------------------------------------------------ the party service over JSON lines

    static final class Party implements AutoCloseable {
        final Process proc;
        final BufferedWriter out;
        final BufferedReader in;

        Party(File studioRoot) throws IOException {
            File tsx = new File(studioRoot, "node_modules/.bin/tsx");
            ProcessBuilder pb = new ProcessBuilder(tsx.getAbsolutePath(), "src/conformance/party-server.ts").directory(studioRoot);
            pb.redirectError(ProcessBuilder.Redirect.INHERIT);
            proc = pb.start();
            out = new BufferedWriter(new OutputStreamWriter(proc.getOutputStream(), StandardCharsets.UTF_8));
            in = new BufferedReader(new InputStreamReader(proc.getInputStream(), StandardCharsets.UTF_8));
        }

        synchronized JsonNode call(ObjectNode req) {
            try {
                out.write(M.writeValueAsString(req));
                out.write('\n');
                out.flush();
                String line = in.readLine();
                if (line == null) throw new IllegalStateException("the party service stopped");
                JsonNode r = M.readTree(line);
                if (r.has("error")) throw new IllegalStateException("party service: " + r.get("error").asText());
                return r;
            } catch (IOException e) {
                throw new IllegalStateException(e);
            }
        }

        @Override public void close() {
            try { out.close(); } catch (IOException ignored) {}
            try { proc.waitFor(5, java.util.concurrent.TimeUnit.SECONDS); } catch (InterruptedException ignored) {}
            proc.destroy();
        }
    }

    static List<String> strings(JsonNode arr) {
        List<String> out = new ArrayList<>();
        if (arr != null && arr.isArray()) for (JsonNode x : arr) out.add(x.asText());
        return out;
    }

    static List<String> csv(String s) {
        List<String> out = new ArrayList<>();
        if (s == null || s.isEmpty()) return out;
        for (String x : s.split(",")) if (!x.isEmpty()) out.add(x);
        return out;
    }

    static ArrayNode arr(List<String> xs) {
        ArrayNode a = F.arrayNode();
        for (String x : xs) a.add(x);
        return a;
    }

    static Iterator<Map.Entry<String, JsonNode>> fields(JsonNode n) {
        return n == null ? java.util.Collections.emptyIterator() : n.fields();
    }

    static Duration lock() { return Duration.ofDays(20); }
}
