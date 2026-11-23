using System.Text.Json.Nodes;

namespace TransactHub.Ports;

// The bank's party register (p.port.party-register.v1; R-12-27, owner's decisions of 23 November 2026). The register is bank
// customer data the runtime keeps (the operations console's registers, R-05-62): `register` reads it from the runtime's own
// store, as of the step; `simulator` answers from its settings, for tests. A record found is the register's record, unmasked:
// it stays in the native host and never reaches the workflow's state.

/// <summary>What the register holds for an id: the record in force, or none.</summary>
public sealed record RegisteredParty(string Key, JsonObject Record);

public interface IPartyRegister
{
    string Adapter { get; }
    /// <summary>The record in force at the moment whose field holds the id, in the register kind named.</summary>
    Task<RegisteredParty?> Lookup(string tenant, string register, string field, string id, DateTimeOffset at, CancellationToken ct = default);
}

/// <summary>How the runtime reads its own register: given by the host, which holds the store.</summary>
public delegate Task<IReadOnlyList<JsonObject>> RegisterReader(string tenant, string register, string field, string value, DateTimeOffset at, CancellationToken ct);

public static class PartyRegisterAdapters
{
    public const string Port = "p.port.party-register.v1";

    public static IPartyRegister Create(PortBinding b, RegisterReader? reader = null) => b.Adapter switch
    {
        StorePartyRegister.Name => new StorePartyRegister(reader ?? throw new PortBindingException($"{b.Port} for {b.Tenant} is bound to the runtime's register, but this host has no store")),
        SimulatedPartyRegister.Name => new SimulatedPartyRegister(b),
        _ => throw new PortBindingException($"No party register adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };
}

/// <summary>The runtime's own register, read through the host's store (setting <c>key</c>: the record's key field, default corporateId).</summary>
public sealed class StorePartyRegister(RegisterReader reader) : IPartyRegister
{
    public const string Name = "register";
    public string Adapter => Name;

    public async Task<RegisteredParty?> Lookup(string tenant, string register, string field, string id, DateTimeOffset at, CancellationToken ct = default)
    {
        var found = await reader(tenant, register, field, id, at, ct);
        // Two records holding one establishment id is a register to correct, not a party to pick: none is taken.
        return found.Count == 1 ? new RegisteredParty((string?)found[0]["corporateId"] ?? id, found[0]) : null;
    }
}

/// <summary>
/// For tests: setting <c>records</c>, a JSON array of records (synthetic). A record is found by the field asked, and its
/// <c>active</c> flag is the record's own.
/// </summary>
public sealed class SimulatedPartyRegister(PortBinding b) : IPartyRegister
{
    public const string Name = "simulator";
    public string Adapter => Name;
    readonly JsonArray records = JsonNode.Parse(b.Setting("records", "[]")) as JsonArray ?? new JsonArray();

    public Task<RegisteredParty?> Lookup(string tenant, string register, string field, string id, DateTimeOffset at, CancellationToken ct = default)
    {
        var hit = records.OfType<JsonObject>().FirstOrDefault(r => r[field] is JsonArray a ? a.Any(x => (string?)x == id) : (string?)r[field] == id);
        return Task.FromResult(hit is null ? null : new RegisteredParty((string?)hit["corporateId"] ?? id, (JsonObject)hit.DeepClone()));
    }
}
