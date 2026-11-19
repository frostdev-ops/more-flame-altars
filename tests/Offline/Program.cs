// SPDX-License-Identifier: MIT
// Managed regression checks only: no game, native hooks or server is started.
using System.Reflection;
using Kiln;
using MoreFlameAltars;
using ModSettings = MoreFlameAltars.Settings;

var modType = typeof(MoreFlameAltarsMod);
const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
var checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new Exception(name);
    checks++;
}
object? Call(object mod, string method, params object?[] args) => modType.GetMethod(method, instance)!.Invoke(mod, args);
void Reject(Action decode, string name)
{
    try { decode(); }
    catch (NetFormatException) { checks++; return; }
    throw new Exception(name);
}
AltarsLimits Limits(AltarsLimits message)
{
    var writer = new NetWriter();
    message.Encode(writer);
    var reader = new NetReader(writer.WrittenSpan);
    return AltarsLimits.Decode(ref reader);
}
AltarsState State(AltarsState message)
{
    var writer = new NetWriter();
    message.Encode(writer);
    var reader = new NetReader(writer.WrittenSpan);
    return AltarsState.Decode(ref reader);
}
AltarsState Snapshot(int altars) => new(false, true, false, true, "", 1, 20, 1, 16, 0, 1,
    [16, 0, 0, 0, 0, 1, 100], [altars, 0, 0, 0, 0, 1, 100], [16, 16], [1, 1], [40, 80, 120, 160, 160]);

foreach (var method in new[] { "SetCommand", "FlameCommand" })
{
    var command = modType.GetMethod(method, instance)!.GetCustomAttribute<CommandAttribute>()!;
    Check(command.Node == "more-flame-altars.manage" && command.Permission == CommandPermission.Admin,
        $"{method} must enforce the window's admin permission node");
}

Check(Limits(new AltarsLimits(true, [1, 16, 32, 128])).ByLevel.SequenceEqual(new byte[] { 1, 16, 32, 128 }), "valid limits up to 128");
Check(Limits(new AltarsLimits(true, [128], true)).Merge, "the merge flag survives the wire");
Check(!Limits(new AltarsLimits(true, [20])).Merge, "no merge unless the server says so");
Reject(() => Limits(new AltarsLimits(true, [129])), "server must not bypass cap 128");
Reject(() => Limits(new AltarsLimits(false, [16], true)), "merged bases without altars above 16");
Reject(() => Limits(new AltarsLimits(false, [17])), "normal server must not bypass cap 16");
Reject(() => Limits(new AltarsLimits(true, [0])), "zero limit");
Reject(() => Limits(new AltarsLimits(true, [])), "empty limits");
Check(State(Snapshot(12)).Saved[0] == 12, "valid state round trip");
Reject(() => State(Snapshot(12) with { WaterByLevel = [] }), "mismatched arrays would crash drawing");
Reject(() => State(Snapshot(12) with { FlameLevel = int.MinValue }), "negative level would index before array");
Reject(() => State(Snapshot(12) with { Saved = [16, 0, 0, 0, 2, 1, 100] }), "boolean must be 0 or 1");
Reject(() => State(Snapshot(12) with { Saved = [16, 0, 0, 0, 1, 2, 100] }), "mergeBases must be 0 or 1");
Check(State(Snapshot(12) with { Saved = [16, 0, 0, 0, 0, 1, 200] }).Saved[6] == 200, "buildArea 200 round trip");
Reject(() => State(Snapshot(12) with { Saved = [16, 0, 0, 0, 0, 1, 201] }), "buildArea 201 is past the cap");
Reject(() => State(Snapshot(12) with { Saved = [16, 0, 0, 0, 0, 1, 99] }), "buildArea below the game's 100");
Check(State(Snapshot(12) with { AreaByAltarLevel = [80, 160, 240, 320, 320] }).AreaByAltarLevel.SequenceEqual(new[] { 80, 160, 240, 320, 320 }),
    "the build area by altar level survives the wire");
Check(State(Snapshot(12) with { AreaByAltarLevel = [] }).AreaByAltarLevel.Length == 0, "an unreadable build area is sent as empty");
Reject(() => State(Snapshot(12) with { AreaByAltarLevel = [-1] }), "a negative build area");
Reject(() => State(Snapshot(12) with { AreaByAltarLevel = new int[17] }), "more build-area levels than the game has room for");
Check(State(Snapshot(128)).Saved[0] == 128, "128 altars round trip");
Reject(() => State(Snapshot(129)), "129 altars is past the cap");

// A result and its state arrive separately; a render frame may run between them.
var window = (UiWindow)Activator.CreateInstance(typeof(UiWindow), instance, null,
    ["offline", (Action<UiDraw>)(_ => { }), new UiWindowOptions { StartOpen = true }, null], null)!;
var uiMod = new MoreFlameAltarsMod();
modType.GetField("_window", instance)!.SetValue(uiMod, window);
Call(uiMod, "OnState", Snapshot(16));
Call(uiMod, "OnResult", new AltarsResult(true, "Saved"));
var edit = modType.GetField("_edit", instance)!;
if (edit.GetValue(uiMod) is null) edit.SetValue(uiMod, Snapshot(16).Saved.Clone()); // an intervening draw
Call(uiMod, "OnState", Snapshot(12));
Check(((int[])edit.GetValue(uiMod)!)[0] == 12, "save response must replace stale edit values");
Call(uiMod, "ResetWindow");
Check(!window.Open && edit.GetValue(uiMod) is null && modType.GetField("_state", instance)!.GetValue(uiMod) is null,
    "leaving a server must close and clear its window");

var root = Path.Combine(Path.GetTempPath(), "mfa-offline-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var paths = typeof(Config).Assembly.GetType("Kiln.KilnPaths")!;
var directory = paths.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)!;
var previous = directory.GetValue(null);
directory.SetValue(null, root);
try
{
    var cfg = Config.Register<ModSettings>(name: "offline");
    var mod = new MoreFlameAltarsMod();
    modType.GetField("_cfg", instance)!.SetValue(mod, cfg);
    (bool Ok, string Text) Save(int[] values) => ((bool, string))Call(mod, "SaveSettings", values)!;
    var before = File.ReadAllText(cfg.FilePath);
    Check(!Save([12, 2, 10, 50, 0, 1, 100]).Ok, "invalid numeric setting must be refused");
    Check(File.ReadAllText(cfg.FilePath) == before, "invalid batch must leave every file value unchanged");
    Check(!Save([12, 2, 10, 5, 2, 1, 100]).Ok, "non-boolean switch must be refused");
    Check(!Save([12, 2, 10, 5, 1, 2, 100]).Ok, "non-boolean mergeBases must be refused");
    Check(!Save([129, 2, 10, 5, 1, 1, 100]).Ok, "129 altars must be refused");
    Check(!Save([12, 2, 10, 5, 1, 1, 201]).Ok, "buildArea 201 must be refused");
    Check(!Save([12, 2, 10, 5, 1, 1, 99]).Ok, "buildArea 99 must be refused");
    Check(File.ReadAllText(cfg.FilePath) == before, "invalid boolean must not partially save");
    Check(!Save([12]).Ok, "short settings must be refused");
    Check(!Save([12, 2, 10, 5, 1, 1]).Ok, "settings without buildArea (a 0.3.0 window) must be refused");
    Check(Save([128, 127, 10, 5, 1, 0, 200]).Ok, "128 altars with beyondSixteen and buildArea 200 must persist");
    Check(Convert.ToInt32(cfg.Entries.First(e => e.Path == "buildArea").FileValue) == 200, "buildArea 200 is in the file");
    Check(Convert.ToInt32(cfg.Entries.First(e => e.Path == "altars").FileValue) == 128 &&
          !Convert.ToBoolean(cfg.Entries.First(e => e.Path == "mergeBases").FileValue), "128 and mergeBases off are in the file");
    Check(Save([12, 2, 10, 5, 1, 1, 150]).Ok, "valid settings must persist");
    Check(cfg.Value.Altars == 16 && cfg.Value.BeyondSixteen == false && cfg.Value.BuildArea == 100, "restart settings must keep running values");
    var pending = cfg.Entries.ToDictionary(e => e.Path, e => e.FileValue);
    Check(Convert.ToInt32(pending["altars"]) == 12 && Convert.ToBoolean(pending["beyondSixteen"]), "saved values pending restart");

    // Restoring a RequiresRestart entry through TrySet emits no event in dev.28.
    modType.GetField("ServerSide", statics)!.SetValue(null, (Func<bool>)(() => true));
    modType.GetField("_observedSettings", instance)!.SetValue(mod, new int[] { 12, 2, 10, 5, 1, 1, 150 });
    Check(cfg.TrySet("altars", 16, out _), "restore running altar setting");
    Call(mod, "CheckSettings", default(TickInfo));
    Check(((int[])modType.GetField("_observedSettings", instance)!.GetValue(mod)!)[0] == 16,
        "tick fallback observes restoring a restart setting without a config event");

    // A directory at the destination makes the SDK's atomic rename fail, even when running as root.
    var blocked = Path.Combine(root, "blocked.json");
    Directory.CreateDirectory(blocked);
    typeof(ConfigFile).GetProperty("FilePath")!.SetValue(cfg, blocked);
    Check(!Save([14, 2, 10, 5, 1, 1, 100]).Ok, "an SDK write failure must not be reported as saved");
}
finally
{
    typeof(Config).GetMethod("Reset", statics)!.Invoke(null, null);
    directory.SetValue(null, previous);
    Directory.Delete(root, recursive: true);
}
// The hook's answer (AltarLimitHook.Answer): the live rule on the game's number, whatever this start's table holds.
{
    var asm = modType.Assembly;
    var ruleType = asm.GetType("MoreFlameAltars.AltarRule")!;
    var hookType = asm.GetType("MoreFlameAltars.AltarLimitHook")!;
    var rulesType = hookType.GetNestedType("Rules", BindingFlags.NonPublic)!;
    object Rule(int atLeast, int extra, int cap) => Activator.CreateInstance(ruleType, atLeast, extra, cap)!;
    object Rules(object boot, object live) => Activator.CreateInstance(rulesType, boot, live)!;
    var answer = hookType.GetMethod("Answer", statics, [rulesType, typeof(uint), typeof(int)])!;
    int Answer(object rules, uint level, int table) => (int)answer.Invoke(null, [rules, level, table])!;
    var start16 = Rule(16, 0, 16);   // a start with the defaults: the table holds 16 at every level
    Check(Answer(Rules(start16, Rule(32, 0, 32)), 1, 16) == 32, "beyondSixteen switched on in a running start answers 32 at once");
    Check(Answer(Rules(start16, Rule(20, 0, 32)), 12, 16) == 20, "a raised number applies to every level at once");
    Check(Answer(Rules(start16, Rule(1, 0, 16)), 1, 16) == 2, "lowering again answers the game's own number (2 at level 1)");
    Check(Answer(Rules(start16, Rule(1, 2, 32)), 1, 16) == 4, "extraAltars applies to the game's number, not to the raised table");
    Check(Answer(Rules(start16, Rule(32, 0, 32)), 1, 2) == 32, "a start whose boot patch did not run (the game's 2) still answers the rule");
    Check(Answer(Rules(start16, Rule(1, 0, 16)), 1, 5) == 5, "a table that matches neither rule (a game update) is never answered below");
    Check(Answer(Rules(start16, Rule(1, 0, 16)), 1, 20) == 16, "an unknown table value above 16 is answered as the game would: 16");
    Check(Answer(Rules(start16, Rule(32, 0, 32)), 1, 40) == 32, "an unknown table value never takes the answer past the cap of 32");
    Check(Answer(Rules(start16, Rule(32, 0, 32)), 40, 16) == 32, "levels past the list use the last level");
    Check(Answer(Rules(start16, Rule(128, 0, 128)), 1, 16) == 128, "128 altars at Flame level 1");
    Check(Answer(Rules(start16, Rule(1, 127, 128)), 11, 16) == 128, "extraAltars 127 stops at the cap of 128");
    Check(Answer(Rules(Rule(128, 0, 128), Rule(128, 0, 128)), 1, 128) == 128, "a start whose table holds 128 answers 128");
    Check(Answer(Rules(Rule(32, 0, 32), Rule(32, 0, 16)), 3, 32) == 16, "beyondSixteen switched off again caps at 16 at once");
}

// The boot patch: buildArea scales the three sides of buildzoneSizesPerAltarLevel, only above 100, never past the cap.
{
    var patch = modType.GetMethod("Patch", statics)!;
    ResourcePatchBlock? Block(ModSettings s) => (ResourcePatchBlock?)patch.Invoke(null, [s]);
    var area = new[] { "buildzoneSizesPerAltarLevel[*].x", "buildzoneSizesPerAltarLevel[*].y", "buildzoneSizesPerAltarLevel[*].z" };
    var none = new ModSettings { Altars = 1 };
    Check(Block(none) is null, "the lowest settings change nothing (no patch)");
    Check(Block(new ModSettings()) is { } d && !d.Set.Keys.Any(k => k.StartsWith("buildzone")), "buildArea 100 leaves the build area alone");
    var doubled = Block(new ModSettings { Altars = 1, BuildArea = 200 });
    Check(doubled is not null && doubled.Set.Count == 3 && area.All(doubled.Set.ContainsKey), "buildArea 200 alone patches the three sides and nothing else");
    Check(area.All(k => doubled!.Set[k].Mul == 2.0 && doubled.Set[k].RaiseOnly && doubled.Set[k].Add is null && doubled.Set[k].Value is null),
        "buildArea 200: the game's size times 2, never below the game's");
    Check(Block(new ModSettings { Altars = 1, BuildArea = 150 })!.Set[area[0]].Mul == 1.5, "buildArea 150: times 1.5");
    Check(Block(new ModSettings { Altars = 1, BuildArea = 500 })!.Set[area[0]].Mul == 2.0, "a value past the cap is held at 200 (the file's range is checked too)");
    Check(Block(new ModSettings { Altars = 1, BuildArea = 50 }) is null, "a value below 100 never shrinks the game's build area");
    var both = Block(new ModSettings { BuildArea = 120, WaterSources = 10 })!;
    Check(both.Set.ContainsKey("altarsPerFlameLevel[*]") && both.Set.ContainsKey("waterSourcesPerFlameLevel[*]") && both.Set.Count == 5,
        "the build area goes into the same patch as the altars and water sources");
    Check(both.Resource == new Guid("82706b40-61b1-4b8f-8b23-dcec6971bda1") && both.Type == "keen::BalancingTable", "the patch names the balancing table");
    var cap = (int)modType.GetField("BuildAreaCap", statics | BindingFlags.Public)!.GetValue(null)!;
    Check(cap == 200, "the cap is 200 percent");
    var range = typeof(ModSettings).GetProperty("BuildArea")!.GetCustomAttribute<RangeAttribute>()!;
    Check(Convert.ToInt32(range.Min) == 100 && Convert.ToInt32(range.Max) == cap, "the config file's range is 100 to the cap");
    var describe = (string)modType.GetMethod("Describe", statics)!.Invoke(null, [new ModSettings { Altars = 1, BuildArea = 200 }])!;
    Check(describe.Contains("200%") && describe.Contains("80 to 320 m"), $"the words name the percent and the sizes ({describe})");
    var differs = modType.GetMethod("Differs", statics)!;
    bool Differs(int[] a, int[] b, bool hooked) => (bool)differs.Invoke(null, [a, b, hooked])!;
    Check(Differs([16, 0, 0, 0, 0, 1, 100], [16, 0, 0, 0, 0, 1, 200], hooked: true), "a different build area matters with the hook in");
    Check(!Differs([16, 0, 0, 0, 0, 1, 100], [20, 0, 0, 0, 1, 1, 100], hooked: true), "with the hook in, altar settings follow the server");
}

Console.WriteLine($"{checks} offline regression checks passed.");
