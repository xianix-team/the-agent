#!/usr/bin/env python3
# ─────────────────────────────────────────────────────────────────────────────
# raise-event-listener.py
#
# Tiny local stand-in for a raise-events listener (e.g. the failure-email relay).
# Prints every POST it receives — headers and pretty JSON body — and answers 200,
# so you can see exactly what the agent would send without a real endpoint.
#
# Usage:
#   python3 raise-event-listener.py [PORT]      # default port 8787
#
# Point a raise-events "url" at http://localhost:8787/<anything>.
# (If the agent itself runs in Docker, use http://host.docker.internal:8787/...)
# ─────────────────────────────────────────────────────────────────────────────
import json
import sys
from datetime import datetime
from http.server import BaseHTTPRequestHandler, HTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8787


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length).decode("utf-8", errors="replace")

        print(f"\n=== {datetime.now():%H:%M:%S}  POST {self.path} ===")
        for name, value in self.headers.items():
            if name.lower() == "authorization":
                value = value[:6] + "…"  # don't echo secrets
            print(f"  {name}: {value}")

        try:
            body = json.loads(raw) if raw else None
            print(json.dumps(body, indent=2, ensure_ascii=False))
            status = (body or {}).get("status") if isinstance(body, dict) else None
            if status == "error":
                print(">>> FAILURE EVENT — this is where the failure email would be sent.")
        except json.JSONDecodeError:
            print("!!! Body is NOT valid JSON:")
            print(raw)

        sys.stdout.flush()
        self.send_response(200)
        self.end_headers()

    def log_message(self, *_):
        pass  # keep output to the events themselves


if __name__ == "__main__":
    print(f"Listening for raise-events on http://localhost:{PORT}  (Ctrl+C to stop)")
    HTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
