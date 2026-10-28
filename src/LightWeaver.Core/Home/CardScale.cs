namespace LightWeaver.Home;

/// <summary>How much the Home and detail rows grow on a large window: <see cref="Cards"/> scales
/// card widths and the gaps around them, <see cref="Text"/> the card captions and row headings.</summary>
public readonly record struct CardScale(double Cards, double Text)
{
    /// <summary>The smallest change of <see cref="Cards"/> that lays the rows out again.</summary>
    public const double Step = 0.02;

    /// <summary>How much wider than its decoded artwork a card may grow before the artwork loads again.</summary>
    public const double ArtGrowth = 0.15;

    /// <summary>The design as drawn, for windows up to the start width.</summary>
    public static CardScale None { get; } = new(1, 1);

    /// <summary>The scale for a window <paramref name="width"/> DIP wide: none up to
    /// <paramref name="from"/>, then width ÷ <paramref name="from"/> up to <paramref name="max"/>;
    /// text grows by <paramref name="textShare"/> of the cards' growth.</summary>
    public static CardScale For(double width, double from, double max, double textShare)
    {
        if (from <= 0 || width <= from) return None;
        var cards = Math.Min(max, width / from);
        return new CardScale(cards, 1 + (cards - 1) * textShare);
    }

    /// <summary>Whether <paramref name="next"/> should replace this scale: a change of at least
    /// <see cref="Step"/>, or reaching no scale or <paramref name="max"/>, so a window drag lays the
    /// rows out in steps and both ends are exact.</summary>
    public bool MovesTo(CardScale next, double max)
        => Math.Abs(next.Cards - Cards) >= Step || next.Cards != Cards && (next.Cards == 1 || next.Cards == max);

    /// <summary>A card <paramref name="width"/> DIP wide in the design, scaled and rounded to whole DIP.</summary>
    public double Width(double width) => Math.Round(width * Cards);

    /// <summary>A gap or spacing of <paramref name="length"/> DIP in the design, scaled.</summary>
    public double Space(double length) => length * Cards;

    /// <summary>A font size of <paramref name="size"/> in the design, scaled.</summary>
    public double Font(double size) => size * Text;

    /// <summary>Whether a card now <paramref name="shownWidth"/> physical pixels wide needs its
    /// artwork decoded again: it is more than <see cref="ArtGrowth"/> wider than the
    /// <paramref name="decodedWidth"/> it was decoded for.</summary>
    public static bool NeedsSharperArt(int decodedWidth, double shownWidth)
        => decodedWidth > 0 && shownWidth > decodedWidth * (1 + ArtGrowth);
}
