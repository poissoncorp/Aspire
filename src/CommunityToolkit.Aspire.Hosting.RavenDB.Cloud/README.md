# RavenDB Cloud hosting integration

Use this integration to model, configure, and orchestrate the deployment of an Aspire AppHost's RavenDB server to [RavenDB Cloud](https://cloud.ravendb.net). `aspire run` keeps starting the local RavenDB container; `aspire deploy` creates or finds a RavenDB Cloud product and hands every application that references the server the product URL and a client certificate of its own.

> [!IMPORTANT]
> This package is a preview. It builds on Aspire's deployment pipeline APIs, which are still experimental, and supports applications deployed with Docker Compose only (see [Known limitations](#known-limitations)). Follow the RavenDB Aspire announcements in [GitHub Discussions of ravendb/ravendb](https://github.com/ravendb/ravendb/discussions) to hear about changes before they land.

## Getting started

Install the package in your AppHost project:

```shell
aspire add CommunityToolkit.Aspire.Hosting.RavenDB.Cloud
```

### Prerequisites

- **A RavenDB Cloud API key.** Create it in the RavenDB Cloud portal (see [Cloud API](https://docs.ravendb.net/cloud/cloud-api)). The key acts with the account owner's authority on every product of the account, so keep it in a secret parameter, never in source control. Keys expire after at most 12 months and RavenDB Cloud sends no reminder: note the date. `aspire deploy` asks for the key when the parameter has no value; in CI, pass it as the `Parameters__ravendb-cloud-api-key` environment variable. Aspire saves parameter values, the key included, in plain text in its deployment state on the machine that deploys (`~/.aspire/deployments`), and a saved value wins over a new one from the environment: after replacing the key, deploy once with `aspire deploy --clear-cache`.
- **Docker Compose.** The AppHost needs a Docker Compose environment for the applications, and `aspire deploy` must run where Docker can start them.
- **Allowed IPs.** RavenDB Cloud accepts connections only from a product's allowed IP ranges. For a product the deployment creates, list the addresses in `WithAllowedIps(...)`. For an existing product, the machine that runs `aspire deploy` (or the CI runner) and the applications' host must already be in its allowed IPs, set in the RavenDB Cloud portal. See [Network access](#network-access).

## Usage example

```csharp
using CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

var apiKey = builder.AddParameter("ravendb-cloud-api-key", secret: true);

var ravendb = builder.AddRavenDB("ravendb")
    .PublishAsRavenDBCloud(
        apiKey,
        productName: "shop-production",
        RavenDBCloudSpec.Production(
            RavenDBCloudProvider.Azure,
            region: "westeurope",
            instanceType: "P20",
            diskSizeInGb: 128,
            RavenDBCloudStorageType.SsdStandard,
            releaseChannel: "Stable72"),
        cloud => cloud.WithAllowedIps("203.0.113.0/24"));

var orders = ravendb.AddDatabase("orders", ensureCreated: true);

builder.AddProject<Projects.Api>("api")
    .WithReference(orders)
    .WaitFor(orders);

builder.Build().Run();
```

In a TypeScript AppHost the spec is chosen in a callback:

```typescript
const apiKey = await builder.addParameter("ravendb-cloud-api-key", { secret: true });

const ravendb = await builder.addRavenDB("ravendb")
    .publishAsRavenDBCloud(
        apiKey,
        "shop-production",
        async spec => {
            await spec.production(
                RavenDBCloudProvider.Azure,
                "westeurope", // region
                "P20", // instance type
                128, // disk size in GB
                RavenDBCloudStorageType.SsdStandard,
                "Stable72"); // release channel
        },
        { configure: async cloud => { await cloud.withAllowedIps(["203.0.113.0/24"]); } });
```

The application uses the RavenDB client integration as it does locally (`builder.AddRavenDBClient("orders")`); nothing changes in its code.

To use a product that already exists, possibly shared with other AppHosts, attach to it instead. The deployment never creates, changes or terminates it:

```csharp
builder.AddRavenDB("ravendb")
    .PublishAsExistingRavenDBCloud(apiKey, productName: "shared-production", clientName: "shop");
```

## Choosing a spec

Everything that decides the cost of the product and where its data lives is explicit:

- `RavenDBCloudSpec.Development(provider, region, instanceType, diskSizeInGb, storageType, releaseChannel, iops?, throughput?)`: one node, no automatic backups.
- `RavenDBCloudSpec.Production(...)`, with the same parameters: three nodes and automatic backups.

The region is the provider's own code, such as `eu-central-1` on AWS, `westeurope` on Azure or `europe-west3` on Google Cloud. The instance types (such as `Dev10` or `P20`) and the release channels (such as `Stable72`, which follows RavenDB 7.2) are RavenDB Cloud's; the same instance type is a different machine on each provider.

To see what you can choose, run the plan. It checks the spec against what RavenDB Cloud offers your account in that region, and for every value that is not offered it lists the valid ones: the regions, the instance types with their vCPUs, RAM, nodes and disk sizes, and the release channels with their RavenDB versions. The list comes from RavenDB Cloud itself, so it is never out of date:

```bash
aspire do ravendb-cloud-plan-ravendb
```

```text
release channel 'Stable99' does not exist. Available: Stable62 (RavenDB 6.2...), Stable72 (RavenDB 7.2...), ...
instance type 'Dev99' is not offered for the Development tier in Aws eu-central-1. Offered: Dev10 (2 vCPU, ...), Dev20 (...), ...
```

## Publish, deploy and destroy

| Command | What happens |
|---|---|
| `aspire run` | The local RavenDB container starts, as without this package. RavenDB Cloud is not contacted. |
| `aspire do ravendb-cloud-plan-<name>` | Shows the [plan](#the-plan-and-approving-changes) and changes nothing. |
| `aspire publish` | Writes `docker-compose.yaml` and `.env` to `aspire-output`. They reference the product URL and the certificates through placeholders (`${RAVENDB_URL}` and one variable per application). The Cloud API is never called. |
| `aspire deploy` | The plan runs first. Then the product is created or changed as approved, the databases declared with `ensureCreated` are created, every application gets a new certificate, the values are written to `aspire-output/.env.<environment>` (`.env.Production` by default), Docker Compose starts the applications, and the previous certificates are revoked. |
| `aspire destroy` | Revokes this AppHost's certificates. Terminates the product only if the AppHost manages it and `TerminateOnDestroy` is set. |

Locally and deployed, the same AppHost gives:

| | `aspire run` | `aspire deploy` |
|---|---|---|
| Server | local container, unsecured | RavenDB Cloud product, TLS |
| Application's access | everything | admin on its referenced databases only |
| Databases with `ensureCreated` | created by the AppHost | created by the deployment |

The databases declared with `ensureCreated: true` are created with as many replicas as the product has nodes. The deployment never deletes a database, also not one removed from the AppHost.

### Destroy

Terminating a product deletes its data, so `aspire destroy` terminates it only when you ask for it with `TerminateOnDestroy` in the options of `PublishAsRavenDBCloud`; it is off by default. The product is found by its name, like on every deployment, so this also applies to a product made in the portal that the AppHost took over. The terminated product is also hidden in the portal, which frees its name for a later deployment once RavenDB Cloud has finished terminating it. A product the AppHost attaches to with `PublishAsExistingRavenDBCloud` is never terminated. In CI, `aspire destroy` needs `--yes`.

## The product

### The name is the identity

The product is found by its name on every deployment, from any machine, CI included: RavenDB Cloud keeps no metadata of ours, so the name is all a deployment has. The name is used exactly as given and must be unique in the account (two live products with the same name stop the deployment). RavenDB Cloud keeps the name of a terminated product taken until the product is hidden in the portal; the plan stops with `name` when that blocks creating the product.

Renaming the product in the AppHost creates a new product; the old one keeps running and keeps costing until you terminate it in the portal. To move to another tier, provider or region, which RavenDB Cloud cannot change, create a product under a new name and move the data (see [Cloud: Migration](https://docs.ravendb.net/cloud/cloud-migration)).

A new product's URL is `https://a.<subdomain>.<account domain>`. The subdomain comes from the product name (up to 13 letters, digits and dashes; a longer name is shortened and given a short hash) unless you choose it with `Subdomain` in the options.

### Managed or attached

- `PublishAsRavenDBCloud(apiKey, productName, spec)`: the AppHost owns the product. A deployment creates it from the spec when there is none, and keeps it to the spec.
- `PublishAsExistingRavenDBCloud(apiKey, productName, clientName)`: the product exists and other AppHosts may use it too. A missing product stops the deployment. `clientName` keeps this AppHost's certificates apart from the other clients' ones; two environments of one AppHost on one product need different client names.

## The plan and approving changes

Every deployment starts with a plan, before anything is built or changed. The plan shows one line per decision:

- `create`: there is no product with the name; it is created from the spec.
- `name`: a terminated product, or one still being terminated, holds the name.
- `no changes`: the product matches the spec.
- `change`: the product differs in something the Cloud API can change (instance type, disk size, storage type, IOPS, throughput). The deployment stops unless the change is approved.
- `impossible`: the tier, the provider, the region or the release channel differ, or the product's disk is larger than the AppHost's, which happens when RavenDB Cloud grows a disk that fills up. RavenDB Cloud cannot change these, so the deployment always stops.
- `other clients`: certificates of other AppHosts on the product, which the deployment leaves alone.
- `allowed IPs`, `unreachable`: see [Network access](#network-access).
- `missing`: the attached product does not exist.
- `no target`: an application is not deployed with Docker Compose (see [Known limitations](#known-limitations)).

Approve changes with `RavenDB:Cloud:ApproveChanges=true`, for example through an environment variable:

```bash
RavenDB__Cloud__ApproveChanges=true aspire deploy
```

In CI, set it only in a manually approved job, so that a change of the AppHost never resizes a paid product without a person looking at the plan.

## Network access

RavenDB Cloud accepts connections only from the product's allowed IP ranges, and the Cloud API cannot change them once the product exists. Declare them with `WithAllowedIps(...)`:

- the machine that runs `aspire deploy`, or your CI runner's outbound addresses: the deployment creates databases and certificates on the product;
- the outbound addresses of the host that runs the applications.

A product the deployment creates gets these ranges. On every deployment the plan checks that each declared range is allowed on the product and stops with `allowed IPs: ... not allowed`, naming the missing ones, which you then add in the RavenDB Cloud portal. For an attached product, the product's owner allows them. When the machine that deploys cannot connect to an existing product, the plan stops with `unreachable`.

Right after the product is created or changed, RavenDB Cloud reports it active a little before it accepts connections: the deployment waits for it, five minutes at most. RavenDB Cloud drops traffic from addresses it does not allow instead of refusing it, so a missing address looks the same as a node that is still starting: check the list before the first deployment, because a new product is created before anything can connect to it.

## What the applications get

Every resource that references the server or one of its databases gets:

- `ConnectionStrings__<name>`: the URLs of the product's nodes, separated by commas, so that the application reaches the cluster while one node is down (and the database);
- `Aspire__RavenDB__Client__<name>__CertificateBase64`: its own client certificate, as base64-encoded .pfx content, which the RavenDB client integration reads. When the application uses no other RavenDB server, the certificate also goes to `Aspire__RavenDB__Client__CertificateBase64`, so a connection renamed with `WithReference(db, connectionName: ...)` still finds it. Applications in other languages can read the same variables.

The certificate grants admin access to the databases the application references, and nothing else. The databases themselves are created by the deployment, from `AddDatabase(..., ensureCreated: true)` in the AppHost; an application that sets `CreateDatabase` in the client settings still starts, since its database already exists. An application that creates other databases while it runs needs a certificate with wider access: create one in the Studio, or download the product's certificate from the RavenDB Cloud portal, and set it in the application with `AddRavenDBClient(..., settings => settings.Certificate = ...)`, which takes precedence over the certificate the deployment delivers.

Every deployment issues new certificates; the previous ones are revoked once the applications run with the new ones. RavenDB issues them valid for five years, but a certificate lives only until the next deployment, and nothing on disk has to be kept between deployments. The certificates are named `aspire.<application>` for a managed product and `aspire.<client name>.<application>` for an attached one.

With Docker Compose the values are written to `aspire-output/.env.<environment>` on the machine that runs `aspire deploy`, in plain text like any other parameter: protect that machine.

## Continuous deployment

```yaml
jobs:
  deploy:
    runs-on: ubuntu-latest
    concurrency: deploy-production
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
      - run: dotnet tool install -g Aspire.Cli
      - run: aspire deploy --non-interactive
        env:
          Parameters__ravendb-cloud-api-key: ${{ secrets.RAVENDB_CLOUD_API_KEY }}
```

`aspire deploy` starts the applications with `docker compose up` on the Docker the runner talks to, so the runner needs Docker, and its outbound addresses must be in the product's allowed IP ranges. GitHub-hosted runners have no fixed addresses: use a runner with a static outbound address (self-hosted, or a larger GitHub runner with a static IP), or allow a wider range on the product and rely on the client certificates. `concurrency` keeps two runs from deploying at once, so one does not revoke the certificates the other just issued. Run a deployment that approves changes in a separate job with a manual approval.

## Known limitations

- The applications that use the server are deployed with Docker Compose for now. Elsewhere the plan stops with `no target`, because the certificate would not travel as a secret: Azure Container Apps and App Service would carry it as a plain template parameter, visible in the deployment history. Support for Azure Container Apps through Azure Key Vault is planned.
- RavenDB Cloud creates free products only in the portal. To use one, attach to it with `PublishAsExistingRavenDBCloud`.

## Troubleshooting

| What you see | Why | What to do |
|---|---|---|
| `allowed IPs: ... not allowed` | A declared range is not allowed on the product | Add it in the RavenDB Cloud portal and deploy again |
| `unreachable`, or "did not answer for 00:05:00" | This machine's address is not allowed on the product | Add the machine's or CI runner's outbound address in the portal |
| `change ...` stops the deployment | The AppHost's spec differs from the product | Review the plan, then deploy with `RavenDB__Cloud__ApproveChanges=true` |
| `impossible ...` | Tier, provider, region or release channel differ | Change the AppHost back, or create a product under a new name and migrate |
| `impossible: disk ...` | RavenDB Cloud grew the disk when it filled up, and a disk cannot shrink | Set the disk size in the AppHost to the size shown |
| `name ...` | A terminated product still holds the name | Wait until it is terminated, or hide it in the portal |
| The Cloud API answers 401 | The API key expired (keys last at most 12 months) or was revoked, or Aspire still uses an old key from its deployment state | Create a new key, update the secret, and deploy once with `aspire deploy --clear-cache` |
| "The SSL connection could not be established" | Something between this machine and RavenDB Cloud replaces its certificate, such as a TLS-inspecting proxy | Let the machine reach RavenDB Cloud directly, or trust that proxy's CA |
| The application gets 403 from RavenDB | The operation needs more than admin access to its own databases | Give the application a certificate of its own for that (see [What the applications get](#what-the-applications-get)) |

## Additional documentation

- https://docs.ravendb.net/cloud/cloud-overview
- https://learn.microsoft.com/dotnet/aspire/community-toolkit/ravendb

## Feedback & contributing

https://github.com/CommunityToolkit/Aspire
