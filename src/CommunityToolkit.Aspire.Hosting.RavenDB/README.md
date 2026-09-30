# CommunityToolkit.Aspire.Hosting.RavenDB library

Provides extension methods and resource definitions for an Aspire AppHost to configure a RavenDB resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire RavenDB Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package CommunityToolkit.Aspire.Hosting.RavenDB
```

## Usage example

Then, in the _Program.cs_ file of `AppHost`, add a RavenDB resource and consume the connection using the following methods:

```csharp
var db = builder.AddRavenDB("ravendb").AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(db);
```

### Open databases in RavenDB Studio

Every database added with `AddDatabase(...)` exposes a clickable **RavenDB Studio** link in the Aspire dashboard that opens that database's Documents view directly (for example `http://localhost:9534/studio/index.html#databases/documents?&database=mydb`). The link is added automatically — no extra configuration is required. For secured servers it uses the configured public server URL.

## Deployment

`aspire run` starts RavenDB as a local container. `aspire publish` and `aspire deploy` add what a deployed server needs on top of Aspire's generic container mapping.

### Docker Compose

With a Docker Compose environment in the AppHost, the published `docker-compose.yaml` gets:

- an HTTP health check on the RavenDB service (`/build/version`) for unsecured servers;
- `condition: service_healthy` for every resource that waits for the server;
- a one-shot `<name>-bootstrap` service that creates the databases added with `ensureCreated: true`, the same databases the AppHost creates locally. Running it again leaves existing databases untouched.

```csharp
builder.AddDockerComposeEnvironment("compose");

var license = builder.AddParameter("ravendb-license", secret: true);

var db = builder.AddRavenDB("ravendb")
    .WithDataVolume()
    .WithLicense(license)
    .AddDatabase("mydb", ensureCreated: true);
```

### License

`WithLicense(parameter)` keeps the license out of the published files: Docker Compose gets a `${RAVENDB_LICENSE}` placeholder backed by `.env`, Kubernetes a `Secret`. A license passed as a string through `RavenDBServerSettings.WithLicense(...)` is written in plain text, and `aspire publish` warns about it.

### Existing server

`PublishAsExisting(url)` deploys nothing for the server: in the published artifacts its consumers connect to an existing RavenDB server, while `aspire run` keeps the local container. Use it for a server that lives independently of the AppHost, such as a shared cluster or one managed through GitOps.

```csharp
var url = builder.AddParameter("ravendb-url");

var db = builder.AddRavenDB("ravendb")
    .PublishAsExisting(url)
    .AddDatabase("mydb");
```

Databases declared with `ensureCreated` are not created on an existing server.

The server belongs to someone else, so its owner issues the applications' client certificates, each with access to that application's databases only. Give each application its certificate; the RavenDB client integration picks it up through `Aspire__RavenDB__Client__<connection name>__CertificatePath`, with no application code:

```csharp
builder.AddProject<Projects.Api>("api")
    .WithReference(db)
    .WithRavenDBClientCertificateFile(db, "certs/api.pfx"); // Docker Compose: mounted as a secret
```

The file holds the application's private key: keep it out of source control.

In Kubernetes, `WithRavenDBClientCertificateSecret(db, "api-cert", "ravendb-ca")` from [CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes](https://www.nuget.org/packages/CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes) mounts an existing Secret instead.

### Checks at publish time

`aspire publish` stops before writing any file when:

- an unsecured server has an external endpoint (`WithExternalHttpEndpoints()`): an unsecured RavenDB server accepts every request that reaches it;
- the server targets Azure Container Apps or Azure App Service with a data volume. Their only persistent storage is Azure Files over SMB or NFS, which [RavenDB does not support](https://docs.ravendb.net/7.2/start/installation/deployment-considerations). Use RavenDB Cloud for applications hosted there.

For secured servers the server certificate has to be available inside the container, and databases declared with `ensureCreated` are not created in deployed environments yet.

## Additional documentation

<!-- TODO: Update the link once it is created -->
https://learn.microsoft.com/dotnet/aspire/community-toolkit/ravendb

## Feedback & contributing

https://github.com/CommunityToolkit/Aspire
