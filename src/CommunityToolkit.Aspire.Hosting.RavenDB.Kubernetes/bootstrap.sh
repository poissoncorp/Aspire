#!/bin/bash
# Bootstrap of a RavenDB server published to Kubernetes by the Aspire RavenDB integration. Runs as a Job in the
# RavenDB image, with the admin client certificate mounted, and:
#   1. waits until the server accepts the admin certificate (and, for an operator cluster, every node has joined);
#   2. creates the declared databases that do not exist yet;
#   3. gives every application a client certificate of its own, with access to its databases only, stored in a
#      Secret the application mounts. The private key never leaves the cluster.
# Every step is idempotent: the Job runs again on each deployment.
#
# Input (environment): RAVENDB_URLS, RAVENDB_WAIT_FOR_NODES, RAVENDB_DATABASES, RAVENDB_REPLICATION_FACTOR,
# RAVENDB_APPLICATIONS ("secret=db1,db2 ..."), RAVENDB_SECRET_OWNER.

set -euo pipefail

ADMIN_PFX=/ravendb/admin/client.pfx
CA_CERT=/ravendb/ca/ca.crt
SERVICE_ACCOUNT=/var/run/secrets/kubernetes.io/serviceaccount
KUBERNETES_API=https://kubernetes.default.svc

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

read -r -a URLS <<< "$RAVENDB_URLS"
LEADER="${URLS[0]}"

wait_for_server() {
    for attempt in $(seq 1 90); do
        if topology=$("${RAVEN[@]}" -f "$LEADER/cluster/topology" 2>/dev/null); then
            missing=""
            if [[ "$RAVENDB_WAIT_FOR_NODES" == "true" ]]; then
                for url in "${URLS[@]}"; do
                    [[ "$topology" == *"\"$url\""* ]] || missing="$missing $url"
                done
            fi

            if [[ -z "$missing" ]]; then
                log "RavenDB at $LEADER is ready."
                return 0
            fi

            log "Waiting for nodes to join the cluster:$missing (attempt $attempt)"
        else
            log "Waiting for RavenDB at $LEADER to accept the admin certificate (attempt $attempt)"
        fi

        sleep 10
    done

    log "RavenDB at $LEADER did not become ready."
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

KUBE=()
NAMESPACE=""
OWNER_UID=""

connect_to_kubernetes() {
    NAMESPACE=$(cat "$SERVICE_ACCOUNT/namespace")
    KUBE=(curl -sS --cacert "$SERVICE_ACCOUNT/ca.crt" -H "Authorization: Bearer $(cat "$SERVICE_ACCOUNT/token")" -H "Content-Type: application/json")

    # The Secrets belong to the chart's ServiceAccount, so uninstalling the chart removes them.
    OWNER_UID=$("${KUBE[@]}" -f "$KUBERNETES_API/api/v1/namespaces/$NAMESPACE/serviceaccounts/$RAVENDB_SECRET_OWNER" \
        | sed -n 's/.*"uid": *"\([^"]*\)".*/\1/p' | head -n 1)
}

permissions_json() {
    local json="" database
    for database in ${1//,/ }; do
        json="$json${json:+,}\"$database\":\"ReadWrite\""
    done
    echo "{$json}"
}

issue_application_certificate() {
    local secret=$1 databases=$2
    local permissions
    permissions=$(permissions_json "$databases")

    local status
    status=$("${KUBE[@]}" -o "$WORK/secret.json" -w '%{http_code}' "$KUBERNETES_API/api/v1/namespaces/$NAMESPACE/secrets/$secret")

    if [[ "$status" == "200" ]]; then
        sed -n 's/.*"client\.pfx": *"\([^"]*\)".*/\1/p' "$WORK/secret.json" | base64 -d > "$WORK/$secret.pfx"
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
            -d "{\"Thumbprint\":\"$thumbprint\",\"Name\":\"$secret\",\"SecurityClearance\":\"ValidUser\",\"Disabled\":false,\"Permissions\":$permissions}" \
            "$LEADER/admin/certificates/edit")
    else
        local certificate
        certificate=$(openssl x509 -in "$WORK/$secret.crt" -outform DER | base64 -w 0)
        status=$("${RAVEN[@]}" -o "$WORK/response" -w '%{http_code}' -X PUT -H "Content-Type: application/json" \
            -d "{\"Name\":\"$secret\",\"Certificate\":\"$certificate\",\"SecurityClearance\":\"ValidUser\",\"Permissions\":$permissions}" \
            "$LEADER/admin/certificates")
    fi

    if [[ "$status" != 2* ]]; then
        log "Registering the certificate of '$secret' failed: HTTP $status $(cat "$WORK/response")"
        exit 1
    fi

    if [[ ! -f "$WORK/$secret.key" ]]; then
        log "Client certificate $thumbprint in Secret '$secret' grants access to: ${databases:-no database}."
        return 0
    fi

    local owner=""
    if [[ -n "$OWNER_UID" ]]; then
        owner=",\"ownerReferences\":[{\"apiVersion\":\"v1\",\"kind\":\"ServiceAccount\",\"name\":\"$RAVENDB_SECRET_OWNER\",\"uid\":\"$OWNER_UID\"}]"
    fi

    status=$("${KUBE[@]}" -o "$WORK/response" -w '%{http_code}' -X POST \
        -d "{\"apiVersion\":\"v1\",\"kind\":\"Secret\",\"metadata\":{\"name\":\"$secret\",\"labels\":{\"app.kubernetes.io/managed-by\":\"aspire-ravendb-bootstrap\"}$owner},\"type\":\"Opaque\",\"data\":{\"client.pfx\":\"$(base64 -w 0 < "$WORK/$secret.pfx")\"}}" \
        "$KUBERNETES_API/api/v1/namespaces/$NAMESPACE/secrets")

    if [[ "$status" != 2* ]]; then
        log "Creating Secret '$secret' failed: HTTP $status $(cat "$WORK/response")"
        exit 1
    fi

    log "Issued client certificate $thumbprint in Secret '$secret' with access to: ${databases:-no database}."
}

wait_for_server
create_databases

if [[ -n "$RAVENDB_APPLICATIONS" ]]; then
    connect_to_kubernetes

    for application in $RAVENDB_APPLICATIONS; do
        issue_application_certificate "${application%%=*}" "${application#*=}"
    done
fi

log "Bootstrap completed."
