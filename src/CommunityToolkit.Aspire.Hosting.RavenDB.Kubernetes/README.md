# CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes library

Deploys the [RavenDB hosting integration](https://www.nuget.org/packages/CommunityToolkit.Aspire.Hosting.RavenDB) to Kubernetes through the [RavenDB operator](https://github.com/ravendb/ravendb-operator). `aspire run` keeps the local RavenDB container; `aspire publish` and `aspire deploy` put a `RavenDBCluster` into the Helm chart and let the operator run it.

## Getting started

### Prerequisites

- The RavenDB operator installed in the cluster.
- An ingress controller the operator can publish the nodes through (`nginx`, `traefik` or `haproxy`).
- DNS for the nodes, inside the cluster as well: node `a` is `https://a.<domain>:443`, and the operator's own bootstrap and the applications connect through those names.
- Secrets in the target namespace, created the way the operator documents them:

```bash
kubectl create secret generic ravendb-server --from-file=server.pfx=./server.pfx
kubectl create secret generic ravendb-admin --from-file=client.pfx=./admin.pfx
kubectl create secret generic ravendb-ca --from-file=ca.crt=./ca.crt
```

The server certificate covers every node (`*.<domain>`), the admin certificate is a client certificate the operator trusts, and the certificate authority is only needed when it is not publicly trusted.

The operator reads the `.pfx` files with SHA-1 and 3DES only, which is not what OpenSSL 3 writes by default. Export them with `openssl pkcs12 -export -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1 ...`; otherwise the operator reports `pkcs12: unknown digest algorithm` and does not start the nodes.

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
- a bootstrap Job, with a ServiceAccount and a Role limited to the applications' Secrets, that waits until every node has joined, creates the databases declared with `ensureCreated: true`, and gives every application a client certificate of its own.

`aspire deploy` installs the chart and waits for the bootstrap Job to complete. The operator only runs pinned images, so set `Image` (or `WithImageTag(...)`) to a concrete tag. It runs one cluster per namespace.

Helm waits for the whole chart, the `RavenDBCluster` included, for five minutes; Aspire does not offer a longer timeout. When the operator puts the cluster in its Error phase, or a bootstrap attempt fails, the deployment logs the reasons right away instead of leaving you with Helm's timeout. A cluster that is still starting when Helm gives up keeps starting: run `aspire deploy` again.

To let the operator obtain the certificates from Let's Encrypt instead, use `cluster.WithLetsEncrypt("ops@example.com", "ravendb-admin")` with a public domain.

### Client certificates

Each application gets a certificate with `ValidUser` clearance and read/write access to the databases it references and to nothing else: `WithReference(db)` grants that database, `WithReference(server)` grants every database declared on the server. The bootstrap generates the key inside the cluster, registers the certificate with RavenDB as `aspire.<namespace>.<secret>` and stores it in the Secret `<server>-<application>-client-certificate`, which the application mounts. The [RavenDB client integration](https://www.nuget.org/packages/CommunityToolkit.Aspire.RavenDB.Client) finds it through `Aspire__RavenDB__Client__<connection name>__CertificatePath`, so no application code is needed:

```csharp
builder.AddRavenDBClient("mydb");
```

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

### Destroy

`aspire destroy` uninstalls the chart. The operator removes the cluster; the data volumes stay, as with every StatefulSet.

The RavenDB operator (2.0.0) cannot start a cluster again on the volumes of a removed one: its initialization Job fails on nodes that already form a cluster, and the cluster stays in its Error phase. Delete its volumes, `ravendb-data-ravendb-<tag>-0`, before deploying again; this deletes the data.

`aspire destroy` only knows about deployments that completed: after a first `aspire deploy` that failed, uninstall the release with `helm uninstall`.

## Feedback & contributing

https://github.com/CommunityToolkit/Aspire
