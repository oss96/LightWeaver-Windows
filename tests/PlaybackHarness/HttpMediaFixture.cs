using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class HttpMediaFixture
{
    public static async Task<int> Run(string file, string ready, string stop)
    {
        using var cancellation = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await File.WriteAllTextAsync(ready, ((IPEndPoint)listener.LocalEndpoint).Port.ToString());
        var handlers = new List<Task>();
        try
        {
            while (!File.Exists(stop))
            {
                if (!listener.Pending()) { await Task.Delay(50); continue; }
                var client = await listener.AcceptTcpClientAsync();
                handlers.Add(Serve(client, file, cancellation.Token));
            }
        }
        finally
        {
            cancellation.Cancel();
            listener.Stop();
            await Task.WhenAll(handlers);
        }
        return 0;
    }

    private static async Task Serve(TcpClient client, string file, CancellationToken token)
    {
        using (client)
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var request = await reader.ReadLineAsync(token);
            string? range = null;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token)))
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line[6..].Trim();
            using var media = File.OpenRead(file);
            long start = 0, end = media.Length - 1;
            var partial = range?.StartsWith("bytes=", StringComparison.Ordinal) == true;
            if (partial)
            {
                var parts = range![6..].Split('-');
                start = long.Parse(parts[0]);
                if (parts.Length > 1 && parts[1].Length > 0) end = Math.Min(end, long.Parse(parts[1]));
            }
            if (start < 0 || start >= media.Length || end < start)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{media.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
                return;
            }
            var header = $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\nContent-Type: video/mp4\r\nContent-Length: {end - start + 1}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n"
                + (partial ? $"Content-Range: bytes {start}-{end}/{media.Length}\r\n" : "") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
            if (request?.StartsWith("HEAD ", StringComparison.Ordinal) == true) return;
            media.Position = start;
            var bytes = new byte[32768];
            while (media.Position <= end)
            {
                var count = await media.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, end - media.Position + 1)), token);
                if (count == 0) break;
                await stream.WriteAsync(bytes.AsMemory(0, count), token);
                await Task.Delay(20, token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
    }
}
