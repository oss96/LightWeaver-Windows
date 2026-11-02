namespace LightWeaver.Settings;

public static class HomeSectionOrder
{
    public static (HomeSectionId Id, bool After)? DropTarget(IReadOnlyList<(HomeSectionId Id, double Top, double Height)> rows, double y)
    {
        if (!double.IsFinite(y) || rows.Count == 0) return null;
        foreach (var row in rows)
            if (y < row.Top + row.Height / 2) return (row.Id, false);
        return (rows[^1].Id, true);
    }

    public static List<HomeSectionPref> Move(IReadOnlyList<HomeSectionPref> layout, HomeSectionId id, HomeSectionId target, bool after)
    {
        var result = layout.ToList();
        var rows = layout.Where(p => p.Id != HomeSectionId.Libraries).ToList();
        var source = rows.FindIndex(p => p.Id == id);
        if (id == target || source < 0 || !rows.Any(p => p.Id == target)) return result;
        var moved = rows[source];
        rows.RemoveAt(source);
        rows.Insert(rows.FindIndex(p => p.Id == target) + (after ? 1 : 0), moved);
        var index = 0;
        for (var i = 0; i < result.Count; i++)
            if (result[i].Id != HomeSectionId.Libraries) result[i] = rows[index++];
        return result;
    }
}
