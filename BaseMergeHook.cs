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
//   - the game's four "which base is this point in" queries answer base 0 for a point inside any altar's zone (the game's
//     own answer first; the zone list is read only when the game says "no base");
//   - on a client, the per-base UI lists of zones (room for 32 and 64, no allocator) get storage of our own too.
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

    private sealed record VecKind(int ElementSize, int InlineOffset, ulong InlineCapacity, bool AnyWithoutAllocator);

    /// <summary>Storage of ours has room for at least this many elements: the world holds at most 255 zones, so a list
    /// grows once and a reader that saw the old pointer never meets a count past its storage.</summary>
    private const ulong MinOwned = 256;

    private static readonly VecKind BaseZones = new(ZoneSize, 0x30, 16, false);
    private static readonly VecKind UiZones = new(0x48, 0x28, 32, true);      // client: a base's zone records
    private static readonly VecKind UiZoneIndex = new(4, 0x28, 64, true);     // client: a base's zone indices

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
    private static long _rebuilds, _merges, _skippedBases, _queryHits;

    public static bool Installed => _hooks.Count > 0;
    public static bool Enabled => _enabled;
    public static long Rebuilds => Interlocked.Read(ref _rebuilds);
    public static long Merges => Interlocked.Read(ref _merges);
    public static long SkippedBases => Interlocked.Read(ref _skippedBases);
    public static long QueryHits => Interlocked.Read(ref _queryHits);
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
            foreach (var name in new[] { "BasesRebuild", "BasesAdd", "BasesContains", "BasesRegion", "BasesPoint", "BasesWater" })
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
                (at["BasesRebuild"], Rebuild), (at["BasesAdd"], AddBase), (at["BasesContains"], Contains),
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
        foreach (var (vec, o) in _owned) Shrink(vec, o, free: false);
        // a merged base keeps base 0 with a mask the 16 zones left can hold, and the game is asked to rebuild its bases
        foreach (var pb in _merged.Keys) *(uint*)(pb + Records + RecordMask) = 0xFFFF;
        foreach (var vec in _zoneLists.Keys) Interlocked.Decrement(ref *(int*)(vec - ZoneList + ZoneVersion));
        _merged.Clear();
    }

    /// <summary>Storage a list grew out of. The game's other threads may still read the old one for a moment, so it is
    /// kept until the mod unloads (a few KB at most: lists grow only when the world gets more zones).</summary>
    private static readonly List<nuint> Retired = [];

    // ---- the lists' resize function: resize(vector, capacity, mayFail) -> bool

    private static nuint Resize(HookCall call, VecKind kind)
    {
        var vec = call[0];
        var capacity = (ulong)call[1];
        // capacity 0 on a base system's zone list: its destructor (server 0x207930 / 0x207ad0): forget the system
        if (capacity == 0 && _zoneLists.TryRemove(vec, out _)) _merged.TryRemove(vec - ZoneList, out _);
        if (_owned.TryGetValue(vec, out var o))
        {
            if (capacity <= o.Kind.InlineCapacity) Shrink(vec, o, capacity);
            else if (capacity <= o.Capacity) *(ulong*)(vec + VecCount) = Math.Min(*(ulong*)(vec + VecCount), capacity);
            else Regrow(vec, o, capacity);
            return 1;
        }
        if (!_enabled || capacity <= kind.InlineCapacity || capacity > MaxZones * 4) return call.CallOriginal();
        if (!kind.AnyWithoutAllocator && !_zoneLists.ContainsKey(vec)) return call.CallOriginal();
        if ((*(ulong*)(vec + VecAllocator) & ~1UL) != 0) return call.CallOriginal();  // the game has an allocator for it
        Take(vec, kind, capacity);
        return 1;
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
            if (_owned.TryGetValue(vec, out var mine)) Shrink(vec, mine);
            _merged.TryRemove(pb, out _);
            return call.CallOriginal();
        }
        var zones = *(ulong*)(world + (nuint)WorldZones + 8);
        if (zones > BaseZones.InlineCapacity && zones <= MaxZones && *(ulong*)(vec + VecCapacity) < zones)
        {
            if (_owned.TryGetValue(vec, out var o)) Regrow(vec, o, zones);
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

    // contains(baseSystem, baseIndex, point) -> bool
    private static nuint Contains(HookCall call)
    {
        var result = call.CallOriginal();
        if ((byte)result != 0 || call[1] != 0) return result;
        return Find(call[0], call[2], out _) ? 1 : result;
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
