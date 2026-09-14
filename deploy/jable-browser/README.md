# NAS browser bridge deployment

This project runs Chromium and the Jable bridge on the external `jable-internal` Docker network. The bridge URL for the Jellyfin plugin is `http://jable-browser-bridge:3000/`.

## Start

Run from this directory. Choose strong, different values for `CHROMIUM_WEB_PASSWORD` and `BRIDGE_TOKEN` in `.env` before starting.

```bash
docker network inspect jable-internal >/dev/null 2>&1 || docker network create jable-internal
cp .env.example .env
docker compose up -d --build
docker compose ps
```

Only the authenticated Chromium GUI is published at `https://NAS_IP:3100/`; it redirects to `/login/`. CDP port 9222 is Docker-internal, and the bridge intentionally has no host `ports` entry.

Connect Jellyfin to the same network in its Compose definition:

```yaml
services:
  jellyfin:
    networks:
      - default
      - jable-internal
networks:
  jable-internal:
    external: true
```

Configure the plugin bridge URL as `http://jable-browser-bridge:3000/`. Open the GUI and complete any manual challenge there; do not use a proxy for Jable traffic in this deployment.

## Validate

```bash
curl -kI https://NAS_IP:3100/
docker exec jellyfin-app-1 sh -c 'wget -qO- http://jable-browser-bridge:3000/healthz'
```

The first command must redirect to `/login/`; the second must return `{"ok":true,"browser":"ready"}`.

## Refresh static Jable IPs

The IP values in `.env` are static host overrides. Query trusted DoH when Jable changes its DNS records:

```bash
curl -fsS 'https://doh.pub/resolve?name=jable.tv&type=A'
curl -fsS 'https://doh.pub/resolve?name=assets-cdn.jable.tv&type=A'
```

Update `JABLE_EDGE_IP` and `JABLE_ASSET_CDN_IP` in `.env`, then recreate only this project:

```bash
docker compose up -d --force-recreate chromium bridge
```

The `docker.1ms.run` prefix is only an image-pull mirror; it does not proxy Jable traffic.
