using LightWeaver.Jellyfin;

namespace LightWeaver.Library;

/// <summary>The user-data fields a card changes on the server.</summary>
public enum UserDataField { Played, Favorite }

/// <summary>Watched and favourite state the user changed this session. <see cref="MediaItem"/> is
/// immutable and cards are recycled, so the page keeps these and every card reads them. Each
/// field of each title is tracked on its own: its changes reach the server one after another in
/// the order the user made them, a refused change is undone only while it is the latest, and an
/// answer from the server replaces the field once its changes have settled (see <see cref="Reconcile"/>).</summary>
public sealed class UserDataOverrides
{
    private sealed class FieldState
    {
        public bool? Value;
        public bool Confirmed;
        public int Version;
        public int Pending;
        public long SettledAt;
        public Task Tail = Task.CompletedTask;
    }

    private readonly Dictionary<(Guid Id, UserDataField Field), FieldState> _state = [];
    private long _clock;

    public (bool Played, bool Favorite) Of(MediaItem item)
        => (Value(item.Id, UserDataField.Played) ?? item.Played, Value(item.Id, UserDataField.Favorite) ?? item.IsFavorite);

    /// <summary>Shows <paramref name="value"/> at once and sends it after the field's earlier
    /// changes for the item. When the server refuses it (or the request throws) and no newer change
    /// to the field has been made, the field returns to the last value the server accepted. True
    /// when the server accepted it.</summary>
    public async Task<bool> ChangeAsync(MediaItem item, UserDataField field, bool value, Func<Task<bool>> send)
    {
        var key = (item.Id, field);
        if (!_state.TryGetValue(key, out var state)) _state[key] = state = new FieldState();
        if (state.Pending == 0)
        {
            var (played, favorite) = Of(item);
            state.Confirmed = field == UserDataField.Played ? played : favorite;
        }
        var version = ++state.Version;
        state.Value = value;
        state.Pending++;
        var request = SendAfterAsync(state.Tail, send);
        state.Tail = request;
        bool accepted;
        try { accepted = await request; }
        finally
        {
            state.Pending--;
            state.SettledAt = ++_clock;
        }
        if (accepted) state.Confirmed = value;
        else if (version == state.Version) state.Value = state.Confirmed;
        return accepted;
    }

    /// <summary>The moment a query is sent; pass it to <see cref="Reconcile"/> with its answer.</summary>
    public long Mark() => ++_clock;

    /// <summary>Drops, field by field, the changes the answer to a query sent at
    /// <paramref name="sentAt"/> already carries: those for its items with nothing on its way to the
    /// server that settled before the query left.</summary>
    public void Reconcile(long sentAt, IEnumerable<MediaItem> answer)
    {
        foreach (var item in answer)
            foreach (var field in Enum.GetValues<UserDataField>())
                if (_state.TryGetValue((item.Id, field), out var state) && state.Pending == 0 && state.SettledAt < sentAt)
                    _state.Remove((item.Id, field));
    }

    private bool? Value(Guid id, UserDataField field) => _state.TryGetValue((id, field), out var state) ? state.Value : null;

    private static async Task<bool> SendAfterAsync(Task previous, Func<Task<bool>> send)
    {
        await previous;
        try { return await send(); }
        catch (Exception) { return false; }
    }
}
