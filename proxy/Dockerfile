# syntax=docker/dockerfile:1
#
# The Fossegrim proxy: nginx in front of the app. It always serves plain
# HTTP on port 80, and adds HTTPS on 443 as soon as a certificate is in
# /certs (fullchain.pem + privkey.pem), whether Fossegrim got it from Let's
# Encrypt or you put it there yourself. It checks the folder every 30
# seconds and reloads nginx when the certificate changes. See DOCKER.md.

FROM nginx:1.30-alpine

COPY conf/ /etc/fossegrim-proxy/
COPY fossegrim-proxy.sh /usr/local/bin/fossegrim-proxy
# /certs is owned by the app's user (uid 100, gid 101), as in the app's
# image: whichever container first mounts the empty shared volume decides
# its owner, and the app has to be able to write there.
RUN chmod 755 /usr/local/bin/fossegrim-proxy \
    && mkdir -p /certs \
    && chown 100:101 /certs

# Where the app is (the Compose service name and its port), the HTTPS port
# people connect to (for redirects, when the host maps 443 to another port),
# and how often to look for a new certificate.
ENV FOSSEGRIM_UPSTREAM=fossegrim:8080 \
    HTTPS_PORT=443 \
    CERT_CHECK_SECONDS=30

EXPOSE 80 443
VOLUME ["/certs"]

ENTRYPOINT ["/usr/local/bin/fossegrim-proxy"]
