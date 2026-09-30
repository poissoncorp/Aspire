using CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

var apiKey = builder.AddParameter("ravendb-cloud-api-key", secret: true);

// aspire run starts a local RavenDB container; aspire deploy finds or creates the RavenDB Cloud product.
var ravendb = builder.AddRavenDB("ravendb")
    .PublishAsRavenDBCloud(apiKey, cloud =>
    {
        cloud.Provider = RavenDBCloudProvider.Azure;
        cloud.Region = "westeurope";
        cloud.Tier = RavenDBCloudTier.Development;
        cloud.WithAllowedIps("203.0.113.0/24");
    });

ravendb.AddDatabase("orders", ensureCreated: true);

builder.Build().Run();
