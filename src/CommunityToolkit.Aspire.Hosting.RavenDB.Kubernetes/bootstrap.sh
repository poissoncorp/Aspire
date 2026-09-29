#!/bin/bash
# Bootstrap of a RavenDB cluster the RavenDB operator runs for the Aspire RavenDB integration. Runs as a Job in the
# RavenDB image, with the admin client certificate mounted, and:
#   1. waits until the cluster accepts the admin certificate and every node has joined;
#   2. creates the declared databases that do not exist yet;
#   3. leaves every application with exactly one client certificate, with access to its databases only, stored in a
#      Secret the application mounts, and revokes the certificates of applications that are gone. The private keys
#      never leave the cluster.
# Every step is idempotent: the Job runs again on each deployment that changes its configuration.
#
# Input (environment): RAVENDB_URLS, RAVENDB_DATABASES, RAVENDB_REPLICATION_FACTOR,
# RAVENDB_APPLICATIONS ("secret=db1,db2 ..."), RAVENDB_SECRET_OWNER.

set -euo pipefail

ADMIN_PFX=/ravendb/admin/client.pfx
CA_CERT=/ravendb/ca/ca.crt
SERVICE_ACCOUNT=/var/run/secrets/kubernetes.io/serviceaccount
KUBERNETES_API=https://kubernetes.default.svc
NAMESPACE=$(cat "$SERVICE_ACCOUNT/namespace")

# Certificates are named after the namespace, which runs one cluster: everything under this prefix is ours.
CERTIFICATE_PREFIX="aspire.$NAMESPACE."

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

log() {
    echo "[$(date '+%H:%M:%S')] $*"
}

# PKCS#12 files written by older tools use algorithms OpenSSL 3 only reads with the legacy provider.
pkcs12() {
    openssl pkcs12 "$@" -passin pass: 2>/dev/null || openssl pkcs12 -legacy "$@" -passin pass:
}

pkcs12 -in "$ADMIN_PFX" -clcerts -nokeys -out "$WORK/admin.crt"
pkcs12 -in "$ADMIN_PFX" -nocerts -nodes -out "$WORK/admin.key"

RAVEN=(curl -sS --cert "$WORK/admin.crt" --key "$WORK/admin.key" -H "User-Agent: aspire-ravendb-bootstrap")
if [[ -f "$CA_CERT" ]]; then
    RAVEN+=(--cacert "$CA_CERT")
fi

KUBE=(curl -sS --cacert "$SERVICE_ACCOUNT/ca.crt" -H "Authorization: Bearer $(cat "$SERVICE_ACCOUNT/token")" -H "Content-Type: application/json")

read -r -a URLS <<< "$RAVENDB_URLS"
LEADER="${URLS[0]}"

wait_for_cluster() {
    for attempt in $(seq 1 90); do
        if topology=$("${RAVEN[@]}" -f "$LEADER/cluster/topology" 2>/dev/null); then
            missing=""
            for url in "${URLS[@]}"; do
                [[ "$topology" == *"\"$url\""* ]] || missing="$missing $url"
            done

            if [[ -z "$missing" ]]; then
                log "RavenDB cluster at $LEADER is ready."
                return 0
            fi

            log "Waiting for nodes to join the cluster:$missing (attempt $attempt)"
        else
            log "Waiting for RavenDB at $LEADER to accept the admin certificate (attempt $attempt)"
        fi

        sleep 10
    done

    log "The RavenDB cluster at $LEADER did not become ready."
    exit 1
}

create_databases() {
    for database in $RAVENDB_DATABASES; do
        status=$("${RAVEN[@]}" -o /dev/null -w '%{http_code}' "$LEADER/databases?name=$database")

        if [[ "$status" == "200" ]]; then
            log "Database '$database' already exists."
            continue
        fi

        status=$("${RAVEN[@]}" -o "$WORK/response" -w '%{http_code}' -X PUT -H "Content-Type: application/json" \
            -d "{\"DatabaseName\":\"$database\"}" \
            "$LEADER/admin/databases?name=$database&replicationFactor=$RAVENDB_REPLICATION_FACTOR")

        case "$status" in
            2*) log "Database '$database' created (replication factor $RAVENDB_REPLICATION_FACTOR)." ;;
            409) log "Database '$database' already exists." ;;
            *) log "Creating database '$database' failed: HTTP $status $(cat "$WORK/response")"; exit 1 ;;
        esac
    done
}

# The certificates the cluster trusts under our prefix, as "thumbprint name" lines.
our_certificates() {
    "${RAVEN[@]}" -f "$LEADER/admin/certificates?start=0&pageSize=1024&metadataOnly=true" \
        | jq -r --arg prefix "$CERTIFICATE_PREFIX" '.Results[] | select(.Name | startswith($prefix)) | "\(.Thumbprint) \(.Name)"'
}

revoke() {
    local thumbprint=$1 name=$2
    "${RAVEN[@]}" -f -o /dev/null -X DELETE "$LEADER/admin/certificates?thumbprint=$thumbprint"
    log "Revoked client certificate $thumbprint ('$name')."
}

issue_application_certificate() {
    local secret=$1 databases=$2
    local name="$CERTIFICATE_PREFIX$secret"
    local permissions
    permissions=$(jq -cn --arg databases "$databases" '$databases | split(",") | map(select(. != "") | {(.): "ReadWrite"}) | add // {}')

    local status
    status=$("${KUBE[@]}" -o "$WORK/secret.json" -w '%{http_code}' "$KUBERNETES_API/api/v1/namespaces/$NAMESPACE/secrets/$secret")

    if [[ "$status" == "200" ]]; then
        jq -r '.data["client.pfx"]' "$WORK/secret.json" | base64 -d > "$WORK/$secret.pfx"
        pkcs12 -in "$WORK/$secret.pfx" -clcerts -nokeys -out "$WORK/$secret.crt"
    elif [[ "$status" == "404" ]]; then
        openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -subj "/CN=$secret" \
            -addext "extendedKeyUsage=clientAuth" \
            -keyout "$WORK/$secret.key" -out "$WORK/$secret.crt" 2>/dev/null
        openssl pkcs12 -export -inkey "$WORK/$secret.key" -in "$WORK/$secret.crt" -out "$WORK/$secret.pfx" -passout pass:
    else
        log "Reading Secret '$secret' failed: HTTP $status $(cat "$WORK/secret.json")"
        exit 1
    fi

    local thumbprint
    thumbprint=$(openssl x509 -in "$WORK/$secret.crt" -noout -fingerprint -sha1 | sed 's/.*=//; s/://g')

    status=$("${RAVEN[@]}" -o /dev/null -w '%{http_code}' "$LEADER/admin/certificates?thumbprint=$thumbprint")

    if [[ "$status" == "200" ]]; then
        status=$("${RAVEN[@]}" -o "$WORK/response" -w '%{http_code}' -X POST -H "Content-Type: application/json" \
            -d "{\"Thumbprint\":\"$thumbprint\",\"Name\":\"$name\",\"SecurityClearance\":\"ValidUser\",\"Disabled\":false,\"Permissions\":$permissions}" \
            "$LEADER/admin/certificates/edit")
    else
        local certificate
        certificate=$(openssl x509 -in "$WORK/$secret.crt" -outform DER | base64 -w 0)
        status=$("${RAVEN[@]}" -o "$WORK/response" -w '%{http_code}' -X PUT -H "Content-Type: application/json" \
            -d "{\"Name\":\"$name\",\"Certificate\":\"$certificate\",\"SecurityClearance\":\"ValidUser\",\"Permissions\":$permissions}" \
            "$LEADER/admin/certificates")
    fi

    if [[ "$status" != 2* ]]; then
        log "Registering the certificate of '$secret' failed: HTTP $status $(cat "$WORK/response")"
        exit 1
    fi

    # One certificate per application: the ones an earlier Secret carried are no longer in use.
    our_certificates | while read -r other other_name; do
        if [[ "$other_name" == "$name" && "$other" != "$thumbprint" ]]; then
            revoke "$other" "$other_name"
        fi
    done

    if [[ ! -f "$WORK/$secret.key" ]]; then
        log "Client certificate $thumbprint in Secret '$secret' grants access to: ${databases:-no database}."
        return 0
    fi

    # The Secrets belong to the chart's ServiceAccount, so uninstalling the chart removes them.
    local owner
    owner=$("${KUBE[@]}" -f "$KUBERNETES_API/api/v1/namespaces/$NAMESPACE/serviceaccounts/$RAVENDB_SECRET_OWNER" | jq -r .metadata.uid)

    jq -n --arg name "$secret" --arg owner "$RAVENDB_SECRET_OWNER" --arg uid "$owner" --arg pfx "$(base64 -w 0 < "$WORK/$secret.pfx")" '{
        apiVersion: "v1",
        kind: "Secret",
        type: "Opaque",
        metadata: {
            name: $name,
            labels: {"app.kubernetes.io/managed-by": "aspire-ravendb-bootstrap"},
            ownerReferences: [{apiVersion: "v1", kind: "ServiceAccount", name: $owner, uid: $uid}]
        },
        data: {"client.pfx": $pfx}
    }' > "$WORK/new-secret.json"

    status=$("${KUBE[@]}" -o "$WORK/response" -w '%{http_code}' -X POST -d "@$WORK/new-secret.json" \
        "$KUBERNETES_API/api/v1/namespaces/$NAMESPACE/secrets")

    if [[ "$status" != 2* ]]; then
        log "Creating Secret '$secret' failed: HTTP $status $(cat "$WORK/response")"
        exit 1
    fi

    log "Issued client certificate $thumbprint in Secret '$secret' with access to: ${databases:-no database}."
}

# Applications that are no longer deployed lose their certificate.
revoke_departed_applications() {
    local current=" "
    for application in $RAVENDB_APPLICATIONS; do
        current="$current$CERTIFICATE_PREFIX${application%%=*} "
    done

    our_certificates | while read -r thumbprint name; do
        if [[ "$current" != *" $name "* ]]; then
            revoke "$thumbprint" "$name"
        fi
    done
}

wait_for_cluster
create_databases

for application in $RAVENDB_APPLICATIONS; do
    issue_application_certificate "${application%%=*}" "${application#*=}"
done

revoke_departed_applications

log "Bootstrap completed."
