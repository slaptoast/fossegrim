# Fossegrim proxy

The nginx proxy that runs in front of [Fossegrim](https://github.com/slaptoast/fossegrim),
a self-hosted music server and web player. It serves plain HTTP on port 80
and, once Fossegrim has a certificate in the shared `/certs` volume, HTTPS on
port 443. It reloads by itself when the certificate is renewed.

You don't run this image on its own: Fossegrim's
[`docker-compose.yml`](https://github.com/slaptoast/fossegrim/blob/main/docker-compose.yml)
starts it alongside the app. See
[DOCKER.md](https://github.com/slaptoast/fossegrim/blob/main/DOCKER.md#https)
for how HTTPS and Let's Encrypt work.

Published to `ghcr.io/slaptoast/fossegrim-proxy` and `slaptoast/fossegrim-proxy`
on Docker Hub, for `linux/amd64` and `linux/arm64`.
