// SPDX-License-Identifier: MIT
// In-server checks with no player: `kiln test more-flame-altars-labtest`. More Flame Altars changes the game's balancing
// table before the game reads it (Kiln's boot phase), so these read what the running game holds and compare it with what
// the mod's settings ask for. Whether a player may then place that many altars needs a player: README.md, "What is checked".
using System.Reflection;
using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace MoreFlameAltars.LabTest;

public sealed class LabTestMod : Mod
{
    public override void OnLoad() { }
}

public sealed class Tests
{
    private const string ModId = "more-flame-altars";
    private static readonly AssetKey Balancing = new(new Guid("82706b40-61b1-4b8f-8b23-dcec6971bda1"), "keen::BalancingTable");

    // The game's own lists, Flame level 0 to 20 (server build 26430253, game data 1287369).
    private static readonly int[] GameAltars = [1, 2, 4, 6, 8, 10, 11, 12, 13, 14, 15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16];
    private static readonly int[] GameWater = [1, 1, 1, 1, 5, 10, 20, 30, 40, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50];

    /// <summary>The registered running settings, including when file edits are waiting for a restart.</summary>
    private static (int Altars, int ExtraAltars, int Water, int ExtraWater, bool Beyond) S => ReadSettings();

    private static (int, int, int, int, bool) ReadSettings()
    {
        var cfg = Assert.NotNull(Config.Find(ModId), "More Flame Altars registered its config");
        return (Convert.ToInt32(cfg.Get("altars")), Convert.ToInt32(cfg.Get("extraAltars")),
            Convert.ToInt32(cfg.Get("waterSources")), Convert.ToInt32(cfg.Get("extraWaterSources")),
            Convert.ToBoolean(cfg.Get("beyondSixteen")));
    }

    /// <summary>What the mod makes of the game's value: plus extra, at least atLeast, at most cap, never below the game's.</summary>
    private static int Expected(int game, int atLeast, int extra, int cap) =>
        Math.Max(game, Math.Clamp(Math.Max(game + extra, atLeast), 0, cap));

    [KilnTest]
    public void The_mods_boot_patch_is_in_this_start()
    {
        Assert.True(Capabilities.Is("mods.boot.resource_patches"), $"mods.boot.resource_patches is on ({Capabilities.Why("mods.boot.resource_patches")})");
        var state = Mods.BootState(ModId);
        Assert.True(state.State == BootModState.Committed,
            $"{ModId} is in the published boot generation (state {state.State}, definitions [{string.Join(", ", state.Definitions)}], " +
            $"issues [{string.Join("; ", state.Issues.Select(i => $"{i.Code}: {i.Reason}"))}])");
        Assert.True(state.Issues.Count == 0, $"no boot issue ({string.Join("; ", state.Issues.Select(i => $"{i.Code}: {i.Reason}"))})");
    }

    [KilnTest]
    public void The_games_tables_hold_what_the_settings_ask_for()
    {
        Assert.True(Assets.Available, "kiln.assets is on");
        var table = Assert.NotNull(Assets.Reflect(Balancing), "the game's balancing table");
        foreach (var (field, game, atLeast, extra, cap) in new[]
                 {
                     ("altarsPerFlameLevel", GameAltars, S.Altars, S.ExtraAltars, S.Beyond ? 128 : 16),
                     ("waterSourcesPerFlameLevel", GameWater, S.Water, S.ExtraWater, 50),
                 })
        {
            var levels = table.Count(field);
            Assert.Equal(game.Length, levels, $"{field}: one number per Flame level, as on build 26430253");
            var have = new List<int>();
            var want = new List<int>();
            for (var level = 0; level < levels; level++)
            {
                have.Add(Convert.ToInt32(table[$"{field}[{level}]"]));
                want.Add(Expected(game[level], atLeast, extra, cap));
                Assert.True(table.HeldBy($"{field}[{level}]") is null, $"{field}[{level}] is the game's own data, not a runtime override");
            }
            Assert.True(have.SequenceEqual(want), $"{field} (settings {atLeast} / +{extra}): have {string.Join(",", have)}, want {string.Join(",", want)}");
        }
    }

    /// <summary>What the limit function answers at <paramref name="level"/> for table value <paramref name="table"/>: the
    /// game's min(value, 16) without beyondSixteen; with it the mod's detour (AltarLimitHook.Answer): the settings' rule on
    /// the game's number when the table holds the game's number or the rule's, else on the table's value, at most 128.</summary>
    private static uint Expected(uint level, byte table)
    {
        // the hook is in when the start had beyondSixteen, or once it is switched on while the server runs; the table was
        // made with the running settings, the answer follows the saved ones (a change made while the server runs)
        var (running, saved) = (S, Saved());
        var hookType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("MoreFlameAltars.AltarLimitHook")).First(t => t is not null)!;
        var installed = (bool)hookType.GetProperty("Installed")!.GetValue(null)!;
        if (running.Beyond || saved.Beyond) Assert.True(installed, "beyondSixteen requires the detour to be installed");
        if (!installed) return Math.Min(table, 16u);
        var game = GameAltars[(int)Math.Min(level, (uint)GameAltars.Length - 1)];
        int Rule(int g, int atLeast, int extra, bool beyond) =>
            Math.Max(g, Math.Clamp(Math.Max(g + Math.Max(0, extra), atLeast), 0, beyond ? 128 : 16));
        var from = table == game || Rule(game, running.Altars, running.ExtraAltars, running.Beyond) == table ? game : Math.Min((int)table, 16);
        return (uint)Math.Max(1, Rule(from, saved.Altars, saved.ExtraAltars, saved.Beyond));
    }

    /// <summary>The altar settings as the file holds them (a change waiting for a restart included).</summary>
    private static (int Altars, int ExtraAltars, bool Beyond) Saved()
    {
        var cfg = Assert.NotNull(Config.Find(ModId), "More Flame Altars registered its config");
        // the file value comes as the file's JSON value: read it through its text, as the mod does
        string File(string path) => Convert.ToString(cfg.Entries.FirstOrDefault(e => e.Path == path)?.FileValue ?? cfg.Get(path),
            System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return (int.Parse(File("altars")), int.Parse(File("extraAltars")), bool.Parse(File("beyondSixteen")));
    }

    /// <summary>The game's own altar-limit function, called with a made-up balancing table: with beyondSixteen it answers
    /// the mod's detour (up to 128), without it the game's min(value, 16). Proves the detour on the real function; whether
    /// the game then lets a player place the altar needs a player.</summary>
    [KilnTest]
    public unsafe void The_games_limit_function_answers_the_mods_cap()
    {
        // the mod loads its symbol only with beyondSixteen on; otherwise find the game's function by the same pattern
        var fn = Symbols.TryGet("more-flame-altars/AltarLimit", out var at) ? at
            : Symbols.Find("48 8B 41 20 48 85 C0 75 06 B8 01 00 00 00 C3 48 05 ?? ?? ?? ?? 48 63 08 85 C9 75 13 33 C9 8B C2 0F B6 04 08 B9 10 00 00 00 3A C1 0F 47 C1 C3");
        var table = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x200);
        var registry = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x40);
        try
        {
            *(nint*)(registry + 0x20) = (nint)table;
            var blob = table + 0x1b8;
            *(int*)blob = 0x10;          // self-relative displacement of the bytes
            *(uint*)(blob + 4) = 4;      // count
            byte[] values = [3, 20, 40, 16];
            for (var i = 0; i < values.Length; i++) blob[0x10 + i] = values[i];
            var limit = (delegate* unmanaged<nint, uint, uint>)fn;
            for (uint level = 0; level < values.Length; level++)
                Assert.Equal(Expected(level, values[level]), limit((nint)registry, level), $"limit at level {level} (table {values[level]}, running {S}, saved {Saved()})");
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(table);
            System.Runtime.InteropServices.NativeMemory.Free(registry);
        }
    }

    /// <summary>The setting the owner asked for: 128 altars. With altars 128 and beyondSixteen saved, the game's own limit
    /// function answers 128 at every Flame level (the table's value does not matter: the rule takes the game's number).</summary>
    [KilnTest]
    public unsafe void The_limit_function_answers_128_when_the_settings_say_128()
    {
        var saved = Saved();
        if (!(saved.Altars == 128 && saved.Beyond)) Assert.Skip($"needs altars 128 and beyondSixteen saved (saved {saved})");
        Assert.True(Symbols.TryGet("more-flame-altars/AltarLimit", out var at), "the mod loaded its AltarLimit symbol");
        var table = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x200);
        var registry = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x40);
        try
        {
            *(nint*)(registry + 0x20) = (nint)table;
            var blob = table + 0x1b8;
            *(int*)blob = 0x10;
            *(uint*)(blob + 4) = 21;
            for (var i = 0; i < 21; i++) blob[0x10 + i] = 128;  // what the boot patch writes with altars 128
            var limit = (delegate* unmanaged<nint, uint, uint>)at;
            for (uint level = 0; level < 21; level++)
                Assert.Equal(128u, limit((nint)registry, level), $"the game's altar limit at Flame level {level}");
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(table);
            System.Runtime.InteropServices.NativeMemory.Free(registry);
        }
    }

    private static Type MergeHook() => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("MoreFlameAltars.BaseMergeHook")).First(t => t is not null)!;

    private static T Static<T>(Type t, string name) =>
        (T)(t.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)
            ?? t.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!);

    /// <summary>mergeBases on the server: the game's own base rebuild, run on the server's real base system with a made-up
    /// world of 40 separate altar zones, ends with all 40 zones in the list and one base; the game's queries put a point
    /// in the 40th zone in base 0 and a point between zones in no base. With the merge switched off the same world gives the
    /// game's 16 zones and 16 bases. At the end the game rebuilds its bases from its own world again.</summary>
    [KilnTest]
    public unsafe void The_games_base_rebuild_makes_40_zones_one_base()
    {
        var cfg = Assert.NotNull(Config.Find(ModId), "More Flame Altars registered its config");
        if (!(S.Beyond && Convert.ToBoolean(cfg.Get("mergeBases")))) Assert.Skip("needs beyondSixteen and mergeBases in this start");
        var hook = MergeHook();
        Assert.True(Static<bool>(hook, "Installed") && Static<bool>(hook, "Enabled"), "mergeBases' hooks are in and on");
        var pb = Static<nuint>(hook, "LastSystem");
        Assert.True(pb != 0, $"the game rebuilt its bases since the hooks went in ({Static<long>(hook, "Rebuilds")} rebuilds)");
        var setEnabled = hook.GetMethod("SetEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        const int n = 40;
        const long worldZones = 0x15fa278;
        var zones = (float*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(n * 24);  // the world's zones: min xyz, max xyz
        var world = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)(worldZones + 16));
        static float X(int i) => 100000 + i * 1000;
        try
        {
            for (var i = 0; i < n; i++)
            {
                var b = zones + i * 6;
                b[0] = X(i); b[1] = 0; b[2] = 0; b[3] = X(i) + 50; b[4] = 50; b[5] = 50;
            }
            *(nint*)(world + worldZones) = (nint)zones;
            *(ulong*)(world + worldZones + 8) = n;
            var rebuild = (delegate* unmanaged<nuint, nint, void>)Symbols.Get("more-flame-altars/BasesRebuild");
            var region = (delegate* unmanaged<byte*, nuint, float*, byte*>)Symbols.Get("more-flame-altars/BasesRegion");
            var point = (delegate* unmanaged<byte*, nuint, float*, byte*>)Symbols.Get("more-flame-altars/BasesPoint");
            var contains = (delegate* unmanaged<nuint, ulong, float*, byte>)Symbols.Get("more-flame-altars/BasesContains");
            // 20 separate zones: the game makes a base of each of them (its zone mask has room for 32), so without the
            // mod's check it would write a 17th to 20th base record over the zone list; the check skips those 4
            var skipped = Static<long>(hook, "SkippedBases");
            *(ulong*)(world + worldZones + 8) = 20;
            rebuild(pb, (nint)world);
            var skippedNow = Static<long>(hook, "SkippedBases") - skipped;
            Assert.True(skippedNow == 4, $"20 zones: the rebuild added 16 base records and skipped 4 (skipped {skippedNow})");
            Assert.True(*(ulong*)(pb + 0x1338) == 20 && *(uint*)(pb + 0x24) == 1 && *(uint*)(pb + 0x140) == 0xFFFFF,
                $"20 zones: one base with all 20 (zones {*(ulong*)(pb + 0x1338)}, bases {*(uint*)(pb + 0x24)}, mask {*(uint*)(pb + 0x140):x})");

            // 40 zones (the game's mask math wraps at 32: it makes 8 bases of the first 8 zones and drops the rest)
            *(ulong*)(world + worldZones + 8) = n;
            rebuild(pb, (nint)world);
            Assert.Equal(40ul, *(ulong*)(pb + 0x1338), "zones in the base system's list");
            Assert.Equal(1u, *(uint*)(pb + 0x24), "bases");
            Assert.Equal(uint.MaxValue, *(uint*)(pb + 0x140), "base 0's zone mask");
            var list = *(float**)(pb + 0x1330);
            Assert.True(*(float*)(pb + 0x120) == X(0) && *(float*)(pb + 0x130) == X(n - 1) + 50,
                $"base 0's box spans the zones (box x {*(float*)(pb + 0x120)} .. {*(float*)(pb + 0x130)}; zone 0 {list[0]},{list[1]},{list[2]} .. " +
                $"{list[4]},{list[5]},{list[6]}; zone 39 {list[39 * 8]} .. {list[39 * 8 + 4]})");

            var inLast = stackalloc float[3] { X(n - 1) + 25, 25, 25 };
            var between = stackalloc float[3] { X(n - 1) + 500, 25, 25 };
            var r = stackalloc byte[16];
            region(r, pb, inLast);
            Assert.True(r[0] == 0 && r[8] == 0, $"a point in the 40th zone is in base 0 (status {r[0]}, base {r[8]})");
            region(r, pb, between);
            Assert.True(r[0] != 0, $"a point between zones is in no base (status {r[0]})");
            var q = stackalloc byte[0x30];
            point(q, pb, inLast);
            Assert.True(q[0] == 0 && *(float*)(q + 0x10) == X(n - 1) && *(float*)(q + 0x20) == X(n - 1) + 50,
                "the point query answers the 40th zone's box");
            Assert.True(contains(pb, 0, inLast) != 0, "base 0 contains a point in the 40th zone");
            Assert.True(contains(pb, 0, between) == 0, "base 0 does not contain a point between zones");

            setEnabled.Invoke(null, [false]);
            rebuild(pb, (nint)world);
            Assert.Equal(16ul, *(ulong*)(pb + 0x1338), "merge off: the game's 16 zones");
            Assert.Equal(16u, *(uint*)(pb + 0x24), "merge off: the game's 16 bases");
            region(r, pb, inLast);
            Assert.True(r[0] != 0, "merge off: the 40th zone is in no base, as in the game");
        }
        finally
        {
            setEnabled.Invoke(null, [true]);  // also asks the game to rebuild from its own world at its next update
            System.Runtime.InteropServices.NativeMemory.Free(world);
            System.Runtime.InteropServices.NativeMemory.Free(zones);
        }
    }

    [KilnTest]
    public void Kiln_reports_the_new_limits()
    {
        Assert.True(WorldProgress.Available, "kiln.quests' world progress");
        var table = Assert.NotNull(Assets.Reflect(Balancing));
        var seen = 0;
        for (var level = 0; level < table.Count("altarsPerFlameLevel"); level++)
        {
            if (WorldProgress.LimitsAt(level) is not { } limits) continue;
            seen++;
            Assert.Equal(Convert.ToInt32(table[$"altarsPerFlameLevel[{level}]"]), limits.Altars, $"WorldProgress.LimitsAt({level}).Altars");
            Assert.Equal(Convert.ToInt32(table[$"waterSourcesPerFlameLevel[{level}]"]), limits.WaterSources, $"WorldProgress.LimitsAt({level}).WaterSources");
        }
        Assert.True(seen > 0, "WorldProgress.LimitsAt answered for at least one Flame level");
    }
}
