// SPDX-License-Identifier: MIT
// More than 16 altars as one base (mergeBases, with beyondSixteen; off unless both are on). The technique is XHL's, from
// "XHL-Unlimited-Flame-Altars" 2.1.0 (MIT): read statically, nothing of it is run or shipped here, this is our own code.
//
// The game's base system (PlayerBasesSystem, the same code on server build 26430253 and client build 26430625) keeps the
// world's base zones in a list with room for 16 and no allocator, so zone 17 and up are dropped, and it has room for 16
// bases. Altars past 16 are then build-only. With mergeBases:
//   - the zone list gets storage of our own when the world has more than 16 base zones (the list's resize function);
//   - the game's rebuild fills it, and adding a 17th base record is skipped (the game does not check, and record 16 would
//     overwrite the zone list);
//   - after the rebuild, when there are more than 16 zones, all zones become ONE base: base 0 spans all of them;
//   - the game's four "which base is this point in" queries answer base 0 for a point inside any altar's zone. Three
//     are detours: the game's own answer first, the zone list read only when it says "no base". The fourth,
//     BasesContains, is a mid hook at its first instruction, because the game's callers of that one keep values in
//     registers a detour's handler overwrites (ContainsEntry below; until 0.4.2, in a world of 2 to 16 bases, this cost
//     every base but the game's base 0 its Magic chests); for a merged base 0 it answers from the zone list, which holds
//     every zone the game's own answer would look at;
//   - on a client, the per-base UI info's lists (room for 32 and 64, no allocator) get storage of our own too, and so
//     do copies the game makes of them; no other list of the game that those two resize functions serve does.
// What it costs: with more than 16 zones there is one base, so the base list shows one base, every base chest is shared by
// all altars, and the bases' comfort, beds and the rest are one base's. Nothing is checked in a real game yet.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Kiln;

namespace MoreFlameAltars;

internal static unsafe class BaseMergeHook
{
    // PlayerBasesSystem (both exes): base records at +0x30, 0x130 bytes each, room for 16, the count at +0x24; a record's
    // box (min x y z, pad, max x y z, pad) at +0xf0 and its zone mask (one bit per zone in the list) at +0x110.
    private const int BaseCount = 0x24, Records = 0x30, RecordSize = 0x130, RecordBox = 0xf0, RecordMask = 0x110, GameBases = 16;
    // The zone list: a vector at +0x1330 (ptr, count, capacity, grow callback, allocator, 16 inline 32-byte boxes at +0x30).
    private const int ZoneList = 0x1330, ZoneSize = 32, VecPtr = 0, VecCount = 8, VecCapacity = 0x10, VecGrow = 0x18, VecAllocator = 0x20;
    // The world's base zones, which the rebuild copies: a (ptr, count) pair at +0x15fa278 of the world it is given.
    private const long WorldZones = 0x15fa278;
    private const int ZoneVersion = 0x20;  // the world zone version the base system last rebuilt from (u32)
    private const ulong MaxZones = 4096;  // the world list holds at most 255; anything above is not a zone count

    /// <summary>A kind of list whose resize function the mod hooks. <paramref name="UiLists"/>: for the client's two
    /// resize functions, where their lists sit in an object of the per-base UI info (empty for the zone list).</summary>
    internal sealed record VecKind(int ElementSize, int InlineOffset, ulong InlineCapacity, int[] UiLists);

    /// <summary>Storage of ours has room for at least this many elements: the world holds at most 255 zones, so a list
    /// grows once and a reader that saw the old pointer never meets a count past its storage.</summary>
    private const ulong MinOwned = 256;

    internal static readonly VecKind BaseZones = new(ZoneSize, 0x30, 16, []);
    // The client's two resize functions (0x300e70 for 0x48-byte elements, 0x300ca0 for 4-byte ones, the inline room fixed
    // in their code) serve many lists of the game besides the per-base UI info. The per-base UI info (client build
    // 26430625): an array with room for 32 objects of 0xbc8 bytes, stored right behind its header (ptr, count, capacity
    // 32, grow, allocator 0, storage at +0x28; set up by 0x836640, rebuilt by 0x31cf20), each object holding a base's
    // zone records at +0x28 and its two index lists at +0x958 and +0xa88, none with an allocator.
    internal static readonly VecKind UiZones = new(0x48, 0x28, 32, [0x28]);              // client: a base's zone records
    internal static readonly VecKind UiZoneIndex = new(4, 0x28, 64, [0x958, 0xa88]);   // client: a base's zone indices
    private const int UiObject = 0xbc8, UiSlots = 32, UiArrayStorage = 0x28;

    /// <summary>A guarded read of 8 bytes (null where nothing can be read): the per-base UI info check reads around a
    /// list the game hands the resize function. Kiln's Memory.TryRead in the game; the offline checks set their own.</summary>
    internal static Func<nuint, ulong?> ReadWord = a =>
    {
        try { return Memory.TryRead<ulong>(a, out var v) ? v : null; }
        catch (Exception) { return null; }  // kiln.hooks off, or no mod context: nothing to read
    };

    private sealed class Owned(nuint buffer, ulong capacity, VecKind kind)
    {
        public nuint Buffer = buffer;
        public ulong Capacity = capacity;
        public readonly VecKind Kind = kind;
    }

    private static readonly List<HookHandle> _hooks = [];
    private static readonly ConcurrentDictionary<nuint, Owned> _owned = new();    // vectors whose storage is ours
    private static readonly ConcurrentDictionary<nuint, byte> _zoneLists = new();  // base systems' zone lists seen
    private static readonly ConcurrentDictionary<nuint, byte> _merged = new();     // base systems merged into one base now
    private static volatile bool _enabled;
    private static long _rebuilds, _merges, _skippedBases, _queryHits, _staleLists;

    /// <summary>The game's functions that get a detour (a managed handler that calls the original).</summary>
    internal static readonly string[] Detoured = ["BasesRebuild", "BasesAdd", "BasesRegion", "BasesPoint", "BasesWater"];

    /// <summary>The game's function that gets a mid hook at its first instruction instead (<see cref="ContainsEntry"/>):
    /// its callers keep values in registers across the call that a detour's handler does not give back.</summary>
    internal const string AtEntry = "BasesContains";

    public static bool Installed => _hooks.Count > 0;
    public static bool Enabled => _enabled;
    public static long Rebuilds => Interlocked.Read(ref _rebuilds);
    public static long Merges => Interlocked.Read(ref _merges);
    public static long SkippedBases => Interlocked.Read(ref _skippedBases);
    public static long QueryHits => Interlocked.Read(ref _queryHits);
    /// <summary>Lists of ours found replaced by another list at the same address (see <see cref="Owns"/>).</summary>
    public static long StaleLists => Interlocked.Read(ref _staleLists);
    public static int MergedNow => _merged.Count;
    /// <summary>The base system of the last rebuild (for the lab check, which rebuilds it from a made-up world).</summary>
    internal static nuint LastSystem;

    /// <summary>Switches the merge on or off. Off: the hooks stay in, the next rebuild gives the zone list its 16 slots back
    /// and the game's own bases return. A change asks every base system seen for a rebuild at its next update (the game
    /// rebuilds when the world's zone version differs from the one at +0x20; XHL retries the same way).
    /// Called on the tick thread, the thread that also creates and destroys the game's base systems, so a system in the
    /// list is not freed while its version is written.</summary>
    public static void SetEnabled(bool on)
    {
        if (_enabled == on) return;
        _enabled = on;
        foreach (var vec in _zoneLists.Keys) Interlocked.Decrement(ref *(int*)(vec - ZoneList + ZoneVersion));
    }

    /// <summary>Installs the hooks once (tick thread or OnLoad). False with the reason when Kiln or this game build cannot.</summary>
    public static bool Install(out string why)
    {
        why = "";
        if (_hooks.Count > 0) return true;
        try
        {
            if (!Hooks.Available) { why = "Kiln's hooks are not available on this game build"; return false; }
            GameSymbols.Load();
            var at = new Dictionary<string, nuint>();
            foreach (var name in Detoured.Append(AtEntry))
            {
                if (!Symbols.TryGet("more-flame-altars/" + name, out var a)) { why = $"the game's {name} function was not found on this game build"; return false; }
                at[name] = a;
            }
            // the zone list's grow callback, set by the base system's constructor; it jumps to the list's resize function
            const string ctor = "48 8D 81 60 13 00 00 48 89 81 30 13 00 00 48 8D 05 {grow:rel32} 48 89 81 48 13 00 00 48 8B C1 " +
                                "4C 89 91 38 13 00 00 48 C7 81 40 13 00 00 10 00 00 00";
            var targets = new List<(nuint At, DetourHandler Handler)>
            {
                (ResizeBehind((nuint)Symbols.Hole(ctor, Symbols.Find(ctor), "grow")), c => Resize(c, BaseZones)),
                (at["BasesRebuild"], Rebuild), (at["BasesAdd"], AddBase),
                (at["BasesRegion"], Region), (at["BasesPoint"], Point), (at["BasesWater"], Water),
            };
            if (Mods.Side == ModSide.Client)
            {
                // the client's per-base UI info: an array of objects, each with a list of 32 zone records and two of 64 indices
                const string ui = "4C 8D 0D {zones:rel32} 49 8B D7 4C 8D 05 {index:rel32} 48 89 69 F8 33 C0 48 89 29 48 89 41 08 " +
                                  "48 8D 41 38 48 89 41 10 48 8D 81 68 09 00 00 48 89 81 40 09 00 00";
                var match = Symbols.Find(ui);
                targets.Add((ResizeBehind((nuint)Symbols.Hole(ui, match, "zones")), c => Resize(c, UiZones)));
                targets.Add((ResizeBehind((nuint)Symbols.Hole(ui, match, "index")), c => Resize(c, UiZoneIndex)));
            }
            foreach (var (target, handler) in targets) _hooks.Add(Hooks.Detour(target, handler));
            _hooks.Add(Hooks.Mid(at[AtEntry], ContainsEntry));
            return true;
        }
        catch (Exception e)
        {
            foreach (var h in _hooks) h.Remove();  // all or nothing
            _hooks.Clear();
            why = e.Message;
            return false;
        }
    }

    /// <summary>The game's grow callbacks for these lists return when the new size fits and otherwise jump to the list's
    /// resize function (jmp rel32 at +0x3e, the same on both exes): that function.</summary>
    private static nuint ResizeBehind(nuint callback)
    {
        var jmp = callback + 0x3e;
        if (Memory.Read<byte>(jmp) != 0xE9) throw new InvalidOperationException("a list's grow callback changed shape on this game build");
        return (nuint)((long)jmp + 5 + Memory.Read<int>(jmp + 1));
    }

    /// <summary>The mod unloads: every list whose storage is ours goes back to the game's own (the hooks are removed with the
    /// mod, and the game's own code would free our storage with an allocator it does not have).</summary>
    public static void Release()
    {
        _enabled = false;
        // Kiln calls OnUnload before it drains the handlers: a query may still read our storage, so none of it is freed
        // here (a few KB stay allocated until the process ends)
        // only lists that still hold our storage: a list destroyed without its resize to 0 left an entry whose address
        // may hold another list now, or nothing. Guarded reads when Kiln's reader answers here (a probe of a local),
        // else plain ones, as before 0.4.3: a list of ours left on our storage would be freed by the game without an allocator
        ulong probe = 0x4B494C4E;
        var guarded = ReadWord((nuint)(&probe)) == probe;
        foreach (var (vec, o) in _owned)
        {
            var ours = guarded
                ? ReadWord(vec + VecPtr) == (ulong)o.Buffer && ReadWord(vec + VecCapacity) == o.Capacity
                : *(nuint*)(vec + VecPtr) == o.Buffer && *(ulong*)(vec + VecCapacity) == o.Capacity;
            if (ours) Shrink(vec, o, free: false);
            else Forget(vec, o);
        }
        // a merged base keeps base 0 with a mask the 16 zones left can hold, and the game is asked to rebuild its bases
        foreach (var pb in _merged.Keys) *(uint*)(pb + Records + RecordMask) = 0xFFFF;
        foreach (var vec in _zoneLists.Keys) Interlocked.Decrement(ref *(int*)(vec - ZoneList + ZoneVersion));
        _merged.Clear();
    }

    /// <summary>Storage a list grew out of. The game's other threads may still read the old one for a moment, so it is
    /// kept until the mod unloads (a few KB at most: lists grow only when the world gets more zones).</summary>
    private static readonly List<nuint> Retired = [];

    // ---- the lists' resize function: resize(vector, capacity, mayFail) -> bool

    private static nuint Resize(HookCall call, VecKind kind) =>
        Decide(call[0], (ulong)call[1], (byte)call[2] != 0, kind) ? 1 : call.CallOriginal();

    /// <summary>The resize of <paramref name="vec"/> to <paramref name="capacity"/>: true when the mod did it (the game's
    /// function then does not run), false for the game's own function. A list past its inline room without an allocator
    /// gets storage of ours only when it is one of the lists the mod is for: a base system's zone list, a list of the
    /// per-base UI info, or a list the game must grow and cannot (<paramref name="mayFail"/> false: the game's function
    /// would stop the game, which only a copy of one of our lists asks for, e.g. client 0xb17fb0). Every other list the
    /// game lets fail (mayFail) keeps the game's own answer: the game caps it at its inline room on purpose.</summary>
    internal static bool Decide(nuint vec, ulong capacity, bool mayFail, VecKind kind)
    {
        // capacity 0 on a base system's zone list: its destructor (server 0x207930 / 0x207ad0): forget the system
        if (capacity == 0 && _zoneLists.TryRemove(vec, out _)) _merged.TryRemove(vec - ZoneList, out _);
        if (Owns(vec, out var o))
        {
            if (capacity <= o.Kind.InlineCapacity) Shrink(vec, o, capacity);
            else if (capacity <= o.Capacity) *(ulong*)(vec + VecCount) = Math.Min(*(ulong*)(vec + VecCount), capacity);
            else Regrow(vec, o, capacity);
            return true;
        }
        if (!_enabled || capacity <= kind.InlineCapacity || capacity > MaxZones * 4) return false;
        if ((*(ulong*)(vec + VecAllocator) & ~1UL) != 0) return false;  // the game has an allocator for it
        var ours = kind.UiLists.Length == 0 ? _zoneLists.ContainsKey(vec) : !mayFail || InUiInfo(vec, kind);
        if (!ours) return false;
        Take(vec, kind, capacity);
        return true;
    }

    /// <summary>Whether <paramref name="vec"/> is one of <paramref name="kind"/>'s lists in an object of the client's
    /// per-base UI info: some object i (below the array's count) of an array whose header points at its own storage
    /// (+0x28) and has room for 32 holds the list at one of the kind's offsets. Reads are guarded (the candidates lie
    /// up to 100 KB below the list).</summary>
    internal static bool InUiInfo(nuint vec, VecKind kind)
    {
        foreach (var at in kind.UiLists)
        {
            for (var i = 0; i < UiSlots; i++)
            {
                var storage = vec - (nuint)at - (nuint)(i * UiObject);
                var header = storage - UiArrayStorage;
                if (ReadWord(header + VecPtr) != (ulong)storage || ReadWord(header + VecCapacity) != (ulong)UiSlots) continue;
                if (ReadWord(header + VecCount) is { } count && (ulong)i < count) return true;
            }
        }
        return false;
    }

    /// <summary>Whether the list at <paramref name="vec"/> is one of ours: it still points at our storage with our
    /// capacity (only the mod changes those of a list it owns, every resize of it comes here). An entry whose list is gone
    /// (destroyed without the resize to 0 that hands our storage back, e.g. a copy the game frees itself) while another
    /// list lives at that address now, on the stack or the heap, is dropped: acting on it would overwrite that list with
    /// old contents, report room it does not have, or hand our storage to a list that frees it with the game's
    /// allocator. Its storage is kept (a moved copy may still read it).</summary>
    private static bool Owns(nuint vec, out Owned o)
    {
        if (!_owned.TryGetValue(vec, out o!)) return false;
        if (*(nuint*)(vec + VecPtr) == o.Buffer && *(ulong*)(vec + VecCapacity) == o.Capacity) return true;
        Forget(vec, o);
        return false;
    }

    private static void Forget(nuint vec, Owned o)
    {
        if (!_owned.TryRemove(new KeyValuePair<nuint, Owned>(vec, o))) return;
        Interlocked.Increment(ref _staleLists);
        lock (Retired) Retired.Add(o.Buffer);
    }

    /// <summary>Moves the list into storage of ours with room for <paramref name="capacity"/>.</summary>
    private static void Take(nuint vec, VecKind kind, ulong capacity)
    {
        capacity = Math.Max(capacity, MinOwned);
        var count = Math.Min(*(ulong*)(vec + VecCount), capacity);
        var buffer = (nuint)NativeMemory.AllocZeroed((nuint)(capacity * (ulong)kind.ElementSize));
        var from = *(nuint*)(vec + VecPtr);
        if (from != 0 && count > 0) Buffer.MemoryCopy((void*)from, (void*)buffer, (long)(capacity * (ulong)kind.ElementSize), (long)(count * (ulong)kind.ElementSize));
        _owned[vec] = new Owned(buffer, capacity, kind);
        *(nuint*)(vec + VecPtr) = buffer;
        *(ulong*)(vec + VecCount) = count;
        *(ulong*)(vec + VecCapacity) = capacity;
    }

    private static void Regrow(nuint vec, Owned o, ulong capacity)
    {
        if (capacity <= o.Capacity) return;  // ours has room already (it is at least MinOwned)
        var size = (ulong)o.Kind.ElementSize;
        var count = Math.Min(*(ulong*)(vec + VecCount), capacity);
        var buffer = (nuint)NativeMemory.AllocZeroed((nuint)(capacity * size));
        if (count > 0) Buffer.MemoryCopy((void*)o.Buffer, (void*)buffer, (long)(capacity * size), (long)(count * size));
        *(nuint*)(vec + VecPtr) = buffer;
        *(ulong*)(vec + VecCount) = count;
        *(ulong*)(vec + VecCapacity) = capacity;
        var old = o.Buffer;
        o.Buffer = buffer;
        o.Capacity = capacity;
        lock (Retired) Retired.Add(old);
    }

    /// <summary>Gives the list its inline storage back, keeping what fits (all of it at most the inline room).</summary>
    private static void Shrink(nuint vec, Owned o, ulong? capacity = null, bool free = true)
    {
        var size = (ulong)o.Kind.ElementSize;
        var cap = Math.Min(capacity ?? o.Kind.InlineCapacity, o.Kind.InlineCapacity);
        var count = Math.Min(*(ulong*)(vec + VecCount), cap);
        var inline = vec + (nuint)o.Kind.InlineOffset;
        if (count > 0) Buffer.MemoryCopy((void*)o.Buffer, (void*)inline, (long)(o.Kind.InlineCapacity * size), (long)(count * size));
        *(nuint*)(vec + VecPtr) = cap == 0 ? 0 : inline;
        *(ulong*)(vec + VecCount) = count;
        *(ulong*)(vec + VecCapacity) = cap;
        _owned.TryRemove(vec, out _);
        // the destructor's resize to 0 is the one place nothing else reads the list; otherwise keep it for late readers
        if (free && cap == 0) NativeMemory.Free((void*)o.Buffer);
        else lock (Retired) Retired.Add(o.Buffer);
    }

    // ---- rebuild(baseSystem, world): the zone list from the world's zones, then the bases from the zones

    private static nuint Rebuild(HookCall call)
    {
        var pb = call[0];
        var world = call[1];
        if (pb == 0 || world == 0) return call.CallOriginal();
        Interlocked.Increment(ref _rebuilds);
        LastSystem = pb;
        var vec = pb + ZoneList;
        _zoneLists.TryAdd(vec, 0);
        if (!_enabled)
        {
            if (Owns(vec, out var mine)) Shrink(vec, mine);
            _merged.TryRemove(pb, out _);
            return call.CallOriginal();
        }
        var zones = *(ulong*)(world + (nuint)WorldZones + 8);
        if (zones > BaseZones.InlineCapacity && zones <= MaxZones && *(ulong*)(vec + VecCapacity) < zones)
        {
            if (Owns(vec, out var o)) Regrow(vec, o, zones);
            else if ((*(ulong*)(vec + VecAllocator) & ~1UL) == 0) Take(vec, BaseZones, zones);
        }
        var result = call.CallOriginal();
        var count = *(ulong*)(vec + VecCount);
        if (count > GameBases) Merge(pb, *(nuint*)(vec + VecPtr), count);
        else _merged.TryRemove(pb, out _);
        return result;
    }

    /// <summary>All zones become base 0: its box spans them all, its mask has every zone the mask can hold (32), the other
    /// records hold none, and the count is 1.</summary>
    private static void Merge(nuint pb, nuint zones, ulong count)
    {
        if (zones == 0) return;
        var box = stackalloc float[8];
        Buffer.MemoryCopy((void*)zones, box, 32, 32);
        for (ulong i = 1; i < count; i++)
        {
            var z = (float*)(zones + (nuint)(i * ZoneSize));
            for (var k = 0; k < 3; k++)
            {
                if (z[k] < box[k]) box[k] = z[k];
                if (z[4 + k] > box[4 + k]) box[4 + k] = z[4 + k];
            }
        }
        for (var b = 0; b < GameBases; b++) *(uint*)(pb + Records + (nuint)(b * RecordSize) + RecordMask) = 0;
        Buffer.MemoryCopy(box, (void*)(pb + Records + RecordBox), 32, 32);
        *(uint*)(pb + Records + RecordMask) = count >= 32 ? uint.MaxValue : (1u << (int)count) - 1;
        Thread.MemoryBarrier();
        *(uint*)(pb + BaseCount) = 1;
        _merged.TryAdd(pb, 0);
        Interlocked.Increment(ref _merges);
    }

    // add(baseSystem, zone, zoneIndex): a new base record. With 16 records there is no room for another one.
    private static nuint AddBase(HookCall call)
    {
        if (call[0] != 0 && *(uint*)(call[0] + BaseCount) >= GameBases)
        {
            Interlocked.Increment(ref _skippedBases);
            return 0;
        }
        return call.CallOriginal();
    }

    // ---- the game's queries: on "no base" for a merged base system, a point inside any zone is in base 0

    /// <summary>The lowest-numbered zone holding <paramref name="point"/> (min inclusive, max exclusive, as the game tests).</summary>
    private static bool Find(nuint pb, nuint point, out nuint zone)
    {
        zone = 0;
        if (pb == 0 || point == 0 || !_merged.ContainsKey(pb)) return false;
        var zones = *(nuint*)(pb + ZoneList + VecPtr);
        var count = *(ulong*)(pb + ZoneList + VecCount);
        if (zones == 0 || count > MaxZones) return false;
        var p = (float*)point;
        float x = p[0], y = p[1], z = p[2];
        for (ulong i = 0; i < count; i++)
        {
            var b = (float*)(zones + (nuint)(i * ZoneSize));
            if (x >= b[0] && y >= b[1] && z >= b[2] && b[4] > x && b[5] > y && b[6] > z)
            {
                zone = (nuint)b;
                Interlocked.Increment(ref _queryHits);
                return true;
            }
        }
        return false;
    }

    // contains(baseSystem rcx, baseIndex rdx, point r8) -> al, through a mid hook at its first instruction, not a detour.
    // Its two callers in the game (both exes) are AddToBaseStock (server 0x209b20, client 0x471220), with which
    // RebuildBaseStock files every Magic chest and water source under its base every frame, and the per-base sets of
    // one kind of entity (server 0x208940, client 0x470150, after every rebuild). Both walk the bases and keep their loop
    // index or the entity id in r11 and the point in xmm4/xmm5 across this call, because the game's compiler knows this
    // function never writes them. A detour's managed handler does (Kiln's adapter alone sets r11 before it): from 0.3.0
    // to 0.4.2, whenever the hooks were in (beyondSixteen and mergeBases, any number of altars), a world of 2 to 16 bases
    // had the Magic chests of the game's base 0 only in base storage, and base 0 is the game's numbering, not the first
    // altar placed (Wittie2m's report, his main altar's). The sets got r11 instead of the id in every base. A merged
    // world (one base) kept its chests.
    // A mid hook gives every register back as it found it. On a merged base system base 0 holds every zone of the list,
    // so a point in any of them answers true at once (the game's function looks at the first 16 zones only); every other
    // call runs the game's function unchanged.
    private static void ContainsEntry(ref Registers r)
    {
        if (r.Rdx != 0 || !Find(r.Rcx, r.R8, out _)) return;
        r.Rax = 1;
        r.TrampolineRsp = r.Rsp;  // back to the caller without running the function
    }

    // region(result, baseSystem, point) -> result; result: status u8 (0 = in a base, 0x1f = none), u32 at +4, base index u8 at +8
    private static nuint Region(HookCall call)
    {
        var result = call.CallOriginal();
        var r = call[0];
        if (r == 0 || *(byte*)r == 0 || !Find(call[1], call[2], out _)) return result;
        *(uint*)(r + 4) = 0;
        *(byte*)(r + 8) = 0;
        *(byte*)r = 0;
        return result;
    }

    // point(result, baseSystem, point) -> result; result: status u8, u32 at +4, the zone's box at +0x10
    private static nuint Point(HookCall call)
    {
        var result = call.CallOriginal();
        var r = call[0];
        if (r == 0 || *(byte*)r == 0 || !Find(call[1], call[2], out var zone)) return result;
        *(uint*)(r + 4) = 0;
        Buffer.MemoryCopy((void*)zone, (void*)(r + 0x10), 32, 32);
        *(byte*)r = 0;
        return result;
    }

    // water(result, boxes, baseSystem, point) -> result; on a hit the zone's box is added to the boxes vector
    private static nuint Water(HookCall call)
    {
        var result = call.CallOriginal();
        var r = call[0];
        var boxes = call[1];
        if (r == 0 || boxes == 0 || *(byte*)r == 0 || !Find(call[2], call[3], out var zone)) return result;
        var count = *(ulong*)(boxes + VecCount);
        var capacity = *(ulong*)(boxes + VecCapacity);
        if (count > capacity) return result;
        if (count == capacity)
        {
            var grow = *(nuint*)(boxes + VecGrow);
            if (grow == 0 || ((delegate* unmanaged<nuint, long, byte, byte>)grow)(boxes, (long)~count, 1) == 0) return result;
        }
        var at = *(nuint*)(boxes + VecPtr);
        if (at == 0 || *(ulong*)(boxes + VecCount) >= *(ulong*)(boxes + VecCapacity)) return result;
        Buffer.MemoryCopy((void*)zone, (void*)(at + (nuint)(count * ZoneSize)), 32, 32);
        *(ulong*)(boxes + VecCount) = count + 1;
        *(byte*)r = 0;
        return result;
    }
}

/// <summary>The mod's shipped sigdb fragment (sigdb/server.json or client.json), loaded once.</summary>
internal static class GameSymbols
{
    private static readonly object Gate = new();
    private static bool _loaded;

    public static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            if (!Capabilities.Is("kiln.hooks")) throw new InvalidOperationException("Kiln's hooks are off");
            Symbols.LoadShipped();
            _loaded = true;
        }
    }
}
