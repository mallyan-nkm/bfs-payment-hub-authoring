using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Engines.Durable;

/// <summary>
/// The bank's values for a runtime's engine host, read from the configuration releases in force in the store as of each step
/// (R-09-135): a parameter its owner reads as of the payment's date (<c>lookupDate: payment-date</c>, R-04-10) as of the payment's
/// acceptance (its <c>distribution.pinned</c> fact, R-09-53), every other as of the step's time. Nothing is held between reads, so
/// a value a later configuration release puts in force reaches the next step that reads it, with no restart of the host.
/// </summary>
public static class StoreValues
{
    /// <summary>
    /// Installs the store as the engines' live source of values (<see cref="Deployment.LiveValues"/>) and of the bank's business calendar
    /// (<see cref="Deployment.LiveCalendar"/>, R-09-183, Issue I-166).
    /// </summary>
    public static void Install(StoreOptions store)
    {
        Deployment.LiveValues = a => Read(store, a).GetAwaiter().GetResult();
        Deployment.LiveCalendar = a => Calendar(store, a).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The bank's business calendar at a moment (R-09-183, Issue I-166), as its configuration release in force installed it (R-09-75):
    /// the edition of its table of domain <c>business-calendar</c> in force then, weekday rows as the weekend and dated rows as holidays,
    /// with its own closed days (domain <c>bank-calendar</c>, kind <c>closed</c>) as holidays too; the same calendar a hold's expiry and
    /// the turnaround count on. Null when the release installed neither table.
    /// </summary>
    public static async Task<JsonObject?> Calendar(StoreOptions store, JsonObject a, CancellationToken ct = default)
    {
        var tenant = J.S(a, "tenant") ?? throw new InvalidOperationException("The bank's calendar is read for a tenant: the run names none");
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)(J.Num(a, "at") ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using var db = StoreDbContext.Open(store);
        var (_, rows) = await ReferenceTables.RowsAt(db, tenant, "business-calendar", at, ct);
        var (_, bankRows) = await ReferenceTables.RowsAt(db, tenant, "bank-calendar", at, ct);
        var closed = (bankRows ?? []).OfType<JsonObject>().Where(r => (string?)r["kind"] == "closed" && ((string?)r["day"])?.Length == 10).Select(r => (string)r["day"]!).ToList();
        if (rows is null && closed.Count == 0) return null;
        var cal = rows is null ? BusinessCalendar.Default : BusinessCalendar.FromRows(rows);
        return new BusinessCalendar(cal.Weekend, new HashSet<string>(cal.Holidays.Concat(closed), StringComparer.Ordinal)).ToJson();
    }

    public static async Task<JsonObject> Read(StoreOptions store, JsonObject a, CancellationToken ct = default)
    {
        var tenant = J.S(a, "tenant") ?? throw new InvalidOperationException("The bank's values are read for a tenant: the run names none");
        var step = DateTimeOffset.FromUnixTimeMilliseconds((long)(J.Num(a, "at") ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using var db = StoreDbContext.Open(store);
        DateTimeOffset? accepted = null;
        if (J.S(a, "paymentId") is { Length: > 0 } paymentId)
            accepted = await db.Facts.AsNoTracking().Where(f => f.SubjectId == paymentId && f.Kind == DistributionRegistry.PinKind).OrderBy(f => f.Seq).Select(f => (DateTimeOffset?)f.At).FirstOrDefaultAsync(ct);
        var read = new Dictionary<DateTimeOffset, Dictionary<string, JsonNode?>>();
        var values = new JsonObject();
        var asOf = new JsonObject();
        foreach (var p in J.A(a, "parameters").OfType<JsonObject>())
        {
            var name = J.S(p, "parameter");
            if (name is null) continue;
            var at = J.S(p, "lookupDate") == "payment-date" && accepted is { } acc ? acc : step;
            if (!read.TryGetValue(at, out var inForce))
                read[at] = inForce = (await ConfigurationValues.InForce(db, tenant, at, ct)).ToDictionary(v => (string)v["parameter"]!, v => v["value"]?.DeepClone(), StringComparer.Ordinal);
            if (inForce.TryGetValue(name, out var v)) values[name] = v?.DeepClone();
            asOf[name] = at.ToString("O");
        }
        return new JsonObject { ["values"] = values, ["asOf"] = asOf };
    }
}
