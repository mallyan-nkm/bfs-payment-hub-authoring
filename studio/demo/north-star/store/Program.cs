using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Model;
using TransactHub.Store;

// The north-star video's store (demo/north-star/store.sh): the runtime's store migrated on PostgreSQL, then, through the store's own
// classes, two distributions of the example bank (the second activated while the first drains), the bank's values by date, and one
// synthetic WPS salary file of two lines pinned to the first distribution, with the facts of its run and the event stream's position.
// The facts stand for a run; no engine runs here. Synthetic data only. The connection comes from the environment, never a file.
var cs = Environment.GetEnvironmentVariable("TH_DEMO_STORE_CONNECTION") ?? throw new InvalidOperationException("Set TH_DEMO_STORE_CONNECTION");
var o = new StoreOptions(StoreProvider.PostgreSql, cs);
await using (var m = StoreDbContext.Open(o)) await m.Database.MigrateAsync();
var clock = new Clock(new DateTimeOffset(2026, 11, 24, 6, 0, 0, TimeSpan.Zero));
await using var db = StoreDbContext.Open(o);
const string bank = "example-bank", env = "laptop";
var dists = new DistributionRegistry(db, clock);
JsonObject Manifest(string id) => new() { ["kind"] = "distribution", ["bank"] = bank, ["id"] = id, ["engine"] = "temporal", ["dsl"] = "1.0.0" };
byte[] Pkg(string id) => Encoding.UTF8.GetBytes($"synthetic distribution {id}");
await dists.Store(bank, "2026.11.24-1", Pkg("2026.11.24-1"), Manifest("2026.11.24-1"), ReleaseRegistry.HashOf(Pkg("2026.11.24-1")), "service.engineer", "first install");
clock.Advance(TimeSpan.FromMinutes(5));
await dists.Activate(DistributionRegistry.IdOf(bank, "2026.11.24-1"), env, "bank.approver", "consent 1");

// The bank's values, effective-dated, as a configuration release in force leaves them.
void Value(string name, int version, JsonNode value, DateTimeOffset from, DateTimeOffset? to) => db.VersionedValues.Add(new VersionedValueRow
{
    Kind = "config", Tenant = bank, Name = name, VersionNo = version, EffectiveFrom = from, EffectiveTo = to,
    BodyJson = new JsonObject { ["value"] = value, ["configurationSet"] = "1.0.0" }.ToJsonString(), PublishedAt = clock.GetUtcNow(), PublishedBy = "bank.approver",
});
DateTimeOffset D(int y, int mo, int d) => new(y, mo, d, 0, 0, 0, TimeSpan.Zero);
Value("b.rule.internal-limit.v1#internalLimit", 1, 250000, D(2026, 1, 1), D(2027, 1, 1));
Value("b.rule.internal-limit.v1#internalLimit", 2, 100000, D(2027, 1, 1), null);
Value("p.table.rail-choice.v1#enabledRails", 1, new JsonArray("uae.wps"), D(2026, 1, 1), null);
await db.SaveChangesAsync();

clock.Advance(TimeSpan.FromMinutes(20));
var T0 = clock.GetUtcNow();
var bulk = new PaymentBulk
{
    MessageId = "SIF-0000000000001-20261124", PaymentInformationId = "PMTINF-1", CreationDateTime = T0,
    RequestedExecutionDate = new DateOnly(2026, 11, 25), CategoryPurposeCode = "SALA",
    Debtor = new Party { Name = "Example Trading LLC", IsOrganisation = true, OtherIds = [new GenericId("0000000000001", "MOL", "AE-MOHRE")] },
    DebtorAccount = new Account { Iban = "AE070000000000000000001", Currency = "AED" },
    DebtorAgent = new Agent { ClearingSystem = "AEUAE", MemberId = "000000001" },
    SupplementaryData = new Dictionary<string, JsonObject> { ["uae.wps"] = new() { ["salaryMonth"] = "2026-10", ["establishmentId"] = "0000000000001" } },
    Transactions =
    [
        Line("E2E-1", 4500.50m, "Employee One", "AE070000000000000000011", 4000m, 500.50m),
        Line("E2E-2", 3200m, "Employee Two", "AE070000000000000000022", 3200m, 0m),
    ],
};
var facts = new FactLog(db, clock);
var accepted = await new PaymentStore(db, clock).AcceptBulk(bank, "uae.wps", Direction.Outward, bulk, "2026.11.24-1");
var id = accepted.BulkId;
await dists.Pin(id, bank, env, "front-door");
JsonObject Step(string wf, string step, string activity) => new() { ["workflow"] = wf, ["step"] = step, ["activity"] = activity, ["release"] = "1.0.0" };
async Task F(int minutes, NewFact f) { clock.Advance(TimeSpan.FromMinutes(minutes)); await facts.Append(id, "bulk", f); }
await F(1, new NewFact("step", "clear", Step("p.wf.corporate.v1", "screen", "p.screen.sanctions.v1"), Actor: "engine"));
await F(1, new NewFact("step", "pass", Step("p.wf.corporate.v1", "limits", "p.validate.limits.v1"), Actor: "engine"));
await F(1, new NewFact("step", "reserved", Step("p.wf.corporate.v1", "reserve", "p.reserve.funds.v1"), Actor: "engine"));
await F(1, new NewFact("step", "uae.wps", Step("p.wf.corporate.v1", "route", "p.decide.rail.v1"), Actor: "engine"));
await F(1, new NewFact("status", "ACSP", new JsonObject { ["from"] = "uae.wps.wf.salary-file-out.v1" }, Actor: "engine"));
await F(1, new NewFact("message", "sif-generated", new JsonObject { ["message"] = "SIF-0000000000001-20261124", ["edr"] = 2, ["evp"] = 0 }));
await F(1, new NewFact("file.handed", "outbox", new JsonObject { ["port"] = "p.port.file-transfer.v1", ["binding"] = "sftp" }, Actor: "file-api"));
await F(40, new NewFact("message", "ack-matched", new JsonObject { ["message"] = "ACK-0000000000001-20261124" }, Actor: "front-door"));
await F(1, new NewFact("step", "posted", Step("p.wf.corporate.v1", "post", "p.post.accounting.v1"), Actor: "engine"));
await F(1, new NewFact("status", "ACSC", new JsonObject { ["from"] = "p.wf.corporate.v1" }, Actor: "engine"));

// Distribution B beside A: activating B drains A in the same step. The payment stays pinned to A.
clock.Advance(TimeSpan.FromMinutes(30));
await dists.Store(bank, "2026.11.24-2", Pkg("2026.11.24-2"), Manifest("2026.11.24-2"), ReleaseRegistry.HashOf(Pkg("2026.11.24-2")), "service.engineer", "second install");
clock.Advance(TimeSpan.FromMinutes(5));
await dists.Activate(DistributionRegistry.IdOf(bank, "2026.11.24-2"), env, "bank.approver", "consent 2");

// The event stream's published position, as the Kafka stream writes it after a batch.
var through = await db.Outbox.MaxAsync(x => x.Id);
db.StreamPositions.Add(new StreamPositionRow { Stream = "p.port.event-stream.v1/th.payments", Through = through, Events = (int)through, At = clock.GetUtcNow() });
await db.SaveChangesAsync();
var check = await facts.Verify(id);
Console.WriteLine($"seeded bulk {id}: {accepted.PaymentIds.Count} payments, chain ok={check.Ok} facts={check.Facts}");

static CreditTransfer Line(string e2e, decimal amount, string name, string iban, decimal fixedPay, decimal variablePay) => new()
{
    PaymentId = new PaymentIdentification { EndToEndId = e2e, Uetr = Guid.NewGuid().ToString() },
    InstructedAmount = new Money(amount, "AED"),
    Creditor = new Party { Name = name, OtherIds = [new GenericId("10000000000001", "PERSON-CODE")] },
    CreditorAccount = new Account { Iban = iban },
    CreditorAgent = new Agent { ClearingSystem = "AEUAE", MemberId = "000000002" },
    PurposeCode = "SALA", RemittanceUnstructured = ["Salary 2026-10"],
    SupplementaryData = new Dictionary<string, JsonObject> { ["uae.wps"] = new() { ["fixedPay"] = fixedPay, ["variablePay"] = variablePay, ["leaveDays"] = 0 } },
};

sealed class Clock(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan d) => now += d;
}
