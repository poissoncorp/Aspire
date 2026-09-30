var builder = DistributedApplication.CreateBuilder(args);

builder.AddKubernetesEnvironment("k8s");

// aspire run starts a local RavenDB container; aspire publish and aspire deploy hand the cluster to the RavenDB
// operator. The operator and the Secrets are prerequisites: see the package README.
var ravendb = builder.AddRavenDB("ravendb")
    .PublishAsRavenDBCluster(cluster =>
    {
        cluster.Domain = "ravendb.example.com";
        cluster.Nodes = 3;
        cluster.Image = "ravendb/ravendb:7.2.6-ubuntu.24.04-x64";
        cluster.LicenseSecretName = "ravendb-license";
        cluster.WithCertificates("ravendb-server", "ravendb-admin", "ravendb-ca");
    });

ravendb.AddDatabase("orders", ensureCreated: true);

builder.Build().Run();
