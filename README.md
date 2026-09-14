# Jellyfin Plugin Jable

A Jellyfin 10.10.7 metadata provider and searchable Jable catalog for locally managed JAV libraries.

## Features

- Strict identifier matching before automatic metadata updates.
- Title, release date, performers, genres, studio, duration, and poster metadata when available.
- Searchable Web catalog with local/online filters and sorting by date, Jable views, or favorites.
- Browser-bridge metadata retrieval through a local Chromium sidecar, with optional HTTP, HTTPS, and SOCKS5 proxy compatibility.
- Atomic local cache with recovery and read-only degradation on corruption.
- No online playback or download functionality.

## Compatibility

- Jellyfin Server 10.10.7
- .NET 8
- The catalog page targets Jellyfin Web. Standard metadata remains available to other clients.

## Install from the plugin repository

1. In Jellyfin, open **Dashboard → Plugins → Repositories**.
2. Add this repository URL:

   ```text
   https://raw.githubusercontent.com/trevzhang/jellyfin-plugin-jable/main/manifest.json
   ```

3. Open **Catalog**, install **Jable**, and restart Jellyfin.
4. Deploy the browser bridge on the same Docker network as Jellyfin (see [deploy/jable-browser](deploy/jable-browser/README.md)). Set the plugin bridge URL to `http://jable-browser-bridge:3000/`, then select the target library.
5. A proxy remains supported for compatible installations, but is not needed for Jable traffic with the browser bridge deployment.

The browser GUI is available at `https://NAS_IP:3100/`. Sign in with the credentials in `deploy/jable-browser/.env`, then complete any manual Jable/Cloudflare challenge in that GUI before retrying the plugin request. Do not expose CDP port 9222 or the bridge to the host network.

Jellyfin 10.10 cannot add a normal-user menu entry from a server plugin. To add the catalog link, merge this entry into the persistent Jellyfin Web `config.json` and mount that file read-only into the container:

```json
{
  "name": "Jable",
  "icon": "search",
  "url": "/Jable/Page"
}
```

The included helper preserves other Web configuration values:

```bash
python3 scripts/merge_web_config.py /path/to/config.json /path/to/config.json
```

Back up the file before changing it. A container restart cannot add a new bind mount; update the existing Compose/container definition when the file is not already persistent.

## Build and verify

Docker is sufficient; a host .NET SDK is not required.

```bash
./scripts/package_plugin.sh
./scripts/smoke_jellyfin.sh dist/jellyfin-plugin-jable-0.1.5.zip
```

## Privacy and security

- Jable credentials are not used or stored.
- Proxy passwords are excluded from the JSON configuration API but remain plaintext in Jellyfin's XML plugin configuration because Jellyfin 10.10 has no plugin secret store. Protect the configuration directory with filesystem permissions.
- The bridge token is also stored in that XML configuration in plaintext. Use a long random token and protect the configuration directory with filesystem permissions.
- The deployment's static Jable IP overrides can go stale. Refresh them with the trusted DoH commands in [deploy/jable-browser](deploy/jable-browser/README.md#refresh-static-jable-ips), update `.env`, and recreate `chromium` and `bridge`.
- `docker.1ms.run` is only a Docker image-pull mirror; it is not used to proxy Jable traffic.
- The catalog API follows the selected Jellyfin library's user access policy.
- The plugin does not bypass CAPTCHA or Cloudflare challenges.

## Limitations

- Jable markup can change; parser updates may be required.
- Studio and duration extraction depend on schema.org fields being present.
- Network timeouts fail safely; per-attempt timeout retries are not currently implemented.
- Installation and configuration are intended only for libraries and users permitted to access adult material.
