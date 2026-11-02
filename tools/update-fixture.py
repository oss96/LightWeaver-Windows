"""Serve a synthetic update for Debug builds using the existing updater test overrides."""

import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import time


PACKAGE = b"LightWeaver synthetic update fixture\n" * 131072
VERSION = "99.0.0"


class UpdateFixture(BaseHTTPRequestHandler):
    def do_GET(self):
        base = f"http://127.0.0.1:{self.server.server_port}"
        if self.path.startswith("/releases?"):
            print("Served synthetic update feed: version 99.0.0", flush=True)
            body = json.dumps([{
                "tag_name": VERSION,
                "draft": False,
                "prerelease": False,
                "html_url": base + "/release",
                "assets": [{
                    "name": f"LightWeaver-{VERSION}-setup.exe",
                    "browser_download_url": base + "/setup.exe",
                    "size": len(PACKAGE),
                }],
            }]).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        elif self.path == "/setup.exe":
            self.send_response(200)
            self.send_header("Content-Length", str(len(PACKAGE)))
            self.end_headers()
            try:
                for offset in range(0, len(PACKAGE), 65536):
                    self.wfile.write(PACKAGE[offset:offset + 65536])
                    self.wfile.flush()
                    time.sleep(0.15)
            except (BrokenPipeError, ConnectionResetError):
                pass
        elif self.path == "/release":
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"Synthetic LightWeaver update. No real installer is served.")
        else:
            self.send_error(404)

    def log_message(self, format, *args):
        pass


if __name__ == "__main__":
    with ThreadingHTTPServer(("127.0.0.1", 5387), UpdateFixture) as server:
        print("Run these in the PowerShell session that launches the Debug app:", flush=True)
        print("$env:LIGHTWEAVER_UPDATE_API = 'http://127.0.0.1:5387/'", flush=True)
        print(f"$env:LIGHTWEAVER_UPDATE_SHA256 = '{hashlib.sha256(PACKAGE).hexdigest()}'", flush=True)
        print("$env:LIGHTWEAVER_TEST_APPDATA_ROOT = Join-Path $env:TEMP ('lw-update-preview-' + [guid]::NewGuid())", flush=True)
        print("$env:LIGHTWEAVER_UPDATE_LAUNCH_RECORD = Join-Path $env:LIGHTWEAVER_TEST_APPDATA_ROOT 'installer-launch.jsonl'", flush=True)
        print("Keep this server running until testing ends. Ctrl+C stops it.", flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass
