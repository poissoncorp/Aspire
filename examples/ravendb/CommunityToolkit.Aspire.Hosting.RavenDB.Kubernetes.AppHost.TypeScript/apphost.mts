import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

await builder.addKubernetesEnvironment("k8s");

// aspire run starts a local RavenDB container; aspire publish and aspire deploy hand the cluster to the RavenDB operator.
const ravendb = await builder.addRavenDB("ravendb")
    .publishAsRavenDBCluster(async cluster => {
        await cluster.domain.set("ravendb.example.com");
        await cluster.nodes.set(3);
        await cluster.image.set("ravendb/ravendb:7.2.6-ubuntu.24.04-x64");
        await cluster.licenseSecretName.set("ravendb-license");
        await cluster.withCertificates("ravendb-server", "ravendb-admin", { certificateAuthoritySecret: "ravendb-ca" });
    });

await ravendb.addDatabase("orders", { ensureCreated: true });

await builder.build().run();
