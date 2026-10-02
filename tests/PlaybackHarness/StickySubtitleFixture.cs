using System.Reflection;
using LightWeaver.Player;

/// <summary>
/// Proves a remembered subtitle re-resolves to the same VARIANT in the next file, not merely to
/// the same language. Track ids are per-file and <c>loadfile</c> resets <c>sid</c>, so the sticky
/// choice is a description that gets matched again per file; when the match only knew language and
/// forced-ness, a file holding both "English" and "English (SDH)" resolved to whichever the
/// container listed first, and the pick appeared to be forgotten one episode later.
/// <para>Offscreen by construction: it builds <see cref="MpvTrack"/> rows by hand and calls the
/// production matcher directly, so there is no Application, no mpv, no window and no network. The
/// first two legs assert their track list in both orders, because a matcher that ignores the title
/// passes the SDH leg by accident whenever the container happens to list SDH first; the later legs
/// are order-insensitive by construction.</para>
/// </summary>
internal static class StickySubtitleFixture
{
    private static MethodInfo? _matcher;

    /// <summary>Resolved on first use inside a leg, never in a static initializer: a rename or a
    /// changed arity has to arrive as this fixture's own FAIL line, not as a
    /// TypeInitializationException thrown out of <c>Run</c> before any leg has reported.</summary>
    private static MethodInfo Matcher => _matcher ??=
        typeof(LightWeaver.MainWindow).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == "MatchStickySubtitle" && method.GetParameters().Length == 4)
        ?? throw new InvalidOperationException(
            "MainWindow has no private static MatchStickySubtitle taking four parameters; it was "
            + "renamed or its signature changed, so this fixture is testing nothing.");

    /// <param name="test">The harness's own test recorder.</param>
    public static void Run(Action<string, Action> test)
    {
        // The reported bug. English and English (SDH) differ in nothing but the title, so this is
        // the leg that fails on a language-only match chain — in one order or the other.
        test("a remembered SDH pick re-resolves to SDH, not its plain-language sibling", () =>
        {
            MpvTrack[] tracks = [Sub(1, "English", "eng"), Sub(2, "English (SDH)", "eng")];
            Expect("English (SDH)", Match(tracks, "eng", "English (SDH)", false), "container order");
            Expect("English (SDH)", Match([.. tracks.Reverse()], "eng", "English (SDH)", false), "reversed order");
        });

        test("a remembered plain-language pick re-resolves to plain, not SDH", () =>
        {
            MpvTrack[] tracks = [Sub(1, "English (SDH)", "eng"), Sub(2, "English", "eng")];
            Expect("English", Match(tracks, "eng", "English", false), "container order");
            Expect("English", Match([.. tracks.Reverse()], "eng", "English", false), "reversed order");
        });

        // A forced track is a different kind of subtitle, not a variant of the same one, so it
        // outranks a title that happens to match a full track.
        test("forced-ness still outranks a matching title", () =>
        {
            MpvTrack[] tracks = [Sub(1, "English", "eng"), Sub(2, "English (Forced)", "eng", forced: true)];
            Expect("English (Forced)", Match(tracks, "eng", "English", true), "forced pick");
            Expect("English", Match(tracks, "eng", "English (Forced)", false), "non-forced pick");
        });

        // The mirror of the leg above, and the reason forced-ness is only the second tier rather
        // than a hard filter: when the file's only English track is forced, taking it beats leaving
        // the episode bare. Unchanged by the variant fix — a guard over an untested tier.
        test("a language-only match still wins over no subtitle at all", () =>
        {
            MpvTrack[] tracks = [Sub(1, "Deutsch", "deu"), Sub(2, "English (Forced)", "eng", forced: true)];
            Expect("English (Forced)", Match(tracks, "eng", "English", false), "forced-only file");
        });

        // Title alone is the last resort, so a same-language track outranks a different language
        // that happens to carry the remembered title.
        test("a same-language track outranks another language wearing the remembered title", () =>
        {
            MpvTrack[] tracks = [Sub(1, "Full", "deu"), Sub(2, "Complete", "eng")];
            Expect("Complete", Match(tracks, "eng", "Full", false), "title on the wrong language");
        });

        // The next file names its tracks differently. Falling through to language keeps the
        // subtitle on rather than silently leaving the episode unsubtitled.
        test("a title the new file does not carry falls back to the language", () =>
        {
            MpvTrack[] tracks = [Sub(1, "Full", "eng"), Sub(2, "Deutsch", "deu")];
            Expect("Full", Match(tracks, "eng", "English (SDH)", false), "renamed track");
        });

        // The external-subtitle case: a sidecar file often arrives with a title and no language tag.
        test("a remembered title alone still matches when no language was recorded", () =>
        {
            MpvTrack[] tracks = [Sub(1, "Deutsch", "deu"), Sub(2, "English (SDH)", null)];
            Expect("English (SDH)", Match(tracks, null, "English (SDH)", false), "language-less pick");
            Expect("English (SDH)", Match(tracks, "", "English (SDH)", false), "empty language");
        });

        // No counterpart at all has to stay NoMatch, so the caller leaves mpv's own default
        // selection alone instead of forcing the wrong track on.
        test("no same-language track leaves mpv's default alone", () =>
        {
            MpvTrack[] tracks = [Sub(1, "English", "eng"), Sub(2, "Deutsch", "deu")];
            if (Match(tracks, "jpn", "Japanese", false) is { } wrong)
                throw new InvalidOperationException(
                    $"Expected no match for a language this file does not carry, got '{wrong.Title}' "
                    + $"(id {wrong.Id}, lang {wrong.Lang}).");
        });
    }

    private static MpvTrack Sub(int id, string? title, string? lang, bool forced = false) =>
        new(id, "sub", title, lang, false, false, forced);

    private static MpvTrack? Match(IReadOnlyList<MpvTrack> subs, string? lang, string? title, bool forced) =>
        (MpvTrack?)Matcher.Invoke(null, [subs, lang, title, forced]);

    private static void Expect(string title, MpvTrack? actual, string what)
    {
        if (actual is null)
            throw new InvalidOperationException($"{what}: expected '{title}', got no match at all.");
        if (!string.Equals(actual.Title, title, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{what}: expected '{title}', got '{actual.Title}' (id {actual.Id}, lang {actual.Lang}, "
                + $"forced {actual.Forced}).");
    }
}
