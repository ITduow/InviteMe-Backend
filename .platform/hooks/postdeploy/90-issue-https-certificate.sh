#!/bin/bash
set -euo pipefail

DOMAIN="api.inviteme.asia"
RENEWAL_CONFIG="/etc/letsencrypt/renewal/${DOMAIN}.conf"
WEBROOT="/var/www/letsencrypt"
CERT_DIR="/etc/letsencrypt/live/${DOMAIN}"
CERT_INCLUDE="/etc/nginx/ssl-${DOMAIN}.conf"

if [[ ! -f "${RENEWAL_CONFIG}" ]]; then
  certbot certonly \
    --webroot --webroot-path "${WEBROOT}" \
    --non-interactive --agree-tos --register-unsafely-without-email \
    --cert-name "${DOMAIN}" --domain "${DOMAIN}"
fi

test -s "${CERT_DIR}/fullchain.pem"
test -s "${CERT_DIR}/privkey.pem"
cat > "${CERT_INCLUDE}" <<EOF
ssl_certificate ${CERT_DIR}/fullchain.pem;
ssl_certificate_key ${CERT_DIR}/privkey.pem;
EOF
chmod 0644 "${CERT_INCLUDE}"
nginx -t
systemctl reload nginx
