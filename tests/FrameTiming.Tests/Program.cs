using System.Diagnostics;
using System.Text.Json;
using LightWeaver.Diagnostics;
using LightWeaver.Imaging;
using LightWeaver.Navigation;
using LightWeaver.Player;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

// --- A steady 60 Hz animation ----------------------------------------------------------------
var steady = FrameStats.From("player_grow", 180.04, Enumerable.Repeat(1000.0 / 60, 11).ToArray());
Check(steady.Frames == 11, "every interval is a frame");
Check(steady.DurationMs == 180.0, "the duration is rounded to 0.1 ms: " + steady.DurationMs);
Check(steady.MeanMs == 16.7 && steady.P50Ms == 16.7 && steady.P95Ms == 16.7 && steady.MaxMs == 16.7, "a steady 60 Hz run reads 16.7 ms throughout");
Check(steady.Over60HzBudget == 0 && steady.Over30HzBudget == 0, "16.67 ms frames are within the 16.7 ms budget");

// --- Hitches ----------------------------------------------------------------------------------
double[] hitchy = [16, 17, 16, 50, 16, 16, 34, 16, 16, 16];
var stats = FrameStats.From("nav_morph_open", 240, hitchy);
Check(stats.Frames == 10, "ten intervals, ten frames");
Check(stats.MeanMs == 21.3, "the mean is the plain average: " + stats.MeanMs);
Check(stats.P50Ms == 16, "the median is nearest-rank: " + stats.P50Ms);
Check(stats.P95Ms == 50, "p95 of ten frames is the slowest: " + stats.P95Ms);
Check(stats.MaxMs == 50, "the max is the slowest frame");
Check(stats.Over60HzBudget == 3, "three frames missed 60 Hz (17, 34, 50): " + stats.Over60HzBudget);
Check(stats.Over30HzBudget == 2, "two frames missed 30 Hz (34, 50): " + stats.Over30HzBudget);
Check(hitchy[3] == 50, "the caller's intervals are not reordered");

var twenty = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();
var ranks = FrameStats.From("ranks", 210, twenty);
Check(ranks.P50Ms == 10 && ranks.P95Ms == 19, $"nearest-rank on 1..20: p50 {ranks.P50Ms}, p95 {ranks.P95Ms}");

// --- No frames (the animation was cut before the first frame) -----------------------------------
var none = FrameStats.From("toast_out", 3.25, []);
Check(none.Frames == 0 && none.MeanMs == 0 && none.MaxMs == 0 && none.Over60HzBudget == 0, "no intervals give zeros, not an error");
Check(none.DurationMs == 3.3, "and keep the duration");

// --- The JSON line ------------------------------------------------------------------------------
var line = stats.ToJson();
Check(!line.Contains('\n'), "one line per animation");
using var json = JsonDocument.Parse(line);
var root = json.RootElement;
string[] fields = ["name", "duration_ms", "frames", "mean_ms", "p50_ms", "p95_ms", "max_ms", "over_16_7_ms", "over_33_3_ms", "images", "image_frames", "images_max_per_frame"];
Check(fields.All(f => root.TryGetProperty(f, out _)) && root.EnumerateObject().Count() == fields.Length, "the line has exactly the documented fields");
Check(root.EnumerateObject().Take(9).Select(p => p.Name).SequenceEqual(fields.Take(9)), "the timing fields keep their names and order; the image counts come after them");
Check(root.GetProperty("name").GetString() == "nav_morph_open" && root.GetProperty("over_16_7_ms").GetInt32() == 3, "with the values: " + line);
Check(root.GetProperty("mean_ms").GetDouble() == 21.3, "numbers are plain JSON numbers");

Check(!line.Contains("layout_"), "a line that watched no page settle leaves the layout fields out");

// --- A detail page settling after it shows ---------------------------------------------------------
var settled = FrameStats.From("detail_settle", 2000.04, [16.7, 16.7], null, (2, 412.345));
using var settledJson = JsonDocument.Parse(settled.ToJson());
var settledRoot = settledJson.RootElement;
Check(settledRoot.GetProperty("layout_changes").GetInt32() == 2 && settledRoot.GetProperty("layout_settled_ms").GetDouble() == 412.3,
    "the settle line adds its layout changes and when the last came, rounded like the times");
Check(settledRoot.EnumerateObject().Select(p => p.Name).Take(fields.Length).SequenceEqual(fields), "after every older field, in their order");
Check(FrameStats.From("detail_settle", 2000, [16.7], null, (0, 0)).ToJson().Contains("\"layout_changes\":0"), "a page that never moved reports 0, not nothing");

// --- Artwork applications while an animation runs -------------------------------------------------
var withImages = FrameStats.From("player_grow", 180, [16.7, 16.7, 16.7, 16.7], [0, 3, 0, 1]);
Check(withImages.Images == 4 && withImages.ImageFrames == 2 && withImages.MaxImagesPerFrame == 3, "images, frames with images and the most on one frame are counted");
Check(stats.Images == 0 && stats.ImageFrames == 0 && stats.MaxImagesPerFrame == 0, "a recording without image counts reads zero");
Check(FrameStats.From("cut", 2, [], [2]).Images == 2, "images landed before the first frame still count");

// --- The image application queue: a budget per frame, held while an animation runs ----------------
long now = 0;
long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);
var queue = new FrameWorkQueue(() => now);
var order = new List<int>();
var waiting = 0;
queue.Waiting += (_, _) => waiting++;
for (var i = 0; i < 10; i++)
{
    var n = i;
    queue.Enqueue(() => { order.Add(n); now += Ms(0.6); });
}
Check(waiting == 1 && queue.Count == 10, "the queue asks for ticks once when it stops being empty");
Check(queue.RunFrame() == 4 && order.SequenceEqual([0, 1, 2, 3]), "a frame runs items until 2 ms are spent, oldest first: " + string.Join(",", order));
var slow = new FrameWorkQueue(() => now);
slow.Enqueue(() => now += Ms(30));
slow.Enqueue(() => { });
Check(slow.RunFrame() == 1 && slow.Count == 1, "one item over budget still runs, the next waits for the next frame");

var hold = queue.Hold();
var held = queue.Hold();
Check(queue.IsHeld && queue.RunFrame() == 0, "nothing runs while an animation holds the queue");
hold.Dispose();
hold.Dispose();
Check(queue.IsHeld, "holds nest, and releasing one twice releases it once");
now = Ms(249);
Check(queue.RunFrame() == 0, "held items wait up to 250 ms");
now = Ms(251);
var before = order.Count;
var ran = queue.RunFrame();
Check(ran > 0 && order.Count == before + ran && order[before] == 4, "an item held 250 ms runs anyway, in order, so art is never more than about 250 ms late");
var lateQueue = new FrameWorkQueue(() => now);
using (lateQueue.Hold())
{
    lateQueue.Enqueue(() => { });
    now += Ms(100);
    lateQueue.Enqueue(() => { });
    now += Ms(160);
    Check(lateQueue.RunFrame() == 1 && lateQueue.Count == 1, "while held, only the items that have waited 250 ms run");
}
held.Dispose();
while (queue.Count > 0) queue.RunFrame();
Check(order.SequenceEqual(Enumerable.Range(0, 10)) && queue.Applied == 10, "released, the rest runs in order and every run is counted");
Check(queue.RunFrame() == 0, "an empty queue runs nothing");

var landing = new FrameWorkQueue(() => now);
var landed = new List<string>();
landing.Submit(() => landed.Add("disk"), now: false);
using (landing.Hold())
{
    landing.Submit(() => landed.Add("memory"), now: true);
    Check(landed.SequenceEqual(["memory"]) && landing.Count == 1 && landing.Applied == 1,
        "a memory hit lands at once, held or not and ahead of queued work, and is counted");
}
landing.RunFrame();
Check(landed.SequenceEqual(["memory", "disk"]) && landing.Applied == 2, "a disk or network image still waits for its frame");

// --- The nav morph -------------------------------------------------------------------------------
Check(NavMorphMotion.Duration == TimeSpan.FromMilliseconds(240), "the morph keeps the design's 240 ms");
Check(NavMorphWidth.Open(6, 64, 6, 900, 198) == 390, "the morph opens wide enough for every item when there is room");
Check(NavMorphWidth.Open(12, 64, 6, 500, 198) == 500, "and no wider than the room the search field can give up");
Check(NavMorphWidth.Open(2, 64, 6, 900, 198) == 198, "two items open no narrower than the closed group");
Check(NavMorphWidth.Open(3, 64, 6, 900, 222) == 222, "three items under German or Arabic labels (76, 76, 64) open no narrower than the closed group");
Check(NavMorphWidth.Open(3, 64, 6, 180, 222) == 222, "nor when the search field is already at its minimum");
Check(NavMorphMotion.WidthAt(198, 262, 0) == 198 && NavMorphMotion.WidthAt(198, 262, 1) == 262 && NavMorphMotion.WidthAt(198, 262, 1.3) == 262, "the group's width starts and lands exactly");
Check(Math.Abs(NavMorphMotion.WidthAt(198, 262, 0.5) - (198 + 64 * 0.875)) < 1e-9, "cubic ease-out, as the storyboard's CubicEase EaseOut");
var slot = new BarSpan(300, 568);
var wide = NavMorphMotion.SearchSpan(slot, 198, 198, 200, 560);
Check(wide == new BarSpan(304, 560), "a column wider than 560 centres the field: " + wide);
var narrowed = NavMorphMotion.SearchSpan(slot, 198, 262, 200, 560);
Check(narrowed == new BarSpan(364, 504), "64 px more group: the column starts 64 later and the field fills it: " + narrowed);
Check(Math.Abs(narrowed.Right - slot.Right) < 1e-9, "the field's trailing edge stays where the column ends");
var least = NavMorphMotion.SearchSpan(slot, 198, 700, 200, 560);
Check(least.Width == 200 && least.Left == 300 + 502, "a column under 200 keeps the field at 200 from its leading edge");
Check(NavMorphMotion.SearchSpan(new BarSpan(364, 504), 262, 198, 200, 560) == wide, "measured at the morphed width, the closed width gives the same field back");

// --- A resize while the morph runs -----------------------------------------------------------------
var laidOutSlot = NavMorphMotion.SlotAt(slot, 198, 262);
Check(laidOutSlot == new BarSpan(364, 504), "the slot follows the group's layout width: " + laidOutSlot);
Check(!NavMorphMotion.SlotMoved(slot, 198, laidOutSlot, 262), "the slot the morph's own layout gives is not a move");
Check(!NavMorphMotion.SlotMoved(slot, 198, slot, 198), "a slot measured before the morph's layout is not a move either");
var maximised = new BarSpan(364, 1144);
Check(NavMorphMotion.SlotMoved(slot, 198, maximised, 262), "a window maximised mid-morph moves the slot");
var afterResize = NavMorphMotion.SearchSpan(maximised, 262, 262, 200, 560);
var mid = NavMorphMotion.WidthAt(198, 262, 0.5);
var retargeted = NavMorphMotion.SearchSpan(maximised, 262, mid, 200, 560);
Check(afterResize == new BarSpan(364 + (1144 - 560) / 2.0, 560), "retargeted, the laid-out field is where XAML puts it in the wider bar: " + afterResize);
Check(Math.Abs(retargeted.Left - (afterResize.Left - (262 - mid) / 2)) < 1e-9 && retargeted.Width == 560, "and each frame's field moves from there, not from the old slot");

// --- Retargeting the morph at any moment -----------------------------------------------------------
bool CompositionKeys(MotionPlan plan)
    => plan.Keys.Count == plan.Times.Count && plan.Keys[0] == 0 && plan.Keys[^1] == 1
        && Enumerable.Range(1, plan.Keys.Count - 1).All(i => plan.Keys[i] > plan.Keys[i - 1])
        && plan.Remaining >= LayerMotion.SnapBelow;
var morphStart = LayerMotion.Plan(0, NavMorphMotion.Duration, NavMorphMotion.Samples)!;
Check(CompositionKeys(morphStart) && morphStart.Keys.Count == NavMorphMotion.Samples + 1 && morphStart.Remaining == NavMorphMotion.Duration, "the morph's first run: 41 keys from 0 to 1 over 240 ms");
var morphMid = LayerMotion.Plan(0.75, NavMorphMotion.Duration, NavMorphMotion.Samples)!;
Check(CompositionKeys(morphMid) && morphMid.Remaining == TimeSpan.FromMilliseconds(60), "a resize three quarters in: keys from 0 to 1 over the 60 ms left");
Check(LayerMotion.Plan(0.95, NavMorphMotion.Duration, NavMorphMotion.Samples) is null
    && LayerMotion.Plan(1, NavMorphMotion.Duration, NavMorphMotion.Samples) is null,
    "a resize with under a frame left, or after the 240 ms before it lands, snaps to the end width instead of 41 keys at 1 over 1 ms");
var everyMorphPlan = true;
for (var step = 0; step <= 1000; step++)
    if (LayerMotion.Plan(step / 1000.0, NavMorphMotion.Duration, NavMorphMotion.Samples) is { } any) everyMorphPlan &= CompositionKeys(any);
Check(everyMorphPlan, "every retarget moment gives the morph strictly increasing keys and at least a frame's duration");

Console.WriteLine($"{checks} checks passed.");
return 0;
