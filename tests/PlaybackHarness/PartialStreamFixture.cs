using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;
using LightWeaver.Downloads;
using LightWeaver.Player;
using LightWeaver.Settings;

/// <summary>
/// Proves the loopback server that lets a still-running download play (<see cref="PartialFileServer"/>).
///
/// <para>The load-bearing assertion is the one at the write head: a read that returns 0 there must
/// NOT end the response. That is the entire reason the partial file is served over HTTP instead of
/// handed to mpv as a path — mpv's stream_file reads 0 as end-of-stream and stops playback at
/// whatever byte had arrived. <see cref="ExpectQuiet"/> is what states it, and it distinguishes
/// "the socket is open with nothing on it" from "the socket said EOF", which a byte count alone
/// cannot.</para>
///
/// <para>Offscreen by construction: the server is pure I/O over loopback, so this runs the real
/// production class with no Application, no window, no mpv and no server. The download is faked by
/// a FileStream opened exactly as the engine opens it (<c>FileAccess.Write, FileShare.Read</c>),
/// which is also what proves that share mode is enough to read the file while it is appended to.</para>
/// </summary>
internal static class PartialStreamFixture
{
    private const int ReadTimeoutMs = 5000;
    /// <summary>How long a response must stay open and silent to count as waiting. Twenty times
    /// the server's 50 ms write-head poll, so a server that ended the stream reports as EOF here
    /// rather than as a slow one.</summary>
    private const int QuietWindowMs = 1000;
    private const int MiB = 1024 * 1024;

    private static int _failures;

    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "lw-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckGrowingFile(root);
            CheckUnknownTotal(root);
            CheckRangesAndHead(root);
            CheckRangesOnRunningDownload(root);
            CheckTokenIsTheOnlyKey(root);
            CheckReaderRelease(root);
            CheckPublicationRetiredAtEof(root);
            CheckConnectionLimits(root);
            CheckStallBound(root);
            CheckResumeCoverage();
            CheckPlayerAgainstServer(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        Console.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")} ({_failures} failures)");
        return _failures == 0 ? 0 : 1;
    }

    // ---- the legs ----------------------------------------------------------------------

    private static void CheckGrowingFile(string root)
    {
        Test("a plain GET waits at the write head and delivers what is appended after it", () =>
        {
            using var download = new FakeDownload(Path.Combine(root, "growing.mkv"))
            {
                TotalBytes = 2 * MiB,
            };
            download.Append(MiB);
            using var server = new PartialFileServer();
            var url = server.Publish(Guid.NewGuid(), download.Path, download.State);
            Expect(true, url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
                "the URL is loopback-only");

            using var response = Request(url);
            Expect("HTTP/1.1 200 OK", response.Status, "status");
            Expect("2097152", response.Header("Content-Length"), "Content-Length is the known total");
            Expect("bytes", response.Header("Accept-Ranges"), "Accept-Ranges");
            VerifyBytes(ReadExactly(response, MiB), 0);

            // THE assertion. Everything on disk has been read; the download is still running.
            ExpectQuiet(response, "the response must hold at the write head, not end");

            download.Append(MiB - 1000);
            VerifyBytes(ReadExactly(response, MiB - 1000), MiB);

            // Completion is persisted while the engine's FileStream is still open, so the last
            // bytes can still be in its buffer. Completed alone therefore is not end-of-file.
            download.Append(1000, flush: false);
            download.Complete();
            ExpectQuiet(response, "an unflushed tail is still owed after Completed");

            download.Flush();
            VerifyBytes(ReadExactly(response, 1000), 2 * MiB - 1000);
            ExpectClosed(response, "the reader is released once the completed file is delivered");
        });
    }

    private static void CheckUnknownTotal(string root)
    {
        Test("a download with no known total streams chunked, and completion terminates it", () =>
        {
            using var download = new FakeDownload(Path.Combine(root, "transcoded.mkv"));
            download.Append(512 * 1024);
            using var server = new PartialFileServer();
            var url = server.Publish(Guid.NewGuid(), download.Path, download.State);

            using var response = Request(url);
            Expect("HTTP/1.1 200 OK", response.Status, "status");
            // -1 means unknown, and a made-up length is a promise the response then breaks.
            Expect(null, response.Header("Content-Length"), "no Content-Length is emitted");
            Expect(null, response.Header("Accept-Ranges"), "an unsized stream is not seekable");
            Expect("chunked", response.Header("Transfer-Encoding"),
                "a body with no last byte is chunk-framed");
            VerifyBytes(ReadExactly(response, 512 * 1024), 0);
            ExpectQuiet(response, "an unsized stream waits at the write head too");

            download.Append(256 * 1024);
            VerifyBytes(ReadExactly(response, 256 * 1024), 512 * 1024);
            download.Complete();
            ExpectChunkedEnd(response, complete: true,
                "completion ends the unsized stream with its terminating chunk");
        });

        Test("a bounded Range on an unsized download stops at the byte it asked for", () =>
        {
            // The old code forced end = -1 whenever the complete length was unknown, so this body
            // ran unbounded past 199 — a protocol lie. A client-named last byte is also a body
            // LENGTH, so the response can declare one even with no complete length to name.
            using var download = new FakeDownload(Path.Combine(root, "transcoded-range.mkv"));
            download.Append(512 * 1024);
            using var server = new PartialFileServer();
            var url = server.Publish(Guid.NewGuid(), download.Path, download.State);

            using var response = Request(url, range: "bytes=100-199");
            Expect("HTTP/1.1 206 Partial Content", response.Status, "status");
            Expect("100", response.Header("Content-Length"), "the named range is its own length");
            Expect("bytes 100-199/*", response.Header("Content-Range"),
                "the complete length is still unknown");
            Expect(null, response.Header("Transfer-Encoding"),
                "a bounded body has a last byte, so it needs no chunk framing");
            VerifyBytes(ReadExactly(response, 100), 100);
            ExpectClosed(response, "the body stops at the requested end");
        });
    }

    private static void CheckRangesAndHead(string root)
    {
        using var download = new FakeDownload(Path.Combine(root, "done.mkv"));
        download.Append(MiB);
        download.Complete();
        using var server = new PartialFileServer();
        var url = server.Publish(Guid.NewGuid(), download.Path, download.State);

        Test("a mid-file Range returns 206 with the range it asked for", () =>
        {
            using var response = Request(url, range: "bytes=1000-1999");
            Expect("HTTP/1.1 206 Partial Content", response.Status, "status");
            Expect("bytes 1000-1999/1048576", response.Header("Content-Range"), "Content-Range");
            Expect("1000", response.Header("Content-Length"), "Content-Length");
            VerifyBytes(ReadExactly(response, 1000), 1000);
            ExpectClosed(response, "the range ends the response");
        });

        Test("an open-ended Range runs from the offset to the end of the file", () =>
        {
            using var response = Request(url, range: "bytes=1048000-");
            Expect("HTTP/1.1 206 Partial Content", response.Status, "status");
            Expect("bytes 1048000-1048575/1048576", response.Header("Content-Range"), "Content-Range");
            VerifyBytes(ReadExactly(response, 576), 1048000);
            ExpectClosed(response, "the response ends at the last byte");
        });

        Test("a range beyond the end of a completed download is refused", () =>
        {
            using var response = Request(url, range: "bytes=2000000-");
            Expect("HTTP/1.1 416 Range Not Satisfiable", response.Status, "status");
            Expect("bytes */1048576", response.Header("Content-Range"), "Content-Range names the total");
            Expect("0", response.Header("Content-Length"), "no body");
            ExpectClosed(response, "nothing follows the 416");
        });

        Test("a Range that starts at 0 but ends early is still answered 206", () =>
        {
            // 200 would say "the whole resource is 1000 bytes long", which is a lie the client
            // only finds out by running out of file. The status has to follow whether the Range
            // was honoured, not whether the offset happened to be non-zero.
            using var response = Request(url, range: "bytes=0-999");
            Expect("HTTP/1.1 206 Partial Content", response.Status, "status");
            Expect("bytes 0-999/1048576", response.Header("Content-Range"), "Content-Range");
            Expect("1000", response.Header("Content-Length"), "Content-Length");
            VerifyBytes(ReadExactly(response, 1000), 0);
            ExpectClosed(response, "the range ends the response");
        });

        Test("HEAD returns the headers and no body", () =>
        {
            using var response = Request(url, method: "HEAD");
            Expect("HTTP/1.1 200 OK", response.Status, "status");
            Expect("1048576", response.Header("Content-Length"), "Content-Length");
            ExpectClosed(response, "a HEAD writes no body");
        });
    }

    /// <summary>The ranges that matter on a download that is still RUNNING — the ones every other
    /// leg here misses, because they all exercise a finished file. A start past the write head is
    /// what a resume position converted to a byte offset produces, and a server that waits there
    /// instead of refusing leaves the user looking at a frozen loading indicator until the stall
    /// bound expires: two minutes, and then playback restarts from the beginning.</summary>
    private static void CheckRangesOnRunningDownload(string root)
    {
        using var download = new FakeDownload(Path.Combine(root, "running.mkv"))
        {
            TotalBytes = 8 * MiB,
        };
        download.Append(MiB);
        // The PRODUCTION stall bound on purpose. A regression does not fail the legs below by
        // answering wrongly, it fails them by not answering at all, so the bound must be longer
        // than the fixture's own read timeout for that to show up as a failure.
        using var server = new PartialFileServer();
        var url = server.Publish(Guid.NewGuid(), download.Path, download.State);

        Test("a Range past the write head of a running download is refused at once", () =>
        {
            var elapsed = Stopwatch.StartNew();
            using var response = Request(url, range: FormattableString.Invariant($"bytes={4 * MiB}-"));
            Expect("HTTP/1.1 416 Range Not Satisfiable", response.Status, "status");
            Expect("bytes */8388608", response.Header("Content-Range"), "Content-Range names the total");
            Expect(true, elapsed.ElapsedMilliseconds < 2000,
                FormattableString.Invariant($"answered promptly ({elapsed.ElapsedMilliseconds} ms)"));
            ExpectClosed(response, "nothing follows the 416");
        });

        Test("a Range past the complete length of a running download is refused", () =>
        {
            using var response = Request(url, range: FormattableString.Invariant($"bytes={9 * MiB}-"));
            Expect("HTTP/1.1 416 Range Not Satisfiable", response.Status, "status");
            ExpectClosed(response, "nothing follows the 416");
        });

        Test("a Range inside the prefix of a running download is served normally", () =>
        {
            using var response = Request(url, range: "bytes=1000-1999");
            Expect("HTTP/1.1 206 Partial Content", response.Status, "status");
            Expect("bytes 1000-1999/8388608", response.Header("Content-Range"), "Content-Range");
            VerifyBytes(ReadExactly(response, 1000), 1000);
        });

        Test("a Range at exactly the write head waits for the next bytes", () =>
        {
            // The line between "not yet" and "no": one byte past the head is refused, the head
            // itself is served, because those bytes are one write away and waiting for them is
            // what this server is for.
            using var response = Request(url, range: FormattableString.Invariant($"bytes={MiB}-"));
            Expect("HTTP/1.1 206 Partial Content", response.Status, "status");
            ExpectQuiet(response, "the head is served by waiting, not refused");
            download.Append(4096);
            VerifyBytes(ReadExactly(response, 4096), MiB);
        });
    }

    private static void CheckTokenIsTheOnlyKey(string root)
    {
        using var download = new FakeDownload(Path.Combine(root, "guarded.mkv"));
        download.Append(64 * 1024);
        download.Complete();
        using var server = new PartialFileServer();
        var url = server.Publish(Guid.NewGuid(), download.Path, download.State);
        var port = new Uri(url).Port;
        var target = new Uri(url).PathAndQuery;                 // "/?t=<token>"
        var token = target[(target.IndexOf('=') + 1)..];

        Test("a target that is not a live token is refused", () =>
        {
            // None of these may reach a file. The server never combines a request target with a
            // directory, so the only thing that names a file is a token it issued itself.
            foreach (var path in new[]
                     {
                         "/",
                         "/?t=" + Guid.NewGuid().ToString("N"),
                         "/../../Windows/win.ini",
                         "/C:/Windows/win.ini",
                         "/" + download.Path.Replace('\\', '/'),
                         target + "/..",
                         target.ToUpperInvariant(),
                         // The capability is the exact string that was issued. Uppercasing and
                         // percent-encoding are the two ways a lookup that went through Uri or a
                         // query parser would accept a variant of it — Guid.TryParseExact already
                         // made the case-insensitive half of that mistake once.
                         "/?t=" + token.ToUpperInvariant(),
                         "/?t=" + PercentEncodeFirst(token),
                         // The old path form. It was moved into the query so AppLog's redaction
                         // can strip it; serving it anyway would keep the leak alive.
                         "/" + token,
                         // A parameter whose name merely ends in the right letter.
                         "/?tt=" + token,
                     })
            {
                using var response = Request(port, path);
                Expect("HTTP/1.1 404 Not Found", response.Status, "refused: " + path);
            }
        });

        Test("an extra query parameter does not stop the token matching", () =>
        {
            // A URL is allowed more than one parameter, and mpv/ffmpeg may append their own.
            foreach (var path in new[] { target + "&x=1", "/?x=1&" + target[2..] })
            {
                using var response = Request(port, path);
                Expect("HTTP/1.1 200 OK", response.Status, "served: " + path);
            }
        });

        Test("a withdrawn token stops working", () =>
        {
            var itemId = Guid.NewGuid();
            var second = server.Publish(itemId, download.Path, download.State);
            using (var live = Request(second))
                Expect("HTTP/1.1 200 OK", live.Status, "the fresh token serves");
            server.Withdraw(itemId);
            // The first publication keeps the listener up, so this is a refusal and not a
            // connection failure.
            using var response = Request(second);
            Expect("HTTP/1.1 404 Not Found", response.Status, "the retired token is refused");
        });
    }

    private static void CheckReaderRelease(string root)
    {
        Test("a removed download releases its reader", () =>
        {
            using var download = new FakeDownload(Path.Combine(root, "removed.mkv"));
            download.Append(128 * 1024);
            using var server = new PartialFileServer();
            var url = server.Publish(Guid.NewGuid(), download.Path, download.State);
            using var response = Request(url);
            ReadExactly(response, 128 * 1024);
            ExpectQuiet(response, "still running, so still waiting");

            download.Removed = true;   // what Remove() looks like from the server's side
            ExpectChunkedEnd(response, complete: false,
                "a removed download must not leave a reader waiting, and must not look finished");
        });

        Test("a paused download is not streamed as if it were live", () =>
        {
            using var download = new FakeDownload(Path.Combine(root, "paused.mkv"));
            download.Append(128 * 1024);
            using var server = new PartialFileServer();
            var url = server.Publish(Guid.NewGuid(), download.Path, download.State);
            using var response = Request(url);
            ReadExactly(response, 128 * 1024);

            download.Status = DownloadStatus.Paused;
            // THE D2 assertion at the server. A pause mid-film must not be indistinguishable from
            // the download finishing: with the connection-close framing it was, and the player
            // answered it by freezing on the last frame or auto-advancing to the next episode.
            ExpectChunkedEnd(response, complete: false,
                "a pause ends the wait, and leaves the message visibly truncated");
        });

        Test("a withdrawn publication releases the reader waiting on it", () =>
        {
            using var download = new FakeDownload(Path.Combine(root, "withdrawn.mkv"));
            download.Append(128 * 1024);
            using var server = new PartialFileServer();
            var itemId = Guid.NewGuid();
            var url = server.Publish(itemId, download.Path, download.State);
            using var response = Request(url);
            ReadExactly(response, 128 * 1024);

            server.Withdraw(itemId);
            ExpectChunkedEnd(response, complete: false,
                "withdrawing is what lets a cancel delete the file, and it is not a finished file");
        });
    }

    private static void CheckPublicationRetiredAtEof(string root)
    {
        Test("a stream that plays out to the end retires its publication", () =>
        {
            using var keepListening = new FakeDownload(Path.Combine(root, "keepalive.mkv"));
            keepListening.Append(4096);
            using var download = new FakeDownload(Path.Combine(root, "playedout.mkv"))
            {
                TotalBytes = 256 * 1024,
            };
            download.Append(128 * 1024);
            using var server = new PartialFileServer();
            // A second publication holds the listener up, so the assertion at the end is a
            // REFUSAL and not a connection failure — those are not the same claim.
            server.Publish(Guid.NewGuid(), keepListening.Path, keepListening.State);
            var url = server.Publish(Guid.NewGuid(), download.Path, download.State);

            using (var response = Request(url))
            {
                VerifyBytes(ReadExactly(response, 128 * 1024), 0);
                ExpectQuiet(response, "still running, so still waiting");
                download.Append(128 * 1024);
                download.Complete();
                VerifyBytes(ReadExactly(response, 128 * 1024), 128 * 1024);
                ExpectClosed(response, "the completed file ends the response");
            }

            // Nothing else in the app notices end-of-file — the player sits paused at the end when
            // there is no Up Next — so without this the socket and a live token outlast the
            // playback for as long as the app stays open.
            using var afterEof = Request(url);
            Expect("HTTP/1.1 404 Not Found", afterEof.Status, "the delivered publication is retired");
        });

        Test("a range that merely runs to the last byte does not retire the publication", () =>
        {
            // The other half of the same guard, and the half that had no leg of its own: retirement
            // is gated on the reader having FOLLOWED the download at the write head, not on the
            // range happening to end at the last byte. ffmpeg reads a container's index exactly
            // that way — a tail range on an open file — and retiring on it would kill the playback
            // that was about to start. It was proved only by the legs that happened to come after
            // it against the same URL, which is coverage by accident.
            using var download = new FakeDownload(Path.Combine(root, "tailread.mkv"));
            download.Append(256 * 1024);
            download.Complete();
            using var server = new PartialFileServer();
            var url = server.Publish(Guid.NewGuid(), download.Path, download.State);

            using (var tail = Request(url, range: FormattableString.Invariant(
                $"bytes={256 * 1024 - 4096}-")))
            {
                Expect("HTTP/1.1 206 Partial Content", tail.Status, "status");
                VerifyBytes(ReadExactly(tail, 4096), 256 * 1024 - 4096);
                ExpectClosed(tail, "the tail range ends the response");
            }

            using var again = Request(url);
            Expect("HTTP/1.1 200 OK", again.Status, "the token still serves after an index read");
        });
    }

    private static void CheckConnectionLimits(string root)
    {
        Test("a connection that never sends a request head is dropped", () =>
        {
            using var download = new FakeDownload(Path.Combine(root, "silent.mkv"));
            download.Append(64 * 1024);
            download.Complete();
            // Production waits five seconds; shortened here for the same reason as the stall
            // bound, so the bound is observable inside a test run.
            using var server = new PartialFileServer(headTimeout: TimeSpan.FromMilliseconds(600));
            var url = server.Publish(Guid.NewGuid(), download.Path, download.State);

            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, new Uri(url).Port);
            var stream = client.GetStream();
            ExpectNoResponse(stream, "a socket that sends nothing is closed, not held open");
        });

        Test("connections past the cap are dropped instead of queued", () =>
        {
            using var download = new FakeDownload(Path.Combine(root, "capped.mkv"))
            {
                TotalBytes = 4 * MiB,
            };
            download.Append(64 * 1024);
            using var server = new PartialFileServer();
            var url = server.Publish(Guid.NewGuid(), download.Path, download.State);
            // The cap itself, not a copy of the number: a copy keeps passing after the cap moves.
            const int cap = PartialFileServer.MaxConnections;

            var held = new List<Response>();
            try
            {
                // Each of these parks at the write head of a running download, so the slot it
                // takes is really taken. Reading the status line back is what proves the server
                // has ACCEPTED it — a bare connect proves nothing, the listen backlog completes
                // the handshake whether anyone is accepting or not.
                for (var i = 0; i < cap; i++)
                {
                    var response = Request(url);
                    held.Add(response);
                    Expect("HTTP/1.1 200 OK", response.Status,
                        FormattableString.Invariant($"held connection {i}"));
                }
                using var over = new TcpClient();
                over.Connect(IPAddress.Loopback, new Uri(url).Port);
                var stream = over.GetStream();
                stream.Write(Encoding.ASCII.GetBytes(
                    $"GET {new Uri(url).PathAndQuery} HTTP/1.1\r\nConnection: close\r\n\r\n"));
                ExpectNoResponse(stream,
                    "the connection past the cap gets no response and no socket");
            }
            finally
            {
                foreach (var response in held)
                    response.Dispose();
            }
        });
    }

    private static void CheckStallBound(string root)
    {
        Test("a download that stops growing does not hold the socket forever", () =>
        {
            using var download = new FakeDownload(Path.Combine(root, "stalled.mkv"));
            download.Append(64 * 1024);
            // Production waits 120 s, past the engine's own 90 s per-read bound, so the download
            // failing is what normally ends the wait. Shortened here so the backstop is observable.
            using var server = new PartialFileServer(TimeSpan.FromMilliseconds(600));
            var url = server.Publish(Guid.NewGuid(), download.Path, download.State);
            using var response = Request(url);
            ReadExactly(response, 64 * 1024);

            // Still Downloading, and it never grows again: only the bound can end this.
            ExpectChunkedEnd(response, complete: false, "the write-head wait is bounded",
                timeoutMs: 4000);
        });
    }

    /// <summary>The rung's own gate: whether the bytes on disk plausibly reach the resume position.
    /// Pure arithmetic on four numbers, which is why <see cref="DownloadManager.PrefixCoversResume"/>
    /// takes scalars — standing up a download engine to reach it would have been the only other
    /// way to state any of this.</summary>
    private static void CheckResumeCoverage()
    {
        const long twoHours = 7200L * TimeSpan.TicksPerSecond;
        const long tenGigabytes = 10L * 1024 * 1024 * 1024;

        Test("a from-the-start playback asks the prefix no question", () =>
        {
            Expect(true, DownloadManager.PrefixCoversResume(-1, 0, 0, null), "no total, no runtime");
            Expect(true, DownloadManager.PrefixCoversResume(tenGigabytes, DownloadManager.MinPartialStreamBytes,
                0, twoHours), "resume 0 passes straight through");
        });

        Test("a resume with nothing to estimate from is refused rather than guessed at", () =>
        {
            var oneMinute = 60L * TimeSpan.TicksPerSecond;
            // A transcoded download has no reported total, so ticks cannot become bytes at all.
            Expect(false, DownloadManager.PrefixCoversResume(-1, tenGigabytes, oneMinute, twoHours),
                "no complete size");
            Expect(false, DownloadManager.PrefixCoversResume(tenGigabytes, tenGigabytes, oneMinute, null),
                "no runtime");
            Expect(false, DownloadManager.PrefixCoversResume(tenGigabytes, tenGigabytes, twoHours, twoHours),
                "a resume at or past the end");
        });

        Test("the resume estimate is padded, and the pad is thin for an early resume", () =>
        {
            // The reviewer's case, stated as numbers rather than left as a worry. One minute into a
            // 2 h / 10 GiB film: the constant-bitrate estimate is ~85 MiB, the quarter pad makes it
            // ~107 MiB, plus the 8 MiB floor = 120,236,714 bytes demanded on disk. A high-bitrate
            // opening really can put the true byte offset past that, and being wrong there costs a
            // 416 and the designed fallback — not a hang — which is why the pad stays where it is.
            var oneMinute = 60L * TimeSpan.TicksPerSecond;
            Expect(false, DownloadManager.PrefixCoversResume(tenGigabytes, 120_000_000, oneMinute, twoHours),
                "just under the padded estimate");
            Expect(true, DownloadManager.PrefixCoversResume(tenGigabytes, 121_000_000, oneMinute, twoHours),
                "just over it");
            // Mid-film, where the pad is worth gigabytes and the estimate is the load-bearing part.
            var oneHour = 3600L * TimeSpan.TicksPerSecond;
            Expect(false, DownloadManager.PrefixCoversResume(tenGigabytes, 6L * 1024 * 1024 * 1024,
                oneHour, twoHours), "half the film needs half the bytes plus the pad");
            Expect(true, DownloadManager.PrefixCoversResume(tenGigabytes, 7L * 1024 * 1024 * 1024,
                oneHour, twoHours), "with the pad covered it is offered");
        });
    }

    // ---- the real player against the real server ---------------------------------------

    /// <summary>
    /// The leg the first review round structurally could not have: a real <see cref="MpvPlayer"/>
    /// pointed at a real <see cref="PartialFileServer"/>, asserting what the PLAYER does when the
    /// response ends mid-film. Every other leg here tests the server in isolation, so the question
    /// that actually decides the user-visible behaviour — does mpv report this as a failure, or as
    /// an ordinary end of file it answers by advancing to the next episode? — went unasked. The
    /// answer, measured below, is end of file in BOTH framings, which is why MainWindow has to
    /// guard end-of-file and not just PlaybackFailed.
    ///
    /// <para>Offscreen: LIGHTWEAVER_MPV_NO_VIDEO puts mpv on vo=null and wid is 0, so no window is
    /// created and no GPU adapter is opened. No input is injected anywhere.</para>
    /// </summary>
    private static void CheckPlayerAgainstServer(string root)
    {
        if (!TryBuildClip(out var clip))
            return;
        var previousNoVideo = Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO");
        var previousNoAudio = Environment.GetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO");
        try
        {
            Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", "1");
            Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", "1");
            var whole = File.ReadAllBytes(clip);
            // Enough for mpv to probe and open, and short of the end so the response is still at the
            // write head when the download is stopped under it.
            var prefix = whole.Length * 3 / 5;

            // MEASURED, 2026-09-15, mpv/libmpv with the fixed server. Both cases end as EOF, and
            // this is the whole answer to "which of the two D2 halves carries the weight":
            //
            //   sized   — ffmpeg DOES see it ("http: Stream ends prematurely at 78651, should be
            //             131086 ... error=I/O error"), reconnects at 0/1/3/7 s, gets the same
            //             truncation each time, and then reports "[mkv] EOF reached" — ~11 s after
            //             the close, as an ordinary end of file.
            //   unsized — with chunked framing ffmpeg logs NOTHING and goes straight to
            //             "[mkv] EOF reached". A truncated chunked body is an end of stream to it.
            //
            // So MPV NEVER RAISES PlaybackFailed FOR A MID-FILM CLOSE, by either framing. The
            // fallback is carried entirely by MainWindow's EndReached guard; the chunked framing is
            // a protocol fix (an unsized response can now express "incomplete" at all, which the
            // legs above assert) and buys nothing at the player. Do not relax this to "some event
            // arrived" — the value being pinned is that it is the EOF path and not the error path,
            // which is what decides where the guard has to live.
            Test("mpv reports a mid-film close on a SIZED partial stream as end-of-file", () =>
                Expect(PlayerEnd.Ended, DriveMidFilmClose(root, "sized", whole, prefix, whole.Length),
                    "the outcome mpv reports"));
            Test("mpv reports a mid-film close on an UNSIZED partial stream as end-of-file", () =>
                Expect(PlayerEnd.Ended, DriveMidFilmClose(root, "unsized", whole, prefix, -1),
                    "the outcome mpv reports"));

            Test("a position posted for the outgoing file is not stamped with the new load", () =>
                CheckPositionGeneration(clip));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_VIDEO", previousNoVideo);
            Environment.SetEnvironmentVariable("LIGHTWEAVER_MPV_NO_AUDIO", previousNoAudio);
        }
    }

    private enum PlayerEnd
    {
        /// <summary>Nothing arrived inside the budget: the player is sitting on a dead stream with
        /// no idea it is dead, which is the failure this leg exists to catch.</summary>
        Nothing,
        /// <summary>MPV_END_FILE_REASON_ERROR — the app's PlaybackFailed, which drives the fallback.</summary>
        Failed,
        /// <summary>eof-reached under keep-open=yes — the app's EndReached, which without a guard
        /// holds the last frame or advances to the next episode.</summary>
        Ended,
    }

    /// <summary>Plays a partial file over the real server, stops the download mid-film, and reports
    /// which terminal event mpv produced.</summary>
    private static PlayerEnd DriveMidFilmClose(string root, string name, byte[] whole, int prefix,
        long totalBytes)
    {
        using var download = new FakeDownload(Path.Combine(root, $"mpv-{name}.mkv"))
        {
            TotalBytes = totalBytes,
        };
        download.AppendRaw(whole.AsSpan(0, prefix));
        using var server = new PartialFileServer();
        var url = server.Publish(Guid.NewGuid(), download.Path, download.State);
        // HardwareDecoding=false: software decode, so the leg needs no GPU.
        using var player = MpvPlayer.Create(0, Dispatcher.CurrentDispatcher,
            new AppSettings { HardwareDecoding = false });
        var outcome = PlayerEnd.Nothing;
        player.PlaybackFailed += _ => { if (outcome == PlayerEnd.Nothing) outcome = PlayerEnd.Failed; };
        player.EndReached += () => { if (outcome == PlayerEnd.Nothing) outcome = PlayerEnd.Ended; };
        // What the user is looking at during the gap between the close and the outcome. The sized
        // case takes ~12 s to signal, and the argument for shipping that is that mpv flips
        // paused-for-cache meanwhile, so the overlay shows its buffering indicator and the gap
        // reads as an ordinary network hiccup rather than a dead frame. That is a claim about a
        // real property and a real event, so it is measured here rather than reasoned about.
        var closed = new Stopwatch();
        var bufferingRaises = 0;
        var bufferingAfterCloseMs = -1L;
        // Every transition, signed and timed from the close. One "it buffered at some point" bit
        // would not settle the question: an indicator that comes up and goes down again leaves the
        // rest of the gap looking exactly like the dead frame the argument says it does not.
        var bufferingTrace = new List<string>();
        player.BufferingChanged += buffering =>
        {
            bufferingRaises++;
            if (buffering && closed.IsRunning && bufferingAfterCloseMs < 0)
                bufferingAfterCloseMs = closed.ElapsedMilliseconds;
            bufferingTrace.Add(FormattableString.Invariant(
                $"{(buffering ? '+' : '-')}{(closed.IsRunning ? closed.ElapsedMilliseconds : -1)}"));
        };
        player.LoadFile(url);

        var loadDeadline = DateTime.UtcNow.AddSeconds(20);
        while (player.Duration <= 0 && outcome == PlayerEnd.Nothing && DateTime.UtcNow < loadDeadline)
            Pump(TimeSpan.FromMilliseconds(50));
        if (player.Duration <= 0)
            throw new InvalidOperationException(
                $"{name}: mpv never opened the partial stream (outcome so far: {outcome}). The clip "
                + "has no playable prefix, or the server refused the load — either way the leg below "
                + "would be measuring nothing.");
        // Playing, and the response is parked at the write head. This is "mid-film".
        Pump(TimeSpan.FromMilliseconds(500));
        download.Status = DownloadStatus.Paused;

        // Generous: in the sized case ffmpeg reconnects at 0, 1, 3 and 7 seconds before it gives up,
        // so the signal is ~11 s behind the close. That latency is itself worth knowing — it is how
        // long the user looks at a frozen picture before the fallback runs.
        closed.Start();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (outcome == PlayerEnd.Nothing && DateTime.UtcNow < deadline)
            Pump(TimeSpan.FromMilliseconds(50));
        // buffering_after_ms is the number that matters: -1 means the user saw nothing at all
        // between the close and the outcome, whatever paused-for-cache reads by the time the
        // stream is already over.
        Console.WriteLine(FormattableString.Invariant(
            $"  PARTIAL-STREAM EVIDENCE: {name} total_bytes={totalBytes} prefix={prefix}/{whole.Length} duration={player.Duration:F2}s vo={player.GetPropertyString("current-vo")} outcome={outcome} after_ms={closed.ElapsedMilliseconds} buffering_raises={bufferingRaises} buffering_after_ms={bufferingAfterCloseMs} buffering_trace={(bufferingTrace.Count > 0 ? string.Join(",", bufferingTrace) : "none")} paused_for_cache={player.GetPropertyString("paused-for-cache") ?? "none"}"));
        return outcome;
    }

    /// <summary>
    /// The mechanism behind the partial-stream fallback's resume position, stated on its own.
    ///
    /// <para>Positions are coalesced onto ONE dispatcher operation, and the dispatcher does not
    /// preempt — so a position mpv posts while the UI thread is inside a click handler is delivered
    /// only after that handler has already loaded the next file. Whoever REMEMBERS that position
    /// therefore cannot ask "what is playing now?" when it arrives; the answer is the wrong file.
    /// MainWindow's fallback remembers exactly this value to restart the server stream at, so a
    /// mis-stamped one restarts the incoming episode forty minutes in.</para>
    ///
    /// <para>The race is forced rather than waited for: the dispatcher is BLOCKED while mpv posts,
    /// so the operation is provably still queued; and the incoming load names a file that does not
    /// exist, so mpv can never post a position for it and the delivered value is provably the
    /// outgoing file's. Both halves are what make this deterministic instead of a coin flip.</para>
    /// </summary>
    private static void CheckPositionGeneration(string clip)
    {
        using var player = MpvPlayer.Create(0, Dispatcher.CurrentDispatcher,
            new AppSettings { HardwareDecoding = false });
        var deliveries = 0;
        var lastGeneration = -1;
        // pos > 0 is MainWindow's own rule: mpv publishes time-pos as unavailable between files and
        // MpvPlayer forwards that as a zero, which is not a position anything should remember.
        player.PositionChanged += (pos, generation) =>
        {
            if (pos <= 0)
                return;
            deliveries++;
            lastGeneration = generation;
        };
        var playing = player.LoadFile(clip);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (deliveries == 0 && DateTime.UtcNow < deadline)
            Pump(TimeSpan.FromMilliseconds(50));
        Expect(true, deliveries > 0, "the clip reported a position at all");
        Expect(playing, lastGeneration, "a live position carries the generation it is playing");

        // Blocking, not pumping: this is what a click handler does to the dispatcher, and it is the
        // only state in which the bug can happen.
        var delivered = deliveries;
        Thread.Sleep(400);
        Expect(delivered, deliveries, "the position stayed queued while the thread was blocked");
        var incoming = player.LoadFile(Path.Combine(Path.GetTempPath(),
            "lightweaver-no-such-file-" + Guid.NewGuid().ToString("N") + ".mkv"));
        Expect(true, incoming != playing, "the load generation moved");

        // Everything queued before this marker, and nothing after it.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        Expect(true, deliveries > delivered, "the queued position was delivered after the load");
        Expect(playing, lastGeneration,
            "the outgoing file's position still names the outgoing file");
        Expect(incoming, player.LoadGeneration, "and the player is on the incoming one");
    }

    /// <summary>A tiny Matroska clip whose prefix is playable. Matroska on purpose: the point of
    /// this leg is what happens when a HEALTHY partial stream is cut off, so a container that plays
    /// from a prefix is a precondition, not a variable. Reports the skip and returns false when
    /// ffmpeg is unavailable — it is a developer tool, not a dependency of the app.</summary>
    private static bool TryBuildClip(out string clip)
    {
        clip = Path.Combine(Path.GetTempPath(), "lightweaver-tests", "partial-320x240-10fps-6s.mkv");
        if (File.Exists(clip) && new FileInfo(clip).Length > 16 * 1024)
            return true;
        Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
        try
        {
            using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
                "-y -loglevel error -f lavfi -i testsrc2=duration=6:size=320x240:rate=10 "
                + $"-c:v libx264 -pix_fmt yuv420p \"{clip}\"")
            { UseShellExecute = false });
            ffmpeg!.WaitForExit();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Console.WriteLine("SKIP: the mpv-against-the-server legs need ffmpeg on PATH to build "
                + "their clip.");
            return false;
        }
        if (File.Exists(clip) && new FileInfo(clip).Length > 16 * 1024)
            return true;
        Console.WriteLine("SKIP: ffmpeg produced no usable clip at " + clip + ".");
        return false;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // ---- the fake download -------------------------------------------------------------

    /// <summary>A partial file, written exactly the way <see cref="DownloadManager"/> writes one.</summary>
    private sealed class FakeDownload : IDisposable
    {
        private readonly FileStream _writer;

        public FakeDownload(string path)
        {
            Path = path;
            _writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        }

        public string Path { get; }
        public long Written { get; private set; }
        public DownloadStatus Status { get; set; } = DownloadStatus.Downloading;
        public long TotalBytes { get; set; } = -1;
        public bool Removed { get; set; }

        /// <param name="flush">False leaves the bytes in the writer's buffer, which is the state
        /// the engine is in when it persists Completed.</param>
        public void Append(int count, bool flush = true)
        {
            var bytes = new byte[count];
            for (var i = 0; i < count; i++)
                bytes[i] = At(Written + i);
            _writer.Write(bytes);
            Written += count;
            if (flush)
                _writer.Flush();
        }

        /// <summary>Real file content rather than the offset-keyed pattern, for the legs that hand
        /// the bytes to a demuxer. <see cref="At"/>-based verification does not apply to these.</summary>
        public void AppendRaw(ReadOnlySpan<byte> bytes)
        {
            _writer.Write(bytes);
            Written += bytes.Length;
            _writer.Flush();
        }

        public void Flush() => _writer.Flush();

        public void Complete()
        {
            TotalBytes = Written;
            Status = DownloadStatus.Completed;
        }

        public PartialFileState? State()
            => Removed ? null : new PartialFileState(Status, TotalBytes);

        public void Dispose() => _writer.Dispose();

        /// <summary>Content keyed to absolute offset, so any range can be checked on its own.</summary>
        public static byte At(long offset) => (byte)(offset % 251);
    }

    // ---- a raw HTTP client -------------------------------------------------------------
    //
    // Raw sockets, not HttpClient: the assertions are about the exact status line and headers on
    // the wire, and about a response that stays OPEN with nothing on it, which a buffering client
    // cannot tell apart from a slow one.

    private sealed class Response : IDisposable
    {
        public required TcpClient Client { get; init; }
        public required NetworkStream Stream { get; init; }
        public required string Status { get; init; }
        public required IReadOnlyList<string> Headers { get; init; }

        /// <summary>The body is chunk-framed, so the bytes on the wire are not the bytes of the
        /// resource and every read goes through <see cref="ReadBody"/>.</summary>
        public bool Chunked => string.Equals(Header("Transfer-Encoding"), "chunked",
            StringComparison.OrdinalIgnoreCase);

        /// <summary>The terminating zero chunk arrived: the message is COMPLETE.</summary>
        public bool ChunkedEndSeen;
        /// <summary>The socket closed with the framing incomplete: the message was CUT OFF.</summary>
        public bool ClosedEarly;
        public long ChunkRemaining;

        public string? Header(string name)
            => Headers.FirstOrDefault(h => h.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                ?[(name.Length + 1)..].Trim();

        public void Dispose()
        {
            Stream.Dispose();
            Client.Dispose();
        }
    }

    /// <summary>Body bytes, de-chunked when the response is chunk-framed. Returns 0 at the end of
    /// the body and records on the response WHICH end it was, which is the distinction the framing
    /// exists to carry: a terminating zero chunk means the download finished and everything went
    /// out; a socket closed mid-framing means it was paused, failed, removed or stalled.</summary>
    private static int ReadBody(Response response, byte[] buffer, int offset, int count)
    {
        try
        {
            if (!response.Chunked)
            {
                var plain = response.Stream.Read(buffer, offset, count);
                if (plain == 0)
                    response.ClosedEarly = true;
                return plain;
            }
            if (response.ChunkedEndSeen)
                return 0;
            if (response.ChunkRemaining == 0)
            {
                var size = ReadChunkSizeLine(response);
                if (size < 0)
                    return 0;               // ReadChunkSizeLine recorded the early close
                if (size == 0)
                {
                    ReadLine(response.Stream);   // the (empty) trailer section
                    response.ChunkedEndSeen = true;
                    return 0;
                }
                response.ChunkRemaining = size;
            }
            var read = response.Stream.Read(buffer, offset,
                (int)Math.Min(count, response.ChunkRemaining));
            if (read == 0)
            {
                response.ClosedEarly = true;
                return 0;
            }
            response.ChunkRemaining -= read;
            if (response.ChunkRemaining == 0)
                ReadLine(response.Stream);   // the CRLF that closes the chunk
            return read;
        }
        catch (IOException ex) when (ex.InnerException is SocketException
        { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        {
            // A close with unread bytes still queued on the peer arrives as a reset rather than a
            // FIN. It says the same thing the FIN would: the framing never finished.
            response.ClosedEarly = true;
            return 0;
        }
    }

    /// <summary>The chunk size in hex, or -1 when the socket closed before one arrived.</summary>
    private static long ReadChunkSizeLine(Response response)
    {
        string? line;
        do
        {
            line = ReadLine(response.Stream);
            if (line is null)
            {
                response.ClosedEarly = true;
                return -1;
            }
        }
        while (line.Length == 0);
        // chunk-ext (";name=value") is legal and this server never emits one; parsing it anyway
        // keeps the reader honest about what it accepts.
        var size = line.Split(';')[0].Trim();
        return Convert.ToInt64(size, 16);
    }

    /// <summary>One CRLF-terminated line, or null at end of stream.</summary>
    private static string? ReadLine(NetworkStream stream)
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (stream.Read(one, 0, 1) == 0)
                return null;
            if (one[0] == '\n')
                return Encoding.ASCII.GetString([.. line]).TrimEnd('\r');
            line.Add(one[0]);
        }
    }

    /// <summary>PathAndQuery, not AbsolutePath: the capability is a query parameter, so the target
    /// that goes on the request line is the whole thing.</summary>
    private static Response Request(string url, string method = "GET", string? range = null)
    {
        var uri = new Uri(url);
        return Request(uri.Port, uri.PathAndQuery, method, range);
    }

    private static Response Request(int port, string path, string method = "GET", string? range = null)
    {
        var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var head = $"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n"
            + (range is null ? "" : $"Range: {range}\r\n") + "Connection: close\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(head));
        stream.ReadTimeout = ReadTimeoutMs;
        var lines = ReadHead(stream);
        return new Response
        {
            Client = client,
            Stream = stream,
            Status = lines.Count > 0 ? lines[0] : "",
            Headers = lines.Skip(1).ToList(),
        };
    }

    /// <summary>Byte at a time on purpose: anything buffered here would eat the body.</summary>
    private static List<string> ReadHead(NetworkStream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 8192)
        {
            if (stream.Read(one, 0, 1) == 0)
                break;
            head.Add(one[0]);
            var n = head.Count;
            if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n'
                && head[n - 2] == '\r' && head[n - 1] == '\n')
                break;
        }
        return [.. Encoding.ASCII.GetString([.. head]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)];
    }

    private static byte[] ReadExactly(Response response, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = ReadBody(response, buffer, read, count - read);
            if (n == 0)
                throw new InvalidOperationException(
                    $"The response ended after {read} of {count} bytes.");
            read += n;
        }
        return buffer;
    }

    /// <summary>The write-head assertion: open, and with nothing on it. A response that ended
    /// reports as EOF here, which is the failure this whole fixture exists to catch.</summary>
    private static void ExpectQuiet(Response response, string what)
    {
        response.Stream.ReadTimeout = QuietWindowMs;
        try
        {
            var n = response.Stream.Read(new byte[1], 0, 1);
            throw new InvalidOperationException(n == 0
                ? $"{what}: the response ended at the write head."
                : $"{what}: {n} unexpected byte(s) past the write head.");
        }
        catch (IOException ex) when (ex.InnerException is SocketException
        { SocketErrorCode: SocketError.TimedOut })
        {
        }
        finally
        {
            response.Stream.ReadTimeout = ReadTimeoutMs;
        }
    }

    /// <summary>No status line, no body, and no socket left — within the fixture's read timeout,
    /// so a server that simply held on fails here instead of passing slowly.</summary>
    private static void ExpectNoResponse(NetworkStream stream, string what)
    {
        stream.ReadTimeout = ReadTimeoutMs;
        try
        {
            if (stream.Read(new byte[16], 0, 16) != 0)
                throw new InvalidOperationException($"{what}: the server answered.");
        }
        catch (IOException ex) when (ex.InnerException is SocketException
        { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        {
            // Closing a socket that still has an unread request in its buffer is an RST rather
            // than a FIN, and Winsock reports the local end of that as aborted or reset depending
            // on which side notices first. All three say "no response and no connection", which is
            // the whole claim; a TIMEOUT is not caught, because being held is the failure.
        }
    }

    /// <summary>Drains a chunk-framed response to its end and states WHICH end it was.
    ///
    /// <para>An unsized response used to be framed by the connection closing, which made "the
    /// download finished" and "the download was paused twelve minutes in" the same bytes on the
    /// wire. Chunked framing can say the difference: the terminating zero chunk is written on
    /// exactly one path. (ffmpeg does not act on the difference — see the mpv legs below — so this
    /// asserts the protocol, not the player behaviour.)</para>
    /// </summary>
    private static void ExpectChunkedEnd(Response response, bool complete, string what,
        int timeoutMs = 2000)
    {
        if (!response.Chunked)
            throw new InvalidOperationException(
                $"{what}: the response was not chunk-framed, so it cannot say how it ended.");
        response.Stream.ReadTimeout = timeoutMs;
        try
        {
            var buffer = new byte[4096];
            while (ReadBody(response, buffer, 0, buffer.Length) > 0)
            {
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException
        { SocketErrorCode: SocketError.TimedOut })
        {
            throw new InvalidOperationException($"{what}: the response never ended.");
        }
        finally
        {
            response.Stream.ReadTimeout = ReadTimeoutMs;
        }
        Expect(complete, response.ChunkedEndSeen, $"{what}: the terminating chunk");
        Expect(!complete, response.ClosedEarly, $"{what}: the framing was cut off");
    }

    private static void ExpectClosed(Response response, string what, int timeoutMs = 2000)
    {
        response.Stream.ReadTimeout = timeoutMs;
        try
        {
            var buffer = new byte[4096];
            if (response.Stream.Read(buffer, 0, buffer.Length) != 0)
                throw new InvalidOperationException($"{what}: the response sent more bytes.");
        }
        catch (IOException ex) when (ex.InnerException is SocketException
        { SocketErrorCode: SocketError.TimedOut })
        {
            throw new InvalidOperationException($"{what}: the response never ended.");
        }
        finally
        {
            response.Stream.ReadTimeout = ReadTimeoutMs;
        }
    }

    // ---- assertions --------------------------------------------------------------------

    /// <summary>The token with its first character percent-encoded. Decoding it yields the issued
    /// token exactly, so a server that decodes the target would accept this and one that matches
    /// the raw bytes cannot.</summary>
    private static string PercentEncodeFirst(string token)
        => FormattableString.Invariant($"%{(int)token[0]:X2}") + token[1..];

    private static void VerifyBytes(byte[] data, long offset)
    {
        for (var i = 0; i < data.Length; i++)
            if (data[i] != FakeDownload.At(offset + i))
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Byte {offset + i} is {data[i]}, expected {FakeDownload.At(offset + i)}."));
    }

    private static void Expect<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{what}: expected {expected?.ToString() ?? "null"}, got {actual?.ToString() ?? "null"}.");
    }

    private static void Test(string name, Action body)
    {
        try
        {
            body();
            Console.WriteLine("PASS: " + name);
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"FAIL: {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
