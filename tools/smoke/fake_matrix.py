"""A tiny fake Matrix homeserver for the smoke test.

Answers PUT /_matrix/client/v3/rooms/{room}/send/m.room.message/{txn} with an event id and appends each request
to a JSON-lines file. For the command listener it also answers GET /account/whoami and GET /sync: the first sync
(no since) returns only a position, the next one returns one "!version" command from SMOKE_USER in the filtered
room, and later ones wait up to a second and return nothing new. Everything else gets 404.
Usage: fake_matrix.py <port> <record-file>
"""

import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

SEND_PREFIX = "/_matrix/client/v3/rooms/"
SEND_MARKER = "/send/m.room.message/"
SYNC = "/_matrix/client/v3/sync"
WHOAMI = "/_matrix/client/v3/account/whoami"
BOT_USER = "@smoke-bot:localhost"
SMOKE_USER = "@smoke-admin:localhost"
COMMAND_EVENT = "$smoke-command"


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
        url = urlsplit(self.path)

        if url.path == WHOAMI:
            self.reply(200, {"user_id": BOT_USER})
        elif url.path == SYNC:
            self.sync(parse_qs(url.query))
        else:
            self.reply(404, {"errcode": "M_UNRECOGNIZED"})

    def do_POST(self):
        self.reply(404, {"errcode": "M_UNRECOGNIZED"})

    def sync(self, query):
        # The listener's filter names the one room it reads; a filter that is not JSON fails the request
        try:
            room = json.loads(query["filter"][0])["room"]["rooms"][0]
        except (KeyError, IndexError, ValueError):
            self.reply(400, {"errcode": "M_INVALID_PARAM"})
            return

        since = query.get("since", [None])[0]

        if since is None:
            self.reply(200, {"next_batch": "s1"})
            return

        if since == "s1":
            command = {
                "type": "m.room.message",
                "event_id": COMMAND_EVENT,
                "sender": SMOKE_USER,
                "origin_server_ts": int(time.time() * 1000),
                "unsigned": {"age": 100},
                "content": {"msgtype": "m.text", "body": "!version"},
            }
            self.reply(200, {"next_batch": "s2", "rooms": {"join": {room: {"timeline": {"events": [command]}}}}})
            return

        time.sleep(min(int(query.get("timeout", ["0"])[0]), 1000) / 1000)
        self.reply(200, {"next_batch": since})

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
