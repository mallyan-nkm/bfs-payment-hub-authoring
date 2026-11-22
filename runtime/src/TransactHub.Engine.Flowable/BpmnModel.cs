using System.Xml.Linq;

namespace TransactHub.Engine.Flowable;

/// <summary>
/// What the binding reads from a BPMN document the studio generated (studio/src/export/bpmn.ts): the
/// process key, the workflow it was generated from, and the <c>th:</c> annotations (namespace
/// urn:transacthub:flowable:1) on each element: the step, activity, release and policy of a task, the
/// places the binding acts. Flow is in the BPMN only; the binding never reads it.
/// </summary>
public sealed class BpmnProcess
{
    public const string ThNs = "urn:transacthub:flowable:1";
    static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    static readonly XNamespace Fl = "http://flowable.org/bpmn";

    public string Key { get; private init; } = "";
    /// <summary>th:workflow: name, release, kind, wfKind, exit.</summary>
    public IReadOnlyDictionary<string, string> Workflow { get; private init; } = new Dictionary<string, string>();
    readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> anns = new();
    readonly Dictionary<string, string> topics = new();
    readonly Dictionary<string, string?> parentOf = new();
    readonly Dictionary<string, string> durations = new();

    /// <summary>The topic of every external worker task.</summary>
    public IEnumerable<string> Topics => topics.Values.Distinct();

    public static BpmnProcess Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var proc = doc.Root!.Elements(Bpmn + "process").Single();
        var p = new BpmnProcess { Key = (string)proc.Attribute("id")!, Workflow = Annotations(proc).GetValueOrDefault("workflow") ?? new() };
        p.Walk(proc, null);
        return p;
    }

    void Walk(XElement container, string? parentId)
    {
        foreach (var el in container.Elements())
        {
            if (el.Name == Bpmn + "extensionElements") continue;
            var id = (string?)el.Attribute("id");
            if (id is null) continue;
            parentOf[id] = parentId;
            var a = Annotations(el);
            if (a.Count > 0) anns[id] = a;
            if ((string?)el.Attribute(Fl + "type") == "external-worker" && (string?)el.Attribute(Fl + "topic") is string t) topics[id] = t;
            if (el.Element(Bpmn + "timerEventDefinition")?.Element(Bpmn + "timeDuration") is { } d) durations[id] = d.Value.Trim();
            Walk(el, id);
        }
    }

    static Dictionary<string, Dictionary<string, string>> Annotations(XElement el)
    {
        var out_ = new Dictionary<string, Dictionary<string, string>>();
        var ext = el.Element(Bpmn + "extensionElements");
        if (ext is null) return out_;
        foreach (var x in ext.Elements().Where(x => x.Name.NamespaceName == ThNs))
            out_[x.Name.LocalName] = x.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value);
        return out_;
    }

    /// <summary>One annotation of an element (task, point, call, slot, hook, forEach, await, timer, block), or null.</summary>
    public IReadOnlyDictionary<string, string>? Ann(string elementId, string tag) =>
        anns.TryGetValue(elementId, out var a) && a.TryGetValue(tag, out var x) ? x : null;

    public string? TopicOf(string elementId) => topics.GetValueOrDefault(elementId);

    /// <summary>The timeDuration of a timer event, as written: an ISO 8601 duration or an expression.</summary>
    public string? DurationOf(string elementId) => durations.GetValueOrDefault(elementId);

    /// <summary>The element and every container it is in, innermost first.</summary>
    public IEnumerable<string> Ancestry(string elementId)
    {
        for (string? e = elementId; e is not null; e = parentOf.GetValueOrDefault(e)) yield return e;
    }

    /// <summary>The compensation handler an element is in (a sub-process marked th:point kind undo), or null.</summary>
    public IReadOnlyDictionary<string, string>? UndoOf(string elementId) =>
        Ancestry(elementId).Select(e => Ann(e, "point")).FirstOrDefault(p => p is not null && p.GetValueOrDefault("kind") == "undo");
}
