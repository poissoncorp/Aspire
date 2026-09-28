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

1. looks the product up by name (`<resource>-<environment>` unless `ProductName` is set) and creates it when it does not exist, picking the smallest instance type of the tier, the smallest disk and the default release channel unless they are set;
2. waits until the product is active;
3. creates the databases declared with `ensureCreated: true`;
4. writes the product URL into the generated artifacts and deploys the application.

`aspire publish` never calls the Cloud API. `aspire do ravendb-cloud-provision-<name>` provisions the product without deploying the application.

### An existing product

For a product managed elsewhere, typically production, the deployment only connects to it. It fails when the account has no product with that name and never creates, changes or terminates the product:

```csharp
var db = builder.AddRavenDB("ravendb")
    .PublishAsExistingRavenDBCloud(apiKey, "orders-production")
    .AddDatabase("mydb");
```

### Destroy

`aspire destroy` stops the application first and then terminates the product, but only if this deployment created it and `TerminateOnDestroy` is set. Terminating a product deletes its data.

### Current limitations

- The application reaches the product through its URL. RavenDB Cloud products require a client certificate; delivering one to the application is not implemented yet.
- The product URL is written into the environment files of Docker Compose and passed as a Bicep parameter in Azure Container Apps. Kubernetes is not wired yet.
- The Cloud API allows at most three products created through the API and about one request per second, so it is not meant for an environment per pull request.

## Feedback & contributing

https://github.com/CommunityToolkit/Aspire
