using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Model;

namespace TransactHub.Store;

/// <summary>Accepts payments into the store and reads them back as the ISO 20022 model.</summary>
public sealed class PaymentStore(StoreDbContext db, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The id a payment is accepted under (R-09-180, Issue I-286): one the front door started it under (<c>header.paymentId</c>), when the
    /// store has its pin (<see cref="DistributionRegistry.PinKind"/>) and no bulk or payment holds that id yet; else a new one. A payment
    /// the front door started then has one id: its pin, its run's facts, its kept files, the read model and the consoles all name it.
    /// </summary>
    async Task<string> IdFor(string? frontDoorId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(frontDoorId) || frontDoorId.Length > 64) return NewId();
        if (!await db.Facts.AsNoTracking().AnyAsync(f => f.SubjectId == frontDoorId && f.Kind == DistributionRegistry.PinKind, ct)) return NewId();
        if (await db.Bulks.AsNoTracking().AnyAsync(b => b.Id == frontDoorId, ct) || await db.Payments.AsNoTracking().AnyAsync(x => x.Id == frontDoorId, ct)) return NewId();
        return frontDoorId;
    }

    /// <summary>
    /// Stores a bulk and its transactions under the release it was accepted on, with a first status fact
    /// for the bulk (RCVD). One save: all of it or nothing. A bulk the front door started is accepted under its id (R-09-180).
    /// </summary>
    public async Task<AcceptedBulk> AcceptBulk(string tenant, string scheme, Direction direction, PaymentBulk bulk, string releaseId, CancellationToken ct = default, string? frontDoorId = null)
    {
        var at = StoreTime.Of(clock.GetUtcNow());
        var bulkId = await IdFor(frontDoorId, ct);
        var debtor = PartyOf(tenant, bulk.Debtor, at);
        db.Parties.Add(debtor);
        var initiator = bulk.InitiatingParty is null ? null : PartyOf(tenant, bulk.InitiatingParty, at);
        if (initiator is not null) db.Parties.Add(initiator);
        db.Bulks.Add(new BulkRow
        {
            Id = bulkId, Tenant = tenant, Scheme = scheme, Direction = direction.ToString(),
            MessageId = bulk.MessageId, PaymentInformationId = bulk.PaymentInformationId,
            CreationDateTime = StoreTime.Of(bulk.CreationDateTime == default ? at : bulk.CreationDateTime),
            RequestedExecutionDate = bulk.RequestedExecutionDate, InitiatingPartyId = initiator?.Id,
            DebtorPartyId = debtor.Id, DebtorAccount = AccountOf(bulk.DebtorAccount), DebtorAgent = AgentOf(bulk.DebtorAgent),
            CategoryPurposeCode = bulk.CategoryPurposeCode,
            NumberOfTransactions = bulk.NumberOfTransactions, ControlSum = bulk.ControlSum,
            SupplementaryJson = Supplementary(bulk.SupplementaryData), ReleaseId = releaseId, AcceptedAt = at,
        });
        var ids = new List<string>();
        var line = 0;
        foreach (var t in bulk.Transactions)
        {
            var id = NewId();
            ids.Add(id);
            db.Payments.Add(PaymentOf(id, tenant, scheme, direction, t, releaseId, at, bulkId, ++line, debtor.Id, bulk.DebtorAccount, bulk.DebtorAgent));
        }
        await db.SaveChangesAsync(ct);
        await new FactLog(db, clock).Append(bulkId, "bulk", new NewFact("status", nameof(TransactionStatus.RCVD), ReleaseId: releaseId), ct);
        return new AcceptedBulk(bulkId, ids);
    }

    public async Task<string> AcceptPayment(string tenant, string scheme, Direction direction, CreditTransfer t, string releaseId, CancellationToken ct = default, string? frontDoorId = null)
    {
        var at = StoreTime.Of(clock.GetUtcNow());
        var id = await IdFor(frontDoorId, ct);
        db.Payments.Add(PaymentOf(id, tenant, scheme, direction, t, releaseId, at, null, 0, null, null, null));
        await db.SaveChangesAsync(ct);
        await new FactLog(db, clock).Append(id, "payment", new NewFact("status", nameof(TransactionStatus.RCVD), ReleaseId: releaseId), ct);
        return id;
    }

    PaymentRow PaymentOf(string id, string tenant, string scheme, Direction direction, CreditTransfer t, string releaseId, DateTimeOffset at,
        string? bulkId, int line, string? bulkDebtorId, Account? bulkAccount, Agent? bulkAgent)
    {
        string? party(Party? p)
        {
            if (p is null) return null;
            var row = PartyOf(tenant, p, at);
            db.Parties.Add(row);
            return row.Id;
        }
        return new PaymentRow
        {
            Id = id, Tenant = tenant, Scheme = scheme, Direction = direction.ToString(), BulkId = bulkId, LineNo = line,
            InstructionId = t.PaymentId.InstructionId, EndToEndId = t.PaymentId.EndToEndId,
            TransactionId = t.PaymentId.TransactionId, Uetr = t.PaymentId.Uetr,
            Amount = t.InstructedAmount.Amount, Currency = t.InstructedAmount.Currency,
            SettlementAmount = t.InterbankSettlementAmount?.Amount, SettlementCurrency = t.InterbankSettlementAmount?.Currency,
            SettlementDate = t.InterbankSettlementDate, ChargeBearer = t.ChargeBearer.ToString(),
            UltimateDebtorPartyId = party(t.UltimateDebtor),
            DebtorPartyId = t.Debtor is null ? bulkDebtorId : party(t.Debtor),
            DebtorAccount = AccountOf(t.DebtorAccount ?? bulkAccount), DebtorAgent = AgentOf(t.DebtorAgent ?? bulkAgent),
            CreditorAgent = AgentOf(t.CreditorAgent),
            CreditorPartyId = party(t.Creditor)!, CreditorAccount = AccountOf(t.CreditorAccount),
            UltimateCreditorPartyId = party(t.UltimateCreditor),
            PurposeCode = t.PurposeCode, CategoryPurposeCode = t.CategoryPurposeCode,
            RemittanceJson = JsonSerializer.Serialize(t.RemittanceUnstructured), SupplementaryJson = Supplementary(t.SupplementaryData),
            ReleaseId = releaseId, AcceptedAt = at,
        };
    }

    /// <summary>A bulk as it was accepted, as the model.</summary>
    public async Task<PaymentBulk?> ReadBulk(string bulkId, CancellationToken ct = default)
    {
        var b = await db.Bulks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == bulkId, ct);
        if (b is null) return null;
        var rows = await db.Payments.AsNoTracking().Where(p => p.BulkId == bulkId).OrderBy(p => p.LineNo).ToListAsync(ct);
        var partyIds = rows.SelectMany(r => new[] { r.CreditorPartyId, r.DebtorPartyId, r.UltimateCreditorPartyId, r.UltimateDebtorPartyId }).Append(b.DebtorPartyId).Append(b.InitiatingPartyId).OfType<string>().Distinct().ToList();
        var parties = await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return new PaymentBulk
        {
            MessageId = b.MessageId, PaymentInformationId = b.PaymentInformationId, CreationDateTime = b.CreationDateTime,
            RequestedExecutionDate = b.RequestedExecutionDate,
            InitiatingParty = b.InitiatingPartyId is null ? null : ModelOf(parties[b.InitiatingPartyId]),
            Debtor = ModelOf(parties[b.DebtorPartyId]), DebtorAccount = ModelOf(b.DebtorAccount)!, DebtorAgent = ModelOf(b.DebtorAgent),
            CategoryPurposeCode = b.CategoryPurposeCode, SupplementaryData = SupplementaryOf(b.SupplementaryJson),
            Transactions = rows.Select(r => TransferOf(r, parties, omitDebtor: r.DebtorPartyId == b.DebtorPartyId)).ToList(),
        };
    }

    public async Task<CreditTransfer?> ReadPayment(string id, CancellationToken ct = default)
    {
        var r = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return null;
        var ids = new[] { r.CreditorPartyId, r.DebtorPartyId, r.UltimateCreditorPartyId, r.UltimateDebtorPartyId }.OfType<string>().ToList();
        var parties = await db.Parties.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return TransferOf(r, parties, omitDebtor: false);
    }

    static CreditTransfer TransferOf(PaymentRow r, Dictionary<string, PartyRow> parties, bool omitDebtor)
    {
        Party? p(string? id) => id is null ? null : ModelOf(parties[id]);
        return new CreditTransfer
        {
            PaymentId = new PaymentIdentification { InstructionId = r.InstructionId, EndToEndId = r.EndToEndId, TransactionId = r.TransactionId, Uetr = r.Uetr },
            InstructedAmount = new Money(r.Amount, r.Currency),
            InterbankSettlementAmount = r.SettlementAmount is decimal s ? new Money(s, r.SettlementCurrency!) : null,
            InterbankSettlementDate = r.SettlementDate,
            ChargeBearer = Enum.Parse<ChargeBearer>(r.ChargeBearer),
            UltimateDebtor = p(r.UltimateDebtorPartyId),
            Debtor = omitDebtor ? null : p(r.DebtorPartyId),
            DebtorAccount = omitDebtor ? null : ModelOf(r.DebtorAccount),
            DebtorAgent = omitDebtor ? null : ModelOf(r.DebtorAgent),
            CreditorAgent = ModelOf(r.CreditorAgent),
            Creditor = p(r.CreditorPartyId)!,
            CreditorAccount = ModelOf(r.CreditorAccount)!,
            UltimateCreditor = p(r.UltimateCreditorPartyId),
            PurposeCode = r.PurposeCode, CategoryPurposeCode = r.CategoryPurposeCode,
            RemittanceUnstructured = JsonSerializer.Deserialize<List<string>>(r.RemittanceJson) ?? [],
            SupplementaryData = SupplementaryOf(r.SupplementaryJson),
        };
    }

    static PartyRow PartyOf(string tenant, Party p, DateTimeOffset at) => new()
    {
        Id = NewId(), Tenant = tenant, Name = p.Name, IsOrganisation = p.IsOrganisation,
        Department = p.PostalAddress?.Department, StreetName = p.PostalAddress?.StreetName, BuildingNumber = p.PostalAddress?.BuildingNumber,
        PostCode = p.PostalAddress?.PostCode, TownName = p.PostalAddress?.TownName, CountrySubDivision = p.PostalAddress?.CountrySubDivision,
        Country = p.PostalAddress?.Country, AddressLinesJson = JsonSerializer.Serialize(p.PostalAddress?.AddressLine ?? []),
        AnyBic = p.AnyBic, Lei = p.Lei, OtherIdsJson = JsonSerializer.Serialize(p.OtherIds), CountryOfResidence = p.CountryOfResidence, CreatedAt = at,
    };

    static Party ModelOf(PartyRow r)
    {
        var lines = JsonSerializer.Deserialize<List<string>>(r.AddressLinesJson) ?? [];
        var hasAddress = r.StreetName ?? r.BuildingNumber ?? r.PostCode ?? r.TownName ?? r.CountrySubDivision ?? r.Country ?? r.Department;
        return new Party
        {
            Name = r.Name, IsOrganisation = r.IsOrganisation, AnyBic = r.AnyBic, Lei = r.Lei, CountryOfResidence = r.CountryOfResidence,
            OtherIds = JsonSerializer.Deserialize<List<GenericId>>(r.OtherIdsJson) ?? [],
            PostalAddress = hasAddress is null && lines.Count == 0 ? null : new PostalAddress
            {
                Department = r.Department, StreetName = r.StreetName, BuildingNumber = r.BuildingNumber, PostCode = r.PostCode,
                TownName = r.TownName, CountrySubDivision = r.CountrySubDivision, Country = r.Country, AddressLine = lines,
            },
        };
    }

    static AccountCols AccountOf(Account? a) => new() { Iban = a?.Iban, OtherId = a?.Other?.Id, OtherScheme = a?.Other?.SchemeName, Currency = a?.Currency, Name = a?.Name, Type = a?.Type };
    static Account? ModelOf(AccountCols? c) => c is null || (c.Iban ?? c.OtherId) is null ? null
        : new Account { Iban = c.Iban, Other = c.OtherId is null ? null : new GenericId(c.OtherId, c.OtherScheme), Currency = c.Currency, Name = c.Name, Type = c.Type };
    static AgentCols AgentOf(Agent? a) => new() { Bicfi = a?.Bicfi, Lei = a?.Lei, ClearingSystem = a?.ClearingSystem, MemberId = a?.MemberId, Name = a?.Name };
    static Agent? ModelOf(AgentCols? c) => c is null || (c.Bicfi ?? c.Lei ?? c.MemberId ?? c.Name) is null ? null
        : new Agent { Bicfi = c.Bicfi, Lei = c.Lei, ClearingSystem = c.ClearingSystem, MemberId = c.MemberId, Name = c.Name };

    static string Supplementary(IReadOnlyDictionary<string, JsonObject> d)
    {
        var o = new JsonObject();
        foreach (var (k, v) in d) o[k] = v.DeepClone();
        return CanonicalJson.Of(o);
    }

    static IReadOnlyDictionary<string, JsonObject> SupplementaryOf(string json) =>
        (JsonNode.Parse(json) as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value!.DeepClone());

    /// <summary>Keeps a message or file as received or sent. Returns its id and whether the same content was seen before.</summary>
    public async Task<(string Id, bool SeenBefore)> KeepMessage(string tenant, string scheme, Direction direction, string kind, string? fileName, byte[] content, string? subjectId, CancellationToken ct = default)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var seen = await db.Messages.AsNoTracking().AnyAsync(m => m.Tenant == tenant && m.Scheme == scheme && m.Kind == kind && m.ContentHash == hash, ct);
        var id = NewId();
        var row = new MessageRow
        {
            Id = id, Tenant = tenant, Scheme = scheme, Direction = direction.ToString(), Kind = kind, FileName = fileName,
            Content = content, ContentHash = hash, SubjectId = subjectId, At = StoreTime.Of(clock.GetUtcNow()),
        };
        db.Messages.Add(row);
        db.Outbox.Add(Outbox.Of(row));
        await db.SaveChangesAsync(ct);
        return (id, seen);
    }
}

public sealed record AcceptedBulk(string BulkId, IReadOnlyList<string> PaymentIds);
