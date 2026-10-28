"""A tiny fake Matrix homeserver for the smoke test.

Answers PUT /_matrix/client/v3/rooms/{room}/send/m.room.message/{txn} with an event id and appends each request
to a JSON-lines file; everything else gets 404. Usage: fake_matrix.py <port> <record-file>
"""

import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SEND_PREFIX = "/_matrix/client/v3/rooms/"
SEND_MARKER = "/send/m.room.message/"


class Handler(BaseHTTPRequestHandler):
    def do_PUT(self):
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length)

        if not (self.path.startswith(SEND_PREFIX) and SEND_MARKER in self.path):
            self.reply(404, {"errcode": "M_UNRECOGNIZED"})
            return

        try:
            body = json.loads(raw)
        except ValueError:
            self.reply(400, {"errcode": "M_NOT_JSON"})
            return

        record = {"method": "PUT", "path": self.path, "authorization": self.headers.get("Authorization"), "body": body}

        with open(self.server.record_file, "a", encoding="utf-8") as out:
            out.write(json.dumps(record) + "\n")

        self.reply(200, {"event_id": "$smoke"})

    def do_GET(self):
        self.reply(404, {"errcode": "M_UNRECOGNIZED"})

    do_POST = do_GET

    def reply(self, status, payload):
        data = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, format, *args):
        sys.stderr.write("fake-matrix: " + (format % args) + "\n")


def main():
    server = ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), Handler)
    server.record_file = sys.argv[2]
    server.serve_forever()


if __name__ == "__main__":
    main()
