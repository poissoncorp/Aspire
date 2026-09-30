# CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes library

Deploys the [RavenDB hosting integration](https://www.nuget.org/packages/CommunityToolkit.Aspire.Hosting.RavenDB) to Kubernetes through the [RavenDB operator](https://github.com/ravendb/ravendb-operator). `aspire run` keeps the local RavenDB container; `aspire publish` and `aspire deploy` put a `RavenDBCluster` into the Helm chart and let the operator run it.

## Getting started

### Prerequisites

- Helm 4 on the machine that runs `aspire deploy`: Aspire's Kubernetes deployment installs the chart with it.
- The RavenDB operator, with cert-manager, installed once per Kubernetes cluster (see below).
- An ingress controller the operator can publish the nodes through: `traefik` (the default), `haproxy` or `nginx`, set with `IngressClassName`. Traefik does not pass TLS through for the operator's Ingress, so for `traefik` the chart adds an `IngressRouteTCP` that does, on the `websecure` entry point: Traefik needs its Kubernetes CRD provider enabled and `websecure` exposed on port 443. `nginx` is ingress-nginx, which [receives no fixes since March 2026](https://kubernetes.io/blog/2025/11/11/ingress-nginx-retirement/).
- DNS for the nodes, inside the cluster as well, before the nodes start: node `a` is `https://a.<domain>:443`, and the operator's own bootstrap and the applications connect through those names. Each node checks its own URL once when it starts; with a private certificate authority, nodes that could not reach themselves reject each other until they restart (the bootstrap Job says so after three minutes). Once DNS resolves the nodes, restart them with `kubectl delete pod --namespace <namespace> -l nodeTag`; if the operator's `ravendb-cluster-init` Job has failed meanwhile, delete it as well (only a failed one), so that the operator runs it again. Then run `aspire deploy` again.
- Secrets in the target namespace, created the way the operator documents them:

```bash
kubectl create secret generic ravendb-server --from-file=server.pfx=./server.pfx
kubectl create secret generic ravendb-admin --from-file=client.pfx=./admin.pfx
kubectl create secret generic ravendb-ca --from-file=ca.crt=./ca.crt
```

The server certificate covers every node (`*.<domain>`), the admin certificate is a client certificate the operator trusts, and the certificate authority is only needed when it is not publicly trusted.

The operator reads the `.pfx` files with SHA-1 and 3DES only, which is not what OpenSSL 3 writes by default. Export them with `openssl pkcs12 -export -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1 ...`; otherwise the operator reports `pkcs12: unknown digest algorithm` and does not start the nodes.

Install cert-manager and the operator outside the AppHost, once per Kubernetes cluster: the operator's chart owns the `RavenDBCluster` definition, so uninstalling it removes every RavenDB cluster in Kubernetes. This integration is tested with operator 2.0.0:

```bash
helm install cert-manager oci://quay.io/jetstack/charts/cert-manager --version v1.21.2 -n cert-manager --create-namespace --set crds.enabled=true
helm install ravendb-operator ravendb-operator --repo https://ravendb.github.io/ravendb-operator/helm --version 2.0.0 -n ravendb-operator-system --create-namespace
```

`aspire deploy` checks for the operator before Helm runs, and stops with a pointer here when it is missing.

### Install the package

In your AppHost project, install the library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes
```

## Usage example

```csharp
builder.AddKubernetesEnvironment("k8s");

var license = builder.AddParameter("ravendb-license", secret: true);

var db = builder.AddRavenDB("ravendb")
    .WithLicense(license)
    .PublishAsRavenDBCluster(cluster =>
    {
        cluster.Domain = "ravendb.example.com";
        cluster.Nodes = 3;
        cluster.Image = "ravendb/ravendb:7.2.6-ubuntu.24.04-x64";
        cluster.WithCertificates("ravendb-server", "ravendb-admin", "ravendb-ca");
    })
    .AddDatabase("mydb", ensureCreated: true);

builder.AddProject<Projects.MyService>("api")
    .WithReference(db);
```

The chart then contains, next to the application:

- the `RavenDBCluster` the operator reconciles, and a Secret with the license;
- a bootstrap Job, with a ServiceAccount and a Role that reads only the applications' Secrets and may create Secrets (Kubernetes cannot limit creation to given names), that waits until every node has joined, creates the databases declared with `ensureCreated: true`, and gives every application a client certificate of its own. It runs as the image's non-root user, and keeps the keys it handles in memory.

`aspire deploy` installs the chart and waits for the bootstrap Job to complete. The operator only runs pinned images, so set `Image` (or `WithImageTag(...)`) to a concrete tag. It runs one cluster per namespace.

Each node keeps its data on a persistent volume of `StorageSize` (`10Gi` by default) and `StorageClassName`, by default the Kubernetes environment's `DefaultStorageClassName`, else the cluster's default storage class. The environment's `DefaultStorageType` and `DefaultStorageSize` do not apply: a database always gets a persistent volume, sized for a database. Both settings take effect when the cluster is created: a StatefulSet keeps its volume templates, so changing them later changes nothing, and neither the operator nor Kubernetes reports it. Resize the volumes themselves where their storage class allows it.

Helm waits for the whole chart, the `RavenDBCluster` included, for five minutes; Aspire does not offer a longer timeout. When the operator puts the cluster in its Error phase, or a bootstrap attempt fails, the deployment logs the reasons right away instead of leaving you with Helm's timeout. A cluster that is still starting when Helm gives up keeps starting: run `aspire deploy` again.

To let the operator obtain the certificates from Let's Encrypt instead, use `cluster.WithLetsEncrypt("ops@example.com", "ravendb-admin")` with a public domain.

### Client certificates

Each application gets a certificate with `ValidUser` clearance and read/write access to the databases it references and to nothing else: `WithReference(db)` grants that database, `WithReference(server)` grants every database declared on the server. The bootstrap generates the key inside the cluster, registers the certificate with RavenDB as `aspire.<namespace>.<secret>` and stores it in the Secret `<server>-<application>-client-certificate`, which the application mounts. The [RavenDB client integration](https://www.nuget.org/packages/CommunityToolkit.Aspire.RavenDB.Client) finds it through `Aspire__RavenDB__Client__<connection name>__CertificatePath`, so no application code is needed:

```csharp
builder.AddRavenDBClient("mydb");
```

Leave the client's `CreateDatabase` setting off: the deployment creates the databases declared with `ensureCreated: true`, and the application's certificate may neither look databases up nor create them, so a client that tries fails when it starts.

With a certificate authority of its own, the application trusts it through `SSL_CERT_DIR`, next to the image's own trusted roots. The applications never get the admin certificate.

The Job runs again whenever its configuration changes and leaves every application with exactly one certificate. It keeps the certificate in the application's Secret, updates its access, and registers it again if it was removed from RavenDB. It revokes every other certificate with the application's name, as well as those of applications that are no longer deployed: every certificate under `aspire.<namespace>.` belongs to the integration. The Secrets belong to the chart's ServiceAccount, so uninstalling the chart removes them.

### A server someone else runs

For a RavenDB server that runs outside the application, such as a cluster a platform team runs for several applications, its owner issues each application a certificate for its databases, and the chart only mounts it. It gets no admin certificate, no Job and no database:

```csharp
var url = builder.AddParameter("ravendb-url"); // https://a.ravendb.example.com:443

var db = builder.AddRavenDB("ravendb")
    .PublishAsExisting(url)
    .AddDatabase("mydb");

builder.AddProject<Projects.Api>("api")
    .WithReference(db)
    .WithRavenDBClientCertificateSecret(db, "api-cert", "ravendb-ca");
```

`api-cert` holds the application's certificate under `client.pfx`, and the optional `ravendb-ca` the server's certificate authority under `ca.crt`. An application of a cluster published with `PublishAsRavenDBCluster` can bring its certificate the same way; the bootstrap then issues it none.

### Changing the AppHost

Aspire writes the chart into the same directory (`aspire-output` by default) on every publish and deploy, and does not delete what earlier runs wrote there. The integration cleans up after itself: the bootstrap Job gets a new name whenever its configuration changes, and the Jobs of earlier configurations are removed from the chart, so changing the cluster, its databases or its applications needs no clean-up. Removing a resource from the AppHost is up to Aspire: its templates stay in the chart, and Helm keeps deploying it. After removing a resource, delete the chart directory before the next `aspire deploy`.

### Destroy

`aspire destroy` uninstalls the chart. The operator removes the cluster; the data volumes stay, as with every StatefulSet.

The RavenDB operator (2.0.0) cannot start a cluster again on the volumes of a removed one: its initialization Job fails on nodes that already form a cluster, and the cluster stays in its Error phase. Delete its volumes, `ravendb-data-ravendb-<tag>-0`, before deploying again; this deletes the data.

`aspire destroy` only knows about deployments that completed: Aspire records the Helm release once `helm upgrade` succeeds. After a first `aspire deploy` that failed, uninstall the release yourself with `helm uninstall <release> --namespace <namespace>`. The release is named after the environment deployed (`production` for `aspire deploy` without `--environment`), unless the AppHost names it with `WithHelm(helm => helm.WithReleaseName(...))`.

## Feedback & contributing

https://github.com/CommunityToolkit/Aspire
