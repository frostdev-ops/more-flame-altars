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
    [16, 0, 0, 0, 0], [altars, 0, 0, 0, 0], [16, 16], [1, 1]);

foreach (var method in new[] { "SetCommand", "FlameCommand" })
{
    var command = modType.GetMethod(method, instance)!.GetCustomAttribute<CommandAttribute>()!;
    Check(command.Node == "more-flame-altars.manage" && command.Permission == CommandPermission.Admin,
        $"{method} must enforce the window's admin permission node");
}

Check(Limits(new AltarsLimits(true, [1, 16, 32])).ByLevel.SequenceEqual(new byte[] { 1, 16, 32 }), "valid limits");
Reject(() => Limits(new AltarsLimits(true, [33])), "server must not bypass cap 32");
Reject(() => Limits(new AltarsLimits(false, [17])), "normal server must not bypass cap 16");
Reject(() => Limits(new AltarsLimits(true, [0])), "zero limit");
Reject(() => Limits(new AltarsLimits(true, [])), "empty limits");
Check(State(Snapshot(12)).Saved[0] == 12, "valid state round trip");
Reject(() => State(Snapshot(12) with { WaterByLevel = [] }), "mismatched arrays would crash drawing");
Reject(() => State(Snapshot(12) with { FlameLevel = int.MinValue }), "negative level would index before array");
Reject(() => State(Snapshot(12) with { Saved = [16, 0, 0, 0, 2] }), "boolean must be 0 or 1");

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
    Check(!Save([12, 2, 10, 50, 0]).Ok, "invalid final numeric setting must be refused");
    Check(File.ReadAllText(cfg.FilePath) == before, "invalid batch must leave every file value unchanged");
    Check(!Save([12, 2, 10, 5, 2]).Ok, "non-boolean switch must be refused");
    Check(File.ReadAllText(cfg.FilePath) == before, "invalid boolean must not partially save");
    Check(!Save([12]).Ok, "short settings must be refused");
    Check(Save([12, 2, 10, 5, 1]).Ok, "valid settings must persist");
    Check(cfg.Value.Altars == 16 && cfg.Value.BeyondSixteen == false, "restart settings must keep running values");
    var pending = cfg.Entries.ToDictionary(e => e.Path, e => e.FileValue);
    Check(Convert.ToInt32(pending["altars"]) == 12 && Convert.ToBoolean(pending["beyondSixteen"]), "saved values pending restart");

    // A directory at the destination makes the SDK's atomic rename fail, even when running as root.
    var blocked = Path.Combine(root, "blocked.json");
    Directory.CreateDirectory(blocked);
    typeof(ConfigFile).GetProperty("FilePath")!.SetValue(cfg, blocked);
    Check(!Save([14, 2, 10, 5, 1]).Ok, "an SDK write failure must not be reported as saved");
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
    Check(Answer(Rules(start16, Rule(32, 0, 32)), 40, 16) == 32, "levels past the list use the last level");
    Check(Answer(Rules(Rule(32, 0, 32), Rule(32, 0, 16)), 3, 32) == 16, "beyondSixteen switched off again caps at 16 at once");
}

Console.WriteLine($"{checks} offline regression checks passed.");
