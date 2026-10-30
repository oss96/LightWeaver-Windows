using LightWeaver.Localization;
using LightWeaver.Player;

L.Initialize("en");
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

// --- Chord equality without WPF's Key enum -------------------------------------------------------
Check(PlayerActionDispatcher.SameChord("Ctrl+Right", "Ctrl+Right"), "the same chord matches");
Check(PlayerActionDispatcher.SameChord("Shift+Ctrl+Right", "Ctrl+Shift+Right"), "modifier order does not matter");
Check(PlayerActionDispatcher.SameChord("ctrl+right", "Ctrl+Right"), "case does not matter");
Check(PlayerActionDispatcher.SameChord("Oem6", "OemCloseBrackets"), "WPF's alias spelling of ] matches the canonical one");
Check(PlayerActionDispatcher.SameChord("Oem4", "OemOpenBrackets"), "and of [");
Check(PlayerActionDispatcher.SameChord("Add", "OemPlus") && PlayerActionDispatcher.SameChord("Subtract", "OemMinus"), "numpad + and - are the main-row keys");
Check(PlayerActionDispatcher.SameChord("Return", "Enter") && PlayerActionDispatcher.SameChord("Next", "PageDown"), "Return is Enter and Next is PageDown");
Check(!PlayerActionDispatcher.SameChord("Right", "Ctrl+Right"), "a modifier makes a different chord");
Check(!PlayerActionDispatcher.SameChord("Right", "Left"), "different keys differ");
Check(!PlayerActionDispatcher.SameChord("", "") && !PlayerActionDispatcher.SameChord(null, "Right"), "unbound never matches");
Check(!PlayerActionDispatcher.SameChord("Ctrl+", "Ctrl+"), "a chord with no key is unbound");

// --- The shared map -------------------------------------------------------------------------------
var map = PlayerActionDispatcher.Resolve(null);
Check(map.Count == Enum.GetValues<PlayerAction>().Length, "every action has a default");
Check(map[PlayerAction.SubtitleEarlier] == "Z" && map[PlayerAction.SubtitleLater] == "X", "subtitle offset defaults to Z and X");
var defaults = map.Values.Where(c => c.Length > 0).ToList();
Check(defaults.Count == defaults.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "no two actions share a default chord");
var saved = new Dictionary<string, string> { ["SubtitleEarlier"] = "Shift+Z", ["SeekForward"] = "", ["NoSuchAction"] = "Q" };
var rebound = PlayerActionDispatcher.Resolve(saved);
Check(rebound[PlayerAction.SubtitleEarlier] == "Shift+Z", "a saved chord replaces the default");
Check(rebound[PlayerAction.SeekForward] == "", "a cleared action stays unbound");
Check(rebound.Count == map.Count, "unknown names are ignored");
Check(PlayerActionDispatcher.DisplayName(PlayerAction.SubtitleLater) == "Subtitles 0.1 s later", "the new actions have names");
Check(PlayerActionDispatcher.DisplayChord("") == PlayerActionDispatcher.DisplayChord(null), "unbound displays the same either way");

// --- The wheel over the player --------------------------------------------------------------------
var wheel = new VolumeWheel();
Check(wheel.Steps(120) == 1 && wheel.Steps(-120) == -1, "a mouse notch is one volume step up or down");
Check(wheel.Steps(360) == 3, "a fast spin reports every notch");
Check(wheel.Steps(40) == 0 && wheel.Steps(40) == 0 && wheel.Steps(40) == 1, "touchpad deltas add up to a notch");
Check(wheel.Steps(100) == 0 && wheel.Steps(-100) == 0 && wheel.Steps(-20) == -1, "reversing drops the part of a notch the other way");
Check(wheel.Steps(0) == 0, "no delta, no step");

// --- The volume readout the wheel and the keys raise ----------------------------------------------
var vm = new LightWeaver.ViewModels.PlayerViewModel { Volume = 50 };
var feedback = 0;
vm.VolumeFeedback += () => feedback++;
PlayerActionDispatcher.Dispatch(PlayerAction.VolumeUp, vm);
PlayerActionDispatcher.Dispatch(PlayerAction.VolumeDown, vm);
PlayerActionDispatcher.Dispatch(PlayerAction.VolumeDown, vm);
Check(vm.Volume == 45 && feedback == 3, "every volume step shows the readout");
vm.Volume = 100;
feedback = 0;
PlayerActionDispatcher.Dispatch(PlayerAction.VolumeUp, vm);
Check(vm.Volume == 100 && feedback == 1, "a step at the limit still shows the readout");
var culture = System.Globalization.CultureInfo.CurrentCulture;
System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
vm.Volume = 44.6;
Check(vm.VolumeReadout == "45%", "the readout rounds to whole percent in invariant digits");
System.Globalization.CultureInfo.CurrentCulture = culture;

Console.WriteLine($"Shortcuts: {checks} checks passed");
