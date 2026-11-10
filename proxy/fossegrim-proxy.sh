#!/bin/sh
# Writes nginx's config for whatever is in /certs, starts nginx, and
# rewrites the config and reloads nginx whenever the certificate (or the
# redirect setting) changes.
set -eu

CERTS=/certs
TEMPLATES=/etc/fossegrim-proxy
CONF=/etc/nginx/conf.d/default.conf

if [ "$HTTPS_PORT" = 443 ]; then https_suffix=""; else https_suffix=":$HTTPS_PORT"; fi

log() { echo "fossegrim-proxy: $*"; }

render() {
    sed -e "s|@UPSTREAM@|$FOSSEGRIM_UPSTREAM|g" -e "s|@HTTPS_SUFFIX@|$https_suffix|g" "$@" > "$CONF"
}

# Changes whenever the certificate, its key or the redirect marker does.
fingerprint() {
    for file in fullchain.pem privkey.pem http-redirect-off; do
        if [ -e "$CERTS/$file" ]; then
            echo "$file $(md5sum < "$CERTS/$file")"
        fi
    done
}

configure() {
    if [ -s "$CERTS/fullchain.pem" ] && [ -s "$CERTS/privkey.pem" ]; then
        if [ -e "$CERTS/http-redirect-off" ]; then http=http-proxy.conf; else http=http-redirect.conf; fi
        render "$TEMPLATES/upstream.conf" "$TEMPLATES/$http" "$TEMPLATES/https.conf"
        if nginx -t -q 2>/tmp/nginx-test; then
            log "serving HTTPS with the certificate in $CERTS ($http)"
            return
        fi
        # A broken or mismatched certificate mustn't take plain HTTP down too.
        log "the certificate in $CERTS doesn't work, so serving HTTP only:"
        cat /tmp/nginx-test
    else
        log "no certificate in $CERTS, serving HTTP only"
    fi
    render "$TEMPLATES/upstream.conf" "$TEMPLATES/http-proxy.conf"
}

configure
last=$(fingerprint)

nginx -g 'daemon off;' &
nginx_pid=$!
trap 'nginx -s quit; wait "$nginx_pid"; exit 0' TERM INT QUIT

while kill -0 "$nginx_pid" 2>/dev/null; do
    # In the background, so a stop signal is handled straight away.
    sleep "$CERT_CHECK_SECONDS" &
    wait $! || true
    now=$(fingerprint)
    if [ "$now" != "$last" ]; then
        last=$now
        configure
        nginx -s reload || log "nginx did not reload"
    fi
done

wait "$nginx_pid"
