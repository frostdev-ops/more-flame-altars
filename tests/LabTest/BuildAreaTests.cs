// SPDX-License-Identifier: MIT
// The build-area setting (buildArea), on a lab server with no player. The same tests run in every start of a sequence of
// starts with different settings; a state file next to the server (kiln/data/mfa-labtest-buildarea.json) carries what one
// start placed and saw to the next, so a start can check what became of earlier altars and terrain. Every observation
// is logged as "buildArea:" lines for the report. Server build 26430253: the RVAs in the comments are the server's.
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace MoreFlameAltars.LabTest;

public sealed class BuildAreaTests
{
    private const string ModId = "more-flame-altars";
    private static readonly AssetKey Balancing = new(new Guid("82706b40-61b1-4b8f-8b23-dcec6971bda1"), "keen::BalancingTable");
    /// <summary>One side of the game's build-area cube at altar level 0 to 5 (game data 1287369).</summary>
    private static readonly double[] GameSides = [0, 40, 80, 120, 160, 160];

    // BuildZoneSystem_addZone (0x916310): rcx is the server world's BuildZoneSystem. A mid hook at its first instruction
    // keeps the pointer; the system's lists are read from it, and the game's own queries are called on copies of them.
    private const string AddZone = "48 83 EC 78 8B 84 24 A8 00 00 00 4C 8B D1 89 44 24 60 48 8B 84 24 B8 00 00 00 44 88 4C 24 65 44 88 44 24 64 0F 10 00 48";
    // The cursor's no-build query (0x94b180): (u8* out, BuildZoneSystem*, float box[8] min/pad/max/pad, u8 owner mask,
    // u32 entity to skip); out bit 1 = a no-build box of list 1 (+0x38), bit 2 = a dynamic box of list 2 (+0x60).
    private const string NoBuildQuery = "48 8B C4 48 89 58 10 48 89 68 18 48 89 70 20 48 89 48 08 57 41 54 41 55 41 56 41 57 48 81 EC C0 00 00 00 8B 9C 24 10 01";
    // The zone test of the build-zone system (0x94ad00): (zone entry*, i64 point[3] in 32.32 metres, float margin) -> bool;
    // IsInBuildZone (0x94be60, the server cursor's "near a Flame Altar") calls it for every base zone of list 0 that is
    // not abandoned, with a margin of 15 m. Called here with margin 0 on a copy of each zone.
    private const string ZoneTest = "48 83 EC 48 44 0F B6 41 44 4C 8B D2 0F 29 7C 24 20 0F 28 FA 4C 8B C9 45 85 C0 0F 84 3C 01 00 00 41 83 F8 01 74 0C 32 C0";
    private const int Bases = 0x10, NoBuild = 0x38, Dynamic = 0x60, Areas = 0xb0, Entry = 0x48, AreaEntry = 0x38;

    private static volatile nuint _bzs;
    private static HookHandle? _hook;
    private static readonly string StatePath = Path.GetFullPath(Path.Combine("kiln", "data", "mfa-labtest-buildarea.json"));

    private static void Say(string line) => LabTestMod.Logger?.Info("buildArea: " + line);

    private static int Percent()
    {
        var cfg = Assert.NotNull(Config.Find(ModId), "More Flame Altars registered its config");
        return Convert.ToInt32(cfg.Get("buildArea"), CultureInfo.InvariantCulture);
    }

    private static JsonObject Load() =>
        File.Exists(StatePath) ? JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject() : new JsonObject { ["starts"] = 0 };

    private static void Store(JsonObject state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => $"({F(v.X)}, {F(v.Y)}, {F(v.Z)})";
    private static string P(WorldPosition p) => $"({F(p.X)}, {F(p.Y)}, {F(p.Z)})";

    private static async Task<nuint> ZoneSystem(TestContext t)
    {
        if (_bzs != 0) return _bzs;
        if (_hook is null)
        {
            var at = Symbols.Find(AddZone);
            Assert.True(at != 0, "BuildZoneSystem_addZone found by its pattern");
            _hook = Hooks.Mid(at, (ref Registers r) => { if (_bzs == 0) _bzs = r.Rcx; });
        }
        await t.Until(() => _bzs != 0, TimeSpan.FromSeconds(20));
        Assert.True(_bzs != 0, "the game added a build zone within 20 s (the BuildZoneSystem pointer)");
        return _bzs;
    }

    /// <summary>A copy of one of the system's lists: (bytes, count).</summary>
    private static (byte[] Bytes, int Count) List(nuint bzs, int at, int stride)
    {
        var data = Memory.Read<nuint>(bzs + (nuint)at);
        var count = (int)Math.Min(Memory.Read<ulong>(bzs + (nuint)at + 8), 4096);
        return (data == 0 || count == 0 ? [] : Memory.ReadBytes(data, count * stride), count);
    }

    private static double M(long fixed32) => fixed32 / 4294967296.0;

    private sealed record Box(double[] Lo, double[] Hi, uint Entity, byte Kind, byte Owner, byte Abandoned, Guid Id);

    private static List<Box> Boxes((byte[] Bytes, int Count) list)
    {
        var r = new List<Box>();
        for (var i = 0; i < list.Count; i++)
        {
            var e = list.Bytes.AsSpan(i * Entry, Entry);
            var mm = MemoryMarshal.Cast<byte, long>(e[..48]);
            r.Add(new([M(mm[0]), M(mm[1]), M(mm[2])], [M(mm[3]), M(mm[4]), M(mm[5])],
                BitConverter.ToUInt32(e[0x40..]), e[0x44], e[0x45], e[0x46], new Guid(e[0x30..0x40])));
        }
        return r;
    }

    /// <summary>The game's two cursor queries, called on a copy of the system's lists (the live lists are rebuilt by the
    /// game's workers every step).</summary>
    private sealed unsafe class Queries : IDisposable
    {
        private readonly byte* _fake = (byte*)NativeMemory.AllocZeroed(0x200);
        private readonly List<nint> _owned = [];
        private readonly delegate* unmanaged<byte*, nuint, float*, byte, uint, byte*> _noBuild;
        private readonly delegate* unmanaged<byte*, long*, float, byte> _zone;
        private readonly byte* _bases;
        private readonly int _baseCount;

        public Queries(nuint bzs)
        {
            var nb = Symbols.Find(NoBuildQuery);
            var zt = Symbols.Find(ZoneTest);
            Assert.True(nb != 0 && zt != 0, "the cursor's no-build query and the zone test found by their patterns");
            _noBuild = (delegate* unmanaged<byte*, nuint, float*, byte, uint, byte*>)nb;
            _zone = (delegate* unmanaged<byte*, long*, float, byte>)zt;
            foreach (var (at, stride) in new[] { (NoBuild, Entry), (Dynamic, Entry), (Areas, AreaEntry), (Bases, Entry) })
            {
                var (bytes, count) = List(bzs, at, stride);
                var copy = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(bytes.Length, 16));
                _owned.Add((nint)copy);
                bytes.CopyTo(new Span<byte>(copy, bytes.Length));
                *(nint*)(_fake + at) = (nint)copy;
                *(ulong*)(_fake + at + 8) = (ulong)count;
                if (at == Bases) { _bases = copy; _baseCount = count; }
            }
        }

        /// <summary>The no-build bits for a box: 1 = a no-build box (list 1), 2 = a dynamic one (list 2).</summary>
        public byte NoBuildBits(double[] lo, double[] hi)
        {
            var box = stackalloc float[8] { (float)lo[0], (float)lo[1], (float)lo[2], 0, (float)hi[0], (float)hi[1], (float)hi[2], 0 };
            byte result = 0;
            _noBuild(&result, (nuint)_fake, box, 0xff, 0);
            return result;
        }

        /// <summary>The game's zone test, margin 0, over the base zones that are not abandoned (IsInBuildZone's loop).</summary>
        public bool Inside(double x, double y, double z)
        {
            var p = stackalloc long[3] { (long)(x * 4294967296.0), (long)(y * 4294967296.0), (long)(z * 4294967296.0) };
            for (var i = 0; i < _baseCount; i++)
                if (_bases[i * Entry + 0x46] == 0 && _zone(_bases + i * Entry, p, 0f) != 0) return true;
            return false;
        }

        public void Dispose()
        {
            foreach (var p in _owned) NativeMemory.Free((void*)p);
            NativeMemory.Free(_fake);
        }
    }

    private static async Task<WorldPosition> Ground(double x, double z)
    {
        var box = Build.BoxBetween(new WorldPosition(x, 500, z), new WorldPosition(x, 1200, z));
        var cells = await Build.ReadAsync(box);
        for (var y = box.SizeY - 1; y >= 0; y--)
        {
            var c = cells[box.Index(0, y, 0)];
            if (c.Fill > 0 && c.Material is > 0 and < 128)
                return new WorldPosition(x, Build.CornerOf(new CellPos(box.X, box.Y + y, box.Z)).Y + c.Fill / 255.0 * 0.5 + 0.05, z);
        }
        throw new InvalidOperationException($"no ground at ({F(x)}, {F(z)})");
    }

    private static Vector3? Shape(EntityId altar)
    {
        if (World.ReadBytes(altar, "TriggerShape") is not { Length: >= 16 } b) return null;
        return new Vector3(BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8), BitConverter.ToSingle(b, 12));
    }

    /// <summary>The altar level whose side, at <paramref name="percent"/>, gives this full side; -1 when none.</summary>
    private static int LevelOf(double side, int percent)
    {
        for (var level = 1; level < GameSides.Length; level++)
            if (Math.Abs(side - GameSides[level] * percent / 100.0) < 0.05) return level;
        return -1;
    }

    [KilnTest(Order = 10)]
    public void Build_area_table_holds_what_the_setting_asks_for()
    {
        var pct = Percent();
        var table = Assert.NotNull(Assets.Reflect(Balancing), "the game's balancing table");
        var n = table.Count("buildzoneSizesPerAltarLevel");
        Assert.Equal(GameSides.Length, n, "six altar levels, as on build 26430253");
        var have = new List<string>();
        for (var level = 0; level < n; level++)
            foreach (var axis in new[] { "x", "y", "z" })
            {
                var v = Convert.ToDouble(table[$"buildzoneSizesPerAltarLevel[{level}].{axis}"], CultureInfo.InvariantCulture);
                if (axis == "x") have.Add(F(v));
                Assert.True(Math.Abs(v - GameSides[level] * pct / 100.0) < 0.01,
                    $"buildzoneSizesPerAltarLevel[{level}].{axis} = {F(v)}, want {F(GameSides[level] * pct / 100.0)} (buildArea {pct})");
            }
        Say($"start with buildArea {pct}: the running table's sides by altar level [{string.Join(", ", have)}] m");
    }

    [KilnTest(Order = 11, TimeoutSeconds = 120)]
    public async Task Every_altar_zone_follows_the_table(TestContext t)
    {
        var pct = Percent();
        await t.Until(() => Build.Bases.Count > 0, TimeSpan.FromSeconds(20));
        var state = Load();
        // one count per server start, not per test run (Wine can give a new server the same process id)
        var started = global::System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O");
        if (state["started"]?.GetValue<string>() != started)
        {
            state["starts"] = state["starts"]!.GetValue<int>() + 1;
            state["started"] = started;
        }
        var start = state["starts"]!.GetValue<int>();
        var seen = state["altars"] as JsonObject ?? new JsonObject();
        if (Build.Bases.Count == 0)
        {
            Store(state);
            Assert.Skip($"start {start}: no altar in this world yet (the next test places some)");
        }
        foreach (var b in Build.Bases)
        {
            var shape = Shape(b.Altar);
            var side = b.Half.X * 2;
            var level = LevelOf(side, pct);
            Say($"start {start} (buildArea {pct}): altar {b.Altar} at {P(b.Center)}: zone half {V(b.Half)}, TriggerShape {(shape is { } s ? V(s) : "none")}, " +
                $"altar level {(level < 0 ? "?" : level)}{(b.Abandoned ? ", abandoned" : "")}");
            var key = $"altar at {P(b.Center)}";   // entity ids change when the world loads again; the place does not
            var history = seen[key] as JsonArray ?? new JsonArray();
            history.Add($"start {start}, buildArea {pct}: half {F(b.Half.X)}");
            seen[key] = history;
            Assert.True(level > 0, $"altar {b.Altar}: zone side {F(side)} m is a game side times {pct}%");
            if (shape is { } sh)
                Assert.True(Math.Abs(sh.X - b.Half.X) < 0.05 && Math.Abs(sh.Y - b.Half.Y) < 0.05 && Math.Abs(sh.Z - b.Half.Z) < 0.05,
                    $"altar {b.Altar}: the server's zone equals its TriggerShape ranges ({V(sh)} vs {V(b.Half)})");
        }
        foreach (var (key, history) in seen) Say($"{key} so far: {string.Join("; ", history!.AsArray().Select(h => h!.GetValue<string>()))}");
        state["altars"] = seen;
        var save = Directory.Exists("savegame") ? Directory.GetFiles("savegame").Sum(f => new FileInfo(f).Length) : -1;
        Say($"start {start}: savegame folder {save} bytes (written by the previous stop)");
        Store(state);
    }

    /// <summary>A new altar, placed in this start next to a no-build box of the game's: its zone has the table's size; the
    /// game's own cursor queries then answer for points inside the bigger zone.</summary>
    [KilnTest(Order = 12, TimeoutSeconds = 240)]
    public async Task A_new_altar_gets_the_tables_size_and_the_games_queries_answer(TestContext t)
    {
        var pct = Percent();
        var bzs = await ZoneSystem(t);
        var noBuild = Boxes(List(bzs, NoBuild, Entry));
        var bases = Boxes(List(bzs, Bases, Entry));
        Say($"the BuildZoneSystem holds {bases.Count} base zones, {noBuild.Count} no-build boxes, {List(bzs, Dynamic, Entry).Count} dynamic boxes, " +
            $"{List(bzs, Areas, AreaEntry).Count} building areas");
        // a box-shaped no-build box of a good size, away from every base, on loaded ground
        var game = GameSides[1] / 2;                 // a new altar is level 1: half 20 m in the game
        var ours = GameSides[1] * pct / 200.0;       // and this much with the setting
        var gap = pct > 100 ? (game + ours) / 2 : game + 5;   // between the two borders (or just outside the game's)
        Box? target = null;
        WorldPosition at = default;
        int near = 0, noGround = 0, height = 0, tried = 0;
        foreach (var b in noBuild.Where(b => b.Kind == 0 && b.Hi[0] - b.Lo[0] is > 2 and < 600 && b.Hi[2] - b.Lo[2] is > 2 and < 600)
                                 .OrderBy(b => b.Owner == 4 ? 1 : 0)   // kind 4: the small boxes round placed props (altars); others first
                                 .ThenByDescending(b => (b.Hi[0] - b.Lo[0]) * (b.Hi[2] - b.Lo[2])))
        {
            var x = b.Hi[0] + gap;
            var z = (b.Lo[2] + b.Hi[2]) / 2;
            tried++;
            if (bases.Any(z0 => x > z0.Lo[0] - 150 && x < z0.Hi[0] + 150 && z > z0.Lo[2] - 150 && z < z0.Hi[2] + 150)) { near++; continue; }
            try { at = await Ground(x, z); }
            catch (Exception e) when (e is InvalidOperationException or BuildException) { noGround++; continue; }
            if (at.Y + ours < b.Lo[1] || at.Y - ours > b.Hi[1]) { height++; continue; }   // the zone reaches the box's height
            target = b;
            break;
        }
        var kinds = noBuild.GroupBy(b => b.Owner).Select(g => $"owner kind {g.Key}: {g.Count()} (largest {F(g.Max(b => b.Hi[0] - b.Lo[0]))} x {F(g.Max(b => b.Hi[2] - b.Lo[2]))} m)");
        Say($"no-build boxes by owner kind: {string.Join("; ", kinds)}");
        // a large no-build box of the world's (not a prop's): the altar floats beside it at its middle height if need be
        if (target is null || target.Owner == 4)
            foreach (var b in noBuild.Where(b => b.Owner != 4 && b.Kind == 0 && b.Hi[0] - b.Lo[0] is > 20 and < 2000)
                                     .OrderByDescending(b => (b.Hi[0] - b.Lo[0]) * (b.Hi[2] - b.Lo[2])))
            {
                var x = b.Hi[0] + gap;
                var z = (b.Lo[2] + b.Hi[2]) / 2;
                if (bases.Any(z0 => x > z0.Lo[0] - 150 && x < z0.Hi[0] + 150 && z > z0.Lo[2] - 150 && z < z0.Hi[2] + 150)) continue;
                target = b;
                at = new WorldPosition(x, (b.Lo[1] + b.Hi[1]) / 2, z);
                break;
            }
        Assert.True(target is not null, $"a no-build box with open ground beside it ({tried} tried: {near} near a base, {noGround} without ground, {height} at another height)");
        var nb = target!;
        Say($"no-build box {P(new(nb.Lo[0], nb.Lo[1], nb.Lo[2]))} .. {P(new(nb.Hi[0], nb.Hi[1], nb.Hi[2]))} (entity {nb.Entity}, owner kind {nb.Owner}); " +
            $"new altar {F(gap)} m past its +x side at {P(at)}");
        var altar = Build.PlaceProp("Home_Base_FlameAltar", at, null, 0, new BuildOptions { Flags = BuildFlags.AllowAltars | BuildFlags.InBases });
        Assert.False(altar.IsNone, "the game's building helper placed a Flame Altar");
        await t.Until(() => Build.Bases.Any(b => b.Altar == altar), TimeSpan.FromSeconds(60));
        var zone = Assert.NotNull(Build.Bases.FirstOrDefault(b => b.Altar == altar), "the new altar has a build zone");
        Say($"new altar {altar}: zone half {V(zone.Half)}, TriggerShape {(Shape(altar) is { } s ? V(s) : "none")} (game: {F(game)}, table: {F(ours)})");
        Assert.True(Math.Abs(zone.Half.X - ours) < 0.05 && Math.Abs(zone.Half.Z - ours) < 0.05 && Math.Abs(zone.Half.Y - ours) < 0.05,
            $"the new altar's zone half is {F(ours)} m on every side (have {V(zone.Half)})");
        await t.Ticks(10);

        using var q = new Queries(_bzs);
        var c = zone.Center;
        // a point the game's border would leave out, inside the bigger one (on the side away from the no-build box)
        var outer = (game + ours) / 2;
        var inBig = pct > 100 ? q.Inside(c.X - outer, c.Y, c.Z) : (bool?)null;
        var inGame = q.Inside(c.X - game / 2, c.Y, c.Z);
        var outside = q.Inside(c.X - ours - 10, c.Y, c.Z);
        Say($"the game's zone test (IsInBuildZone's, margin 0): {F(game / 2)} m from the altar {inGame}, {F(outer)} m {inBig?.ToString() ?? "-"}, " +
            $"{F(ours + 10)} m (past the border) {outside}");
        Assert.True(inGame && !outside, "the building-zone test answers inside near the altar and outside past the border");
        if (inBig is { } big) Assert.True(big, $"the building-zone test counts {F(outer)} m from the altar as inside: the bigger zone is real");

        // where the zone and the no-build box overlap: a 1 m box there, and one as far into the zone but not in the box
        var lo = new[] { Math.Max(nb.Lo[0], c.X - zone.Half.X), Math.Max(nb.Lo[1], c.Y - zone.Half.Y), Math.Max(nb.Lo[2], c.Z - zone.Half.Z) };
        var hi = new[] { Math.Min(nb.Hi[0], c.X + zone.Half.X), Math.Min(nb.Hi[1], c.Y + zone.Half.Y), Math.Min(nb.Hi[2], c.Z + zone.Half.Z) };
        if (hi[0] > lo[0] + 1 && hi[1] > lo[1] + 1 && hi[2] > lo[2] + 1)
        {
            var m = new[] { (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2 };
            var bits = q.NoBuildBits([m[0] - 0.5, m[1] - 0.5, m[2] - 0.5], [m[0] + 0.5, m[1] + 0.5, m[2] + 0.5]);
            var free = q.NoBuildBits([c.X - 1.5, m[1] - 0.5, m[2] - 0.5], [c.X - 0.5, m[1] + 0.5, m[2] + 0.5]);
            Say($"zone and no-build box overlap {F(hi[0] - lo[0])} m along x; a 1 m box there at {P(new(m[0], m[1], m[2]))}: inside the building zone " +
                $"{q.Inside(m[0], m[1], m[2])}, no-build bits {bits}; a 1 m box at the altar's other side: no-build bits {free}");
        }
        else Say("the zone does not reach the no-build box (buildArea 100: the game's border stops short of it)");

        // a second altar whose zone the game's size would not touch, but the bigger one does
        var second = new WorldPosition(c.X, c.Y, c.Z + (game + ours) * 1.0);
        try { second = await Ground(second.X, second.Z); } catch (Exception e) when (e is InvalidOperationException or BuildException) { }
        var other = Build.PlaceProp("Home_Base_FlameAltar", second, null, 0, new BuildOptions { Flags = BuildFlags.AllowAltars | BuildFlags.InBases });
        Assert.False(other.IsNone, "a second altar placed");
        await t.Until(() => Build.Bases.Any(b => b.Altar == other), TimeSpan.FromSeconds(60));
        await t.Ticks(10);
        var mine = Boxes(List(_bzs, Bases, Entry)).Where(b => b.Entity == (uint)altar.Value || b.Entity == (uint)other.Value).ToList();
        var overlap = mine.Count == 2 && mine[0].Lo[2] <= mine[1].Hi[2] && mine[1].Lo[2] <= mine[0].Hi[2];
        Say($"second altar {other} {F(second.Z - c.Z)} m away along z: zones {mine.Count}, overlapping {overlap}, base ids " +
            $"{string.Join(" / ", mine.Select(b => b.Id))}, owner kinds {string.Join("/", mine.Select(b => b.Owner))}, " +
            $"building areas now {List(_bzs, Areas, AreaEntry).Count}");

        var state = Load();
        var placed = state["placed"] as JsonArray ?? new JsonArray();
        placed.Add($"start {state["starts"]}, buildArea {pct}: {altar} at {P(at)}, {other} at {P(second)}");
        state["placed"] = placed;
        Store(state);
    }

    /// <summary>Terrain: a pillar built (once) where only the bigger zone reaches, with Kiln's own save region released,
    /// so only the game's base-region save can keep it. Every later start reports whether it is still there.</summary>
    [KilnTest(Order = 13, TimeoutSeconds = 180)]
    public async Task Terrain_in_the_bigger_zone_and_the_games_save(TestContext t)
    {
        var pct = Percent();
        var state = Load();
        if (state["pillar"] is JsonObject p)
        {
            var box = new CellBox(p["x"]!.GetValue<int>(), p["y"]!.GetValue<int>(), p["z"]!.GetValue<int>(),
                p["sx"]!.GetValue<int>(), p["sy"]!.GetValue<int>(), p["sz"]!.GetValue<int>());
            var cells = await Build.ReadAsync(box);
            var solid = cells.Count(v => v.IsSolid);
            Say($"start {state["starts"]} (buildArea {pct}): the pillar built at start {p["start"]} (buildArea {p["pct"]}, {p["metres"]} m from altar " +
                $"{p["altar"]}, game half {p["game"]}): {solid} of {cells.Length} cells solid (built: {p["solid"]})");
            var log = state["pillarSeen"] as JsonArray ?? new JsonArray();
            log.Add($"start {state["starts"]}, buildArea {pct}: {solid}/{cells.Length}");
            state["pillarSeen"] = log;
            Store(state);
            return;
        }
        if (pct == 100) Assert.Skip("builds the pillar in a start with buildArea above 100");
        // the oldest altar of the world (the one the save had before the setting)
        var home = Build.Bases.OrderBy(b => b.Altar.Value).First(b => !b.Abandoned);
        var level = LevelOf(home.Half.X * 2, pct);
        Assert.True(level > 0, "the altar's level from its zone");
        var game = GameSides[level] / 2;
        var metres = (game + home.Half.X) / 2;
        var ground = await Ground(home.Center.X + metres, home.Center.Z);
        var shape = new BuildShape(ShapeKind.Box, new WorldPosition(ground.X + 1, ground.Y + 2, ground.Z + 1), new Vector3(1, 2, 1));
        var below = Build.ReadCell(Build.CellOf(new WorldPosition(ground.X, ground.Y - 0.3, ground.Z)));
        var material = below.Material is > 0 and < 128 ? below.Material : (byte)1;
        var r = await Build.Add(shape, material, new BuildOptions { Flags = BuildFlags.InBases });
        Assert.True(r.Ok, $"the pillar was built ({r.Refusal})");
        var cbox = Build.BoxBetween(new WorldPosition(ground.X, ground.Y + 0.25, ground.Z), new WorldPosition(ground.X + 1.99, ground.Y + 3.99, ground.Z + 1.99));
        var released = Build.Release(Build.BoxBetween(new WorldPosition(ground.X - 8, ground.Y - 8, ground.Z - 8), new WorldPosition(ground.X + 10, ground.Y + 12, ground.Z + 10)));
        var cells2 = await Build.ReadAsync(cbox);
        state["pillar"] = new JsonObject
        {
            ["x"] = cbox.X, ["y"] = cbox.Y, ["z"] = cbox.Z, ["sx"] = cbox.SizeX, ["sy"] = cbox.SizeY, ["sz"] = cbox.SizeZ, ["start"] = state["starts"]!.GetValue<int>(), ["pct"] = pct,
            ["metres"] = F(metres), ["altar"] = home.Altar.ToString(), ["game"] = F(game), ["solid"] = $"{cells2.Count(v => v.IsSolid)}/{cells2.Length}",
        };
        Store(state);
        Say($"pillar built {F(metres)} m from altar {home.Altar} (zone half {F(home.Half.X)}, the game's {F(game)}) at {P(ground)}, cells {cbox.X},{cbox.Y},{cbox.Z}: " +
            $"{cells2.Count(v => v.IsSolid)} of {cells2.Length} solid; Kiln's save regions released there: {released} chunk(s)");
    }

    /// <summary>The game's base system (PlayerBasesSystem) after its rebuild: how many bases the world's zones make, and
    /// which zones each base holds. The pointer comes from mergeBases' hook, so this needs beyondSixteen in the start.</summary>
    [KilnTest(Order = 14, TimeoutSeconds = 60)]
    public async Task Overlapping_zones_and_the_games_bases(TestContext t)
    {
        var hook = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MoreFlameAltars.BaseMergeHook")).First(x => x is not null)!;
        var installed = (bool)hook.GetProperty("Installed")!.GetValue(null)!;
        if (!installed) Assert.Skip("needs beyondSixteen (mergeBases' hook gives the base system's pointer)");
        var field = hook.GetField("LastSystem", global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.NonPublic)!;
        await t.Until(() => (nuint)field.GetValue(null)! != 0, TimeSpan.FromSeconds(30));
        var pb = (nuint)field.GetValue(null)!;
        Assert.True(pb != 0, "the game rebuilt its bases");
        await t.Ticks(5);
        var zones = Memory.Read<ulong>(pb + 0x1338);
        var count = Memory.Read<uint>(pb + 0x24);
        var parts = new List<string>();
        for (var i = 0; i < Math.Min(count, 16u); i++)
        {
            var rec = pb + 0x30 + (nuint)(i * 0x130);
            var mask = Memory.Read<uint>(rec + 0x110);
            parts.Add($"base {i}: box x {F(Memory.Read<float>(rec + 0xf0))}..{F(Memory.Read<float>(rec + 0x100))}, z {F(Memory.Read<float>(rec + 0xf8))}..{F(Memory.Read<float>(rec + 0x108))}, zone mask {mask:x}");
        }
        Say($"the game's base system: {zones} zones make {count} bases ({Build.Bases.Count} altar zones): {string.Join("; ", parts)}");
        Assert.True(count >= 1 && count <= zones, "at least one base, never more bases than zones");
    }
}
