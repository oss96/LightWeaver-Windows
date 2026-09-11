using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LightWeaver.Jellyfin;

/// <summary>Server session persisted between runs. Token only — never the password.
/// ServerName is display-only (switcher UI); null on profiles saved before v2.</summary>
public sealed record SavedCredentials(string ServerUrl, string Username, string AccessToken,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(LenientGuidConverter))] Guid UserId,
    string? ServerName = null);

/// <summary>
/// Reads a Guid in either form. Jellyfin returns <c>User.Id</c> as **32 hex chars with no dashes**,
/// and <c>System.Text.Json</c>'s built-in Guid reader rejects that — it throws, `LoadProfiles`
/// catches `JsonException` as "corrupt/foreign file", and the user silently lands on the login form
/// with no indication why. The app never writes that form itself, so this only bit hand-seeded
/// files: it cost six CI dispatches on the Windows runner before being worked around there.
/// Being lenient on READ costs nothing (writes still emit the dashed form) and removes a
/// failure mode whose symptom is indistinguishable from an expired token.
/// </summary>
public sealed class LenientGuidConverter : System.Text.Json.Serialization.JsonConverter<Guid>
{
    public override Guid Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
        => reader.TokenType == System.Text.Json.JsonTokenType.String
            && Guid.TryParse(reader.GetString(), out var parsed)
            ? parsed
            : reader.TokenType == System.Text.Json.JsonTokenType.String
                ? throw new System.Text.Json.JsonException($"not a Guid in any recognised form")
                : throw new System.Text.Json.JsonException($"expected a string Guid, got {reader.TokenType}");

    // Always the canonical dashed form, so what we write stays unambiguous.
    public override void Write(System.Text.Json.Utf8JsonWriter writer, Guid value,
        System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("D"));
}

/// <summary>All saved profiles plus which one is active (Phase 5 M7).</summary>
public sealed record CredentialProfiles(List<SavedCredentials> Profiles, int ActiveIndex)
{
    public SavedCredentials? Active => ActiveIndex >= 0 && ActiveIndex < Profiles.Count
        ? Profiles[ActiveIndex]
        : null;
}

/// <summary>
/// Persists credentials with Windows DPAPI (CurrentUser scope — tied to this Windows
/// account, no key management of our own) and the random device id Jellyfin sessions
/// are keyed by (a GUID, so the Windows username never leaks to the server).
///
/// v2 payload (Phase 5 M7): {"Profiles":[…],"ActiveIndex":n}. A v1 file (single
/// credentials object) is read transparently as a one-profile store and only
/// rewritten on the next save — migration can't lose the original on a crash.
/// </summary>
public static class CredentialStore
{
    private static readonly string Dir = AppPaths.Root;
    private static readonly string CredentialsPath = Path.Combine(Dir, "credentials.dat");
    private static readonly string DeviceIdPath = Path.Combine(Dir, "device-id");

    private sealed record StoreDocument(List<SavedCredentials> Profiles, int ActiveIndex);

    public static CredentialProfiles LoadProfiles()
    {
        try
        {
            if (!File.Exists(CredentialsPath))
            {
                Diagnostics.AppLog.Detail("credentials", "event=load outcome=absent profiles=0");
                return new CredentialProfiles([], -1);
            }
            var encrypted = File.ReadAllBytes(CredentialsPath);
            var json = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Profiles", out _))
            {
                var store = JsonSerializer.Deserialize<StoreDocument>(json);
                // Counts and the schema version only. Nothing decrypted from this file — token,
                // username, server URL — may reach a log line, so there is no per-profile record.
                LoadDetail("v2", store?.Profiles.Count ?? 0);
                return store is { Profiles.Count: > 0 }
                    ? new CredentialProfiles(store.Profiles, Math.Clamp(store.ActiveIndex, 0, store.Profiles.Count - 1))
                    : new CredentialProfiles([], -1);
            }
            // v1: a single SavedCredentials object.
            var single = JsonSerializer.Deserialize<SavedCredentials>(json);
            LoadDetail("v1", single is null ? 0 : 1);
            return single is null
                ? new CredentialProfiles([], -1)
                : new CredentialProfiles([single], 0);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Corrupt/foreign file — treat as logged out. This is the silent path that drops a user
            // back to the login form for no visible reason, so it is an unconditional line; the
            // exception TYPE alone distinguishes the three real causes (wrong Windows account,
            // truncated file, locked file) and none of them needs the message or the path.
            Diagnostics.AppLog.Error("credentials",
                $"event=load outcome=unreadable error={ex.GetType().Name}");
            return new CredentialProfiles([], -1);
        }
    }

    private static void LoadDetail(string schema, int profiles)
        => Diagnostics.AppLog.Detail("credentials", FormattableString.Invariant(
            $"event=load outcome={(profiles > 0 ? "success" : "empty")} schema={schema} profiles={profiles}"));

    public static void SaveProfiles(CredentialProfiles profiles)
    {
        if (profiles.Profiles.Count == 0)
        {
            Clear();
            return;
        }
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new StoreDocument(profiles.Profiles, profiles.ActiveIndex));
        var encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        // Atomic write: never leave a torn file behind.
        var tmp = CredentialsPath + ".tmp";
        File.WriteAllBytes(tmp, encrypted);
        File.Move(tmp, CredentialsPath, overwrite: true);
        Diagnostics.AppLog.Detail("credentials", FormattableString.Invariant(
            $"event=save outcome=success profiles={profiles.Profiles.Count} active={profiles.ActiveIndex} bytes={encrypted.Length}"));
    }

    /// <summary>The active profile (auto-login target), or null when logged out.</summary>
    public static SavedCredentials? Load() => LoadProfiles().Active;

    /// <summary>Upserts the profile (same server + username = same profile) and makes
    /// it active.</summary>
    public static void Save(SavedCredentials credentials)
    {
        var store = LoadProfiles();
        var list = store.Profiles;
        var idx = list.FindIndex(p =>
            string.Equals(p.ServerUrl, credentials.ServerUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Username, credentials.Username, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            list[idx] = credentials;
        else
        {
            list.Add(credentials);
            idx = list.Count - 1;
        }
        SaveProfiles(new CredentialProfiles(list, idx));
    }

    /// <summary>Removes the active profile (logout); other profiles stay saved.</summary>
    public static void RemoveActive()
    {
        var store = LoadProfiles();
        if (store.Active is null)
            return;
        var list = store.Profiles;
        list.RemoveAt(store.ActiveIndex);
        SaveProfiles(new CredentialProfiles(list, list.Count > 0 ? 0 : -1));
    }

    public static void Clear()
    {
        try
        {
            File.Delete(CredentialsPath);
            Diagnostics.AppLog.Detail("credentials", "event=clear outcome=success");
        }
        catch (IOException ex)
        {
            // Best effort — but a store that could not be deleted means the app will auto-log-in
            // again on the next launch after an explicit log out, which is worth a line.
            Diagnostics.AppLog.Error("credentials",
                $"event=clear outcome=failure error={ex.GetType().Name}");
        }
    }

    /// <summary>Stable random device id (not a secret — stored in plain text).</summary>
    public static string GetOrCreateDeviceId()
    {
        try
        {
            if (File.Exists(DeviceIdPath))
            {
                var existing = File.ReadAllText(DeviceIdPath, Encoding.UTF8).Trim();
                if (Guid.TryParse(existing, out var parsed))
                    return parsed.ToString();
            }
        }
        catch (IOException)
        {
        }

        var id = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Dir);
        File.WriteAllText(DeviceIdPath, id, Encoding.UTF8);
        return id;
    }
}
