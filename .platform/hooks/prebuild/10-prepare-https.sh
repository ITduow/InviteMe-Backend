#!/bin/bash
set -euo pipefail

DOMAIN="api.inviteme.asia"
CERT_DIR="/etc/letsencrypt/live/${DOMAIN}"
BOOTSTRAP_CERT="/etc/pki/tls/certs/${DOMAIN}-bootstrap.crt"
BOOTSTRAP_KEY="/etc/pki/tls/private/${DOMAIN}-bootstrap.key"
CERT_INCLUDE="/etc/nginx/ssl-${DOMAIN}.conf"

dnf install -y certbot
install -d -m 0755 /var/www/letsencrypt/.well-known/acme-challenge
install -d -m 0755 /etc/nginx /etc/pki/tls/certs
install -d -m 0700 /etc/pki/tls/private

if [[ -s "${CERT_DIR}/fullchain.pem" && -s "${CERT_DIR}/privkey.pem" ]]; then
  CERT_PATH="${CERT_DIR}/fullchain.pem"
  KEY_PATH="${CERT_DIR}/privkey.pem"
else
  if [[ ! -s "${BOOTSTRAP_CERT}" || ! -s "${BOOTSTRAP_KEY}" ]]; then
    openssl req -x509 -nodes -newkey rsa:2048 -sha256 -days 2 \
      -keyout "${BOOTSTRAP_KEY}" \
      -out "${BOOTSTRAP_CERT}" \
      -subj "/CN=${DOMAIN}"
    chmod 0600 "${BOOTSTRAP_KEY}"
    chmod 0644 "${BOOTSTRAP_CERT}"
  fi
  CERT_PATH="${BOOTSTRAP_CERT}"
  KEY_PATH="${BOOTSTRAP_KEY}"
fi

cat > "${CERT_INCLUDE}" <<EOF
ssl_certificate ${CERT_PATH};
ssl_certificate_key ${KEY_PATH};
EOF
chmod 0644 "${CERT_INCLUDE}"

cat > /etc/systemd/system/certbot-renew.service <<'EOF'
[Unit]
Description=Renew Let's Encrypt certificates

[Service]
Type=oneshot
ExecStart=/usr/bin/certbot renew --quiet --deploy-hook "/usr/bin/systemctl reload nginx"
EOF

cat > /etc/systemd/system/certbot-renew.timer <<'EOF'
[Unit]
Description=Twice-daily Let's Encrypt renewal check

[Timer]
OnCalendar=*-*-* 03,15:00:00
RandomizedDelaySec=1h
Persistent=true

[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
systemctl enable --now certbot-renew.timer
