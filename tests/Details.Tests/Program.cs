using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Details;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;
using LightWeaver.Player;
using LightWeaver.ViewModels;

L.Initialize("en");
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

const long Minute = TimeSpan.TicksPerMinute;
MediaItem Item(BaseItemDto_Type type, string name = "Title") => new() { Id = Guid.NewGuid(), Name = name, Type = type };
MediaItem Episode(int number, bool played = false, long position = 0, Guid? seasonId = null) => new()
{
    Id = Guid.NewGuid(), Name = "Episode " + number, Type = BaseItemDto_Type.Episode, ParentIndexNumber = 1, IndexNumber = number,
    Played = played, ResumePositionTicks = position, SeasonId = seasonId,
};
MediaStreamChoice Stream(int index, string? language, bool isDefault = false) => new(index, index, language ?? "Unknown", language, null, isDefault, false);

// --- Layout (detail sketches) -------------------------------------------------------------------
Check(DetailModel.LayoutOf(Item(BaseItemDto_Type.Movie)) == DetailLayout.Movie, "a movie gets the info panel");
Check(DetailModel.LayoutOf(Item(BaseItemDto_Type.Series)) == DetailLayout.Series, "a series gets the season grid");
Check(DetailModel.LayoutOf(Item(BaseItemDto_Type.Season)) == DetailLayout.Season, "a season gets the episode grid");
Check(DetailModel.LayoutOf(Item(BaseItemDto_Type.Episode)) == DetailLayout.Episode, "an episode gets its season's episode grid");
Check(DetailModel.LayoutOf(Item(BaseItemDto_Type.Video)) == DetailLayout.Movie, "any other playable title is laid out like a movie");

// --- The preview's shape ---------------------------------------------------------------------------
bool Close(double? value, double expected) => value is { } v && Math.Abs(v - expected) < 1e-9;
Check(Close(DetailModel.KnownAspect(1920, 1080, null), 16.0 / 9), "the stored size gives the video's shape");
Check(DetailModel.KnownAspect(null, null, null) is null && DetailModel.KnownAspect(1920, null, null) is null, "no size, no shape");
Check(DetailModel.KnownAspect(1920, 0, null) is null && DetailModel.KnownAspect(0, 1080, null) is null, "a zero size is ignored");
Check(Close(DetailModel.KnownAspect(720, 576, "16:9"), 16.0 / 9), "an anamorphic PAL source takes Jellyfin's 16:9, not its 1.25:1 storage");
Check(Close(DetailModel.KnownAspect(720, 480, "4:3"), 4.0 / 3), "an NTSC 4:3 source takes 4:3, not its 1.5:1 storage");
Check(DetailModel.KnownAspect(720, 576, null) is null && DetailModel.KnownAspect(704, 480, null) is null, "a DVD frame without a display aspect is unknown, not 1.25:1 or 1.47:1");
Check(Close(DetailModel.KnownAspect(1440, 1080, "16:9"), 16.0 / 9), "an anamorphic HD source takes 16:9");
Check(Close(DetailModel.KnownAspect(1920, 804, "2.40:1"), 1920.0 / 804), "a square-pixel source keeps its exact shape over Jellyfin's rounded 2.40:1");
Check(Close(DetailModel.KnownAspect(1920, 1080, "1.78:1"), 16.0 / 9), "a rounded display aspect does not round the shape");
Check(Close(DetailModel.KnownAspect(1920, 1080, "0:1"), 16.0 / 9) && Close(DetailModel.KnownAspect(1920, 1080, "wide"), 16.0 / 9)
    && Close(DetailModel.KnownAspect(1920, 1080, "16:9:1"), 16.0 / 9), "a malformed display aspect falls back to the size");
Check(Close(DetailModel.KnownAspect(null, null, "2.35:1"), 2.35) && Close(DetailModel.KnownAspect(null, null, "1.85"), 1.85), "a display aspect alone gives the shape");
MediaItem Shaped(BaseItemDto_Type type, int? width, int? height, string? aspect = null)
    => new() { Id = Guid.NewGuid(), Name = "Title", Type = type, VideoWidth = width, VideoHeight = height, VideoAspectRatio = aspect };
Check(Close(DetailModel.PreviewAspect(DetailLayout.Movie, Shaped(BaseItemDto_Type.Movie, 1920, 804)), 1920.0 / 804), "a scope film's preview opens in its own shape");
Check(Close(DetailModel.PreviewAspect(DetailLayout.Episode, Shaped(BaseItemDto_Type.Episode, 1440, 1080, "4:3")), 4.0 / 3), "so does an episode's");
Check(DetailModel.PreviewAspect(DetailLayout.Movie, Shaped(BaseItemDto_Type.Movie, null, null)) == DetailModel.DefaultAspect,
    "a title whose list entry had no size opens 16:9 and stays so");
Check(DetailModel.PreviewAspect(DetailLayout.Series, Shaped(BaseItemDto_Type.Series, 1920, 804)) == DetailModel.DefaultAspect
    && DetailModel.PreviewAspect(DetailLayout.Season, Shaped(BaseItemDto_Type.Season, 640, 480)) == DetailModel.DefaultAspect,
    "a show or a season is 16:9: the episode it plays is not known when the page opens");
var (fitWidth, fitHeight) = DetailModel.PreviewSize(1600, 1000, 16.0 / 9);
Check(fitWidth == 1600 && Math.Abs(fitHeight - 900) < 1e-9, "a wide space: the preview is as wide as the space, in the video's shape");
(fitWidth, fitHeight) = DetailModel.PreviewSize(1600, 600, 16.0 / 9);
Check(fitHeight == 600 && Math.Abs(fitWidth - 600 * 16.0 / 9) < 1e-9, "a short window: as tall as fits, so none of it is below the window");
(fitWidth, fitHeight) = DetailModel.PreviewSize(1600, 1000, 2.39);
Check(Math.Abs(fitWidth - 1600) < 1e-9 && Math.Abs(fitHeight - 1600 / 2.39) < 1e-9, "a scope film is wider and shorter");
(fitWidth, fitHeight) = DetailModel.PreviewSize(1000, 320, 16.0 / 9);
Check(fitHeight == 320 && Math.Abs(fitWidth - 320 * 16.0 / 9) < 1e-9, "a short window above the minimum: as tall as the rows leave");
Check(Math.Abs(fitWidth / fitHeight - 16.0 / 9) < 1e-9 && 1000 - fitWidth > 400, "and it keeps the video's shape, the spare width going to the side panel");
(fitWidth, fitHeight) = DetailModel.PreviewSize(704, 35, 16.0 / 9);
Check(fitHeight == DetailModel.PreviewMinHeight && Math.Abs(fitWidth - DetailModel.PreviewMinHeight * 16.0 / 9) < 1e-9,
    "1050x700 with two card rows below: the rows no longer squeeze the preview to a strip");
(fitWidth, fitHeight) = DetailModel.PreviewSize(1000, -50, 16.0 / 9);
Check(fitHeight == DetailModel.PreviewMinHeight && fitWidth <= 1000, "no room at all: the minimum, never a negative size");
(fitWidth, fitHeight) = DetailModel.PreviewSize(540, 200, 2.39);
Check(Math.Abs(fitWidth - 540) < 1e-9 && Math.Abs(fitHeight - 540 / 2.39) < 1e-9, "a scope film in a narrow cell stays in its shape below the minimum");
(fitWidth, fitHeight) = DetailModel.PreviewSize(0, 200, 16.0 / 9);
Check(fitHeight == 1 && fitWidth == 0, "no width: one DIP tall, never a negative size");

// --- The page's frame, decided before any lookup answers --------------------------------------------
var spacing = new DetailSpacing(32, 30, 14, 14);
var frame = DetailModel.Frame(1920, 1000, 300, 16.0 / 9, spacing);
Check(Close(frame.PreviewHeight, 1000 - 30 - 300 - 14) && Close(frame.PreviewWidth, frame.PreviewHeight * 16 / 9),
    "1920x1000: the preview takes the height the rows leave, in its shape");
Check(Close(frame.PreviewWidth + frame.SideWidth, 1920 - 32 - 14) && frame.SideWidth >= DetailModel.WideSidePanel,
    "and the side panel takes the rest of the width, never under 360");
var scope = DetailModel.Frame(1920, 1000, 300, 2.39, spacing);
Check(Close(scope.PreviewWidth, 1920 - 32 - 14 - 360) && Close(scope.PreviewHeight, scope.PreviewWidth / 2.39) && Close(scope.SideWidth, 360),
    "a scope film is as wide as the cell allows and shorter");
Check(DetailModel.Frame(1050, 600, 300, 16.0 / 9, spacing).PreviewHeight == DetailModel.PreviewMinHeight
    && DetailModel.Frame(1050, 600, 300, 16.0 / 9, spacing).SideWidth >= DetailModel.NarrowSidePanel, "a short 1050 DIP page: the 300 floor, beside a panel of at least 300");
Check(DetailModel.Frame(1920, 1000, 0, 16.0 / 9, spacing).PreviewHeight > frame.PreviewHeight, "without rows the preview has their room too");
Check(DetailModel.Frame(1920, 1000, 300, 16.0 / 9, spacing) == frame, "the same page, rows and shape give the same frame: nothing that arrives later is an input");

// --- The season and episode grid (panel width less 38 DIP of padding and border) -------------------
Check(DetailModel.ChildGrid(702, 210, 18) == (3, 222), "a 740 DIP panel at 1.5x: three seasons of 222 DIP");
Check(DetailModel.ChildGrid(952, 210, 18) == (4, 224.5), "a 990 DIP panel at 1.5x: four seasons of 224.5 DIP, not two of 467");
Check(DetailModel.ChildGrid(1262, 210, 18) == (5, 238), "a 1300 DIP panel at 1.5x: five seasons of 238 DIP");
Check(DetailModel.ChildGrid(262, 140, 12) == (2, 125), "the narrowest panel keeps two columns, the cards smaller than drawn");
Check(DetailModel.ChildGrid(952, 444, 18) == (2, 467) && DetailModel.ChildGrid(1404, 444, 18) == (3, 456), "episodes take a third column once three wide cards fit");
Check(DetailModel.ChildGrid(0, 210, 18) == (2, 0), "a panel not laid out yet has two empty columns");
Check(Enumerable.Range(438, 2000).Select(w => DetailModel.ChildGrid(w, 210, 18))
    .All(g => g.CardWidth >= 210 && g.CardWidth < 210 + 228.0 / g.Columns), "once two fit, a card is never narrower than drawn and grows by less than a column's share");

// --- Counting layout changes after the page shows -------------------------------------------------
var opened = new DetailGeometry(16, 12, 1200, 675, 701, 646, 214, 402);
var settle = new DetailSettle();
settle.Observe(opened, 0);
settle.Observe(opened with { PreviewWidth = 1200.3 }, 16);
Check(settle.Changes == 0 && settle.SettledMs == 0, "the first layout is the baseline and rounding is not a change");
settle.Observe(opened with { PreviewHeight = 502, RowsTop = 528 }, 120);
settle.Observe(opened with { PreviewHeight = 502, RowsTop = 528 }, 136);
settle.Observe(opened with { PreviewHeight = 502, RowsTop = 528, SideWidth = 700 }, 400);
Check(settle.Changes == 2 && settle.SettledMs == 400, "each frame that moved something is one change, a still frame none: " + settle.Changes);
Check(!opened.MovedFrom(opened) && opened.MovedFrom(opened with { PreviewX = 17 }), "any edge that moves counts");
var inside = new DetailSettle();
inside.Observe(opened, 0);
inside.Observe(opened with { OverviewTop = 242, GridTop = 430 }, 180);
inside.Observe(opened with { OverviewTop = 242, GridTop = 458 }, 260);
Check(inside.Changes == 2 && inside.SettledMs == 260,
    "text pushed down inside the side panel counts while the preview, the rows and the panel's width stay: " + inside.Changes);

// --- The video surface's fit ---------------------------------------------------------------------
var fit = SurfaceFit.Of(1280, 800, 1920, 1080, 16.0 / 9);
Check(Math.Abs(1920 * fit.ScaleX - 1280) < 1e-9 && Math.Abs(fit.TranslateX) < 1e-9, "a 16:9 picture fills a 1280x800 surface's width from a 1920x1080 buffer");
Check(Math.Abs(fit.TranslateY - (800 - 1080 * fit.ScaleY) / 2) < 1e-9 && fit.ScaleX == fit.ScaleY, "centred vertically, never stretched");
var stale = SurfaceFit.Of(1280, 800, 1920, 1080, 4.0 / 3);
Check(1920 * stale.ScaleX > 1280 + 100, "the previous entry's 4:3 shape would push a 16:9 picture past the surface, so the shape has to follow the file");
var pillar = SurfaceFit.Of(1280, 800, 1920, 1080, 4.0 / 3);
Check(Math.Abs(1080 * pillar.ScaleY * 4.0 / 3 - Math.Min(1280, 800 * 4.0 / 3)) < 1e-9, "a 4:3 picture is as large as the surface allows");
var unknown = SurfaceFit.Of(1280, 800, 1920, 1080, null);
Check(Math.Abs(unknown.ScaleX - 1280.0 / 1920) < 1e-9 && Math.Abs(unknown.ScaleY - 800.0 / 1080) < 1e-9 && unknown.TranslateX == 0, "without a shape the buffer covers the surface");

// --- The player's grow and shrink ----------------------------------------------------------------
var card = new LayerRect(312, 540, 160, 240);
var window = new LayerRect(0, 0, 1920, 1032);
Check(LayerMotion.At(card, window, 0) == card, "the grow starts exactly on the card's artwork");
Check(LayerMotion.At(card, window, 1) == window && LayerMotion.At(card, window, 1.4) == window, "and ends exactly on the window, however late the last frame is");
var half = LayerMotion.At(card, window, 0.5);
Check(half.Width > (card.Width + window.Width) / 2 && half.X < card.X, "ease-out: past halfway at half the time");
var previous = card;
var monotonic = true;
for (var step = 1; step <= 60; step++)
{
    var now = LayerMotion.At(card, window, step / 60.0);
    monotonic &= now.Width >= previous.Width && now.Height >= previous.Height && now.X <= previous.X && now.Y <= previous.Y;
    previous = now;
}
Check(monotonic, "every edge moves one way only, no overshoot");
Check(LayerMotion.Duration == TimeSpan.FromMilliseconds(180), "180 ms, inside the design's 200 ms");
var preview = new LayerRect(16, 68, 1180, 664);
var settled = new LayerRect(16, 68, 1110, 625);
Check(LayerMotion.At(window, settled, 1) == settled && LayerMotion.At(window, settled, 1) != preview, "a shrink whose target moved while the window settled lands on where the preview ended up");

// --- The grow on the compositor: laid out once at the target, drawn at each frame's rect ----------
bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
var identity = LayerMotion.Frame(window, window, new LayerContent(16.0 / 9), 0);
Check(identity == new LayerFrame(1, 1, 0, 0, 0, 0, window.Width, window.Height, 0, 0), "at the target the layer is drawn as laid out: " + identity);
var poster = new LayerContent(2.0 / 3);
var start = LayerMotion.Frame(card, window, poster, 8);
Check(Near(start.ScaleX, start.ScaleY) && Near(1032 * 2.0 / 3 * start.ScaleX, 160), "the card's poster, fitted in the window, is drawn exactly the card's size, never stretched");
Check(Near(window.CenterX + start.TranslateX, card.CenterX) && Near(window.CenterY + start.TranslateY, card.CenterY), "centred on the card");
Check(Near((start.ClipRight - start.ClipLeft) * start.ScaleX, card.Width) && Near((start.ClipBottom - start.ClipTop) * start.ScaleY, card.Height), "and clipped to the card's rect on screen");
Check(Near(start.RadiusX * start.ScaleX, 8) && Near(start.RadiusY * start.ScaleY, 8), "with 8 px corners on screen at any scale");
Check(Near((start.ClipLeft + start.ClipRight) / 2, window.Width / 2), "the clip is centred on the layer, where the scale is centred");
var videoShape = new LayerContent(16.0 / 9);
var shrinkStart = LayerMotion.Frame(window, preview, videoShape, 0);
Check(Near(Math.Min(preview.Width, preview.Height * 16 / 9) * shrinkStart.ScaleX, Math.Min(window.Width, window.Height * 16 / 9)), "a shrink laid out at the preview draws the video at the window's fitted size first");
var backdrop = LayerMotion.Frame(card, window, new LayerContent(16.0 / 9, Covers: true), 8);
Check(Near(Math.Max(window.Width, window.Height * 16 / 9) * backdrop.ScaleX, Math.Max(card.Width, card.Height * 16 / 9)), "a covering backdrop still covers the smaller rect");
var stretched = LayerMotion.Frame(card, window, LayerContent.Stretched, 0);
Check(Near(stretched.ScaleX, card.Width / window.Width) && Near(stretched.ScaleY, card.Height / window.Height), "a picture of unknown shape stretches with the rect, as it did when the layer was resized");
var tallWindow = new LayerRect(0, 0, 1920, 1200);
var widePreview = new LayerRect(16, 68, 1120, 630);
var shrinkFrame = LayerMotion.Frame(tallWindow, widePreview, videoShape, 0);
Check(widePreview.Height * shrinkFrame.ScaleY < tallWindow.Height - 100 && Near(widePreview.Width * shrinkFrame.ScaleX, tallWindow.Width),
    "a 16:9 video leaving a 16:10 window: the scaled layer covers only 1920x1080, so the black under it has to be drawn separately");
var growFrame = LayerMotion.Frame(card, window, videoShape, 8);
Check(window.Height * growFrame.ScaleY < card.Height - 100,
    "a 16:9 video growing out of a 2:3 card: the scaled layer is far shorter than the card, so the backdrop fills the rest");
Check(LayerMotion.At(tallWindow, widePreview, 0) == tallWindow && LayerMotion.At(tallWindow, widePreview, 1) == widePreview, "the backdrop's rect is the frame's rect, unscaled, from the window to the preview");

var times = LayerMotion.SampleTimes(0);
Check(times.Count == LayerMotion.Samples + 1 && times[0] == 0 && times[^1] == 1, "keyframes run from the start to exactly the end");
var rest = LayerMotion.SampleTimes(0.6, 4);
Check(rest.Count == 5 && Near(rest[0], 0.6) && Near(rest[2], 0.8) && rest[^1] == 1, "a retarget samples only the time left");
var samples = times.Select(t => LayerMotion.At(card, window, t)).ToList();
var worst = 0.0;
for (var i = 0; i + 1 < times.Count; i++)
    for (var j = 1; j < 8; j++)
    {
        var t = times[i] + (times[i + 1] - times[i]) * j / 8;
        var k = j / 8.0;
        var linear = samples[i].Width + (samples[i + 1].Width - samples[i].Width) * k;
        worst = Math.Max(worst, Math.Abs(linear - LayerMotion.At(card, window, t).Width));
    }
Check(worst < 0.5, $"between keyframes the compositor's straight line is within {worst:0.00} px of the curve");

// --- Retargeting at any moment: every plan is one the compositor takes -----------------------------
bool CompositionKeys(MotionPlan plan)
{
    var keys = plan.Keys;
    var increasing = Enumerable.Range(1, keys.Count - 1).All(i => keys[i] > keys[i - 1]);
    return keys.Count == plan.Times.Count && keys.Count >= 2 && keys[0] == 0 && keys[^1] == 1 && increasing
        && keys.All(float.IsFinite) && plan.Times.All(double.IsFinite) && plan.Times[^1] == 1
        && plan.Remaining >= TimeSpan.FromMilliseconds(1) && plan.Remaining >= LayerMotion.SnapBelow;
}
var fromStart = LayerMotion.Plan(0, LayerMotion.Duration)!;
Check(CompositionKeys(fromStart) && fromStart.Keys.Count == LayerMotion.Samples + 1 && fromStart.Remaining == LayerMotion.Duration && fromStart.Times[0] == 0,
    "the first run: 61 keys from 0 to 1 over the whole 180 ms");
var halfway = LayerMotion.Plan(0.5, LayerMotion.Duration)!;
Check(CompositionKeys(halfway) && halfway.Remaining == TimeSpan.FromMilliseconds(90) && Near(halfway.Times[0], 0.5) && halfway.Times[^1] == 1,
    "a retarget mid-way: keys still from 0 to 1, over the 90 ms left, sampling from where it is");
Check(LayerMotion.Plan(-0.2, LayerMotion.Duration) is { } early && CompositionKeys(early) && early.Remaining == LayerMotion.Duration,
    "a retarget before it starts runs the whole animation");
var lastFrame = 1 - LayerMotion.SnapBelow / LayerMotion.Duration;
Check(LayerMotion.Plan(lastFrame - 0.01, LayerMotion.Duration) is { } late && CompositionKeys(late),
    "a retarget with just over a frame left still animates, with valid keys");
Check(LayerMotion.Plan(lastFrame + 0.01, LayerMotion.Duration) is null && LayerMotion.Plan(0.999, LayerMotion.Duration) is null,
    "less than a frame left: no animation, the end state is set at once");
var stalled = (57.6 + 185.6) / LayerMotion.Duration.TotalMilliseconds;
Check(LayerMotion.Plan(stalled, LayerMotion.Duration) is null, "a shrink started before a 185 ms window change has nothing left to animate when the UI thread comes back, which is why it waits for the window");
Check(LayerMotion.Plan(1, LayerMotion.Duration) is null && LayerMotion.Plan(1.7, LayerMotion.Duration) is null,
    "after the 180 ms but before it lands (the crash: 61 keys all at 1 over 1 ms): it snaps instead");
Check(LayerMotion.Plan(double.NaN, LayerMotion.Duration) is null, "a time that is not a number snaps too");
var everyPlan = true;
for (var step = 0; step <= 1000; step++)
    if (LayerMotion.Plan(step / 1000.0, LayerMotion.Duration) is { } any) everyPlan &= CompositionKeys(any);
Check(everyPlan, "every retarget moment gives strictly increasing keys from 0 to 1, finite times and at least a frame's duration");

Check(new LayerRect(0, 0, 10, 10).IsUsable, "a rect with a size is usable");
Check(!new LayerRect(0, 0, 0, 10).IsUsable && !new LayerRect(0, 0, 10, -1).IsUsable && !new LayerRect(double.NaN, 0, 10, 10).IsUsable
    && !new LayerRect(0, 0, double.PositiveInfinity, 10).IsUsable, "a zero-size, negative, NaN or infinite rect from a window between sizes is not");

var shapes = new[] { poster, videoShape, LayerContent.Stretched, new LayerContent(16.0 / 9, Covers: true), new LayerContent(0) };
var pairs = new[] { (card, window), (window, preview), (tallWindow, widePreview), (new LayerRect(5, 5, 1, 1), window), (window, new LayerRect(0, 0, 1, 1)) };
var everyFrame = true;
foreach (var (a, b) in pairs)
    foreach (var shape in shapes)
        foreach (var startT in new[] { 0, 0.3, 0.9 })
            foreach (var t in LayerMotion.Plan(startT, LayerMotion.Duration)!.Times)
                everyFrame &= LayerMotion.Frame(LayerMotion.At(a, b, t), b, shape, 8).IsValid;
Check(everyFrame, "every keyframe of every grow and shrink, from any retarget moment, has finite values, a positive scale and clip, and corners within the clip");
var roundest = LayerMotion.Frame(new LayerRect(0, 0, 10, 6), window, LayerContent.Stretched, 8);
Check(roundest.IsValid && Near(roundest.RadiusX * roundest.ScaleX, 5) && Near(roundest.RadiusY * roundest.ScaleY, 3),
    "an 8 px radius on a 10x6 rect is held at half of it on screen");
Check(LayerMotion.Frame(card, window, poster, double.NaN) is { RadiusX: 0, RadiusY: 0 } nanCorners && nanCorners.IsValid
    && LayerMotion.Frame(card, window, poster, -4) is { RadiusX: 0 } negative && negative.IsValid, "a NaN or negative radius draws square corners");
Check(!new LayerFrame(1, 1, 0, 0, 0, 0, 10, 10, 6, 1).IsValid && !new LayerFrame(1, double.NaN, 0, 0, 0, 0, 10, 10, 1, 1).IsValid
    && !new LayerFrame(1, 1, 0, 0, 5, 0, 5, 10, 0, 0).IsValid, "a corner past half the clip, a NaN or an empty clip is not valid");

// --- Full screen without a presenter switch -------------------------------------------------------
const long OverlappedWindow = 0x00CF0000, ClipChildren = 0x02000000, Minimize = 0x20000000;
var maximized = OverlappedWindow | FullscreenStyle.Visible | FullscreenStyle.Maximize | ClipChildren;
var borderless = FullscreenStyle.Borderless(maximized);
Check(FullscreenStyle.Frame == OverlappedWindow, "the frame is exactly WS_OVERLAPPEDWINDOW");
Check((borderless & OverlappedWindow) == 0, "full screen has no caption, caption buttons or sizing frame");
Check(borderless == (FullscreenStyle.Visible | ClipChildren), "and is not maximized, which Windows keeps the taskbar over, but keeps visible and clipping");
Check(FullscreenStyle.Restored(borderless, maximized) == maximized, "leaving puts the exact style back: maximized again");
var restored = OverlappedWindow | FullscreenStyle.Visible;
Check(FullscreenStyle.Restored(FullscreenStyle.Borderless(restored), restored) == restored, "a restored window leaves restored");
var minimizedMeanwhile = borderless | Minimize;
Check(FullscreenStyle.Restored(minimizedMeanwhile, maximized) == ((maximized & ~FullscreenStyle.Maximize) | Minimize), "a window minimized in full screen gets its frame back and stays minimized, not also maximized");

Check(FullscreenStyle.Exit(minimizedNow: false, restored) == FullscreenExit.MoveBack, "a restored or snapped window goes back to its exact rect in one move");
Check(FullscreenStyle.Exit(minimizedNow: false, maximized) == FullscreenExit.Remaximize, "a maximized window goes back maximized at its exact rect, with its restored rect");
Check(FullscreenStyle.Exit(minimizedNow: true, restored) == FullscreenExit.SavedPlacement && FullscreenStyle.Exit(true, maximized) == FullscreenExit.SavedPlacement,
    "minimized in full screen: the saved placement goes back, so restoring later is not monitor-sized");
Check(FullscreenStyle.MinimizedPlacementFlags(maximized) == FullscreenStyle.RestoreToMaximized && FullscreenStyle.MinimizedPlacementFlags(restored) == 0,
    "and a window that was maximized restores maximized");
const uint NoZOrder = 0x0004, NoActivate = 0x0010;
Check(FullscreenStyle.FillingFlags(FullscreenStyle.NoMove | FullscreenStyle.NoSize | NoZOrder | NoActivate) == (NoZOrder | NoActivate),
    "a move of the content in full screen always takes the window's whole client area, whatever it meant to keep");
var secondWork = new ScreenRect(3840, 340, 6400, 1732);
Check(WindowFrame.MaximizedClient(new ScreenRect(3840, 343, 6400, 1732), secondWork) == secondWork,
    "maximized on a 100% monitor next to a 150% one, the client starts at the work area's top, not 3 px below it");
Check(WindowFrame.MaximizedClient(new ScreenRect(0, -3, 3840, 2088), new ScreenRect(0, 0, 3840, 2088)).Top == 0,
    "nor above it on a monitor at a higher scale than the main one");

Check(FullscreenIntent.Toggle(growingIntoFullscreen: true, osFullscreen: false) == FullscreenToggle.CancelGrow,
    "the toggle during a grow into full screen ends the grow in the window, not in full screen again");
Check(FullscreenIntent.Toggle(false, osFullscreen: true) == FullscreenToggle.Exit, "in full screen the toggle leaves it");
Check(FullscreenIntent.Toggle(false, osFullscreen: false) == FullscreenToggle.Enter, "out of full screen, a shrink included, the toggle enters it");

Check(FullscreenStyle.Entry(minimizedNow: false, foregroundNow: true) == FullscreenEntry.Cover, "entering in the foreground covers the monitor on top");
Check(FullscreenStyle.Entry(minimizedNow: false, foregroundNow: false) == FullscreenEntry.CoverBehind, "a window that lost the foreground before the change applies covers it without activating");
Check(FullscreenStyle.Entry(minimizedNow: true, foregroundNow: true) == FullscreenEntry.OnRestore && FullscreenStyle.Entry(true, false) == FullscreenEntry.OnRestore,
    "a window minimized before the change applies stays minimized and covers the monitor once restored");

var windowed = new LayerRect(0, 0, 1264, 792);
Check(!WindowChange.CanRelease(1920, 1080, windowed, TimeSpan.FromMilliseconds(40)), "leaving full screen, the content keeps the window's size while XAML still has the monitor's");
Check(WindowChange.CanRelease(1264, 792.4, windowed, TimeSpan.FromMilliseconds(40)), "and lets go once XAML has the window's new size");
Check(WindowChange.CanRelease(1920, 1080, windowed, WindowChange.CatchUp), "or once the catch-up time is over");
var monitorDips = new LayerRect(0, 0, 1920, 1080);
var leaving = LayerMotion.Frame(monitorDips, windowed, new LayerContent(16.0 / 9), 0);
Check(Math.Abs(leaving.ScaleX - 1920.0 / 1264) < 1e-9 && leaving.ScaleY == leaving.ScaleX, "laid out for the window it returns to, the full player is still drawn at the monitor's size, never stretched");
Check(leaving.TranslateX == monitorDips.CenterX - windowed.CenterX && leaving.TranslateY == monitorDips.CenterY - windowed.CenterY, "and centred on the monitor until the window changes");
var entering = LayerMotion.Frame(windowed, monitorDips, new LayerContent(16.0 / 9), 0);
Check(Math.Abs(entering.ScaleX - 1264.0 / 1920) < 1e-9 && entering.ClipWidth * entering.ScaleX <= 1264 + 1e-6, "laid out for the monitor, it is still drawn inside the window it fills until the window covers the monitor");
Check(!WindowChange.TakesCaptionInset(fullscreen: true, 0) && !WindowChange.TakesCaptionInset(true, 138), "in full screen the top bar keeps the caption buttons' width it had");
Check(!WindowChange.TakesCaptionInset(fullscreen: false, 0) && WindowChange.TakesCaptionInset(false, 138), "and right after it a zero inset is not the window's");

// --- The player bar's button row --------------------------------------------------------------------
Check(!PlayerBar.FoldsTracks(1888, 560, folded: false, 40), "a wide bar keeps Audio and Subtitles as two buttons");
Check(PlayerBar.FoldsTracks(506, 530, folded: false, 40), "a 900 x 600 preview's bar folds them into Tracks rather than wrap");
Check(PlayerBar.FoldsTracks(506, 490, folded: true, 40), "and stays folded while unfolding would wrap again");
Check(!PlayerBar.FoldsTracks(560, 490, folded: true, 40), "a wider preview unfolds them once both fit");
Check(!PlayerBar.FoldsTracks(double.PositiveInfinity, 9000, folded: false, 40), "an unmeasured row folds nothing");

Check(!PlayerTooltips.Opens(keyboardFocus: false, pointerMovedSinceShown: false), "the bar shown again under a resting pointer opens no tooltip (Play, paused in full screen)");
Check(PlayerTooltips.Opens(false, true) && PlayerTooltips.Opens(true, false), "a pointer that moved there, or keyboard focus, does");
Check(!PlayerTooltips.Moved((40, 1050), (40, 1050)) && !PlayerTooltips.Moved((40, 1050), null), "a pointer event at the same spot is not a move");
Check(PlayerTooltips.Moved((40, 1050), (41, 1050)) && PlayerTooltips.Moved(null, (41, 1050)), "one pixel is");

// --- Track names in the player's flyouts --------------------------------------------------------------
Check(new MpvTrack(1, "audio", null, "eng", true, true, false).Display == "English", "an audio track without a title is named by its language, as the detail panel names it, not \"eng\"");
Check(new MpvTrack(2, "audio", "Commentary", "en", false, false, false).Display == "Commentary (English)", "a titled track names its language too");
Check(new MpvTrack(3, "sub", null, "ger", false, false, false).Display == "German" && new MpvTrack(4, "sub", null, "und", false, false, false).Display == "und",
    "bibliographic codes are known; an undetermined tag stays as it is");
L.Initialize("de");
var trackInGerman = new MpvTrack(1, "audio", null, "eng", true, true, false).Display;
L.Initialize("en");
Check(trackInGerman == "Englisch", "in German the language reads in German: " + trackInGerman);

var monitor = new ScreenRect(0, 0, 3840, 2160);
var work = new ScreenRect(0, 0, 3840, 2088);
var secondary = new ScreenRect(3840, 0, 5760, 1080);
Check(FullscreenStyle.Covers(borderless, monitor, monitor), "a borderless window on the monitor's exact rect is full screen");
Check(!FullscreenStyle.Covers(borderless, work, monitor), "the work area is not: the taskbar stays");
Check(!FullscreenStyle.Covers(borderless, monitor with { Bottom = 2159 }, monitor) && !FullscreenStyle.Covers(borderless, new ScreenRect(-1, 0, 3840, 2160), monitor),
    "one pixel short or over is not");
Check(!FullscreenStyle.Covers(borderless | FullscreenStyle.Maximize, monitor, monitor), "a maximized window on the monitor's rect is not");
Check(!FullscreenStyle.Covers(borderless | FullscreenStyle.Caption, monitor, monitor) && !FullscreenStyle.Covers(borderless | FullscreenStyle.ThickFrame, monitor, monitor),
    "nor one with a caption or sizing frame");
Check(!FullscreenStyle.Covers(borderless, monitor, secondary) && FullscreenStyle.Covers(borderless, secondary, secondary),
    "it covers the monitor it is on, in that monitor's own pixels");
Check(monitor.InDips(1.5) == new LayerRect(0, 0, 2560, 1440) && secondary.InDips(1) == new LayerRect(0, 0, 1920, 1080),
    "the content is laid out for the monitor in that monitor's DIPs before the window covers it: 4K at 150 % is 2560 x 1440");
Check(new ScreenRect(-11, -11, 3851, 2099).Width == 3862 && work.Height == 2088, "width and height are right minus left and bottom minus top");

// --- No frame of the previous file ----------------------------------------------------------------
var gate = new FirstFrameGate();
Check(!gate.IsHeld && !gate.TryRelease(1, 1), "nothing held, nothing to release");
gate.Hold(3);
Check(gate.IsHeld && !gate.TryRelease(3, 3), "a seek in the file that was playing when the title was asked for does not show the video");
Check(!gate.TryRelease(4, 5), "nor does a load that a newer one has replaced");
Check(gate.TryRelease(5, 5) && !gate.IsHeld, "the newest load's first frame shows it");
gate.Hold(5);
gate.Hold(5);
Check(gate.TryRelease(6, 6), "asking twice before the load (window, then controller) waits for the same first frame");
gate.Hold(6);
gate.Clear();
Check(!gate.IsHeld && !gate.TryRelease(7, 7), "a stop clears the hold");
gate.Hold(7);
Check(!gate.TryRelease(7, 7) && gate.IsHeld, "a request the SyncPlay group takes over makes no new load, so only a cancel can end its hold");
gate.Clear();
Check(!gate.IsHeld, "and the cancel shows the video that kept playing");

// --- Next episode to play in this show ----------------------------------------------------------
Check(DetailModel.NextEpisode([]) is null, "no episodes, no next episode");
var fresh = new[] { Episode(1), Episode(2), Episode(3) };
Check(DetailModel.NextEpisode(fresh) == fresh[0], "nothing watched starts at the first episode");
var started = new[] { Episode(1), Episode(2, position: 10 * Minute), Episode(3) };
Check(DetailModel.NextEpisode(started) == started[1], "nothing watched but one started resumes it");
var midway = new[] { Episode(1, played: true), Episode(2, played: true), Episode(3), Episode(4) };
Check(DetailModel.NextEpisode(midway) == midway[2], "the first unwatched one after the last watched");
var gap = new[] { Episode(1), Episode(2, played: true), Episode(3, played: true), Episode(4) };
Check(DetailModel.NextEpisode(gap) == gap[3], "a skipped early episode does not pull the show back");
var caughtUpWithGap = new[] { Episode(1), Episode(2, played: true) };
Check(DetailModel.NextEpisode(caughtUpWithGap) == caughtUpWithGap[0], "past the last watched, the remaining unwatched one");
var done = new[] { Episode(1, played: true), Episode(2, played: true) };
Check(DetailModel.NextEpisode(done) == done[0], "a finished show starts over");

// --- Season to show ------------------------------------------------------------------------------
var s1 = new MediaItem { Id = Guid.NewGuid(), Name = "Season 1", Type = BaseItemDto_Type.Season, UnplayedItemCount = 0 };
var s2 = new MediaItem { Id = Guid.NewGuid(), Name = "Season 2", Type = BaseItemDto_Type.Season, UnplayedItemCount = 4 };
Check(DetailModel.SeasonOf([s1, s2], Episode(1, seasonId: s1.Id)) == s1, "the season of the next episode");
Check(DetailModel.SeasonOf([s1, s2], null) == s2, "without one, the first season with unwatched episodes");
Check(DetailModel.SeasonOf([], null) is null, "no seasons, none");

// --- Chips ---------------------------------------------------------------------------------------
var movie = new MediaItem
{
    Id = Guid.NewGuid(), Name = "8 Mile", Type = BaseItemDto_Type.Movie, ProductionYear = 2002, OfficialRating = "R",
    CommunityRating = 7.1f, RuntimeTicks = 110 * Minute,
};
var chips = DetailModel.Chips(movie);
Check(chips.SequenceEqual(["2002", "R", "★ 7.1", "1 h 50 min"]), "info chips: year, rating, stars, runtime (" + string.Join(" | ", chips) + ")");
Check(DetailModel.Runtime(42 * Minute) == "42 min", "a short runtime in minutes");
Check(DetailModel.Runtime(null) == "", "no runtime, no chip");
Check(DetailModel.Chips(Item(BaseItemDto_Type.Movie)).Count == 0, "a title without facts has no chips");

var streams = new MediaSourceStreams("source", [], [], [],
[
    new MediaInfoField("Resolution", "1920x812", "Resolution"),
    new MediaInfoField("Aspect", "2.35:1"),
    new MediaInfoField("Codec", "HEVC Main 10"),
    new MediaInfoField("Range", "SDR", "Range"),
    new MediaInfoField("Audio", "DD 5.1", "Audio"),
]);
Check(DetailModel.TechnicalChips(streams).SequenceEqual(["1920x812", "2.35:1", "HEVC Main 10", "SDR"]), "technical chips leave the audio badge out");
Check(DetailModel.TechnicalChips(null).Count == 0, "no streams, no technical chips");
Check(DetailModel.EpisodeTag(Episode(3)) == "S1 · E3", "episode tag: " + DetailModel.EpisodeTag(Episode(3)));

// --- Default stream picks ------------------------------------------------------------------------
var audio = new[] { Stream(1, "eng", isDefault: true), Stream(2, "ger") };
Check(DetailModel.DefaultAudio(audio, "deu") == 2, "the preferred audio language wins (deu matches ger)");
Check(DetailModel.DefaultAudio(audio, "") == 1, "without a preference, the source default");
Check(DetailModel.DefaultAudio([Stream(1, "eng"), Stream(2, "fre")], "jpn") == 1, "no match and no default, the first track");
Check(DetailModel.DefaultAudio([], "eng") is null, "no audio, no pick");
var subtitles = new[] { Stream(3, "eng"), Stream(4, "ger") };
Check(DetailModel.DefaultSubtitle(subtitles, "ger") == 4, "the preferred subtitle language wins");
Check(DetailModel.DefaultSubtitle(subtitles, "") is null, "subtitles stay off without a preference or a default");
Check(DetailModel.DefaultSubtitle([Stream(3, "eng"), Stream(4, "ger", isDefault: true)], "") == 4, "the source's default subtitle is used");

// --- Start time ----------------------------------------------------------------------------------
Check(DetailModel.ClampStart(TimeSpan.FromMinutes(-1), 90 * Minute) == TimeSpan.Zero, "the start cannot go below zero");
Check(DetailModel.ClampStart(TimeSpan.FromMinutes(100), 90 * Minute) == TimeSpan.FromMinutes(90), "the start cannot pass the runtime");
Check(DetailModel.DefaultStart(new MediaItem { Id = Guid.NewGuid(), Name = "x", ResumePositionTicks = 12 * Minute }) == TimeSpan.FromMinutes(12), "a started title starts at its resume point");
Check(DetailModel.DefaultStart(new MediaItem { Id = Guid.NewGuid(), Name = "x", ResumePositionTicks = 12 * Minute, Played = true }) == TimeSpan.Zero, "a watched title starts from the beginning");

// --- Quality names ---------------------------------------------------------------------------------
Check(QualityNames.Simple(0) == "Original quality" && QualityNames.Simple(40) == "Very high · 4K" && QualityNames.Simple(20) == "High · 1080p", "plain names for the top caps");
Check(QualityNames.Simple(10) == QualityNames.Simple(8) && QualityNames.Simple(4) == "Low · 720p" && QualityNames.Simple(1) == "Data saver · 480p", "plain names group nearby caps");
Check(QualityNames.Of(20) == "High · 1080p · 20 Mbps" && QualityNames.Of(10) == "Good · 1080p · 10 Mbps" && QualityNames.Of(0) == "Original quality", "a cap reads its plain name with its bitrate beside it");
Check(QualityNames.Tier(null, 20) == "As in Settings · 20 Mbps" && QualityNames.Tier(null, 0) == "As in Settings · Unlimited" && QualityNames.Tier(4, 20) == "Low · 720p · 4 Mbps", "the Settings tier shows the cap Settings sets, in the Settings page's words");
using (var arabic = typeof(QualityNames).Assembly.GetManifestResourceStream("LightWeaver.Localization.locales.ar.json")!)
    Check(System.Text.Json.JsonDocument.Parse(arabic).RootElement.GetProperty("strings").GetProperty("Quality.Named").GetString()!.Contains("\u200F{1}"),
        "the Arabic tier puts a right-to-left mark before the bitrate, so it keeps its place after 1080p");
Check(PlayerViewModel.QualityTierMbps.SequenceEqual([null, 0, 20, 10, 4, 1]), "the player and the detail page share one tier list, the Settings default first");
Check(PlayerViewModel.QualityTierMbps.Select(t => QualityNames.Tier(t, 20)).Distinct().Count() == PlayerViewModel.QualityTierMbps.Count, "every tier has its own name");

// --- Quality tiers in the player -------------------------------------------------------------------
var tiers = new PlayerViewModel();
QualityRequest? requested = null;
tiers.QualityChangeRequested += r => requested = r;
tiers.SetQualityState(10, forceTranscode: true, settingsMaxBitrateMbps: 20);
Check(tiers.QualityTierIndex == 3 && tiers.QualityForceTranscode, "a choice made before Play shows as the player's tier and switch");
tiers.SetQualityState(8, forceTranscode: false, settingsMaxBitrateMbps: 20);
Check(tiers.QualityTierIndex == 0, "a cap outside the tiers shows as the Settings default");
tiers.SetQualityState(null, forceTranscode: false, settingsMaxBitrateMbps: 20);
tiers.QualityTierIndex = 1;
Check(requested == new QualityRequest(0, false), "picking Original asks for the original file");
tiers.QualityForceTranscode = true;
Check(requested == new QualityRequest(0, true), "Always transcode keeps the tier and forces a transcode");

// --- What Play starts with -------------------------------------------------------------------------
var plain = new PlaybackChoice(null, null, null, TimeSpan.Zero, null);
Check(plain.MaxBitrateMbps is null && !plain.ForceTranscode, "Play without picks uses the Settings bitrate and allows direct play");
var forced = new PlaybackChoice("v2", 1, 3, TimeSpan.FromMinutes(5), 10, ForceTranscode: true).AtOriginalQuality();
Check(forced is { MaxBitrateMbps: 0, ForceTranscode: false, VersionId: "v2", AudioIndex: 1, SubtitleIndex: 3 }, "playing at original quality drops Always transcode and keeps the other picks");

// --- Version drop-down -----------------------------------------------------------------------------
Check(DetailModel.ShowsVersions(2, null), "a title its list entry says has two versions shows the drop-down from the start");
Check(!DetailModel.ShowsVersions(null, null) && !DetailModel.ShowsVersions(1, null), "a single version shows no drop-down");
Check(DetailModel.ShowsVersions(null, 2) && !DetailModel.ShowsVersions(2, 1), "the loaded versions decide once they are known");
MediaVersion Version(string id, string label, int bitrate) => new(id, label, "mkv", bitrate, 1920, 1080, null, false);
var versions = new[] { Version("a", "1080p", 4_200_000), Version("b", "1080p", 9_000_000), Version("c", "4K", 40_000_000) };
Check(DetailModel.VersionName(versions[0], versions) == "1080p · 4.2 Mbps" && DetailModel.VersionName(versions[2], versions) == "4K", "a version is named by its label, with its bitrate when another shares the label");

// --- More like this ------------------------------------------------------------------------------
var drama = new MediaItem { Id = Guid.NewGuid(), Name = "x", Type = BaseItemDto_Type.Movie, Genres = ["Drama", "Music"] };
var like = DetailModel.LikeThis(drama);
Check(like.Types.SequenceEqual([LightWeaver.Search.SearchType.Movies]) && like.Genres.SequenceEqual(["Drama"]), "More like this searches movies in the first genre");
var show = new MediaItem { Id = Guid.NewGuid(), Name = "x", Type = BaseItemDto_Type.Series };
Check(DetailModel.LikeThis(show).Types.SequenceEqual([LightWeaver.Search.SearchType.Shows]) && DetailModel.LikeThis(show).Genres.Count == 0, "a show without genres searches shows only");

// --- Media info ----------------------------------------------------------------------------------
// The server fills these; the SDK model only lets its deserializer set them.
MediaStream ServerComputed(MediaStream stream, params (string Property, object Value)[] values)
{
    foreach (var (property, value) in values) typeof(MediaStream).GetProperty(property)!.SetValue(stream, value);
    return stream;
}
var main = new MediaSourceInfo
{
    Name = "2160p", Container = "mkv", Path = "/media/movie.mkv", Size = 8_100_000_000, Bitrate = 24_500_000,
    MediaStreams =
    [
        ServerComputed(new MediaStream
        {
            Type = MediaStream_Type.Video, Codec = "hevc", Profile = "Main 10", Width = 3840, Height = 2160,
            RealFrameRate = 23.976f, BitRate = 22_000_000, BitDepth = 10, IsInterlaced = false,
        }, ("DisplayTitle", "4K HEVC Dolby Vision"), ("VideoRangeType", MediaStream_VideoRangeType.DOVIWithHDR10)),
        new MediaStream { Type = MediaStream_Type.Audio, Codec = "truehd", ChannelLayout = "7.1", Language = "eng", BitRate = 4_000_000, SampleRate = 48000, IsDefault = true },
        new MediaStream { Type = MediaStream_Type.Audio, Codec = "ac3", ChannelLayout = "5.1", Language = "ger", BitRate = 640_000, IsDefault = false },
        new MediaStream { Type = MediaStream_Type.Subtitle, Codec = "srt", Language = "eng", IsExternal = true, IsForced = true },
    ],
};
var info = MediaInfoModel.Build([main, new MediaSourceInfo { Container = "mp4", MediaStreams = [] }]);
string? Value(MediaInfoSection section, string label) => section.Lines.FirstOrDefault(l => l.Label == label)?.Value;
Check(info.Count == 2 && info[0].Name == "2160p" && info[1].Name == "Version 2", "every version is listed, an unnamed one by number");
var file = info[0].Sections[0];
Check(file.Title == "File" && Value(file, "Container") == "MKV" && Value(file, "Size") == "7.54 GB" && Value(file, "Bitrate") == "24.5 Mbps"
    && Value(file, "Path") == "/media/movie.mkv", "the file block: container, path, size, bitrate");
Check(info[0].Sections.Select(s => s.Title).SequenceEqual(["File", "Video", "Audio 1", "Audio 2", "Subtitle"]), "one block per stream, numbered when there are several");
var video = info[0].Sections[1];
Check(Value(video, "Codec") == "HEVC" && Value(video, "Profile") == "Main 10" && Value(video, "Resolution") == "3840x2160" && Value(video, "Frame rate") == "23.976 fps"
    && Value(video, "Video range") == "Dolby Vision / HDR10" && Value(video, "Bitrate") == "22.0 Mbps" && Value(video, "Bit depth") == "10 bit"
    && Value(video, "Interlaced") == "No", "the video block: codec, profile, resolution, frame rate, range, bitrate");
var german = info[0].Sections[3];
Check(Value(german, "Codec") == "AC3" && Value(german, "Channel layout") == "5.1" && Value(german, "Language") == "ger" && Value(german, "Bitrate") == "640 kbps"
    && Value(german, "Default") == "No", "an audio block: codec, layout, language, bitrate, default flag");
Check(Value(info[0].Sections[2], "Sample rate") == "48 kHz" && Value(info[0].Sections[2], "Default") == "Yes", "the default audio stream says so");
var subtitle = info[0].Sections[4];
Check(Value(subtitle, "Codec") == "SRT" && Value(subtitle, "External") == "Yes" && Value(subtitle, "Forced") == "Yes", "a subtitle block: codec, external, forced");
Check(info[1].Sections.Count == 1 && Value(info[1].Sections[0], "Container") == "MP4", "a version without streams has its file block only");
Check(MediaInfoModel.Build([]).Count == 0, "no sources, no media info");
var text = MediaInfoModel.AsText("Movie", info);
Check(text.StartsWith("Movie") && text.Contains("2160p") && text.Contains("  Codec: HEVC") && text.Contains("Version 2"), "the copied text carries every block");
Check(!MediaInfoModel.AsText("Movie", [info[0]]).Contains("2160p"), "a single version is not named in the copied text");
Check(MediaInfoModel.Size(700L << 20) == "700 MB" && MediaInfoModel.Bitrate(320_000) == "320 kbps", "small sizes and bitrates");
Check(MediaInfoModel.RangeType("HDR10Plus") == "HDR10+" && MediaInfoModel.RangeType("SDR") == "SDR" && MediaInfoModel.RangeType("Unknown") is null, "range types read as people know them");

Console.WriteLine($"{checks} checks passed.");
return 0;
