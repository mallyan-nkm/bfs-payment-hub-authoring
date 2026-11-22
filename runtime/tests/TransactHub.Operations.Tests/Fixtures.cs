using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using TransactHub.Model;
using TransactHub.Store;

namespace TransactHub.Operations.Tests;

/// <summary>SQL Server 2022 and PostgreSQL 16, one container each for the run; each test gets databases of its own.</summary>
public sealed class Databases : IAsyncLifetime
{
    readonly MsSqlContainer sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    readonly PostgreSqlContainer pg = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync() => await Task.WhenAll(sql.StartAsync(), pg.StartAsync());
    public async Task DisposeAsync() => await Task.WhenAll(sql.DisposeAsync().AsTask(), pg.DisposeAsync().AsTask());

    public StoreOptions Fresh(StoreProvider p)
    {
        var name = $"th_ops_{Guid.NewGuid():N}"[..24];
        return p switch
        {
            StoreProvider.SqlServer => new(p, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sql.GetConnectionString()) { InitialCatalog = name, TrustServerCertificate = true }.ConnectionString),
            _ => new(p, new Npgsql.NpgsqlConnectionStringBuilder(pg.GetConnectionString()) { Database = name }.ConnectionString),
        };
    }

    /// <summary>A store and a read model, migrated: in the same database, as an installation that keeps both together.</summary>
    public async Task<(StoreOptions store, StoreOptions ops)> Pair(StoreProvider p)
    {
        var o = Fresh(p);
        await using (var db = StoreDbContext.Open(o)) await db.Database.MigrateAsync();
        await using (var ops = OpsDbContext.Open(o)) await ops.Database.MigrateAsync();
        return (o, o);
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<Databases> { public const string Name = "ops-databases"; }

public static class Providers
{
    public static TheoryData<StoreProvider> Both => new() { StoreProvider.SqlServer, StoreProvider.PostgreSql };
}

public static class Repo
{
    public static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "spec")) && Directory.Exists(Path.Combine(d.FullName, "runtime"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    /// <summary>The factory's operations views, as published on the content branch.</summary>
    public static ViewCatalog Views() => ViewCatalog.FromFolder(Path.Combine(Root(), "content-configurations", "artefacts"));
}

/// <summary>
/// A WPS salary file as the runtime records it, with synthetic values only: the bulk accepted as ISO 20022, its payload's
/// facts (the claim check), each step a worker answered (StepFacts), the hold opened by the internal limit, the file kept.
/// </summary>
public static class WpsRun
{
    public const string Tenant = "example-bank";
    public const string FileId = "0000001234567261117083000";

    public static async Task<string> Seed(StoreOptions o, string tenant = Tenant, string fileId = FileId, bool hold = true, string ack = "ack", bool console = false)
    {
        await using var db = StoreDbContext.Open(o);
        var store = new PaymentStore(db);
        var bulk = new PaymentBulk
        {
            MessageId = fileId, PaymentInformationId = fileId, Debtor = new Party { Name = "Example Trading LLC", IsOrganisation = true },
            DebtorAccount = new Account { Iban = "AE070331234567890123999" }, DebtorAgent = new Agent { MemberId = "302620122" },
            // The channel's upload (Issue I-100): who initiated the file, by a synthetic id only.
            InitiatingParty = console ? new Party { OtherIds = [new GenericId("U-000000123456", "CHANNEL-USER")] } : null,
            SupplementaryData = new Dictionary<string, JsonObject> { ["uae.wps"] = new() { ["employerId"] = "0000001234567", ["salaryMonth"] = "102026", ["employerReference"] = "REF-0001" } },
            Transactions =
            [
                Line(1, 1000m, "AE070331234567890123456", "203320101", "00001234567890"),
                Line(2, 500m, "AE970336543210987654321", "302620122", "00001234567891"),
            ],
        };
        var accepted = await store.AcceptBulk(tenant, "uae.wps", Direction.Outward, bulk, "p.wf.corporate.v1");
        var log = new FactLog(db);
        var root = $"root-{fileId[^6..]}-{Guid.NewGuid():N}"[..24];
        var items = new JsonArray(new JsonObject { ["payment"] = new JsonObject { ["creditorAccount"] = new JsonObject { ["iban"] = "AE070331234567890123456" } } },
                                  new JsonObject { ["payment"] = new JsonObject { ["creditorAccount"] = new JsonObject { ["iban"] = "AE970336543210987654321" } } });
        await log.Append($"{root}#ctx", "payload", new NewFact("payload", "create", new JsonObject { ["payload"] = new JsonObject { ["items"] = items } }, Actor: "claim-check"));
        await log.Append($"{root}#ctx", "payload", new NewFact("payload", "accepted", new JsonObject { ["bulk"] = accepted.BulkId, ["payments"] = 2 }, Actor: "claim-check"));
        await log.Append($"{root}/leg3#ctx", "payload", new NewFact("payload", "fork", new JsonObject { ["from"] = $"{root}#ctx", ["payload"] = new JsonObject { ["items"] = items.DeepClone() } }, Actor: "claim-check"));
        var header = new JsonObject { ["scheme"] = "uae.wps", ["direction"] = "outward", ["kind"] = "payment", ["segment"] = "corporate", ["fileId"] = fileId };
        async Task Step(string instance, string step, string activity, string outcome, JsonObject? writes = null, string? reasonCode = null, JsonObject? status = null, string? level = null)
        {
            var d = new JsonObject { ["instance"] = instance, ["step"] = step, ["activity"] = activity, ["release"] = "1.0.0", ["call"] = 1, ["attempt"] = 1, ["level"] = level, ["header"] = header.DeepClone(), ["outcome"] = outcome };
            if (writes is not null) d["writes"] = writes;
            if (reasonCode is not null) d["reasonCode"] = reasonCode;
            if (status is not null) d["status"] = status;
            await log.Append(accepted.BulkId, "bulk", new NewFact("step", outcome, d, Actor: "engine"));
        }
        await Step(root, "validate", "p.validate.canonical.v1", "pass", new JsonObject { ["facts.validation"] = new JsonObject { ["worst"] = "pass", ["findings"] = new JsonArray() } });
        await Step(root, "duplicate", "p.validate.duplicate.v1", "unique");
        await Step(root, "sanctions", "p.screen.sanctions.v1", "clear", new JsonObject { ["facts.screening.sanctions"] = new JsonObject { ["outcome"] = "clear" } });
        await Step(root, "fraud", "p.screen.fraud.v1", "clear");
        await Step(root, "reserve", "p.reserve.funds.v1", "reserved");
        await Step(root, "route", "p.decide.rail.v1", "decided", new JsonObject { ["facts.route"] = new JsonObject { ["rail"] = "uae.wps" } });
        if (hold)
        {
            await Step($"{root}/hook2", "internal-limit", "b.validate.internal-limit.v1", "reject", reasonCode: "AM02");
            await log.Append(accepted.BulkId, "bulk", new NewFact("hold", "opened", new JsonObject { ["hold"] = $"hold-{fileId[^4..]}-{root[^6..]}", ["kind"] = "approval", ["activity"] = "p.hold.approval.v1", ["reason"] = "AM02 at internal-limit (b.validate.internal-limit.v1)", ["expiry"] = "P1D" }, Actor: "runtime"));
        }
        await Step($"{root}/leg3", "business-date", "uae.p.enrich.business-date.v1", "set");
        await Step($"{root}/leg3", "file-checks", "uae.wps.validate.salary-file.v1", "pass", new JsonObject { ["facts.validation"] = new JsonObject { ["worst"] = "pass", ["findings"] = new JsonArray() } }, level: "batch");
        // The lines' own checks, as the claim check commits them on the leg's payload.
        for (var i = 0; i < 2; i++)
        {
            var token = $"{root}/leg3#ctx!{i + 1}";
            await log.Append($"{root}/leg3#ctx", "payload", new NewFact("payload", "staged", new JsonObject { ["token"] = token, ["writes"] = new JsonArray(new JsonObject { ["path"] = $"items[{i}].facts.validation", ["value"] = new JsonObject { ["worst"] = i == 0 ? "pass" : "repair", ["findings"] = new JsonArray() } }) }, Actor: "claim-check"));
            await log.Append($"{root}/leg3#ctx", "payload", new NewFact("payload", "commit", new JsonObject { ["token"] = token }, Actor: "claim-check"));
        }
        await Step($"{root}/leg3", "render", "uae.wps.map.sif-out.v1", "mapped");
        await Step($"{root}/leg3", "submit.send", "uae.wps.exchange.send-salary-file.v1", "accepted");
        await Step($"{root}/leg3", "submit.read-ack", "uae.wps.map.ack-in.v1", ack, new JsonObject { ["status"] = new JsonObject { ["code"] = ack == "ack" ? "ACCP" : "RJCT" } });
        await store.KeepMessage(tenant, "uae.wps", Direction.Inward, "uae.wps.sif", $"{fileId}.SIF", "SCR,synthetic\n"u8.ToArray(), accepted.BulkId);
        if (console)
        {
            // The posting as camt.054 reports it, and the payment's end (Issues I-97, I-100). Synthetic accounts, masked.
            await Step(root, "post", "p.post.accounting.v1", "posted", new JsonObject
            {
                ["facts.posting"] = new JsonObject
                {
                    ["outcome"] = "posted", ["totalAmount"] = "1510.50", ["currency"] = "AED",
                    ["charges"] = new JsonObject { ["amount"] = "10.00", ["currency"] = "AED", ["type"] = "BANK", ["tax"] = new JsonObject { ["rate"] = "5.00", ["amount"] = "0.50" }, ["totalChargesAndTaxAmount"] = "10.50" },
                    ["entries"] = new JsonArray(new JsonObject { ["creditDebitIndicator"] = "DBIT", ["amount"] = "1500.00", ["currency"] = "AED", ["account"] = "*******************3999" }),
                },
            });
            await log.Append(accepted.BulkId, "bulk", new NewFact("payment.end", "settled", new JsonObject { ["outcome"] = "settled" }, Actor: "front-door"));
        }
        return accepted.BulkId;
    }

    static CreditTransfer Line(int n, decimal amount, string iban, string routing, string person) => new()
    {
        PaymentId = new PaymentIdentification { EndToEndId = $"{FileId}-{n}" },
        InstructedAmount = Money.Aed(amount), Creditor = new Party(), CreditorAccount = new Account { Iban = iban },
        CreditorAgent = new Agent { MemberId = routing },
        SupplementaryData = new Dictionary<string, JsonObject> { ["uae.wps"] = new() { ["personId"] = person, ["payStartDate"] = "2026-10-01", ["payEndDate"] = "2026-10-31", ["daysInPeriod"] = 31, ["fixedPay"] = amount * 0.8m, ["variablePay"] = amount * 0.2m, ["leaveDays"] = 0 } },
    };
}
