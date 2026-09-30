#pragma warning disable ASPIREPIPELINES001, ASPIREPIPELINES004 // Pipeline APIs are experimental

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CommunityToolkit.Aspire.Hosting.RavenDB;

/// <summary>
/// Publish-mode behaviour of <see cref="RavenDBServerResource"/>: what a deployed server needs but Aspire's generic
/// container mapping does not provide. Nothing here runs in run mode.
/// </summary>
internal static partial class RavenDBPublishing
{
    /// <summary>Answers 200 without authentication as soon as the server serves requests.</summary>
    internal const string ReadinessPath = "/build/version";

    internal const string DataDirectory = "/var/lib/ravendb/data";

    internal const string DeploymentConsiderationsUrl = "https://docs.ravendb.net/7.2/start/installation/deployment-considerations";

    internal const string AzureContainerAppsArticleUrl = "https://github.com/ravendb/docs/pull/2293";

    /// <summary>Compute environments whose only persistent storage is Azure Files (SMB / NFS).</summary>
    private static readonly HashSet<string> s_networkStorageEnvironments = new(StringComparer.Ordinal)
    {
        "AzureContainerAppEnvironmentResource",
        "AzureAppServiceEnvironmentResource",
    };

    internal static void Configure(IResourceBuilder<RavenDBServerResource> builder)
    {
        // Aspire refuses a PublishAs*Service customization for an environment that is not in the model, so the
        // Compose callbacks are registered once the model is known. They are consumed later, when publish-{env}
        // writes the files.
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            var consumers = RavenDBConsumers.Find(@event.Model, builder.Resource);
            EnsureCertificatesAreForReferencedServers(@event.Model, builder.Resource, consumers);

            var composeEnvironments = @event.Model.Resources.OfType<DockerComposeEnvironmentResource>().ToList();

            if (composeEnvironments.Count == 0)
            {
                return Task.CompletedTask;
            }

            if (builder.Resource.ExternalUrl is null)
            {
                ConfigureDockerCompose(builder, @event.Model, composeEnvironments);
            }
            else
            {
                // Published as external (existing server, RavenDB Cloud, operator): there is no service to wait for.
                // WaitFor(...) would otherwise leave a depends_on entry that `docker compose up` rejects.
                RemoveDependenciesOnServer(builder, @event.Model);
            }

            MountBroughtCertificates(builder, consumers, composeEnvironments, @event.Services.GetRequiredService<IPipelineOutputService>());

            return Task.CompletedTask;
        });

        builder.WithPipelineStepFactory(_ => CreateValidationStep(builder.Resource));

        // The environments' own publish steps (publish-compose, publish-k8s, ...) would otherwise run next to the
        // validation and write artifacts for a configuration that is about to be rejected. The deployment targets
        // the validation inspects are attached before the pipeline starts, so it can run first.
        builder.WithPipelineConfiguration(context =>
        {
            var validationStepName = ValidationStepName(builder.Resource);

            // Every RavenDB server brings a validation step of its own, required by publish as well: they must not
            // wait for each other.
            foreach (var step in context.Steps)
            {
                if (!step.Name.StartsWith(ValidationStepPrefix, StringComparison.Ordinal) &&
                    (step.Name.StartsWith("publish-", StringComparison.Ordinal) ||
                     step.RequiredBySteps.Contains(WellKnownPipelineSteps.Publish)))
                {
                    step.DependsOn(validationStepName);
                }
            }
        });
    }

    private const string ValidationStepPrefix = "validate-ravendb-";

    private static string ValidationStepName(RavenDBServerResource server) => ValidationStepPrefix + server.Name;

    private static void ConfigureDockerCompose(
        IResourceBuilder<RavenDBServerResource> builder,
        DistributedApplicationModel model,
        IReadOnlyList<DockerComposeEnvironmentResource> composeEnvironments)
    {
        var server = builder.Resource;
        var targetPort = GetPrimaryTargetPort(server);

        if (!server.IsSecured)
        {
            // The image's own HEALTHCHECK only runs `pgrep Raven.Server`, which passes long before the server
            // answers requests.
            builder.PublishAsDockerComposeService((_, service) =>
            {
                service.Healthcheck = new Healthcheck
                {
                    Test = ["CMD-SHELL", $"curl -sf http://127.0.0.1:{targetPort.ToString(CultureInfo.InvariantCulture)}{ReadinessPath} > /dev/null || exit 1"],
                    Interval = "10s",
                    Timeout = "5s",
                    Retries = 12,
                    StartPeriod = "60s",
                };
            });
        }

        // AddDatabase(ensureCreated: true) is honoured by the AppHost process in run mode only. In a deployed
        // stack the same databases are created by a one-shot service. Secured servers need a client certificate
        // inside that service, which is not supported yet (the validation step warns).
        var createsDatabases = !server.IsSecured && server.DatabasesToCreate.Count > 0;

        // Aspire maps WaitFor(...) to `condition: service_started`. The service has a health check (ours, or the
        // image's for secured servers), so dependents can wait until it is healthy, and until the bootstrap has
        // created the databases, as they do in run mode.
        foreach (var dependent in model.Resources.OfType<IComputeResource>())
        {
            if (ReferenceEquals(dependent, server) || !WaitsFor(dependent, server))
            {
                continue;
            }

            builder.ApplicationBuilder.CreateResourceBuilder(dependent)
                .PublishAsDockerComposeService((_, service) =>
                {
                    foreach (var (name, dependency) in service.DependsOn.ToList())
                    {
                        if (string.Equals(name, server.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            dependency.Condition = "service_healthy";

                            if (createsDatabases)
                            {
                                service.DependsOn[BootstrapServiceName(name)] = new() { Condition = "service_completed_successfully" };
                            }
                        }
                    }
                });
        }

        if (!createsDatabases)
        {
            return;
        }

        foreach (var environment in composeEnvironments)
        {
            builder.ApplicationBuilder.CreateResourceBuilder(environment)
                .ConfigureComposeFile(file => AddBootstrapService(file, server, targetPort));
        }
    }

    private static void RemoveDependenciesOnServer(IResourceBuilder<RavenDBServerResource> builder, DistributedApplicationModel model)
    {
        var server = builder.Resource;

        foreach (var dependent in model.Resources.OfType<IComputeResource>())
        {
            if (ReferenceEquals(dependent, server) || !WaitsFor(dependent, server))
            {
                continue;
            }

            builder.ApplicationBuilder.CreateResourceBuilder(dependent)
                .PublishAsDockerComposeService((_, service) =>
                {
                    foreach (var name in service.DependsOn.Keys.Where(k => string.Equals(k, server.Name, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        service.DependsOn.Remove(name);
                    }
                });
        }
    }

    /// <summary>A certificate for a server the application does not reference would be delivered to no connection.</summary>
    private static void EnsureCertificatesAreForReferencedServers(
        DistributedApplicationModel model,
        RavenDBServerResource server,
        IReadOnlyList<RavenDBConsumer> consumers)
    {
        foreach (var resource in model.Resources)
        {
            if (RavenDBConsumers.OwnCertificatesFor(resource, server).Count > 0 && consumers.All(c => !ReferenceEquals(c.Resource, resource)))
            {
                throw new DistributedApplicationException(
                    $"'{resource.Name}' has a client certificate for RavenDB server '{server.Name}' but does not reference it. " +
                    "Add WithReference(...) with the server or one of its databases.");
            }
        }
    }

    private static void MountBroughtCertificates(
        IResourceBuilder<RavenDBServerResource> builder,
        IReadOnlyList<RavenDBConsumer> consumers,
        IReadOnlyList<DockerComposeEnvironmentResource> composeEnvironments,
        IPipelineOutputService output)
    {
        foreach (var consumer in consumers)
        {
            if (consumer.OwnCertificate(RavenDBClientCertificateSource.File) is not { } certificate)
            {
                continue;
            }

            foreach (var environment in composeEnvironments)
            {
                // Docker Compose resolves the path against the compose file. Relative to it, the published artifacts
                // keep working on another machine with the same layout, such as a CI runner.
                var directory = OutputDirectory(output, environment, composeEnvironments.Count);
                string RelativeToCompose(string file) => Path.GetRelativePath(directory, file).Replace('\\', '/');

                var path = RelativeToCompose(certificate.Location);
                var authority = certificate.CertificateAuthority is { } file ? RelativeToCompose(file) : null;

                builder.ApplicationBuilder.CreateResourceBuilder(environment)
                    .ConfigureComposeFile(compose => RavenDBComposeCertificates.Mount(compose, builder.Resource, consumer, path, authority));
            }
        }
    }

    /// <summary>Where an environment writes its artifacts: its own directory once there are several.</summary>
    internal static string OutputDirectory(IPipelineOutputService output, IResource environment, int environmentCount) =>
        environmentCount > 1 ? output.GetOutputDirectory(environment) : output.GetOutputDirectory();

    private static void AddBootstrapService(ComposeFile file, RavenDBServerResource server, int targetPort)
    {
        // Only the environment that hosts this server gets its bootstrap.
        var (serviceName, ravenService) = file.Services
            .FirstOrDefault(s => string.Equals(s.Key, server.Name, StringComparison.OrdinalIgnoreCase));

        if (ravenService is null)
        {
            return;
        }

        var bootstrapName = BootstrapServiceName(serviceName);

        if (file.Services.ContainsKey(bootstrapName))
        {
            return;
        }

        file.Services[bootstrapName] = new Service
        {
            Name = bootstrapName,
            // Same image as the server: it is already pulled in the target environment and it ships curl.
            Image = ravenService.Image,
            Entrypoint = ["/bin/sh"],
            Command = ["-c", BuildBootstrapScript($"http://{serviceName}:{targetPort.ToString(CultureInfo.InvariantCulture)}", server.DatabasesToCreate)],
            Networks = [.. ravenService.Networks],
            DependsOn = new Dictionary<string, ServiceDependency>
            {
                [serviceName] = new() { Condition = "service_healthy" },
            },
        };
    }

    private static string BootstrapServiceName(string serverServiceName) => $"{serverServiceName}-bootstrap";

    /// <summary>
    /// A POSIX shell script that creates the given databases if they do not exist.
    /// </summary>
    /// <remarks>
    /// The script contains no shell variables, because Docker Compose interpolates <c>$VAR</c> in the file.
    /// <c>GET /databases?name=x</c> answers 404 for a missing database and 200 for an existing one, and
    /// <c>PUT /admin/databases</c> answers 409 for an existing one, so running it again changes nothing. A database
    /// that could not be created fails the service, and with it the applications that wait for it.
    /// </remarks>
    internal static string BuildBootstrapScript(string serverUrl, IEnumerable<string> databases)
    {
        var script = new StringBuilder()
            .Append("set -e; ")
            .Append(CultureInfo.InvariantCulture, $"echo 'ravendb-bootstrap: waiting for {serverUrl}'; ")
            .Append(CultureInfo.InvariantCulture, $"until curl -sf {serverUrl}{ReadinessPath} >/dev/null 2>&1; do sleep 2; done; ");

        foreach (var database in databases.Distinct(StringComparer.Ordinal))
        {
            EnsureValidDatabaseName(database);

            script
                .Append(CultureInfo.InvariantCulture, $"if curl -sf '{serverUrl}/databases?name={database}' >/dev/null 2>&1; ")
                .Append(CultureInfo.InvariantCulture, $"then echo 'ravendb-bootstrap: database {database} already exists'; ")
                .Append(CultureInfo.InvariantCulture, $"elif curl -sf -X PUT '{serverUrl}/admin/databases?name={database}&replicationFactor=1' ")
                .Append("-H 'Content-Type: application/json' ")
                .Append(CultureInfo.InvariantCulture, $"-d '{{\"DatabaseName\":\"{database}\"}}' >/dev/null; ")
                .Append(CultureInfo.InvariantCulture, $"then echo 'ravendb-bootstrap: created database {database}'; ")
                // Created meanwhile by someone else: fine. Otherwise the database is missing.
                .Append(CultureInfo.InvariantCulture, $"elif curl -sf '{serverUrl}/databases?name={database}' >/dev/null 2>&1; ")
                .Append(CultureInfo.InvariantCulture, $"then echo 'ravendb-bootstrap: database {database} already exists'; ")
                .Append(CultureInfo.InvariantCulture, $"else echo 'ravendb-bootstrap: creating database {database} failed' >&2; exit 1; fi; ");
        }

        return script.Append("echo 'ravendb-bootstrap: done'").ToString();
    }

    /// <summary>
    /// RavenDB database names are letters, digits, '_', '-' and '.'. Anything else would also break the script's
    /// quoting, so it is rejected at publish time rather than at deploy time.
    /// </summary>
    internal static void EnsureValidDatabaseName(string database)
    {
        if (!DatabaseNamePattern().IsMatch(database))
        {
            throw new DistributedApplicationException(
                $"'{database}' is not a valid RavenDB database name. Use letters, digits, '_', '-' or '.'.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex DatabaseNamePattern();

    private static PipelineStep CreateValidationStep(RavenDBServerResource server)
    {
        var step = new PipelineStep
        {
            Name = ValidationStepName(server),
            Description = $"Validates the deployment configuration of RavenDB server '{server.Name}'",
            Resource = server,
            Action = context =>
            {
                Validate(server, context.Logger);
                WarnAboutMissingCertificateFiles(context.Model, server, context.Logger);
                return Task.CompletedTask;
            },
        };

        step.RequiredBy(WellKnownPipelineSteps.Publish);

        return step;
    }

    internal static void Validate(RavenDBServerResource server, ILogger logger)
    {
        if (server.ExternalUrl is not null)
        {
            ValidateExternal(server, logger);
            return;
        }

        var endpoints = server.Annotations.OfType<EndpointAnnotation>().ToList();

        if (!server.IsSecured && endpoints.Any(e => e.IsExternal))
        {
            throw new DistributedApplicationException(
                $"RavenDB server '{server.Name}' is unsecured but published with an external endpoint. An unsecured " +
                "RavenDB server accepts every request that reaches it. Use RavenDBServerSettings.Secured(...) or " +
                "SecuredWithLetsEncrypt(...), or remove WithExternalHttpEndpoints().");
        }

        foreach (var target in server.Annotations.OfType<DeploymentTargetAnnotation>())
        {
            var environmentType = target.ComputeEnvironment?.GetType().Name;

            if (environmentType is null || !s_networkStorageEnvironments.Contains(environmentType))
            {
                continue;
            }

            var message =
                $"RavenDB server '{server.Name}' targets '{target.ComputeEnvironment!.Name}', whose only persistent " +
                "storage is Azure Files over SMB or NFS. RavenDB does not support network file systems " +
                $"({DeploymentConsiderationsUrl}); data gets corrupted or lost ({AzureContainerAppsArticleUrl}). " +
                "Use RavenDB Cloud for applications hosted there.";

            if (HasPersistentData(server))
            {
                throw new DistributedApplicationException(message);
            }

            logger.LogWarning("{Message} Without a data volume the server is ephemeral: its data is lost on every restart.", message);
        }

        if (server.HasLiteralLicense)
        {
            logger.LogWarning(
                "The license of RavenDB server '{Server}' is a literal string, so it is written into the published " +
                "artifacts in plain text. Pass it with WithLicense(builder.AddParameter(\"...\", secret: true)) instead.",
                server.Name);
        }

        if (server.IsSecured)
        {
            logger.LogWarning(
                "RavenDB server '{Server}' is secured: its server certificate must be available inside the container " +
                "at the configured path (for example through WithBindMount), and databases declared with " +
                "ensureCreated are not created in deployed environments yet.",
                server.Name);
        }
    }

    /// <summary>The artifacts can be published before the owner's certificate files are in place, but not deployed.</summary>
    private static void WarnAboutMissingCertificateFiles(DistributedApplicationModel model, RavenDBServerResource server, ILogger logger)
    {
        foreach (var consumer in RavenDBConsumers.Find(model, server))
        {
            if (consumer.OwnCertificate(RavenDBClientCertificateSource.File) is not { } certificate)
            {
                continue;
            }

            foreach (var file in new[] { certificate.Location, certificate.CertificateAuthority })
            {
                if (file is not null && !File.Exists(file))
                {
                    logger.LogWarning(
                        "A certificate file of '{Resource}' for RavenDB server '{Server}' does not exist yet: {File}. Docker " +
                        "Compose cannot start the application until it does.",
                        consumer.Resource.Name,
                        server.Name,
                        file);
                }
            }
        }
    }

    private static void ValidateExternal(RavenDBServerResource server, ILogger logger)
    {
        if (server.DatabasesToCreate.Count > 0 && !server.CreatesDatabasesWhenExternal)
        {
            logger.LogWarning(
                "RavenDB server '{Server}' is published as an existing server, so the databases declared with " +
                "ensureCreated ({Databases}) are not created by the deployment. Create them on that server beforehand.",
                server.Name,
                string.Join(", ", server.DatabasesToCreate));
        }
    }

    private static bool HasPersistentData(RavenDBServerResource server) =>
        server.Annotations.OfType<ContainerMountAnnotation>()
            .Any(m => string.Equals(m.Target, DataDirectory, StringComparison.Ordinal));

    private static bool WaitsFor(IResource dependent, RavenDBServerResource server) =>
        dependent.TryGetAnnotationsOfType<WaitAnnotation>(out var waits) &&
        waits.Any(w => ReferenceEquals(w.Resource, server) ||
                       (w.Resource is RavenDBDatabaseResource database && ReferenceEquals(database.Parent, server)));

    private static int GetPrimaryTargetPort(RavenDBServerResource server) =>
        server.Annotations.OfType<EndpointAnnotation>()
            .FirstOrDefault(e => string.Equals(e.Name, server.PrimaryEndpointName, StringComparison.Ordinal))
            ?.TargetPort ?? (server.IsSecured ? 443 : 8080);
}
