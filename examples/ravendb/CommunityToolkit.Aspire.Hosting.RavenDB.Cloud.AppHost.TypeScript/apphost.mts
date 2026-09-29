import { createBuilder, RavenDBCloudProvider, RavenDBCloudTier } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

await builder.addDockerComposeEnvironment("compose");

const apiKey = await builder.addParameter("ravendb-cloud-api-key", { secret: true });

// aspire run starts a local RavenDB container; aspire deploy finds or creates the RavenDB Cloud product.
const ravendb = await builder.addRavenDB("ravendb")
    .publishAsRavenDBCloud(apiKey, {
        configure: async cloud => {
            await cloud.provider.set(RavenDBCloudProvider.Azure);
            await cloud.region.set("westeurope");
            await cloud.tier.set(RavenDBCloudTier.Development);
            await cloud.withAllowedIps(["203.0.113.0/24"]);
        },
    });

await ravendb.addDatabase("orders", { ensureCreated: true });

await builder.build().run();
