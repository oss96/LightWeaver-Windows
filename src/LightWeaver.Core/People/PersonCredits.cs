using System.Globalization;
using Jellyfin.Sdk.Generated.Models;
using LightWeaver.Jellyfin;
using LightWeaver.Localization;

namespace LightWeaver.People;

/// <summary>One credit of a person: a title in one role, with the character for an actor.</summary>
public sealed record Credit(MediaItem Item, string Role, string? Character);

/// <summary>How the filmography is ordered (design 07).</summary>
public enum CreditSort { Newest, Oldest, Name }

/// <summary>The person page's rules (design 07): credits grouped by role in a fixed order, the
/// role filter's counts, card lines and the header's roles line.</summary>
public static class PersonCredits
{
    public const string Actor = "Actor";

    /// <summary>Roles come in this order; any other role follows, by name.</summary>
    public static readonly IReadOnlyList<string> RoleOrder = [Actor, "Director", "Writer", "Producer", "Composer"];

    /// <summary>A person as a card item (cast rows, search): the role rides in the overview.</summary>
    public static MediaItem AsItem(PersonEntry person) => new()
    {
        Id = person.Id, Name = person.Name, Type = BaseItemDto_Type.Person, PosterUrl = person.ImageUrl, Overview = person.Role,
    };

    /// <summary>Every credit of the person across the titles; a title with several roles appears
    /// once per role. Guest stars count as actors.</summary>
    public static IReadOnlyList<Credit> From(IEnumerable<MediaItem> titles, Guid personId)
    {
        var credits = new List<Credit>();
        foreach (var title in titles)
        {
            foreach (var person in title.People.Where(p => p.Id == personId))
            {
                var role = Normalize(person.Kind ?? person.Role);
                if (credits.Any(c => c.Item.Id == title.Id && c.Role == role)) continue;
                var character = role == Actor && person.Role is { Length: > 0 } r && !string.Equals(r, person.Kind, StringComparison.OrdinalIgnoreCase) && r != Actor ? r : null;
                credits.Add(new Credit(title, role, character));
            }
        }
        return credits;
    }

    /// <summary>The role filter: each role with its count, in <see cref="RoleOrder"/>.</summary>
    public static IReadOnlyList<(string Role, int Count)> Roles(IEnumerable<Credit> credits)
        => [.. credits.GroupBy(c => c.Role).OrderBy(g => Rank(g.Key)).ThenBy(g => g.Key, StringComparer.CurrentCulture).Select(g => (g.Key, g.Count()))];

    /// <summary>The sections shown: every role as its own section, or only <paramref name="role"/>.</summary>
    public static IReadOnlyList<(string Role, IReadOnlyList<Credit> Credits)> Sections(IEnumerable<Credit> credits, string? role, CreditSort sort)
        => [.. Roles(credits.Where(c => role is null || c.Role == role)).Select(r => (r.Role, Sorted(credits.Where(c => c.Role == r.Role), sort)))];

    public static IReadOnlyList<Credit> Sorted(IEnumerable<Credit> credits, CreditSort sort) => sort switch
    {
        CreditSort.Oldest => [.. credits.OrderBy(c => Released(c.Item)).ThenBy(c => c.Item.SortName ?? c.Item.Name, StringComparer.CurrentCultureIgnoreCase)],
        CreditSort.Name => [.. credits.OrderBy(c => c.Item.SortName ?? c.Item.Name, StringComparer.CurrentCultureIgnoreCase)],
        _ => [.. credits.OrderByDescending(c => Released(c.Item)).ThenBy(c => c.Item.SortName ?? c.Item.Name, StringComparer.CurrentCultureIgnoreCase)],
    };

    /// <summary>The card line: "as Mara Quill · 2025" for an actor, else the year.</summary>
    public static string Subtitle(Credit credit)
    {
        var year = credit.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "";
        if (credit.Character is not { } character) return year;
        return year.Length > 0 ? L.Format("Person.AsCharacterYear", character, year) : L.Format("Person.AsCharacter", character);
    }

    /// <summary>A role's name in the interface language; unknown roles keep the server's word.</summary>
    public static string RoleLabel(string role) => role switch
    {
        Actor => L.Text("Person.RoleActor"),
        "Director" => L.Text("Person.RoleDirector"),
        "Writer" => L.Text("Person.RoleWriter"),
        "Producer" => L.Text("Person.RoleProducer"),
        "Composer" => L.Text("Person.RoleComposer"),
        _ => role,
    };

    /// <summary>"Actor · Director · Writer" under the name.</summary>
    public static string RolesLine(IEnumerable<Credit> credits) => string.Join(" · ", Roles(credits).Select(r => RoleLabel(r.Role)));

    /// <summary>"Born 12 March 1978 · Lisbon"; empty when neither is known.</summary>
    public static string BornLine(DateTimeOffset? birth, string? place)
    {
        var date = birth?.ToString("D", CultureInfo.CurrentUICulture);
        return (date, place is { Length: > 0 } ? place : null) switch
        {
            ({ } d, { } p) => L.Format("Person.BornOnIn", d, p),
            ({ } d, null) => L.Format("Person.BornOn", d),
            (null, { } p) => L.Format("Person.BornIn", p),
            _ => "",
        };
    }

    private static string Normalize(string? kind) => kind switch
    {
        null or "" or "GuestStar" => Actor,
        _ => kind,
    };

    private static int Rank(string role)
    {
        for (var i = 0; i < RoleOrder.Count; i++)
            if (RoleOrder[i] == role) return i;
        return RoleOrder.Count;
    }

    private static DateTimeOffset Released(MediaItem item)
        => item.PremiereDate ?? (item.ProductionYear is { } y ? new DateTimeOffset(y, 1, 1, 0, 0, 0, TimeSpan.Zero) : DateTimeOffset.MinValue);
}
