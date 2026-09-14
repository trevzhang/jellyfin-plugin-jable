#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
archive=${1:-"$repo/dist/jellyfin-plugin-jable-0.1.6.zip"}
[[ -f "$archive" ]] || { echo "Missing plugin archive: $archive" >&2; exit 1; }
image=jellyfin/jellyfin:10.10.7
temporary=$(mktemp -d "${TMPDIR:-/tmp}/jable-smoke.XXXXXXXX")
container="jable-smoke-$(basename "$temporary" | tr '[:upper:].' '[:lower:]-')"
cleanup() {
    status=$?
    if (( status != 0 )); then docker logs "$container" >&2 2>/dev/null || true; fi
    docker rm -f "$container" >/dev/null 2>&1 || true
    rm -rf -- "$temporary"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -p "$temporary/config/plugins/Jable_0.1.6.0" "$temporary/cache"
python3 - "$archive" "$temporary/config/plugins/Jable_0.1.6.0" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    assert sorted(archive.namelist()) == ['Jellyfin.Plugin.Jable.dll', 'build.yaml'], 'unexpected package contents'
    archive.extractall(sys.argv[2])
PY
# Discover the web root from the image environment and filesystem, not a guessed host path.
docker run --name "$container" --entrypoint /bin/sh "$image" -c \
    'test -n "$JELLYFIN_WEB_DIR" && find "$JELLYFIN_WEB_DIR" -maxdepth 1 -type f -name config.json' \
    > "$temporary/web-path.txt"
web_config=$(cat "$temporary/web-path.txt")
[[ "$web_config" == /* && "$web_config" != *$'\n'* ]] || { echo 'Expected one absolute Web config path' >&2; exit 1; }
docker cp "$container:$web_config" "$temporary/stock.json"
docker rm "$container" >/dev/null
python3 "$repo/scripts/merge_web_config.py" "$temporary/stock.json" "$temporary/web-config.json"
# A root-created atomic file defaults to 0600; the public Web config must be readable by the image user.
chmod 644 "$temporary/web-config.json"
docker run -d --name "$container" -p 127.0.0.1::8096 \
    -v "$temporary/config:/config" -v "$temporary/cache:/cache" \
    -v "$temporary/web-config.json:$web_config:ro" "$image" >/dev/null
port=$(docker inspect --format '{{(index (index .NetworkSettings.Ports "8096/tcp") 0).HostPort}}' "$container")
python3 - "$port" "$temporary" "$container" <<'PY'
import json, secrets, subprocess, sys, time
import xml.etree.ElementTree as ET
from pathlib import Path
from http.client import HTTPException
from urllib.error import HTTPError, URLError
from urllib.request import Request, build_opener, ProxyHandler

base = 'http://127.0.0.1:' + sys.argv[1]
opener = build_opener(ProxyHandler({}))
token = None

def request(path, data=None, expected=200, timeout=5):
    headers = {'Content-Type': 'application/json',
               'Authorization': 'MediaBrowser Client="Jable Smoke", Device="Local", DeviceId="jable-smoke", Version="0.1.6"'}
    if token:
        headers['X-Emby-Token'] = token
    body = None if data is None else json.dumps(data).encode()
    try:
        response = opener.open(Request(base + path, data=body, headers=headers), timeout=timeout)
    except HTTPError as error:
        response = error
    with response:
        payload = response.read()
        assert response.code == expected, (path, response.code, payload[:500])
        return json.loads(payload) if payload and 'json' in response.headers.get('Content-Type', '') else payload

deadline = time.monotonic() + 60
while True:
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise RuntimeError('Jellyfin did not become ready within 60 seconds')
    try:
        info = request('/System/Info/Public', timeout=min(5, remaining))
        break
    except (URLError, OSError, HTTPException, AssertionError):
        if time.monotonic() >= deadline:
            raise RuntimeError('Jellyfin did not become ready within 60 seconds')
        time.sleep(min(1, max(0, deadline - time.monotonic())))
assert info['Version'] == '10.10.7', info
assert b'Jable' in request('/Jable/Page')
request('/Jable/Assets/not-found', expected=404)
request('/Jable/Assets/jable.mjs')
request('/Jable/Assets/jable.css')
links = request('/web/config.json')['menuLinks']
assert sum(link.get('url') == '/Jable/Page' for link in links) == 1

# Complete only this disposable server's setup so host discovery can be queried with an admin token.
password = secrets.token_urlsafe(24)
request('/Startup/Configuration', {'UICulture': 'en-US', 'MetadataCountryCode': 'US', 'PreferredMetadataLanguage': 'en'}, 204)
request('/Startup/User')  # Jellyfin creates the initial user on this wizard GET.
request('/Startup/User', {'Name': 'smoke', 'Password': password}, 204)
request('/Startup/RemoteAccess', {'EnableRemoteAccess': False, 'EnableAutomaticPortMapping': False}, 204)
request('/Startup/Complete', {}, 204)
token = request('/Users/AuthenticateByName', {'Username': 'smoke', 'Pw': password})['AccessToken']
tasks = request('/ScheduledTasks')
assert any(task['Key'] == 'JableCatalogSync' for task in tasks), 'scheduled task activation missing'
options = request('/Libraries/AvailableOptions?libraryContentType=movies&isNewLibrary=true')
movie = next(option for option in options['TypeOptions'] if option['Type'] == 'Movie')
assert any(provider['Name'] == 'Jable' for provider in movie['MetadataFetchers']), movie
assert any(provider['Name'] == 'Jable' for provider in movie['ImageFetchers']), movie
request('/Jable/Catalog', expected=403)  # No selected library: controller and access service fail closed.
request('/Jable/Status', expected=403)
request('/Jable/Sync', {}, 403)
print('PASS: Jellyfin 10.10.7; Page 200; unknown asset 404; menu; controller/LibraryAccessService; both providers; scheduled task; unconfigured catalog 403')

config_path = '/Plugins/7378435d-77d2-4ef4-8e7f-c1269f624b24/Configuration'
config = request(config_path)
assert 'ProxyPassword' not in config
assert 'BrowserBridgeToken' not in config and 'NewBrowserBridgeToken' not in config
request('/Jable/Bridge/Test', {}, 502)
bridge_secret = secrets.token_urlsafe(20)
request(config_path, dict(config, BrowserBridgeUrl='http://127.0.0.1:3103/', NewBrowserBridgeToken=bridge_secret), 204)
config = request(config_path)
assert config['BrowserBridgeUrl'] == 'http://127.0.0.1:3103/'
assert bridge_secret not in json.dumps(config) and 'BrowserBridgeToken' not in config and 'NewBrowserBridgeToken' not in config
xml_path, = Path(sys.argv[2], 'config').rglob('Jellyfin.Plugin.Jable.xml')
def persisted_bridge_token():
    return ET.parse(xml_path).getroot().findtext('BrowserBridgeToken') or ''
assert persisted_bridge_token() == bridge_secret
request(config_path, dict(config, SelectedLibraryId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'), 204)
sync_status = request('/Jable/Status')
sync_task = next(task for task in tasks if task['Key'] == 'JableCatalogSync')
assert sync_status['CanManage'] is True and sync_status['SyncTaskId'] == sync_task['Id']
assert sync_status['IsSyncRunning'] is False
request(config_path, config, 204)  # Restore no selected library; no remote sync is queued.
print('PASS: administrator bridge test bypasses library access; bridge token persists only in XML; status exposes the registered Jable worker ID')
secret = secrets.token_urlsafe(20)
config.update(ProxyUrl='http://smoke-user:' + secret + '@127.0.0.1:1080', ProxyUsername='')
request(config_path, config, 204)
config = request(config_path)
assert config['ProxyUrl'] == 'http://127.0.0.1:1080/' and config['ProxyUsername'] == 'smoke-user'
assert secret not in json.dumps(config) and 'ProxyPassword' not in config and 'NewProxyPassword' not in config
def persisted_secret():
    return ET.parse(xml_path).getroot().findtext('ProxyPassword') or ''
assert persisted_secret() == secret
request(config_path, dict(config, NewProxyPassword=''), 204)
assert persisted_secret() == secret
for url in ('http://user@127.0.0.1:1080', 'http://user:@127.0.0.1:1080'):
    request(config_path, dict(config, ProxyUrl=url, ProxyUsername='', NewProxyPassword='', ClearProxyPassword=False), 204)
    config = request(config_path)
    assert config['ProxyUrl'] == 'http://127.0.0.1:1080/' and config['ProxyUsername'] == 'user'
    assert persisted_secret() == secret
    assert ET.parse(xml_path).getroot().findtext('ProxyUsername') == 'user'
    assert ET.parse(xml_path).getroot().findtext('ProxyUrl') == 'http://127.0.0.1:1080/'
    assert secret not in json.dumps(config) and 'ProxyPassword' not in config and 'NewProxyPassword' not in config
replacement = secrets.token_urlsafe(20)
request(config_path, dict(config, NewProxyPassword=replacement), 204)
assert persisted_secret() == replacement
assert replacement not in json.dumps(request(config_path))
request(config_path, dict(config, ClearProxyPassword=True), 204)
assert persisted_secret() == ''
config = request(config_path)
before = xml_path.read_bytes()
# Snapshot activation logs before deliberate rejected POSTs generate expected ArgumentException logs.
Path(sys.argv[2], 'container.log').write_bytes(subprocess.check_output(['docker', 'logs', sys.argv[3]], stderr=subprocess.STDOUT))
for fields in ({'ProxyUrl': 'ftp://127.0.0.1'}, {'ProxyUrl': 'http://127.0.0.1/path'},
               {'RecentPageCount': 0}, {'RecentPageCount': 101}, {'RequestTimeoutSeconds': 4},
               {'RequestTimeoutSeconds': 61}, {'MinimumRequestIntervalMs': 249}):
    request(config_path, dict(config, **fields), 400)
    assert request(config_path) == config, 'rejected config replaced current settings'
    assert xml_path.read_bytes() == before, 'rejected config modified XML'
request(config_path, dict(config, ProxyUrl='', ProxyUsername=''), 204)
config = request(config_path)
request(config_path, dict(config, BrowserBridgeUrl='', ClearBrowserBridgeToken=True), 204)
config = request(config_path)
assert config['BrowserBridgeUrl'] == '' and persisted_bridge_token() == ''
assert bridge_secret not in json.dumps(config) and 'BrowserBridgeToken' not in config and 'NewBrowserBridgeToken' not in config
print('PASS: real plugin configuration GET/POST; JSON secret redaction; bridge XML persist/clear; URL credential split; user@ and user:@ preserve XML password; proxy XML preserve/replace/clear; seven invalid saves rejected without mutation')
PY
python3 - "$temporary/container.log" <<'PY'
from pathlib import Path
import re, sys
logs = Path(sys.argv[1]).read_text()
assert re.search(r'Loaded (?:assembly|plugin).*Jable', logs, re.I), 'plugin load confirmation missing'
assert not re.search(r'(?:fail|error|exception|unable)[^\n]*(?:Jellyfin\.Plugin\.Jable|\bJable\b)|(?:Jellyfin\.Plugin\.Jable|\bJable\b)[^\n]*(?:fail|error|exception)', logs, re.I), logs
assert not re.search(r'(?:TypeLoadException|ReflectionTypeLoadException|Unable to resolve service)[\s\S]{0,800}Jellyfin\.Plugin\.Jable', logs, re.I), logs
print('PASS: activation log contains plugin load and no detected Jable load/DI failure; deliberate validation-error requests excluded')
PY
printf 'Image Web config: %s\n' "$web_config"
shasum -a 256 "$archive"
