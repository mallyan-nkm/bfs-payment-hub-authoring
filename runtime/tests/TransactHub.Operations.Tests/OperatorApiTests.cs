using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TransactHub.Model;
using TransactHub.Operations.Api;
using TransactHub.Store;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The console's API (R-05-48, R-05-50, R-06-24): who may call it, what the list filters by, the detail of a payment with
/// several legs, an action through the API; dev sign-in refused in Production and the studio's issuer never trusted.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OperatorApiTests(Databases dbs)
{
    sealed class Host(StoreOptions store, string environment, Dictionary<string, string?>? extra = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment(environment);
            b.UseSetting("Store:Provider", store.Provider.ToString());
            b.UseSetting("Store:Connection", store.ConnectionString);
            b.UseSetting("Operations:Views", Path.Combine(Repo.Root(), "content-configurations", "artefacts"));
            b.UseSetting("Operations:PollMilliseconds", "100");
            foreach (var (k, v) in extra ?? new() { ["TH_AUTH_DEV"] = "1" }) b.UseSetting(k, v);
        }
    }

    static HttpClient As(HttpClient c, string sub, params string[] roles)
    {
        c.DefaultRequestHeaders.Remove(OperatorAuth.DevHeader);
        c.DefaultRequestHeaders.Add(OperatorAuth.DevHeader, new JsonObject { ["sub"] = sub, ["name"] = sub, ["roles"] = new JsonArray(roles.Select(r => (JsonNode?)r).ToArray()) }.ToJsonString());
        return c;
    }

    static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    static async Task<JsonNode> Until(HttpClient c, string url, Func<JsonNode, bool> ok)
    {
        for (var i = 0; ; i++)
        {
            var r = await c.GetAsync(url);
            if (r.IsSuccessStatusCode && await Json(r) is { } j && ok(j)) return j;
            if (i > 200) throw new TimeoutException($"{url}: {await r.Content.ReadAsStringAsync()}");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Filters_the_list_by_what_every_leg_has_and_by_declared_fields_and_shows_a_payment_with_its_legs()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        var held = await WpsRun.Seed(store);
        var nak = await WpsRun.Seed(store, fileId: "0000001234567261117083222", hold: false, ack: "nak");
        var other = await WpsRun.Seed(store, tenant: "other-bank", fileId: "0000001234567261117083333");
        await using var host = new Host(store, "Development");
        var c = As(host.CreateClient(), "op-1", "example-bank:Operator");

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/ops/api/payments")).StatusCode);
        var all = await Until(c, "/ops/api/payments", j => (int)j["total"]! == 2);
        Assert.DoesNotContain(other, all.ToJsonString());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/ops/api/payments?tenant=other-bank")).StatusCode);

        var holds = await Json(await c.GetAsync("/ops/api/payments?hold=open&scheme=uae.wps&direction=outward"));
        Assert.Equal(held, (string?)holds["rows"]![0]!["id"]);
        Assert.Equal(1, (int)holds["total"]!);
        var byLeg = await Json(await c.GetAsync("/ops/api/payments?leg=uae.wps.wf.salary-file-out.v1&legOutcome=nak"));
        Assert.Equal(nak, (string?)byLeg["rows"]![0]!["id"]);
        var byField = await Json(await c.GetAsync("/ops/api/payments?f:uae.wps.view.operations.v1:uae.wps.wf.salary-file-out.v1:cbResponse=ack"));
        Assert.Equal(held, (string?)byField["rows"]![0]!["id"]);
        var byAmount = await Json(await c.GetAsync("/ops/api/payments?f:uae.wps.view.operations.v1:subject:totalAmount:min=1000&minAmount=1500&maxAmount=1500"));
        Assert.Equal(2, (int)byAmount["total"]!);
        var byFile = await Json(await c.GetAsync("/ops/api/payments?q=083222.SIF"));
        Assert.Equal(nak, (string?)byFile["rows"]![0]!["id"]);
        var facets = await Json(await c.GetAsync("/ops/api/facets"));
        Assert.Contains("uae.wps.wf.salary-file-out.v1", facets["legs"]!.AsArray().Select(x => (string?)x));

        var detail = await Json(await c.GetAsync($"/ops/api/payments/{held}"));
        Assert.Single(detail["payment"]!["legs"]!.AsArray());
        Assert.Equal(2, detail["lines"]!.AsArray().Count);
        Assert.Contains(detail["timeline"]!.AsArray(), t => (string?)t!["step"] == "submit.read-ack");
        Assert.Contains(detail["offers"]!.AsArray(), o => (string?)o!["action"] == "approve");
        var doc = (string)detail["documents"]![0]!["id"]!;
        Assert.Equal("SCR,synthetic\n", await c.GetStringAsync($"/ops/api/payments/{held}/documents/{doc}"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/ops/api/payments/{other}")).StatusCode);

        var act = await c.PostAsJsonAsync($"/ops/api/payments/{held}/actions", new { action = "approve", remarks = "ok" });
        Assert.Equal(HttpStatusCode.OK, act.StatusCode);
        await Until(c, $"/ops/api/payments/{held}", j => j["payment"]!["hold"] is null && j["actions"]!.AsArray().Count == 1);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.DeleteAsync($"/ops/api/payments/{held}")).StatusCode);
        var health = await Json(await c.GetAsync("/ops/api/health"));
        Assert.Equal(0, (int)health["lag"]!["behind"]!);
    }

    [Fact]
    public async Task A_stored_file_is_a_document_of_its_payment_with_its_leg_and_signed_hash_and_only_its_operators_download_it()
    {
        // Issue I-97 part 2 (R-09-64): a file a host kept in the store (here the SIF a leg sent) is in the documents tab.
        var (store, _) = await dbs.Pair(StoreProvider.SqlServer);
        var held = await WpsRun.Seed(store);
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        StoredFileRow kept;
        await using (var db = StoreDbContext.Open(store))
            kept = await new FileStore(db, key).Keep(new FileToKeep(WpsRun.Tenant, "uae.wps", "out", "encrypted", "0000001234567261117083000.SIF.gpg", held, "root-leg3", "p.protect.encrypt-file.v1"), [0x85, 0x01, 0x0c]);
        await using var host = new Host(store, "Development");
        var c = As(host.CreateClient(), "op-1", "example-bank:Operator");
        var detail = await Until(c, $"/ops/api/payments/{held}", j => j["documents"]!.AsArray().Any(d => (string?)d!["id"] == kept.Id));
        var doc = detail["documents"]!.AsArray().Single(d => (string?)d!["id"] == kept.Id)!;
        Assert.Equal(("file", "root-leg3", true, kept.SigningKeyId, "out"), ((string?)doc["source"], (string?)doc["leg"], (bool)doc["signed"]!, (string?)doc["signingKeyId"], (string?)doc["direction"]));
        Assert.Equal(3, (int)doc["size"]!);
        Assert.Equal(new byte[] { 0x85, 0x01, 0x0c }, await c.GetByteArrayAsync($"/ops/api/payments/{held}/documents/{kept.Id}"));
        var other = As(host.CreateClient(), "op-2", "other-bank:Operator");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/ops/api/payments/{held}/documents/{kept.Id}")).StatusCode);
    }

    /// <summary>
    /// R-09-180, R-05-95 (Issue I-286): one payment, one id. A salary file the File API's front door started under its own id is pinned and
    /// its file kept under that id before the run accepts it; the store accepts the bulk under the same id, so the console's payment route
    /// resolves the front door's id (the upload page's link), its timeline starts with the pin, and its Documents tab lists the file in,
    /// the SIF out and the ACK in. On SQL Server and PostgreSQL; synthetic data only.
    /// </summary>
    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_payment_the_front_door_started_opens_under_its_id_with_its_files(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        var fd = $"uae-wps-{Guid.NewGuid():N}";
        const string bank = WpsRun.Tenant;
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        await using (var db = StoreDbContext.Open(store))
        {
            var reg = new DistributionRegistry(db);
            var dist = $"2026.11.24-{Random.Shared.Next(100, 99999)}";
            await reg.Store(bank, dist, "synthetic"u8.ToArray(), new JsonObject { ["id"] = dist, ["bank"] = bank }, new string('b', 64), "test", "e2e");
            await reg.Activate(DistributionRegistry.IdOf(bank, dist), "test", "test", "e2e");
            await reg.Pin(fd, bank, "test", "front-door");
            await new FileStore(db, key).Keep(new FileToKeep(bank, "uae.wps", "in", "uae.wps.sif", "SALARY-0001.SIF", fd, null, null), "SCR,synthetic\n"u8.ToArray());
        }
        // The run accepts the file's batch under the front door's id.
        await using (var db = StoreDbContext.Open(store))
        {
            var bulk = new PaymentBulk
            {
                MessageId = "0000001234567261124090001", PaymentInformationId = "0000001234567261124090001", Debtor = new Party { Name = "Example Trading LLC", IsOrganisation = true },
                DebtorAccount = new Account { Iban = "AE070331234567890123999" },
                Transactions = [new CreditTransfer { PaymentId = new PaymentIdentification { EndToEndId = "E2E-FD-1" }, InstructedAmount = new Money(100m, "AED"), Creditor = new Party(), CreditorAccount = new Account { Iban = "AE070331234567890123456" } }],
            };
            var accepted = await new PaymentStore(db).AcceptBulk(bank, "uae.wps", Direction.Outward, bulk, "p.wf.corporate.v1@1.0.0", frontDoorId: fd);
            Assert.Equal(fd, accepted.BulkId);
            await new FileStore(db, key).Keep(new FileToKeep(bank, "uae.wps", "out", "uae.wps.sif-out", "0000001234567261124090001.SIF", fd, "scheme-leg", "uae.wps.exchange.send-salary-file.v2"), "SCR,synthetic\n"u8.ToArray());
            await new FileStore(db, key).Keep(new FileToKeep(bank, "uae.wps", "in", "uae.wps.ack", "0000001234567261124090001ABCDEF012345.ACK", fd, null, null), "ACK,synthetic\n"u8.ToArray());
        }
        await using var host = new Host(store, "Development");
        var c = As(host.CreateClient(), "op-1", $"{bank}:Operator");
        var detail = await Until(c, $"/ops/api/payments/{fd}", j => j["documents"]!.AsArray().Count == 3);
        Assert.Equal(fd, (string?)detail["payment"]!["id"]);
        Assert.Equal(["uae.wps.ack", "uae.wps.sif", "uae.wps.sif-out"], detail["documents"]!.AsArray().Select(d => (string)d!["kind"]!).Order());
        var timeline = detail["timeline"]!.AsArray().Select(t => (string?)t!["kind"]).ToList();
        Assert.Equal(DistributionRegistry.PinKind, timeline[0]);
        Assert.Contains("status", timeline);
    }

    [Fact]
    public void Dev_sign_in_is_refused_in_production()
    {
        var store = dbs.Fresh(StoreProvider.PostgreSql);
        using var host = new Host(store, "Production");
        var e = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        Assert.Contains("TH_AUTH_DEV=1 is refused in Production", e.ToString());
    }


    const string Issuer = "https://idp.bank.example.test/realms/ops";
    const string Audience = "transacthub-ops";
    const string RoleMapJson = """
        { "kind": "runtime-role-map",
          "grants": [ { "claim": "groups", "value": "ops-wps-operators", "roles": ["Operator"], "tenants": ["example-bank"] },
                      { "claim": "groups", "value": "ops-wps-supervisors", "roles": ["Supervisor"], "tenants": ["example-bank"] } ] }
        """;

    /// <summary>A synthetic identity provider: a key pair, its JWKS, and tokens signed with it.</summary>
    sealed class Idp : IDisposable
    {
        readonly System.Security.Cryptography.RSA rsa = System.Security.Cryptography.RSA.Create(2048);
        public string Jwks
        {
            get
            {
                var p = rsa.ExportParameters(false);
                return new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "RSA", ["kid"] = "ops-1", ["use"] = "sig", ["alg"] = "RS256", ["n"] = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(p.Modulus), ["e"] = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(p.Exponent) }) }.ToJsonString();
            }
        }
        public string Token(string sub, string[] groups, string issuer = Issuer) => new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = Audience, Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = sub, ["groups"] = groups },
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = "ops-1" }, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256),
        });
        public void Dispose() => rsa.Dispose();
    }

    [Fact]
    public void The_studios_issuer_is_never_the_operators_identity_provider()
    {
        var store = dbs.Fresh(StoreProvider.PostgreSql);
        using var idp = new Idp();
        using var host = new Host(store, "Production", new() { ["Auth:Oidc:Issuer"] = "https://studio.example.invalid/", ["Auth:Oidc:Audience"] = Audience, ["Auth:Oidc:SigningKeys"] = idp.Jwks, ["Auth:RoleMap"] = RoleMapJson, ["TH_STUDIO_ISSUER"] = "https://studio.example.invalid" });
        var e = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        Assert.Contains("the studio's issuer", e.ToString());
    }

    [Fact]
    public async Task With_the_banks_identity_provider_only_its_valid_tokens_are_taken_and_its_groups_give_the_roles()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        var mine = await WpsRun.Seed(store);
        await WpsRun.Seed(store, tenant: "other-bank", fileId: "0000001234567261117083444");
        using var idp = new Idp();
        await using var host = new Host(store, "Production", new() { ["Auth:Oidc:Issuer"] = Issuer, ["Auth:Oidc:Audience"] = Audience, ["Auth:Oidc:SigningKeys"] = idp.Jwks, ["Auth:RoleMap"] = RoleMapJson, ["Auth:Oidc:ClientId"] = "th-ops-console" });
        var anon = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/ops/api/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(host.CreateClient(), "op-1", "example-bank:Supervisor").GetAsync("/ops/api/payments")).StatusCode);
        var cfg = await Json(await anon.GetAsync("/ops/api/auth/config"));
        Assert.Equal("oidc", (string?)cfg["mode"]);
        Assert.Equal("th-ops-console", (string?)cfg["clientId"]);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/ops/api/health")).StatusCode);

        HttpClient Bearer(string token) { var c = host.CreateClient(); c.DefaultRequestHeaders.Authorization = new("Bearer", token); return c; }
        var op = Bearer(idp.Token("op-1", ["ops-wps-operators"]));
        var list = await Until(op, "/ops/api/payments", j => (int)j["total"]! == 1);
        Assert.Equal(mine, (string?)list["rows"]![0]!["id"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/ops/api/payments?tenant=other-bank")).StatusCode);
        var me = await Json(await op.GetAsync("/ops/api/me"));
        Assert.Equal("op-1", (string?)me["subject"]);
        // An operator may not ask for a rebuild; a supervisor may, and it then waits for a bank approver's consent (R-09-77).
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/ops/api/rebuild", new { remarks = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Bearer(idp.Token("sup-1", ["ops-wps-supervisors"])).PostAsJsonAsync("/ops/api/rebuild", new { remarks = "after an incident (synthetic)" })).StatusCode);
        // A token of another issuer, or one with no group the map knows, gives nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(idp.Token("op-1", ["ops-wps-operators"], issuer: "https://studio.example.invalid")).GetAsync("/ops/api/payments")).StatusCode);
        Assert.Equal(0, (int)(await Json(await Bearer(idp.Token("x-1", ["unrelated"])).GetAsync("/ops/api/payments")))["total"]!);
    }

    [Fact]
    public void Roles_come_from_the_banks_claims_through_the_runtimes_role_map()
    {
        var map = TransactHub.Auth.RoleMap.Parse(RoleMapJson, OperatorRoles.All);
        var id = new System.Security.Claims.ClaimsIdentity([new("sub", "u-1"), new("groups", "ops-wps-operators"), new("groups", "unrelated")], "jwt");
        map.Apply(id);
        var who = new RuntimeAuthOperators().Resolve(new System.Security.Claims.ClaimsPrincipal(id))!;
        Assert.True(who.Has("example-bank", OperatorRoles.Operator));
        Assert.False(who.Has("example-bank", OperatorRoles.Supervisor));
        Assert.False(who.Has("other-bank", OperatorRoles.Operator));
        Assert.Contains("which this host does not know", Assert.Throws<InvalidOperationException>(() => TransactHub.Auth.RoleMap.Parse("""{ "kind": "runtime-role-map", "grants": [ { "claim": "groups", "value": "x", "roles": ["release-importer"] } ] }""", OperatorRoles.All)).Message);
        var all = new OperatorIdentity("s", "s", new Dictionary<string, IReadOnlySet<string>> { [OperatorIdentity.AnyTenant] = new HashSet<string> { OperatorRoles.Supervisor } });
        Assert.True(all.Has("any-bank", OperatorRoles.Operator));
        Assert.Null(new RuntimeAuthOperators().Resolve(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    [Fact]
    public void One_person_is_an_operator_in_one_bank_and_a_supervisor_in_another()
    {
        // The owner's decision of 22 November 2026 (R-06-28): each grant gives its role in its own tenants.
        var map = TransactHub.Auth.RoleMap.Parse("""
            { "kind": "runtime-role-map", "grants": [
              { "claim": "groups", "value": "ops-bank-a", "roles": ["Operator"], "tenants": ["bank-a"] },
              { "claim": "groups", "value": "sup-bank-b", "roles": ["Supervisor"], "tenants": ["bank-b"] } ] }
            """, OperatorRoles.All);
        var id = new System.Security.Claims.ClaimsIdentity([new("sub", "u-2"), new("groups", "ops-bank-a"), new("groups", "sup-bank-b")], "jwt");
        map.Apply(id);
        var who = new RuntimeAuthOperators().Resolve(new System.Security.Claims.ClaimsPrincipal(id))!;
        Assert.True(who.Has("bank-a", OperatorRoles.Operator));
        Assert.False(who.Has("bank-a", OperatorRoles.Supervisor));
        Assert.True(who.Has("bank-b", OperatorRoles.Supervisor));
        Assert.True(who.Has("bank-b", OperatorRoles.Operator));
        Assert.Equal(["bank-a", "bank-b"], who.Tenants.Order());
    }
}
