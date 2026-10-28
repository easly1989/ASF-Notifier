#!/usr/bin/env bash
# Smoke test against an official OS-specific ASF release, which is trimmed (issue #23): installs the plugin, starts
# ASF with IPC on localhost and one disabled bot (IPC commands need a bot to run on; it never logs on), points the
# plugin at a local fake Matrix homeserver, runs notifytest through IPC and shuts ASF down. Fails unless the plugin
# loaded, ASF logged no error or exception, and the fake received the m.notice.
#
# Usage: tools/smoke/smoke.sh <unzipped-asf-release-dir> <published-plugin-dir>
# Env: PYTHON (default python3), MATRIX_PORT (default 18008), IPC_PORT (default 12421)
set -euo pipefail

if [ "$#" -ne 2 ]; then
	echo "Usage: $0 <unzipped-asf-release-dir> <published-plugin-dir>" >&2
	exit 2
fi

asf_src=$1
plugin_src=$2
here=$(cd "$(dirname "$0")" && pwd)
python=${PYTHON:-python3}
# The message carries an emoji; a Windows console code page cannot print it otherwise
export PYTHONIOENCODING=utf-8
matrix_port=${MATRIX_PORT:-18008}
ipc_port=${IPC_PORT:-12421}
ipc="http://127.0.0.1:${ipc_port}"
token=smoke_FAKE_TOKEN_0000
work=$(mktemp -d)
asf_pid=
fake_pid=

# A Windows ASF build (local runs under Git Bash) needs native paths
native() {
	if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

fail() {
	echo "SMOKE FAIL: $*" >&2
	echo "----- ASF output -----" >&2
	cat "$work/asf.out" >&2 2>/dev/null || true
	echo "----- fake homeserver output -----" >&2
	cat "$work/fake.out" >&2 2>/dev/null || true
	exit 1
}

cleanup() {
	[ -n "$asf_pid" ] && kill "$asf_pid" 2>/dev/null || true
	[ -n "$fake_pid" ] && kill "$fake_pid" 2>/dev/null || true
	rm -rf "$work"
}
trap cleanup EXIT

cp -R "$asf_src"/. "$work"/
exe="$work/ArchiSteamFarm"
[ -f "$exe" ] || exe="$work/ArchiSteamFarm.exe"
[ -f "$exe" ] || fail "no ArchiSteamFarm executable in $asf_src"
chmod +x "$exe"

mkdir -p "$work/config" "$work/plugins/ASFNotifier"
cp -R "$plugin_src"/. "$work/plugins/ASFNotifier/"

cat > "$work/config/ASF.json" <<EOF
{
	"AutoRestart": false,
	"Headless": true,
	"IPC": true,
	"UpdateChannel": 0,
	"UpdatePeriod": 0,
	"ASFNotifier": {
		"Matrix": {
			"HomeserverUrl": "http://127.0.0.1:${matrix_port}",
			"RoomId": "!smoke:localhost"
		}
	}
}
EOF

cat > "$work/config/IPC.config" <<EOF
{
	"Kestrel": {
		"Endpoints": {
			"HTTP": {
				"Url": "${ipc}"
			}
		}
	}
}
EOF

echo '{ "Enabled": false }' > "$work/config/smoke.json"

"$python" "$here/fake_matrix.py" "$matrix_port" "$work/matrix.jsonl" 2> "$work/fake.out" &
fake_pid=$!

ASF_NOTIFIER_MATRIX_ACCESS_TOKEN=$token "$exe" --no-restart --path "$(native "$work")" > "$work/asf.out" 2>&1 &
asf_pid=$!

echo "Waiting for ASF's IPC on ${ipc}"

for _ in $(seq 1 120); do
	kill -0 "$asf_pid" 2>/dev/null || fail "ASF exited during start-up"
	curl -fsS --max-time 20 "${ipc}/Api/ASF" > /dev/null 2>&1 && break
	sleep 1
done

curl -fsS --max-time 20 "${ipc}/Api/ASF" > /dev/null || fail "IPC did not come up within 120 s"

grep -Eq 'ASFNotifier [0-9.]+ loaded' "$work/asf.out" || fail "the plugin did not log that it loaded"
echo "OK: $(grep -Eo 'ASFNotifier [0-9.]+ loaded' "$work/asf.out" | head -n 1)"

# The fake answers 404 to anything but a send; any HTTP answer proves it is up
kill -0 "$fake_pid" 2>/dev/null || fail "the fake homeserver is not running"
fake_status=$(curl -sS --max-time 20 -o /dev/null -w '%{http_code}' "http://127.0.0.1:${matrix_port}/") || fail "the fake homeserver does not answer"
[ "$fake_status" = 404 ] || fail "the fake homeserver answered $fake_status"

reply=$(curl -fsS --max-time 20 -H 'Content-Type: application/json' -d '{"Command":"notifytest"}' "${ipc}/Api/Command") || fail "IPC command failed"
echo "notifytest: $reply"
case "$reply" in
	*"Test notification sent to Matrix."*) ;;
	*) fail "unexpected notifytest reply" ;;
esac

"$python" - "$work/matrix.jsonl" "$token" <<'EOF' || fail "the fake homeserver did not get the expected message"
import json, sys

records = [json.loads(line) for line in open(sys.argv[1], encoding="utf-8")]
assert len(records) == 1, f"expected one request, got {len(records)}"
record = records[0]
assert record["path"].startswith("/_matrix/client/v3/rooms/%21smoke%3Alocalhost/send/m.room.message/"), record["path"]
assert record["authorization"] == "Bearer " + sys.argv[2], "wrong or missing bearer token"
body = record["body"]
assert body["msgtype"] == "m.notice", body
assert body["format"] == "org.matrix.custom.html", body
assert "delivery works" in body["formatted_body"], body
print("OK: fake homeserver got m.notice:", body["body"])
EOF

curl -fsS --max-time 20 -X POST "${ipc}/Api/ASF/Exit" > /dev/null || fail "IPC exit failed"

for _ in $(seq 1 30); do
	kill -0 "$asf_pid" 2>/dev/null || break
	sleep 1
done

if kill -0 "$asf_pid" 2>/dev/null; then
	fail "ASF did not exit within 30 s"
fi

status=0
wait "$asf_pid" || status=$?
asf_pid=
[ "$status" -eq 0 ] || fail "ASF exited with code $status"

# ASF's log lines read date|process|LEVEL|logger|message
if grep -E '\|(ERROR|FATAL)\||Exception' "$work/asf.out"; then
	fail "ASF logged an error or exception"
fi

echo "SMOKE OK"
