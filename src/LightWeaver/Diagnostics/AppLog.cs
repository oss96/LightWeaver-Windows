using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace LightWeaver.Diagnostics;

/// <summary>
/// The app's own on-disk diagnostics, in <c>%LOCALAPPDATA%\LightWeaver\logs</c> (and the
/// isolated <c>LightWeaver-Debug\logs</c> for Debug builds). Four kinds of file, no logging
/// framework:
///
/// <list type="bullet">
/// <item><c>crash-&lt;ts&gt;.log</c> — one per unhandled exception, with the environment header,
///   the full exception chain and the breadcrumb ring.</item>
/// <item><c>playback-&lt;ts&gt;.log</c> — one per playback failure the app could not recover from,
///   with the stream summary the caller supplies and the same ring.</item>
/// <item><c>app.log</c> — rolling record of errors the app catches and recovers from, plus
///   informational events when <see cref="Verbose"/> is on.</item>
/// <item><c>mpv.log</c> — every mpv message, written only while <see cref="Verbose"/> is on.</item>
/// </list>
///
/// Design rules, each of them load-bearing:
///
/// <para><b>A run where nothing fails writes no file.</b> The breadcrumb ring is memory-only and
/// each file's session banner is written lazily, immediately before that file's first real entry,
/// so a launch that hits no error leaves the folder exactly as it found it. That is what makes it
/// acceptable to have this on by default in Release. A recovered failure (a transcode fallback, a
/// rejected token) does cost one <c>app.log</c> line — an incident FILE means something actually
/// broke.</para>
///
/// <para><b>Every line is redacted.</b> <see cref="Redact"/> runs on all of them, including mpv's
/// and including each file's title line. The Jellyfin access token reaches mpv through
/// <c>http-header-fields</c> (<see cref="Player.MpvPlayer.SetHttpHeaders"/>), so verbose mpv
/// output would otherwise print it into a file whose whole purpose is to be sent to someone else.
/// This is also why the verbose mpv file is written here rather than by mpv's own <c>log-file</c>
/// option, which cannot be filtered — that option stays reachable only through
/// <c>LIGHTWEAVER_MPV_LOG</c> for tests.</para>
///
/// <para><b>Logging entry points may not throw or block real work.</b> Their write paths catch
/// <see cref="Exception"/> rather than a filtered set: this is the one place in the codebase where
/// swallowing everything is correct, because a diagnostics failure must never become the incident
/// — a throw from <see cref="Crash"/> would run on whatever thread was already dying, and a throw from
/// <see cref="MpvLine"/> would run on mpv's event thread. Verbose mpv lines are handed to a
/// background writer for the same reason: file IO measured 364 µs per line, and that thread
/// delivers the playback events the UI waits on. <see cref="ExportTo"/> is not a logging entry
/// point: it is a user-requested filesystem operation and intentionally propagates failures that
/// prevent creating the bundle so Settings can report them.</para>
///
/// The pre-existing <c>LIGHTWEAVER_*_LOG</c> environment hooks are untouched and independent:
/// they are test instrumentation with their own formats, and the suites read them.
/// </summary>
public static class AppLog
{
    /// <summary>Retention: newest N <c>crash-*</c>/<c>playback-*</c> files survive a prune.</summary>
    private const int MaxIncidentFiles = 10;

    /// <summary>Retention: the folder's ceiling. Reachable only as a backstop — see
    /// <see cref="Prune"/> for why the other bounds keep the real total an order of magnitude
    /// below it.</summary>
    private const long MaxFolderBytes = 20L * 1024 * 1024;

    /// <summary>A rolling file rotates to <c>&lt;name&gt;.1.log</c> at this size, one generation
    /// kept. Four rolling files (<c>app</c>, <c>app.1</c>, <c>mpv</c>, <c>mpv.1</c>) therefore
    /// cost at most ~8 MB, which is what leaves <see cref="MaxFolderBytes"/> achievable.</summary>
    private const long RollingMaxBytes = 2L * 1024 * 1024;

    /// <summary>Breadcrumbs held in memory for the crash/playback flush.</summary>
    public const int RingCapacity = 500;

    /// <summary>Per-line ceiling. A single mpv line can be arbitrarily long, and 500 unbounded
    /// lines in each of 10 incident files is how a "bounded" folder stops being bounded.</summary>
    private const int MaxLineChars = 1000;

    private static readonly Lock FileLock = new();
    private static readonly Lock RingLock = new();
    private static readonly string?[] Ring = new string?[RingCapacity];
    private static long _ringWritten;

    /// <summary>Which files have had this session's banner written. Per FILE, not per session: one
    /// shared flag meant whichever of app.log / mpv.log was written first consumed the only
    /// banner and the other got none.</summary>
    private static readonly HashSet<string> BanneredFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The exception already written to a crash file. A UI-thread throw arrives twice —
    /// once via the Dispatcher handler, then again via the AppDomain handler when WPF rethrows —
    /// and two files for one crash burn two of ten retention slots.</summary>
    private static Exception? _lastCrashLogged;

    /// <summary>%LOCALAPPDATA%\LightWeaver\logs (…\LightWeaver-Debug\logs for Debug builds).</summary>
    public static string Dir { get; } = Path.Combine(AppPaths.Root, "logs");

    private static volatile bool _verbose;

    /// <summary>Mirrors <c>AppSettings.VerboseLogging</c>. Set at startup and live-applied when
    /// the setting changes; read by <see cref="Player.MpvPlayer"/> when it picks mpv's log level.
    /// Volatile because the writer is the UI thread and one reader is mpv's event thread.</summary>
    public static bool Verbose
    {
        get => _verbose;
        set => _verbose = value;
    }

    private static string RollingPath => Path.Combine(Dir, "app.log");
    private static string MpvPath => Path.Combine(Dir, "mpv.log");

    // ---- Entry points -----------------------------------------------------------------

    /// <summary>Memory-only breadcrumb: costs no IO and is what a later crash or playback failure
    /// flushes as context. Use for the running commentary — mpv messages, playback and session
    /// transitions — that is only interesting once something has gone wrong.</summary>
    public static void Breadcrumb(string area, string message)
    {
        try
        {
            var line = Stamp(area, message);
            lock (RingLock)
            {
                Ring[(int)(_ringWritten % RingCapacity)] = line;
                _ringWritten++;
            }
        }
        catch (Exception)
        {
            // a lost breadcrumb is never worth a throw on the caller's thread
        }
    }

    /// <summary>A notable-but-fine event: breadcrumb always, <c>app.log</c> only when verbose.</summary>
    public static void Info(string area, string message)
    {
        Breadcrumb(area, message);
        if (_verbose)
            AppendRolling(RollingPath, Stamp(area, message));
    }

    /// <summary>Verbose-only diagnostic detail. Unlike <see cref="Info"/>, this never enters the
    /// incident ring: high-volume state snapshots would otherwise evict the sparse breadcrumbs an
    /// incident needs. The flag check deliberately precedes formatting, redaction and IO so call
    /// sites can remain in hot paths while verbose logging is off.</summary>
    public static void Detail(string area, string message, Exception? ex = null)
    {
        if (!_verbose)
            return;
        try
        {
            var text = new StringBuilder(Stamp(area, message));
            if (ex is not null)
                text.Append(Environment.NewLine).Append(Describe(ex, indent: "    "));
            AppendRolling(RollingPath, text.ToString());
        }
        catch (Exception)
        {
            // diagnostic detail must never become the failure it was meant to explain
        }
    }

    /// <summary>A deterministic, non-reversible short token for an identity a structural record
    /// has to CORRELATE but must never print — the session profile key
    /// (<c>{userId:N}@{serverUrl}</c>) above all, which carries both a server URL and a user id.
    /// Eight hex characters of SHA-256: enough to tell two profiles apart inside one file, far too
    /// little to walk back to the input, and stable across a run so events can be joined.
    ///
    /// <para>No-throw like every other entry point, and it never returns an empty string: a
    /// <c>session=</c> field with nothing after it reads as a formatting bug rather than as a
    /// missing value.</para></summary>
    public static string ShortHash(string? value)
    {
        try
        {
            if (string.IsNullOrEmpty(value))
                return "none";
            return Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant()[..8];
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>An event worth recording even when verbose is off, and not a failure: the
    /// diagnostics settings changing, mainly. Turning verbose OFF has to be one of these — an
    /// <see cref="Info"/> call would be dropped by the very flag it is reporting, leaving the
    /// file to just stop mid-session with no explanation.</summary>
    public static void Milestone(string area, string message)
    {
        Breadcrumb(area, message);
        AppendRolling(RollingPath, Stamp(area, message));
    }

    /// <summary>Something failed. Always reaches <c>app.log</c> — these are the failures the app
    /// catches and recovers from, which is exactly the class that used to leave no trace.</summary>
    public static void Error(string area, string message, Exception? ex = null)
    {
        try
        {
            Breadcrumb(area, ex is null ? $"ERROR {message}" : $"ERROR {message} — {ex.GetType().Name}: {ex.Message}");
            var text = new StringBuilder(Stamp(area, "ERROR " + message));
            if (ex is not null)
                text.Append(Environment.NewLine).Append(Describe(ex, indent: "    "));
            AppendRolling(RollingPath, text.ToString());
        }
        catch (Exception)
        {
        }
    }

    /// <summary>One mpv message, straight from the event thread. Queued for the background writer
    /// and written to <c>mpv.log</c> only while verbose. This does NOT feed the ring: the ring gets
    /// warn/error/fatal lines from the event loop's own <see cref="Breadcrumb"/> call, so each line
    /// has exactly one ring writer.</summary>
    public static void MpvLine(string level, string text)
    {
        if (!_verbose)
            return;
        try
        {
            Enqueue(FormattableString.Invariant($"{DateTime.Now:HH:mm:ss.fff} [{level}] {Clip(Redact(text))}"));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Writes one crash file. The app is dying: this runs on whatever thread threw, so it
    /// must not touch the Dispatcher and must not throw.</summary>
    public static void Crash(string source, Exception ex)
    {
        try
        {
            // Same exception, second handler: log it once. See _lastCrashLogged.
            lock (FileLock)
            {
                if (IsAlreadyLogged(ex))
                    return;
            }
            Breadcrumb("crash", $"unhandled exception from {source}: {ex.GetType().Name}");
            // Claimed only once the file is actually on disk. Setting it before the write meant a
            // failed first write (disk full, ACL, a transient lock) permanently suppressed the
            // second handler — which for a UI-thread throw IS the retry — leaving ZERO crash files.
            if (WriteIncident("crash", $"Unhandled exception ({source})", Describe(ex, indent: string.Empty)))
                lock (FileLock)
                    _lastCrashLogged = ex;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Writes one playback-failure file. <paramref name="summary"/> is the stream state
    /// the caller could see at the moment of failure — the whole point of the file, since mpv's
    /// error string alone ("unrecognized file format") names no codec, no container and no play
    /// method.</summary>
    public static void PlaybackFailure(string reason, IEnumerable<string> summary)
    {
        try
        {
            // No breadcrumb here: MpvPlayer.Say has already put "Playback failed: <reason>" in the
            // ring at the source. Adding one produced two ring entries 3 ms apart differing only in
            // capitalisation.
            var body = new StringBuilder();
            body.Append("Reason: ").AppendLine(Redact(reason));
            body.AppendLine().AppendLine("Stream:");
            foreach (var line in summary)
                body.Append("  ").AppendLine(Redact(line));
            WriteIncident("playback", $"Playback failed: {reason}", body.ToString());
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Called once at startup: applies retention. Deliberately writes nothing.</summary>
    public static void StartSession()
    {
        lock (FileLock)
        {
            try
            {
                Prune();
            }
            catch (Exception)
            {
                // retention is best-effort; a locked file just survives to the next launch
            }
        }
    }

    // ---- Folder surface (the DIAGNOSTICS settings group) -------------------------------

    /// <summary>Total size of the logs folder, for the settings readout.</summary>
    public static long CurrentSizeBytes()
    {
        try
        {
            var dir = new DirectoryInfo(Dir);
            return dir.Exists ? dir.GetFiles("*.log").Sum(f => f.Length) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Deletes every log file. Files still open elsewhere are skipped, not fatal.</summary>
    public static void Clear()
    {
        lock (FileLock)
        {
            try
            {
                var dir = new DirectoryInfo(Dir);
                if (!dir.Exists)
                    return;
                foreach (var f in dir.GetFiles("*.log"))
                    try { f.Delete(); } catch (Exception) { }
                // A cleared file must get this session's banner again before its next entry.
                BanneredFiles.Clear();
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Bundles the logs into one zip for sending to someone (2026-08-05). Returns the
    /// number of log files included, so the caller can tell "nothing to send" from "sent".
    /// <para>Only log files owned by this facility from <see cref="Dir"/> go in, plus a generated
    /// <c>about.txt</c>. Deliberately NOT unrelated <c>*.log</c>, <c>settings.json</c>, or
    /// <c>credentials.dat</c>: AppLog's own files are the surface the redaction rules cover and
    /// <c>verify/tools/RedactCheck</c> guards, so they are the only thing known to be safe to hand
    /// over. Widening this bundle means extending that guard first.</para>
    /// <para>Files are copied while held open — the writer keeps handles — so each is read with
    /// <see cref="FileShare.ReadWrite"/> rather than by <c>ZipFile.CreateFromDirectory</c>, which
    /// opens exclusively and would throw on the file currently being appended to.</para></summary>
    public static int ExportTo(string zipPath)
    {
        Flush();
        var included = 0;
        var dir = new DirectoryInfo(EnsureDir());
        var files = dir.Exists ? dir.GetFiles("*.log").Where(IsOwned).ToArray() : [];
        // Overwrite: the caller has already confirmed the path through a save dialog.
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        using var zip = System.IO.Compression.ZipFile.Open(
            zipPath, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var f in files)
        {
            try
            {
                using var src = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var entry = zip.CreateEntry(f.Name,
                    System.IO.Compression.CompressionLevel.Optimal).Open();
                src.CopyTo(entry);
                included++;
            }
            catch (Exception)
            {
                // One unreadable file must not lose the rest of the bundle.
            }
        }
        try
        {
            using var about = new StreamWriter(zip.CreateEntry("about.txt").Open());
            about.WriteLine("LightWeaver diagnostics bundle");
            about.WriteLine($"exported     : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            about.WriteLine($"app version  : {AppVersion}");
            about.WriteLine($"runtime      : {Environment.Version}");
            about.WriteLine($"os           : {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
            about.WriteLine($"log files    : {included}");
            about.WriteLine($"verbose      : {Verbose}");
            about.WriteLine();
            about.WriteLine("Contains only the app's own log files. Access tokens and the");
            about.WriteLine("Authorization header are redacted where they are written; credentials");
            about.WriteLine("and settings are not included at all.");
        }
        catch (Exception)
        {
        }
        return included;
    }

    /// <summary>Creates the folder if needed and returns it — so "Open folder" works before
    /// anything has ever been logged, which is the normal case.</summary>
    public static string EnsureDir()
    {
        try
        {
            Directory.CreateDirectory(Dir);
        }
        catch (Exception)
        {
        }
        return Dir;
    }

    // ---- The verbose mpv writer -------------------------------------------------------
    //
    // mpv's event thread must not do file IO: measured 364 us per AppendAllText, and one local
    // file at log level `v` produces ~300 lines inside the load burst - about 110 ms of blocking
    // on the thread that delivers FILE_LOADED and PLAYBACK_RESTART, which is what the loading
    // indicator waits on. A network source is chattier. So lines are queued and one background
    // thread drains them in batches, holding a single StreamWriter per batch.

    private static readonly ConcurrentQueue<string> MpvQueue = new();
    private static readonly AutoResetEvent MpvSignal = new(false);
    private static readonly Lock WriterInitLock = new();
    private static readonly Lock DrainLock = new();
    private static Thread? _mpvWriter;
    private static int _mpvDropped;

    /// <summary>Lines per batch. Unbounded batches broke the rolling-file bound: a full 8192-line
    /// queue is several MB in one append, which sails past the rotation check.</summary>
    private const int MpvBatchLines = 2000;

    /// <summary>Bounded on purpose: verbose diagnostics are never worth unbounded memory if the
    /// disk stalls. Overflow is dropped and counted, and the count is written with the next batch
    /// so the file says it is incomplete rather than lying by omission.</summary>
    private const int MpvQueueLimit = 8192;

    private static void Enqueue(string line)
    {
        if (MpvQueue.Count >= MpvQueueLimit)
        {
            Interlocked.Increment(ref _mpvDropped);
            return;
        }
        MpvQueue.Enqueue(line);
        EnsureMpvWriter();
        MpvSignal.Set();
    }

    /// <summary>Creates the writer once. Its own lock, NOT <see cref="FileLock"/>: this runs on
    /// mpv's event thread, and taking the file lock there — even once per process — is exactly the
    /// blocking this queue exists to remove.</summary>
    private static void EnsureMpvWriter()
    {
        if (_mpvWriter is not null)
            return;
        lock (WriterInitLock)
        {
            if (_mpvWriter is not null)
                return;
            _mpvWriter = new Thread(MpvWriterLoop)
            {
                IsBackground = true,
                Name = "lw-mpv-log",
                Priority = ThreadPriority.BelowNormal,
            };
            _mpvWriter.Start();
        }
    }

    private static void MpvWriterLoop()
    {
        while (true)
        {
            // No timeout: the thread sleeps until a line arrives. A 1 s poll meant it kept waking
            // for the life of the process after verbose logging was switched back off.
            MpvSignal.WaitOne();
            try
            {
                DrainMpvQueue();
            }
            catch (Exception)
            {
                // keep the thread alive: a transient IO error must not end verbose logging
            }
        }
    }

    /// <summary>Flushes anything queued. Called at exit and before an incident file is written, so
    /// <c>mpv.log</c> on disk is current at the moment something went wrong.</summary>
    public static void Flush()
    {
        try
        {
            while (!MpvQueue.IsEmpty)
                DrainMpvQueue();
        }
        catch (Exception)
        {
        }
    }

    private static void DrainMpvQueue()
    {
        // Serialized: the writer thread and an incident/exit flush can both be here, and
        // interleaved TryDequeue put mpv.log out of order around exactly the events worth reading.
        lock (DrainLock)
        {
            if (MpvQueue.IsEmpty)
                return;
            var batch = new StringBuilder();
            var dropped = Interlocked.Exchange(ref _mpvDropped, 0);
            if (dropped > 0)
                batch.AppendLine(FormattableString.Invariant(
                    $"---- {dropped} line(s) dropped: the writer could not keep up ----"));
            for (var i = 0; i < MpvBatchLines && MpvQueue.TryDequeue(out var line); i++)
                batch.AppendLine(line);
            if (batch.Length > 0)
                AppendRolling(MpvPath, batch.ToString().TrimEnd('\r', '\n'));
        }
    }

    // ---- Internals --------------------------------------------------------------------

    private static string Stamp(string area, string message) =>
        FormattableString.Invariant($"{DateTime.Now:HH:mm:ss.fff} [{area}] {Clip(Redact(message))}");

    private static string Clip(string text) =>
        text.Length <= MaxLineChars ? text : text[..MaxLineChars] + "…(clipped)";

    /// <summary>
    /// Strips the credentials and personal paths that legitimately appear in diagnostics text.
    ///
    /// <para>The token shapes are deliberately broader than what this app is known to emit: mpv
    /// echoes header and URL values the app never formats itself, a server or plugin can put a
    /// token anywhere in a message, and the cost of an over-broad rule is a redacted word while
    /// the cost of a missing one is a leaked credential in a file meant for sharing. Bare, single-
    /// and double-quoted, JSON and percent-encoded forms are all covered — earlier versions of
    /// these rules missed <c>Token=SECRET</c> without quotes and, through a quantifier bug, failed
    /// to match <c>api_key="SECRET"</c> at all.</para>
    ///
    /// <para>The user profile path is masked too: stack traces carry the build machine's source
    /// paths and app-data errors carry <c>C:\Users\&lt;name&gt;\…</c>, and a real name is personal
    /// data in a file whose purpose is to be sent to someone else.</para>
    /// </summary>
    private static string Redact(string text)
    {
        if (text.Length == 0)
            return text;
        try
        {
            // External providers choose arbitrary parameter names for signed URLs. Remove
            // every query/fragment before the named-secret rules, including URL user-info.
            text = DiagnosticUrl.Replace(text, match =>
            {
                var url = match.Value;
                var suffix = url.IndexOfAny(['?', '#']);
                if (suffix >= 0) url = url[..suffix];
                var authorityStart = url.IndexOf("://", StringComparison.Ordinal) + 3;
                var authorityEnd = url.IndexOf('/', authorityStart);
                if (authorityEnd < 0) authorityEnd = url.Length;
                var at = url.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
                return at < 0 ? url : url[..authorityStart] + url[(at + 1)..];
            });
            foreach (var (pattern, replacement) in RedactionRules)
                text = pattern.Replace(text, replacement);
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            // Redaction is the safety property, so an un-scanned line is dropped, not written.
            return "<line withheld: redaction timed out>";
        }
    }

    private static readonly TimeSpan RedactTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly Regex DiagnosticUrl = new(
        @"https?://[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase, RedactTimeout);

    /// <summary>Secret NAMES, with or without a <c>-</c>/<c>_</c> word separator.</summary>
    private const string SecretNames =
        @"(?:api[-_]?key|access[-_]?token|refresh[-_]?token|token|secret|password|passwd|pwd|pw)";

    /// <summary>
    /// The rules, and the one judgement call in them: a bare <c>name:</c> separator is NOT treated
    /// as a credential, only <c>name=</c> or a QUOTED JSON key.
    ///
    /// <para>That line exists because over-redaction has a real cost in this file. With <c>:</c>
    /// accepted anywhere, the playback log's most important line —
    /// <c>item: The Secret: Dare to Dream [Movie] …</c> — came back as
    /// <c>The Secret: &lt;redacted&gt; to Dream</c>, i.e. the rule ate a film title in the one field
    /// that justifies the file's existence. Prose writes "Secret: " with a space and no quotes;
    /// credentials arrive as <c>key=value</c>, as a quoted JSON pair, or in a named header, and all
    /// three are matched below. The residual gap is an unquoted <c>api_key: value</c> in YAML-ish
    /// text, which nothing in this app or in mpv emits.</para>
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] RedactionRules =
    [
        // name=value / name%3Dvalue, value bare, 'single' or "double" quoted.
        //
        // The lookbehind keeps PATHS intact: `item: D:\Movies\Secret=Agent\a.mkv` was coming back
        // as `D:\Movies\Secret=<redacted>`, eating the filename in the field the playback log
        // exists for. A path separator immediately before the name means this is a path component,
        // not a parameter. The value class deliberately still allows `/` and `\` so that a base64
        // token containing them is redacted WHOLE — narrowing the value to stop at a slash would
        // redact a token's prefix and leak its tail, which is worse than either.
        (new Regex($@"(?i)(?<![\\/])\b({SecretNames})(\s*(?:=|=>|%3[Dd])\s*[""']?)[^""'\s,;)&}}\]]+",
            RegexOptions.Compiled, RedactTimeout), "$1$2<redacted>"),
        // JSON, key quoted: {"AccessToken": "…"}. Jellyfin's auth response is exactly this shape,
        // and an earlier rule missed it because the closing quote of the KEY sat between the name
        // and the colon — while the doc comment claimed the shape was covered.
        (new Regex($@"(?i)(""{SecretNames}""\s*:\s*"")[^""]*",
            RegexOptions.Compiled, RedactTimeout), "$1<redacted>"),
        // Authorization headers, whatever scheme: MediaBrowser Token="…", Bearer …, Basic …
        //
        // Multiline is load-bearing, not tidiness: without it `$` only matches at the end of the
        // whole string, so a header on any line but the last passed through untouched. Measured on
        // "Response status 401\nAuthorization: Bearer SECRET\nContent-Type: …" — and exception
        // messages and mpv output, which is exactly what Describe() and PlaybackFailure() feed
        // through here, are routinely multi-line. `MediaBrowser Client="…", Token="…"` survives via
        // the name=value rule above; the schemes this catches are the ones whose credential is not
        // a named parameter at all — Bearer, Basic, a reverse proxy's own header.
        (new Regex(@"(?i)\b(Authorization\s*:\s*)\S.*$",
            RegexOptions.Compiled | RegexOptions.Multiline, RedactTimeout), "$1<redacted>"),
        (new Regex(@"(?i)\b(X-(?:Emby|MediaBrowser)-(?:Token|Authorization)\s*:\s*)\S+",
            RegexOptions.Compiled, RedactTimeout), "$1<redacted>"),
        // The user's own name, via their profile directory. Either slash: file:// URLs and ffmpeg
        // messages use forward slashes where Windows APIs use backslashes.
        (new Regex(@"(?i)([A-Z]:[\\/]Users[\\/])[^\\/""'\s]+", RegexOptions.Compiled, RedactTimeout),
            "$1<user>"),
    ];

    /// <summary>Type, message and stack for the whole <c>InnerException</c> chain — an
    /// AggregateException's real cause is always one level down.</summary>
    private static string Describe(Exception ex, string indent)
    {
        var sb = new StringBuilder();
        var current = ex;
        var depth = 0;
        while (current is not null && depth < 8)
        {
            var prefix = depth == 0 ? string.Empty : "Caused by: ";
            sb.Append(indent).Append(prefix).Append(current.GetType().FullName)
              .Append(": ").AppendLine(Redact(current.Message));
            if (current.StackTrace is { Length: > 0 } stack)
                foreach (var frame in stack.Split('\n'))
                    sb.Append(indent).Append("  ").AppendLine(Redact(frame.TrimEnd()));
            current = current.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    /// <summary>Reference identity against the last crash written, including one level of
    /// AggregateException unwrapping (the Task path wraps what the other handlers see raw).</summary>
    private static bool IsAlreadyLogged(Exception ex)
    {
        if (_lastCrashLogged is not { } previous)
            return false;
        if (ReferenceEquals(previous, ex))
            return true;
        if (ex is AggregateException agg && agg.InnerExceptions.Any(i => ReferenceEquals(previous, i)))
            return true;
        return previous is AggregateException prevAgg
               && prevAgg.InnerExceptions.Any(i => ReferenceEquals(i, ex));
    }

    /// <summary>One incident file: header, body, then the breadcrumb ring. Returns whether the file
    /// reached disk — <see cref="Crash"/>'s de-duplication depends on knowing that.</summary>
    private static bool WriteIncident(string kind, string title, string body)
    {
        // Flush queued mpv lines so mpv.log on disk is current as of this incident. Note what this
        // does NOT do: those lines go to mpv.log, not into the ring below. The ring's mpv source is
        // the warn/error/fatal breadcrumb raised on mpv's event thread.
        Flush();
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var safeTitle = Clip(Redact(title));
                var text = new StringBuilder();
                text.AppendLine(safeTitle);
                text.AppendLine(new string('=', Math.Min(safeTitle.Length, 100)));
                text.AppendLine();
                foreach (var line in EnvironmentHeader())
                    text.AppendLine(line);
                text.AppendLine();
                text.Append(body);
                text.AppendLine();
                var ring = RingSnapshot();
                text.AppendLine(FormattableString.Invariant(
                    $"Recent activity ({ring.Count} of the last {RingCapacity} entries, oldest first):"));
                if (ring.Count == 0)
                    text.AppendLine("  (nothing recorded)");
                foreach (var line in ring)
                    text.Append("  ").AppendLine(line);
                File.WriteAllText(UniqueIncidentPath(kind), text.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                return false;
            }
            // Retention runs in its OWN try, after the file is safely on disk. Inside the block
            // above, a throw from Prune's directory enumeration would have reported failure for a
            // write that succeeded — which un-suppresses the second crash handler and produces the
            // duplicate file that de-duplication exists to prevent.
            try
            {
                Prune();
            }
            catch (Exception)
            {
            }
            return true;
        }
    }

    /// <summary>A free filename. The stamp is millisecond-resolution and two handlers can fire
    /// inside the same millisecond, where <c>WriteAllText</c> would silently overwrite the first
    /// file with the second.</summary>
    private static string UniqueIncidentPath(string kind)
    {
        var stamp = FormattableString.Invariant($"{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        var path = Path.Combine(Dir, $"{kind}-{stamp}.log");
        for (var n = 2; File.Exists(path) && n < 100; n++)
            path = Path.Combine(Dir, $"{kind}-{stamp}-{n}.log");
        return path;
    }

    /// <summary>What a bug report needs before any stack trace is worth reading.</summary>
    private static IEnumerable<string> EnvironmentHeader()
    {
        yield return FormattableString.Invariant($"Time      : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        yield return $"App       : LightWeaver {AppVersion} ({(AppPaths.IsDebugEnvironment ? "Debug" : "Release")})";
        yield return $"Runtime   : .NET {Environment.Version}";
        yield return $"OS        : {System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                     $"({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";
        yield return $"GPU       : {Player.GpuCapabilities.DesktopAdapterNames}";
        yield return $"Verbose   : {(_verbose ? "on" : "off")}";
        yield return FormattableString.Invariant(
            $"Uptime    : {(DateTime.UtcNow - ProcessStartUtc).TotalSeconds:F1} s");
    }

    private static readonly DateTime ProcessStartUtc = DateTime.UtcNow;

    private static string AppVersion { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The ring in write order, oldest first.</summary>
    private static List<string> RingSnapshot()
    {
        lock (RingLock)
        {
            var count = (int)Math.Min(_ringWritten, RingCapacity);
            var start = _ringWritten <= RingCapacity ? 0 : (int)(_ringWritten % RingCapacity);
            var lines = new List<string>(count);
            for (var i = 0; i < count; i++)
                if (Ring[(start + i) % RingCapacity] is { } line)
                    lines.Add(line);
            return lines;
        }
    }

    /// <summary>Appends one entry to a rolling file, rotating it first if it has grown past
    /// <see cref="RollingMaxBytes"/>. Writes that file's session banner ahead of its first entry
    /// of the run, so an error is attributable to a launch.</summary>
    private static void AppendRolling(string path, string entry)
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                Rotate(path, entry.Length);
                if (BanneredFiles.Add(path))
                {
                    var banner = new StringBuilder();
                    banner.AppendLine();
                    banner.AppendLine(FormattableString.Invariant(
                        $"---- session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ----"));
                    foreach (var line in EnvironmentHeader())
                        banner.AppendLine(line);
                    File.AppendAllText(path, banner.ToString(), new UTF8Encoding(false));
                }
                File.AppendAllText(path, entry + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // best-effort: losing a log line must never surface anywhere
            }
        }
    }

    /// <summary>Rolling files keep exactly one previous generation. <c>app.log</c> rotates to
    /// <c>app.1.log</c>, which is never itself passed here, so it cannot rotate into itself.
    ///
    /// <para>The incoming entry's size is part of the decision. Checking only the file's CURRENT
    /// length let one append cross the limit by however large the entry was, and an mpv batch is
    /// thousands of lines — so the "2 MB" bound was really 2 MB plus one batch.</para></summary>
    private static void Rotate(string path, int incomingChars)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length + incomingChars < RollingMaxBytes)
            return;
        var previous = Path.ChangeExtension(path, null) + ".1.log";
        try
        {
            File.Move(path, previous, overwrite: true);
            // The banner belongs to the live file, and the live file is now empty.
            BanneredFiles.Remove(path);
            return;
        }
        catch (Exception)
        {
        }
        // The move failed — a third party holding the .1 target (AV, a sync agent, a read-only ACL)
        // or the live file itself. The old code swallowed this and appended anyway, so the "bounded
        // by rotation" claim that lets Prune spare live rolling files became false: measured, one
        // held .1.log and 30,000 verbose lines produced a 30.5 MB mpv.log that no pass would touch.
        // Truncating loses this generation's tail, which is the lesser cost of the two, and the
        // marker says so rather than leaving a silent gap.
        try
        {
            File.WriteAllText(path, FormattableString.Invariant(
                $"---- truncated {DateTime.Now:yyyy-MM-dd HH:mm:ss}: could not rotate to {Path.GetFileName(previous)} ----")
                + Environment.NewLine, new UTF8Encoding(false));
            BanneredFiles.Remove(path);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Retention. Callers hold <see cref="FileLock"/>.
    ///
    /// <para>The count cap is the bound that actually fires. The size cap is a backstop, and the
    /// other limits keep the real total well under it: four rolling files at
    /// <see cref="RollingMaxBytes"/> are ~8 MB, and an incident file is
    /// <see cref="RingCapacity"/> × <see cref="MaxLineChars"/> plus a summary — 0.5 MB of ASCII,
    /// up to ~2 MB if every character were multi-byte UTF-8 — across at most
    /// <see cref="MaxIncidentFiles"/> files. So the honest worst case is tens of MB rather than the
    /// 13 MB an earlier version of this comment claimed; what makes that safe is not the
    /// arithmetic but the ORDER below. A size pass has to sacrifice something, and an earlier
    /// version deleted oldest-first regardless of size: it destroyed 15 crash logs to reclaim 84
    /// bytes against an 8 MB overage — measured, not hypothetical.</para>
    ///
    /// <para>So when the backstop does fire it spends the cheapest data first: the previous
    /// rolling generations (superseded by definition), then the largest incident files (the ones
    /// actually responsible for the overage), oldest first among equals. Small crash logs are the
    /// last thing to go, because they are the whole reason this folder exists.</para>
    /// </summary>
    private static void Prune()
    {
        var dir = new DirectoryInfo(Dir);
        if (!dir.Exists)
            return;

        var incidents = dir.GetFiles("crash-*.log")
            .Concat(dir.GetFiles("playback-*.log"))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        foreach (var stale in incidents.Skip(MaxIncidentFiles))
            TryDelete(stale);
        var kept = incidents.Take(MaxIncidentFiles).ToList();

        // Only OUR files count toward the budget, and only our files can be spent to meet it.
        // Measured against the earlier version, which summed every *.log: dropping one unrelated
        // 25 MB file into the folder — and the settings page hands the user an Open folder button —
        // made the size pass delete all five crash logs and reclaim nothing, because the file
        // responsible was in no deletable class. A cap the app cannot enforce must not be paid for
        // with the evidence it is supposed to protect.
        var owned = dir.GetFiles("*.log").Where(IsOwned).ToList();
        var total = owned.Sum(f => f.Length);
        if (total <= MaxFolderBytes)
            return;

        // Cheapest data first: superseded rolling generations, then the LARGEST incident files —
        // the ones actually responsible for an overage — oldest first among equals.
        var sacrificial = owned.Where(f => f.Name.EndsWith(".1.log", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.LastWriteTimeUtc)
            .Concat(kept.OrderByDescending(f => f.Length).ThenBy(f => f.LastWriteTimeUtc));

        foreach (var victim in sacrificial)
        {
            var size = victim.Length;
            if (!TryDelete(victim))
                continue;
            total -= size;
            if (total <= MaxFolderBytes)
                return;
        }
        // Falling out of the loop means the live rolling files alone exceed the cap. They are
        // bounded by rotation and are the freshest thing here, so they are not deleted: the folder
        // is left slightly over rather than blinded.
    }

    /// <summary>The files this facility writes: the two incident prefixes plus the two rolling logs
    /// and their one retained generation each. Anything else in the folder is someone else's and is
    /// neither counted nor deleted.</summary>
    private static bool IsOwned(FileInfo f) =>
        f.Name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase)
        || f.Name.StartsWith("playback-", StringComparison.OrdinalIgnoreCase)
        || f.Name.Equals("app.log", StringComparison.OrdinalIgnoreCase)
        || f.Name.Equals("app.1.log", StringComparison.OrdinalIgnoreCase)
        || f.Name.Equals("mpv.log", StringComparison.OrdinalIgnoreCase)
        || f.Name.Equals("mpv.1.log", StringComparison.OrdinalIgnoreCase);

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
