#!/usr/bin/env bash
# Generate a local CA + a multi-SAN leaf cert covering all Sparx/TFTF hosts.
set -e
export MSYS_NO_PATHCONV=1   # stop git-bash mangling /CN=.. subjects into paths
cd "$(dirname "$0")"
mkdir -p certs && cd certs

# --- CA ---
if [ ! -f ca.key ]; then
  openssl genrsa -out ca.key 2048
  openssl req -x509 -new -nodes -key ca.key -sha256 -days 3650 \
    -subj "/CN=TFTF-Local-CA/O=TFTF-Offline" -out ca.crt
  echo "[gen] created CA"
fi

# --- leaf cert with SANs ---
cat > san.cnf <<'EOF'
[req]
distinguished_name = dn
req_extensions = v3_req
prompt = no
[dn]
CN = tform-0901-hzlhiniyfcwf.tf-cdn.net
O  = TFTF-Offline
[v3_req]
basicConstraints = CA:FALSE
keyUsage = digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = @alt
[alt]
DNS.1  = tform-0901-hzlhiniyfcwf.tf-cdn.net
DNS.2  = *.tf-cdn.net
DNS.3  = tf-cdn.net
DNS.4  = static.tf-cdn.net
DNS.5  = words-express.tf-cdn.net
DNS.6  = *.mcoc-cdn.cn
DNS.7  = mcoc-cdn.cn
DNS.8  = tf-odr.mcoc-cdn.cn
DNS.9  = tf-static.mcoc-cdn.cn
DNS.10 = tftf-odr.mcoc-cdn.cn
DNS.11 = *.sparx.io
DNS.12 = sparx.io
DNS.13 = gametalk.sparx.io
DNS.14 = *.s3.amazonaws.com
DNS.15 = *.amazonaws.com
EOF

openssl genrsa -out server.key 2048
openssl req -new -key server.key -out server.csr -config san.cnf
openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -out server.crt -days 3650 -sha256 -extfile san.cnf -extensions v3_req
cat server.crt server.key > server.pem
echo "[gen] created leaf cert (server.pem)"

# --- Android system-CA filename: <subject_hash_old>.0 ---
HASH=$(openssl x509 -subject_hash_old -in ca.crt -noout)
cp ca.crt "${HASH}.0"
# Android system store format: PEM cert followed by openssl text dump
openssl x509 -in ca.crt -text -fingerprint -noout >> "${HASH}.0"
echo "[gen] android CA file: ${HASH}.0"
ls -la
