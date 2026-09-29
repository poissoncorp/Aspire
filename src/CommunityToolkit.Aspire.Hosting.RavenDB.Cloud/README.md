# CommunityToolkit.Aspire.Hosting.RavenDB.Cloud library

Deploys the [RavenDB hosting integration](https://www.nuget.org/packages/CommunityToolkit.Aspire.Hosting.RavenDB) to [RavenDB Cloud](https://cloud.ravendb.net). `aspire run` keeps the local RavenDB container; `aspire deploy` finds or creates a RavenDB Cloud product and connects the application to it.

## Getting started

### Install the package

In your AppHost project, install the library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package CommunityToolkit.Aspire.Hosting.RavenDB.Cloud
```

### Create an API key

Create an API key in the RavenDB Cloud portal and pass it as a secret parameter, for example through the `Parameters__ravendb-cloud-api-key` environment variable in CI. The key has account-owner rights.

## Usage example

```csharp
var apiKey = builder.AddParameter("ravendb-cloud-api-key", secret: true);

var db = builder.AddRavenDB("ravendb")
    .PublishAsRavenDBCloud(apiKey, cloud =>
    {
        cloud.Provider = RavenDBCloudProvider.Azure;
        cloud.Region = "westeurope";
        cloud.Tier = RavenDBCloudTier.Development;
        cloud.WithAllowedIps("203.0.113.0/24");
    })
    .AddDatabase("mydb", ensureCreated: true);

builder.AddProject<Projects.MyService>("api")
    .WithReference(db);
```

`aspire deploy` then:

1. looks the product up by name (`<resource>-<environment>` unless `ProductName` is set) and creates it when it does not exist, picking the smallest instance type of the tier, the smallest disk and the default release channel unless they are set. The node URLs get the subdomain `Subdomain`, derived from the name when unset (up to 13 letters, digits and dashes, unique in the account);
2. waits until the product is active;
3. creates the databases declared with `ensureCreated: true`;
4. issues a client certificate to each application that references the server or one of its databases (see below);
5. writes the product URL into the generated artifacts and deploys the application.

`aspire publish` never calls the Cloud API. `aspire do ravendb-cloud-provision-<name>` provisions the product without deploying the application.

### Client certificates

RavenDB Cloud products only accept clients that present a certificate. Each application gets a certificate of its own, with `ValidUser` clearance and read/write access to the databases it references and to nothing else: `WithReference(db)` grants that database, `WithReference(server)` grants every database declared on the server with `AddDatabase(...)`.

In Docker Compose the certificate is written to `ravendb-certs/` next to `docker-compose.yaml` (the directory gets a `.gitignore`), mounted into the application's container as a secret, and handed to the [RavenDB client integration](https://www.nuget.org/packages/CommunityToolkit.Aspire.RavenDB.Client) through `Aspire__RavenDB__Client__<connection name>__CertificatePath`. No application code is needed:

```csharp
builder.AddRavenDBClient("mydb");
```

The certificates are named `aspire.<AppHost>.<environment>.<application>`, and every deployment leaves each application with exactly one. It keeps the certificate in the application's file while the product still knows it, and updates its access when the references change. It revokes every other certificate with the application's name, so a deployment from a machine without that file, such as a CI runner, replaces the certificate instead of adding one. It also revokes the certificates of applications that are no longer deployed. Two AppHosts with the same name and environment on one product would revoke each other's certificates.

An application that brings a certificate of its own (`WithRavenDBClientCertificateFile`) gets none issued.

### A product someone else owns

A product in your account with the configured name is used as it is, and `aspire destroy` never terminates it. For a product owned by someone else, do not share the account's API key, which has owner rights: publish the server with `PublishAsExisting(url)` from the [RavenDB hosting integration](https://www.nuget.org/packages/CommunityToolkit.Aspire.Hosting.RavenDB), and give each application the certificate the owner issued for it with `WithRavenDBClientCertificateFile`.

### Destroy

`aspire destroy` stops the application first, revokes the client certificates and then terminates the product, but only if this deployment created it and `TerminateOnDestroy` is set. Terminating a product deletes its data. Without deployment state it still finds the product by name and revokes the certificates, but leaves the product running.

`aspire destroy` also clears the deployment state. A product it leaves running is therefore found by name on the next deployment and treated as one this deployment did not create: terminate it in the portal once it is no longer needed.

### Current limitations

- Client certificates are delivered to applications deployed with Docker Compose. Elsewhere the deployment warns, and the application needs `Aspire:RavenDB:Client:<connection name>:CertificatePath` from another source.
- The product URL is written into the environment files of Docker Compose and passed as a Bicep parameter in Azure Container Apps. Kubernetes is not wired yet.
- The Cloud API allows at most three products created through the API and about one request per second, so it is not meant for an environment per pull request.

## Feedback & contributing

https://github.com/CommunityToolkit/Aspire
