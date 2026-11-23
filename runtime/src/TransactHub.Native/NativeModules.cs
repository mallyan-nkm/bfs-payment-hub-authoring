using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Store;

namespace TransactHub.Native;

/// <summary>A native attempt the host does not run: its error's name and message, as the protocol answers it.</summary>
public sealed class NativeRefusal(string name, string message) : Exception(message)
{
    public string Name { get; } = name;
}

/// <summary>
/// The activities one distribution runs on this host (spec R-09-122, R-09-126): its native modules' (the platform's, a scheme's, a
/// bank's), loaded from the distribution's own assemblies in a collectible context of its own, and the builtin services and
/// production adapters the runtime image answers itself. A payment
/// pinned to the distribution is served from here only, by activity and release: an activity at a release the distribution does
/// not carry is refused.
/// </summary>
public sealed class ActivityTable
{
    public required string Key { get; init; }
    public required IReadOnlyDictionary<string, INativeActivity> ByName { get; init; }
    /// <summary>The <c>name@release</c> of every activity the distribution carries; null in the development table (any release).</summary>
    public required IReadOnlySet<string>? Releases { get; init; }
    public required IReadOnlyList<JsonObject> Modules { get; init; }
    public readonly ConcurrentDictionary<string, int> Served = new(StringComparer.Ordinal);
    internal ModuleContext? Context { get; init; }
    public DateTimeOffset LoadedAt { get; } = DateTimeOffset.UtcNow;

    public INativeActivity Find(string name, string? release)
    {
        if (Releases is not null && !Releases.Contains($"{name}@{release}"))
            throw new NativeRefusal("native.NotInDistribution", $"{Key} carries no {name}@{release}: a payment runs only the activities of the distribution it is pinned to");
        return ByName.TryGetValue(name, out var a) ? a : throw new NativeRefusal("native.NotNative", $"The native host serves no {name}{(Releases is null ? "" : $" in {Key}")}");
    }
}

/// <summary>
/// One distribution's load context, collectible (spec R-09-122): it loads the assemblies the distribution carries, from their
/// checked bytes, and nothing else. Every other assembly, the native contract and the framework among them, is the runtime image's,
/// from the default context, so a module and the host share the contract's types.
/// </summary>
sealed class ModuleContext(string name, IReadOnlyDictionary<string, byte[]> carried) : AssemblyLoadContext(name, isCollectible: true)
{
    // What the distribution carries is loaded here, from its own bytes, even where an assembly of the same name is loaded elsewhere:
    // each distribution runs its own build. The contract and the image's runtime software are never carried (NativeAssemblies.Shared), so they,
    // and every other assembly, resolve in the default context.
    protected override Assembly? Load(AssemblyName n) =>
        n.Name is { } name && !NativeAssemblies.Shared.Contains(name) && carried.TryGetValue(name, out var bytes) ? LoadFromStream(new MemoryStream(bytes)) : null;
}

/// <summary>
/// The native host's distributions, side by side (spec R-09-122): one table per distribution, loaded from the store the first time
/// a payment pinned to it asks, its native assemblies checked again against the signed manifest; a distribution removed in every
/// environment it was known in is dropped and its context unloaded. Attempts that name no distribution (the studio's
/// interpreter, a development run) are served by the development table: what the image answers and the modules
/// <c>TH_NATIVE_DEV_MODULES</c> names (the platform's among them, for a platform native activity).
/// </summary>
public sealed class NativeModules : IDisposable
{
    public const string Development = "development";
    readonly Func<IReadOnlyDictionary<string, object>, IEnumerable<INativeActivity>> platform;
    readonly Func<string?, Func<IReadOnlyDictionary<string, object>>, INativeServices> services;
    readonly StoreOptions? store;
    readonly ConcurrentDictionary<string, Lazy<Task<ActivityTable>>> tables = new(StringComparer.Ordinal);
    /// <summary>Contexts unloaded: the distribution and a weak reference, collected once nothing holds the context (R-09-122).</summary>
    readonly ConcurrentDictionary<string, (WeakReference Context, DateTimeOffset At)> unloaded = new(StringComparer.Ordinal);
    readonly Timer? refresh;

    /// <param name="platform">The image's platform activities, made with what the modules contributed.</param>
    /// <param name="services">What a module of a distribution (null: development) is given; the second argument reads its contributions.</param>
    public NativeModules(Func<IReadOnlyDictionary<string, object>, IEnumerable<INativeActivity>> platform, Func<string?, Func<IReadOnlyDictionary<string, object>>, INativeServices> services,
        StoreOptions? store, IEnumerable<string> developmentModules, TimeSpan? refreshEvery = null)
    {
        this.platform = platform;
        this.services = services;
        this.store = store;
        var dev = developmentModules.SelectMany(ReadFolder).ToList();
        tables[Development] = new Lazy<Task<ActivityTable>>(() => Task.FromResult(Build(Development, dev, null)));
        if (store is not null) refresh = new Timer(_ => { try { Refresh().GetAwaiter().GetResult(); } catch { /* the next tick tries again */ } }, null, refreshEvery ?? TimeSpan.FromSeconds(10), refreshEvery ?? TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The modules of a build folder: each sub-folder (or the folder itself) with a <c>native-module.json</c> (scope, assembly,
    /// files), as the studio's module build and <c>runtime/tools/native-module/build.sh</c> write it.
    /// </summary>
    public static IEnumerable<NativeModulePackage> ReadFolder(string dir)
    {
        var found = File.Exists(Path.Combine(dir, "native-module.json")) ? [dir] : Directory.Exists(dir) ? Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "native-module.json"))).ToArray() : [];
        foreach (var d in found)
        {
            var m = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(d, "native-module.json")))!;
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in m["files"] as JsonArray ?? [])
            {
                var name = (string?)f?["name"] ?? "";
                files[name[..^4]] = File.ReadAllBytes(Path.Combine(d, name));
            }
            yield return new NativeModulePackage((string)m["scope"]!, (string)m["assembly"]!, files, [], files.ToDictionary(kv => kv.Key, kv => "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(kv.Value))));
        }
    }

    /// <summary>The table of a distribution (loaded once), or the development table for an attempt that names none.</summary>
    public async Task<ActivityTable> For(string? distribution, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(distribution)) return await tables[Development].Value;
        if (unloaded.ContainsKey(distribution))
        {
            // Installed again after its removal (a rollback past a removal): loaded afresh from the store.
            await using (var db = StoreDbContext.Open(store!))
                if (!Removed(await new DistributionRegistry(db).StatesOf(distribution, ct))) unloaded.TryRemove(distribution, out _);
            if (unloaded.ContainsKey(distribution)) throw new NativeRefusal("native.DistributionRemoved", $"{distribution} was removed: its native assemblies are unloaded");
        }
        // Loaded once for every attempt that asks, whichever asked first: not cancelled with that one attempt.
        var lazy = tables.GetOrAdd(distribution, d => new Lazy<Task<ActivityTable>>(() => Load(d, CancellationToken.None)));
        try { return await lazy.Value; }
        catch
        {
            tables.TryRemove(new KeyValuePair<string, Lazy<Task<ActivityTable>>>(distribution, lazy));
            throw;
        }
    }

    async Task<ActivityTable> Load(string distribution, CancellationToken ct)
    {
        if (store is null) throw new NativeRefusal("native.DistributionUnknown", $"The native host has no store (TH_STORE_PROVIDER, TH_STORE_CONNECTION): it cannot load {distribution}");
        (string bank, string id) parts;
        try { parts = DistributionRegistry.Split(distribution); }
        catch (DistributionRefused e) { throw new NativeRefusal("native.DistributionUnknown", e.Message); }
        await using var db = StoreDbContext.Open(store);
        var row = await new ReleaseRegistry(db).Get(DistributionRegistry.Scope(parts.bank), parts.id, ct)
            ?? throw new NativeRefusal("native.DistributionUnknown", $"{distribution} is not installed in this runtime's store");
        var states = await new DistributionRegistry(db).StatesOf(distribution, ct);
        if (Removed(states)) throw new NativeRefusal("native.DistributionRemoved", $"{distribution} was removed");
        var manifest = JsonNode.Parse(row.ManifestJson) as JsonObject ?? new JsonObject();
        var files = NativeAssemblies.Unzip(row.Package);
        IReadOnlyList<NativeModulePackage> modules;
        // Checked again as it is loaded (R-09-121): the bytes are the ones the signed bill of materials names.
        try { modules = NativeAssemblies.Check(manifest, p => files.TryGetValue(p, out var c) ? c : null, files.Keys); }
        catch (NativeAssembliesRefused e) { throw new NativeRefusal(e.Code ?? "native.ModuleRefused", $"{distribution}: {e.Message}"); }
        var releases = (manifest["billOfMaterials"]?["artefacts"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(a => (string?)a["kind"] == "activity").Select(a => $"{(string?)a["name"]}@{(string?)a["release"]}").ToHashSet(StringComparer.Ordinal);
        return Build(distribution, modules, releases);
    }

    ActivityTable Build(string key, IReadOnlyList<NativeModulePackage> modules, IReadOnlySet<string>? releases)
    {
        var carried = modules.SelectMany(m => m.Files).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        ModuleContext? ctx = modules.Count == 0 ? null : new ModuleContext($"th-native:{key}", carried);
        var contributions = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        var svc = services(key == Development ? null : key, () => contributions);
        var byName = new Dictionary<string, INativeActivity>(StringComparer.Ordinal);
        var described = new List<JsonObject>();
        try
        {
            foreach (var m in modules)
            {
                var asm = ctx!.LoadFromAssemblyName(new AssemblyName(m.Assembly));
                var type = asm.GetCustomAttribute<NativeModuleAttribute>()?.Module
                    ?? throw new NativeRefusal("native.ModuleRefused", $"{m.Assembly}.dll of {key} names no native module ([assembly: NativeModule(...)])");
                var module = (INativeModule)Activator.CreateInstance(type)!;
                if (module.Scope != m.Scope) throw new NativeRefusal("native.ModuleRefused", $"{m.Assembly}.dll of {key} implements {module.Scope}, not {m.Scope}");
                var serves = module.Activities(new Scoped(svc, contributions)).ToList();
                foreach (var a in serves) byName[a.Name] = a;
                // Every native activity the distribution lists for the module must be served by it.
                var missing = m.Activities.Select(x => x.Split('@')[0]).Where(n => !serves.Any(a => a.Name == n)).ToList();
                if (missing.Count > 0) throw new NativeRefusal("native.ModuleRefused", $"{m.Assembly}.dll of {key} does not serve {string.Join(", ", missing)}");
                described.Add(new JsonObject
                {
                    ["scope"] = m.Scope, ["assembly"] = m.Assembly, ["sha256"] = m.Sha256.TryGetValue(m.Assembly, out var s) ? s : null,
                    ["version"] = asm.GetName().Version?.ToString(), ["activities"] = new JsonArray(serves.Select(a => a.Name).Order(StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray()),
                });
            }
            // The image's platform activities, made with what this distribution's modules gave them.
            foreach (var a in platform(contributions)) byName.TryAdd(a.Name, a);
        }
        catch
        {
            ctx?.Unload();
            throw;
        }
        return new ActivityTable { Key = key, ByName = byName, Releases = releases, Modules = described, Context = ctx };
    }

    /// <summary>A module's view of the host's services: its contributions go to its distribution's table.</summary>
    sealed class Scoped(INativeServices inner, ConcurrentDictionary<string, object> contributions) : INativeServices
    {
        public string? Distribution => inner.Distribution;
        public void KeepRendered(string sha256, string fileName, byte[] content) => inner.KeepRendered(sha256, fileName, content);
        public T? Service<T>() where T : class => inner.Service<T>();
        public void Contribute(string key, object value) => contributions[key] = value;
        public object? Contribution(string key) => contributions.TryGetValue(key, out var v) ? v : null;
        public INativeCoreBanking CoreBanking => inner.CoreBanking;
        public INativePorts Ports => inner.Ports;
        public INativeFiles Files => inner.Files;
    }

    /// <summary>
    /// Drops the table of every loaded distribution that is removed in every environment it is known in, and unloads its context
    /// (R-09-122). Returns the distributions it unloaded.
    /// </summary>
    public async Task<IReadOnlyList<string>> Refresh(CancellationToken ct = default)
    {
        var done = new List<string>();
        if (store is null) return done;
        foreach (var key in tables.Keys.Where(k => k != Development).ToList())
        {
            await using var db = StoreDbContext.Open(store);
            var states = await new DistributionRegistry(db).StatesOf(key, ct);
            if (!Removed(states)) continue;
            if (!tables.TryRemove(key, out var lazy)) continue;
            unloaded[key] = (Unload(lazy), DateTimeOffset.UtcNow);
            done.Add(key);
        }
        return done;
    }

    /// <summary>Removed in every environment it has a state in (an installation again after removal makes it installed there).</summary>
    static bool Removed(Dictionary<string, DistributionRegistry.StateFact> states)
    {
        var envs = states.Where(kv => kv.Key != DistributionRegistry.AnyEnvironment).Select(kv => kv.Value).ToList();
        return envs.Count > 0 && envs.All(s => s.State == "removed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference Unload(Lazy<Task<ActivityTable>> lazy)
    {
        if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully || lazy.Value.Result.Context is not { } ctx) return new WeakReference(null);
        var weak = new WeakReference(ctx, trackResurrection: true);
        ctx.Unload();
        return weak;
    }

    /// <summary>Whether a removed distribution's context has been collected; collects first, as an operator's check would.</summary>
    public bool Collected(string distribution)
    {
        if (!unloaded.TryGetValue(distribution, out var u)) return false;
        for (var i = 0; i < 10 && u.Context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return !u.Context.IsAlive;
    }

    /// <summary>What the host has loaded and unloaded, for <c>GET /modules</c>.</summary>
    public JsonObject Describe()
    {
        var loaded = new JsonArray();
        foreach (var (key, lazy) in tables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully) continue;
            var t = lazy.Value.Result;
            loaded.Add(new JsonObject
            {
                ["distribution"] = key, ["context"] = t.Context?.Name, ["collectible"] = t.Context?.IsCollectible ?? false, ["loadedAt"] = t.LoadedAt.ToString("O"),
                ["modules"] = new JsonArray(t.Modules.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["served"] = new JsonObject(t.Served.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
            });
        }
        return new JsonObject
        {
            ["contract"] = NativeContract.Version, ["loaded"] = loaded,
            ["unloaded"] = new JsonArray(unloaded.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (JsonNode)new JsonObject { ["distribution"] = kv.Key, ["at"] = kv.Value.At.ToString("O"), ["collected"] = Collected(kv.Key) }).ToArray()),
        };
    }

    public void Dispose() => refresh?.Dispose();
}
