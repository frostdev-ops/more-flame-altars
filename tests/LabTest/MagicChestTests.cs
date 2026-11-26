// SPDX-License-Identifier: MIT
// Magic chests as base storage with mergeBases' hooks in (Wittie2m's report for 0.4.2: with beyondSixteen and mergeBases
// on and fewer than 16 altars, a base's Magic chests behaved like ordinary chests: the sorter found no receiving chest and
// stations could not take from them). On a lab server with no player: the game's own filing of crafting-stock containers
// into bases, called on a made-up base system, and altars, Magic chests and charcoal kilns placed in the world, then the
// game's base-stock list, its deposit filter and the game's own fill from the base's chests. A state file next to the
// server (kiln/data/mfa-labtest-chests.json) keeps what earlier starts placed, so a later start checks those chests too
// (existing chests after a restart); kiln/data/mfa-labtest-chests-place.txt says how many new altars a run places (3 if
// it is missing, 0 to only check). Every observation is logged as "magicChests:" lines. Server build 26430253.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace MoreFlameAltars.LabTest;

public sealed class MagicChestTests
{
    private const string ModId = "more-flame-altars";
    private const string Altar = "Home_Base_FlameAltar", MagicChest = "Prop_Decoration_T1_Storage_24_Magic", KilnTemplate = "T1_Charcoal_Kiln_Factory";
    private static readonly BuildOptions InBases = new() { Flags = BuildFlags.InBases };
    private static readonly BuildOptions Altars = new() { Flags = BuildFlags.AllowAltars | BuildFlags.InBases };

    // AddToBaseStock (server 0x209b20, client 0x471220, the same bytes): (u8* result, PlayerBasesSystem*, entry*, bool water).
    // entry: u32 entity, then x, y, z as i64 32.32 fixed metres; result: 0 filed, 0x1f in no base, 0x27 the list is full.
    // RebuildBaseStock calls it every server frame for every entity with InventoryCraftingStock (the Magic chests). It asks
    // BasesContains(system, base, point) base after base and keeps its loop index in r11 and the point in xmm4/xmm5 across
    // that call: the game's compiler knows BasesContains never writes them.
    private const string AddToBaseStock = "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 56 48 81 EC 80 00 00 00 41 8B 18 45 33 DB 45 0F B6 F1";
    // PlayerBasesSystem: base count at +0x24, base records at +0x30 (0x130 bytes each): the crafting-stock list (ptr, count,
    // capacity, grow, allocator) at +0, the box (min xyz, pad, max xyz, pad) at +0xf0, the zone mask at +0x110.
    private const int BaseCount = 0x24, Records = 0x30, RecordSize = 0x130, RecordBox = 0xf0, RecordMask = 0x110, SystemSize = 0x2070;

    private static readonly string StatePath = Path.GetFullPath(Path.Combine("kiln", "data", "mfa-labtest-chests.json"));
    private static readonly string PlacePath = Path.GetFullPath(Path.Combine("kiln", "data", "mfa-labtest-chests-place.txt"));

    private static void Say(string line) => LabTestMod.Logger?.Info("magicChests: " + line);
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string P(WorldPosition p) => $"({F(p.X)}, {F(p.Y)}, {F(p.Z)})";

    private static string Settings()
    {
        var cfg = Assert.NotNull(Config.Find(ModId), "More Flame Altars registered its config");
        string V(string k) => Convert.ToString(cfg.Get(k), CultureInfo.InvariantCulture) ?? "?";
        var hook = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MoreFlameAltars.BaseMergeHook")).First(t => t is not null)!;
        bool B(string name) => (bool)hook.GetProperty(name)!.GetValue(null)!;
        return $"altars {V("altars")}, extraAltars {V("extraAltars")}, beyondSixteen {V("beyondSixteen")}, mergeBases {V("mergeBases")}, " +
               $"buildArea {V("buildArea")}; mergeBases' hooks installed {B("Installed")}, on {B("Enabled")}, merged now {hook.GetProperty("MergedNow")!.GetValue(null)}";
    }

    private static long Fixed(double metres) => (long)(metres * 4294967296.0);

    /// <summary>The game's AddToBaseStock on a made-up base system of four separate bases: each entry's point lies in one
    /// base's box, and the game must file each entry under that base. With BasesContains hooked so that r11 or xmm4/xmm5
    /// change across the call, only the first base gets its entry.</summary>
    [KilnTest(Order = 20)]
    public unsafe void Chests_the_games_stock_filing_puts_each_chest_in_its_own_base()
    {
        Say("settings: " + Settings());
        var at = Symbols.Find(AddToBaseStock);
        Assert.True(at != 0, "the game's AddToBaseStock found by its pattern");
        var add = (delegate* unmanaged<byte*, nuint, long*, byte, byte*>)at;
        const int bases = 4, room = 8;
        static float X(int b) => 100000 + b * 1000;
        var pb = (byte*)NativeMemory.AllocZeroed(SystemSize);
        var lists = (uint*)NativeMemory.AllocZeroed(bases * room * sizeof(uint));
        try
        {
            *(uint*)(pb + BaseCount) = bases;
            for (var b = 0; b < bases; b++)
            {
                var rec = pb + Records + b * RecordSize;
                *(uint**)rec = lists + b * room;
                *(ulong*)(rec + 8) = 0;
                *(ulong*)(rec + 0x10) = room;
                var box = (float*)(rec + RecordBox);
                box[0] = X(b); box[1] = 0; box[2] = 0; box[4] = X(b) + 50; box[5] = 50; box[6] = 50;
                *(uint*)(rec + RecordMask) = 1u << b;   // one zone: the game's test is the box alone
            }
            var entry = stackalloc long[4];
            var result = stackalloc byte[16];
            var answers = new List<string>();
            for (var b = 0; b < bases; b++)
            {
                *(uint*)entry = 1000u + (uint)b;
                entry[1] = Fixed(X(b) + 25); entry[2] = Fixed(25); entry[3] = Fixed(25);
                result[0] = 0xff;
                add(result, (nuint)pb, entry, 0);
                answers.Add($"entity {1000 + b} (in base {b}'s box): result 0x{result[0]:x2}");
            }
            var filed = new List<string>();
            var wrong = 0;
            for (var b = 0; b < bases; b++)
            {
                var rec = pb + Records + b * RecordSize;
                var n = (int)*(ulong*)(rec + 8);
                var ids = Enumerable.Range(0, Math.Min(n, room)).Select(i => lists[b * room + i]).ToList();
                filed.Add($"base {b}: [{string.Join(", ", ids)}]");
                if (n != 1 || ids[0] != 1000u + (uint)b) wrong++;
            }
            Say($"the game's AddToBaseStock on a made-up system of {bases} bases: {string.Join("; ", answers)}; lists {string.Join("; ", filed)}");
            Assert.True(wrong == 0, $"every base's list holds exactly its own entry ({wrong} of {bases} wrong: {string.Join("; ", answers)}; {string.Join("; ", filed)})");
        }
        finally
        {
            NativeMemory.Free(lists);
            NativeMemory.Free(pb);
        }
    }

    // ---- the player's main altar need not be base 0 (Wittie2m: the broken chests were "all within range of the basic flame
    // altar area", his first altar). The game's rebuild makes base 0 of the first zone in its zone list and numbers the
    // other bases by that list (server 0x206510); the list follows the game's own order, not the order the altars were
    // placed in, and the numbers change when altars come and go (seen on 0.4.2: the main altar, base #0 when it was
    // placed, was another number once three more altars stood). Here (Order 19): a main altar with a Magic chest and a
    // kiln, then a second altar with its own; then the main altar is taken away and put back where it stood (the game's
    // build-zone list then has it second). Order 23, after the grid test placed more altars, checks both chests again
    // and says which base the main altar is now. kiln/data/mfa-labtest-main.json keeps the layout for later starts.
    private static readonly string MainPath = Path.GetFullPath(Path.Combine("kiln", "data", "mfa-labtest-main.json"));

    private static WorldPosition MainLayout(int k) => new(9000 + k * 200, 1300, 9000);

    private static string Zones()
    {
        var zones = Build.Bases;
        return string.Join("; ", zones.Select((z, i) => $"zone {i}: altar {z.Altar.Value} at {P(z.Center)}"));
    }

    [KilnTest(Order = 19, TimeoutSeconds = 600)]
    public async Task Chests_at_the_main_altar_when_another_altar_is_base_0(TestContext t)
    {
        Say("main altar: settings: " + Settings());
        Assert.True(await Wait(t, () => Stations.Available, 60), "kiln.stations reads here");
        var placed = new Dictionary<int, (EntityId Chest, EntityId Kiln)>();
        var fresh = !File.Exists(MainPath);
        if (fresh)
        {
            for (var k = 0; k < 2; k++)
            {
                var a = MainLayout(k);
                Assert.False(Build.PlaceProp(Altar, a, null, 0, Altars).IsNone, $"altar {k} placed at {P(a)}");
                var chest = Build.PlaceProp(MagicChest, new WorldPosition(a.X + 6, a.Y, a.Z + 6), null, 0, InBases);
                var kiln = Build.PlaceProp(KilnTemplate, new WorldPosition(a.X + 6, a.Y, a.Z + 10), null, 0, InBases);
                Assert.False(chest.IsNone || kiln.IsNone, $"the Magic chest and the kiln of altar {k} placed");
                placed[k] = (chest, kiln);
                Assert.True(await Wait(t, () => Bases.At(a) is not null, 90), $"altar {k}'s zone is a base");
                await t.Ticks(5);
            }
            Say($"main altar: placed first, the second after it: {Zones()}");
            var main = Bases.At(MainLayout(0))!.Altars.Select(x => x.Entity).FirstOrDefault(e => Near(World.Position(e), MainLayout(0)));
            Assert.False(main.IsNone, "the main altar's entity found through its base");
            Assert.True(Build.RemoveProp(main, Altars), "the main altar taken away");
            Assert.True(await Wait(t, () => Bases.At(MainLayout(0)) is null, 90), "its zone left the bases");
            await t.Ticks(5);
            Assert.False(Build.PlaceProp(Altar, MainLayout(0), null, 0, Altars).IsNone, "the main altar put back where it stood");
            Assert.True(await Wait(t, () => Bases.At(MainLayout(0)) is not null, 90), "its zone is a base again");
            await t.Ticks(30);
            Store(MainPath, new JsonObject { ["placed"] = DateTime.UtcNow.ToString("O") });
        }
        else
        {
            await Wait(t, () => Bases.At(MainLayout(0)) is not null && Bases.At(MainLayout(1)) is not null, 90);
            await t.Ticks(30);
        }
        await Wait(t, () => Stations.Scanned, fresh ? 0 : 60);
        var mainBase = Bases.At(MainLayout(0));
        var secondBase = Bases.At(MainLayout(1));
        Say($"main altar ({(fresh ? "just put back" : "a later start")}): {Zones()}; the main altar's base #{mainBase?.Index}, the second altar's #{secondBase?.Index}; " +
            $"bases: {string.Join("; ", Bases.All.Select(b => $"#{b.Index} altar(s) {string.Join(",", b.Altars.Select(x => P(World.Position(x.Entity) ?? default)))}"))}");
        Assert.NotNull(mainBase, "the main altar is a base");
        await CheckMainChests(t, fresh ? "placed now" : "placed in an earlier start", placed);
    }

    /// <summary>The main and the second altar's chests: both base storage, each filling its kiln, whichever base number the
    /// game gives them now.</summary>
    private static async Task CheckMainChests(TestContext t, string when, Dictionary<int, (EntityId Chest, EntityId Kiln)> placed)
    {
        var stations = World.Find("FactoryStation");
        var bad = new List<string>();
        for (var k = 0; k < 2; k++)
        {
            var a = MainLayout(k);
            var (inBase, good, line) = await CheckChest(t, k == 0 ? "the main altar's chest" : "the second altar's chest",
                new WorldPosition(a.X + 6, a.Y, a.Z + 6), new WorldPosition(a.X + 6, a.Y, a.Z + 10), when,
                placed.TryGetValue(k, out var p) ? p : null, stations);
            Say("main altar: " + line);
            if (!inBase || !good) bad.Add(line);
        }
        Assert.True(bad.Count == 0, $"both chests are base storage and fill their kilns ({string.Join(" | ", bad)})");
    }

    [KilnTest(Order = 23, TimeoutSeconds = 600)]
    public async Task Chests_at_the_main_altar_after_more_altars_came(TestContext t)
    {
        Say("main altar later: settings: " + Settings());
        Assert.True(File.Exists(MainPath), "the main altar's layout was placed (Order 19 ran in this world)");
        await Wait(t, () => Bases.At(MainLayout(0)) is not null, 90);
        await t.Ticks(30);
        var main = Bases.At(MainLayout(0));
        Assert.NotNull(main, "the main altar is a base");
        Say($"main altar later: {Bases.All.Count} base(s); the main altar (placed first in this world) is base #{main!.Index}" +
            (main.Index == 0 ? " (base 0 this time: nothing shown about other numbers)" : ", not base 0") +
            $", the second #{Bases.At(MainLayout(1))?.Index}; {Zones()}");
        await CheckMainChests(t, "after more altars came", []);
    }

    // ---- the game's other caller of BasesContains (server 0x208940, client 0x470150): after a rebuild of the bases it
    // files the ids of the world's entities of one kind under the base that holds them, in a set per base record (at
    // +0x50: occupancy bits +0x58, room +0x60, ids +0x78, count +0x88), keeping the id in r11 across the call. A detour on
    // BasesContains changed r11, so the sets held something else than the ids, in base 0 too. Here: every id in every
    // base's set must be a live entity of the world, standing in that base.
    [KilnTest(Order = 22)]
    public unsafe void Chests_every_bases_entity_set_holds_live_entities_of_that_base()
    {
        Say("entity sets: settings: " + Settings());
        var hook = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MoreFlameAltars.BaseMergeHook")).First(t => t is not null)!;
        if (!(bool)hook.GetProperty("Installed")!.GetValue(null)!) Assert.Skip("mergeBases' hooks are not in this start (mergeBases off): BasesContains is the game's own");
        var system = (nuint)hook.GetField("LastSystem", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.True(system != 0, "the mod saw the game rebuild its bases (its rebuild hook is in)");
        var count = *(uint*)(system + BaseCount);
        Assert.True(count is > 0 and <= 16, $"the base system has 1 to 16 bases ({count})");
        var lines = new List<string>();
        int ids = 0, dead = 0, elsewhere = 0;
        string Kind(EntityId e) =>
            string.Join("+", new[] { "BaseIdComponent", "InventoryCraftingStock", "FactoryStation" }.Where(c => World.Has(e, c)).DefaultIfEmpty("other"));
        for (var b = 0; b < count; b++)
        {
            var rec = (byte*)system + Records + b * RecordSize;
            var room = *(ulong*)(rec + 0x60);
            var bits = *(ulong**)(rec + 0x58);
            var keys = *(uint**)(rec + 0x78);
            var n = *(uint*)(rec + 0x88);
            Assert.True(room <= 1u << 20 && (room == 0 || bits != null && keys != null), $"base {b}'s set looks like one (room {room})");
            var kinds = new Dictionary<string, int>();
            var odd = new List<string>();
            for (ulong i = 0; i < room; i++)
            {
                if ((bits[i >> 6] >> (int)(i & 63) & 1) == 0) continue;
                ids++;
                var e = new EntityId(keys[i]);
                var at = World.Position(e);
                if (at is not { } p) { dead++; odd.Add($"{keys[i]} (no entity)"); continue; }
                var inBase = Bases.At(p) is { } there && there.Index == b;
                if (!inBase) { elsewhere++; odd.Add($"{keys[i]} at {P(p)} (base #{Bases.At(p)?.Index})"); }
                var k = Kind(e);
                kinds[k] = kinds.GetValueOrDefault(k) + 1;
            }
            lines.Add($"base #{b}: count {n}, room {room}, kinds {string.Join(", ", kinds.Select(x => $"{x.Key} {x.Value}"))}" +
                      (odd.Count > 0 ? $", not live or not in this base: {string.Join(", ", odd.Take(8))}" : ""));
        }
        Say($"entity sets: {count} base(s), {ids} id(s), {dead} not a live entity, {elsewhere} not in their base: {string.Join(" | ", lines)}");
        Assert.True(dead == 0 && elsewhere == 0, $"every id in every base's set is a live entity in that base ({dead} not live, {elsewhere} elsewhere: {string.Join(" | ", lines)})");
    }

    private static JsonObject Load() =>
        File.Exists(StatePath) ? JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject() : new JsonObject { ["chests"] = new JsonArray() };

    private static void Store(JsonObject state) => Store(StatePath, state);

    private static void Store(string path, JsonObject state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static WorldPosition Pos(JsonNode n, string key) =>
        new(n[key]!["x"]!.GetValue<double>(), n[key]!["y"]!.GetValue<double>(), n[key]!["z"]!.GetValue<double>());

    private static JsonObject Json(WorldPosition p) => new() { ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z };

    private static bool Near(WorldPosition? a, WorldPosition b, double d = 1.5) =>
        a is { } p && Math.Abs(p.X - b.X) < d && Math.Abs(p.Y - b.Y) < d && Math.Abs(p.Z - b.Z) < d;

    /// <summary>Waits up to <paramref name="seconds"/> for <paramref name="cond"/>, without failing: false when it never held.</summary>
    private static async Task<bool> Wait(TestContext t, Func<bool> cond, double seconds)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed.TotalSeconds > seconds) return false;
            await t.Ticks(5);
        }
        return true;
    }

    /// <summary>One Magic chest with a charcoal kiln beside it: the game's position lookup puts it in a base, that base's
    /// crafting-stock list (the one the sorter and the stations read) holds it, it takes deposits, and the game's fill
    /// moves a craft's inputs from it into the kiln. (in a base, all of that, the line to log)</summary>
    private static async Task<(bool InBase, bool Good, string Line)> CheckChest(TestContext t, string name, WorldPosition c,
        WorldPosition kilnAt, string when, (EntityId Chest, EntityId Kiln)? placedNow, IReadOnlyList<EntityId> stations)
    {
        var b = Bases.At(c);
        if (b is null) return (false, false, $"{name} at {P(c)} ({when}): in no base (its altar's zone is not one of the game's 16)");
        var storage = Bases.Storage(b.Id);
        var deposit = Bases.Storage(b.Id, deposit: true);
        var chest = placedNow?.Chest ?? storage.FirstOrDefault(e => Near(World.Position(e), c));
        var inStorage = storage.Any(e => Near(World.Position(e), c));
        var inDeposit = deposit.Any(e => Near(World.Position(e), c));
        // the game's own fill from the base's chests, into the kiln beside it: one craft more
        var kiln = placedNow?.Kiln ?? stations.FirstOrDefault(e => Near(World.Position(e), kilnAt));
        var fill = chest.IsNone ? "no fill tried (the chest is not in the base's storage)" : "no kiln";
        var filled = false;
        if (!kiln.IsNone && !chest.IsNone && Stations.Read(kiln) is { } before)
        {
            var recipe = Stations.RecipesOf(kiln).Select(Stations.Recipe).FirstOrDefault(r => r is not null && r.Outputs.Count > 0);
            if (recipe is not null)
            {
                var selected = "";
                if (before.SelectedRecipe != recipe.Id)
                {
                    var stop = await Stations.Stop(kiln);
                    var select = await Stations.Select(kiln, recipe.Id, keepStopped: true, allowLocked: true);
                    selected = $"stop {stop.Status}, select {select.Status}, ";
                }
                foreach (var input in Stations.ResolveInputs(recipe.Id)) await Items.Give(chest, input.Item, input.Count);
                await t.Ticks(3);
                var loaded = Stations.Read(kiln)?.CraftsLoaded ?? -1;
                var r = await Stations.Fill(kiln, new FillOptions { Crafts = 1, Mode = FillMode.Add });
                await t.Ticks(8);
                var after = Stations.Read(kiln)?.CraftsLoaded ?? -1;
                filled = r.Status == StationActionStatus.Done && after >= loaded + 1;
                fill = $"{selected}fill {r.Status}, crafts loaded {loaded} -> {after}";
            }
            else fill = "the kiln has no recipe";
        }
        var line = $"{name} at {P(c)} ({when}): base #{b.Index} ({b.ZoneCount} zone(s), {storage.Count} in its storage), " +
                   $"in its storage {inStorage}, takes deposits {inDeposit}, {fill}";
        return (true, inStorage && inDeposit && filled, line);
    }

    /// <summary>Altar k of this world's test layout: a grid in open sky, 200 m apart (separate bases at every build area).</summary>
    private static WorldPosition AltarAt(int k) => new(5000 + (k % 6) * 200, 1300, 5000 + (k / 6) * 200);

    /// <summary>Places new altars (each with a Magic chest and a charcoal kiln beside it), then checks every chest this
    /// world's runs placed: the game's position lookup puts it in a base, that base's crafting-stock list (the one the
    /// sorter and the stations read) holds it, it takes deposits, and the game's fill moves a craft's inputs from it
    /// into the kiln beside it. A chest whose altar zone the game dropped (past 16 with mergeBases off) is in no base and
    /// is only reported.</summary>
    [KilnTest(Order = 21, TimeoutSeconds = 600)]
    public async Task Chests_in_every_altars_area_are_base_storage(TestContext t)
    {
        Say("settings: " + Settings());
        Assert.True(await Wait(t, () => Stations.Available, 60), "kiln.stations reads here");
        var state = Load();
        var chests = state["chests"]!.AsArray();
        var place = File.Exists(PlacePath) ? int.Parse(File.ReadAllText(PlacePath).Trim(), CultureInfo.InvariantCulture) : 3;
        var started = global::System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O");
        if (state["started"]?.GetValue<string>() != started)
        {
            state["starts"] = (state["starts"]?.GetValue<int>() ?? 0) + 1;
            state["started"] = started;
        }
        var start = state["starts"]!.GetValue<int>();
        var placedNow = new Dictionary<int, (EntityId Chest, EntityId Kiln)>();
        for (var i = 0; i < place; i++)
        {
            var k = chests.Count;
            var a = AltarAt(k);
            var altar = Build.PlaceProp(Altar, a, null, 0, Altars);
            Assert.False(altar.IsNone, $"altar {k} placed at {P(a)}");
            var c = new WorldPosition(a.X + 6, a.Y, a.Z + 6);
            var chest = Build.PlaceProp(MagicChest, c, null, 0, InBases);
            Assert.False(chest.IsNone, $"Magic chest {k} placed at {P(c)}");
            var s = new WorldPosition(a.X + 6, a.Y, a.Z + 10);
            var kiln = Build.PlaceProp(KilnTemplate, s, null, 0, InBases);
            Assert.False(kiln.IsNone, $"charcoal kiln {k} placed at {P(s)}");
            placedNow[k] = (chest, kiln);
            chests.Add(new JsonObject { ["altar"] = Json(a), ["chest"] = Json(c), ["kiln"] = Json(s), ["start"] = start });
            await t.Ticks(2);
        }
        Store(state);
        // the game's bases follow the zones within a few frames; the base-stock list is rebuilt every frame
        var settled = await Wait(t, () => chests.All(n => Bases.At(Pos(n!, "altar")) is not null) || Build.Bases.Count > 16, 90);
        await t.Ticks(30);
        var scanned = await Wait(t, () => Stations.Scanned, placedNow.Count == chests.Count ? 0 : 60);

        var all = Bases.All;
        Say($"start {start}: {chests.Count} altar(s) with a Magic chest placed by this test ({place} new; bases settled {settled}, stations scanned {scanned}), {Build.Bases.Count} altar zone(s) in the world, " +
            $"{all.Count} base(s): {string.Join("; ", all.Select(b => $"#{b.Index} {b.ZoneCount} zone(s), storage {b.StorageCount}"))}");
        // the kilns of earlier starts: the world's production stations, by position (entity ids change when the world loads)
        var stations = World.Find("FactoryStation");
        var missing = new List<string>();
        int ok = 0, noBase = 0;
        for (var k = 0; k < chests.Count; k++)
        {
            var n = chests[k]!;
            var c = Pos(n, "chest");
            var now = placedNow.TryGetValue(k, out var placed) ? placed : ((EntityId, EntityId)?)null;
            var (inBase, good, line) = await CheckChest(t, $"chest {k}", c, Pos(n, "kiln"), $"placed in start {n["start"]}", now, stations);
            Say(line);
            if (!inBase) noBase++;
            else if (good) ok++;
            else missing.Add(line);
        }
        Say($"start {start}: {ok} of {chests.Count} chest(s) are base storage, {noBase} in no base, {missing.Count} not");
        Assert.True(chests.Count > 0, "the test has chests to check (place some: kiln/data/mfa-labtest-chests-place.txt)");
        Assert.True(missing.Count == 0, $"every Magic chest in a base is that base's storage and its stations fill from it ({missing.Count} not: {string.Join(" | ", missing)})");
    }
}
