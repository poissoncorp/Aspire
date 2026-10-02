using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Resources;
using Aspire.Hosting.Pipelines;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;

/// <summary>
/// Publish-mode wiring of a server published to Kubernetes. The server itself leaves the chart; a bootstrap
/// resource carries everything RavenDB needs there: the operator's <c>RavenDBCluster</c> and license Secret, and a
/// Job (with its script, ServiceAccount and Role) that creates the databases and the applications' certificates.
/// </summary>
internal static partial class RavenDBClusterPublishing
{
    private const string ScriptPath = "/ravendb/bootstrap/bootstrap.sh";
    private const string LicenseVariable = "RAVENDB_LICENSE";

    // Ports of the Service the operator creates for each node.
    private const int NodeHttpsPort = 443;
    private const int NodeTcpPort = 38888;

    private static readonly Lazy<string> s_script = new(() =>
    {
        using var stream = typeof(RavenDBClusterPublishing).Assembly
            .GetManifestResourceStream("CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes.bootstrap.sh")!;
        using var reader = new StreamReader(stream);

        // The script runs in a Linux container; a checkout with CRLF line endings must not break it.
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    });

    internal static string Script => s_script.Value;

    public static void Configure(IResourceBuilder<RavenDBServerResource> builder, RavenDBClusterDeployment deployment)
    {
        var server = builder.Resource;
        var applicationBuilder = builder.ApplicationBuilder;

        server.PublishAsExternal(deployment.Url, createsDatabases: true);
        builder
            .WithAnnotation(deployment)
            .WithPipelineStepFactory(_ => RavenDBClusterPipelineSteps.Create(deployment))
            .WithPipelineConfiguration(context => RavenDBClusterPipelineSteps.Configure(deployment, context))
            .ExcludeFromManifest();

        var (image, tag) = ResolveImage(deployment);

        var bootstrap = applicationBuilder.AddContainer(deployment.BootstrapName, image, tag)
            .WithEntrypoint("/bin/bash")
            .WithArgs(ScriptPath)
            .WithEnvironment("RAVENDB_URLS", string.Join(' ', deployment.NodeUrls))
            .WithEnvironment(context =>
            {
                // Evaluated when the chart is written: databases and references declared after this call count.
                foreach (var (name, value) in Settings(deployment))
                {
                    context.EnvironmentVariables[name] = value;
                }

                // Carried to the operator's license Secret, see ApplyToBootstrap.
                if (deployment.Options.LicenseSecretName is null && server.LicenseParameter is { } license)
                {
                    context.EnvironmentVariables[LicenseVariable] = license;
                }
            })
            .PublishAsKubernetesService(k8s => ApplyToBootstrap(k8s, deployment));

        applicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            Validate(@event.Model, deployment);

            // Next to other compute environments, Aspire needs to know where the server and its bootstrap go. The
            // operator runs the server: the Kubernetes environment deploys nothing for it.
            var environment = applicationBuilder.CreateResourceBuilder<IComputeEnvironmentResource>(TargetEnvironment(@event.Model, server));
            applicationBuilder.CreateResourceBuilder(server).WithComputeEnvironment(environment);
            bootstrap.WithComputeEnvironment(environment);

            // An application that brings its own certificate gets it from WithRavenDBClientCertificateSecret.
            deployment.Consumers = RavenDBConsumers.Find(@event.Model, server)
                .Where(c => !c.BringsOwnCertificate && !ReferenceEquals(c.Resource, bootstrap.Resource))
                .ToList();

            foreach (var consumer in deployment.Consumers)
            {
                if (consumer.Resource is IComputeResource compute)
                {
                    applicationBuilder.CreateResourceBuilder(compute).PublishAsKubernetesService(k8s => RavenDBKubernetesCertificates.Mount(
                        k8s,
                        server,
                        consumer,
                        deployment.ApplicationSecretName(consumer.Resource),
                        deployment.CertificateAuthoritySecret));
                }
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>The bootstrap's settings, apart from the URLs and the license.</summary>
    private static SortedDictionary<string, string> Settings(RavenDBClusterDeployment deployment) => new(StringComparer.Ordinal)
    {
        ["RAVENDB_DATABASES"] = string.Join(' ', deployment.Server.DatabasesToCreate),
        ["RAVENDB_REPLICATION_FACTOR"] = deployment.Options.Nodes.ToString(CultureInfo.InvariantCulture),
        ["RAVENDB_APPLICATIONS"] = string.Join(' ', deployment.Consumers.Select(c =>
            $"{deployment.ApplicationSecretName(c.Resource)}={string.Join(',', c.Databases)}")),
    };

    private static void Validate(DistributedApplicationModel model, RavenDBClusterDeployment deployment)
    {
        var server = deployment.Server;

        if (!model.Resources.OfType<KubernetesEnvironmentResource>().Any())
        {
            throw new DistributedApplicationException(
                $"RavenDB server '{server.Name}' is published to Kubernetes, but the application has no Kubernetes " +
                "environment. Add one with builder.AddKubernetesEnvironment(...).");
        }

        var clusters = model.Resources
            .OfType<RavenDBServerResource>()
            .Where(r => r.Annotations.OfType<RavenDBClusterDeployment>().Any())
            .Select(r => r.Name)
            .ToList();

        if (clusters.Count > 1)
        {
            throw new DistributedApplicationException(
                $"RavenDB servers {string.Join(", ", clusters.Select(c => $"'{c}'"))} are all published as RavenDB " +
                "clusters. The RavenDB operator runs one cluster per namespace; publish the others as existing servers.");
        }

        var (image, tag) = ResolveImage(deployment);

        if (tag is "latest" || tag.EndsWith("-latest", StringComparison.OrdinalIgnoreCase))
        {
            throw new DistributedApplicationException(
                $"The RavenDB operator only runs pinned images, and '{image}:{tag}' is a floating tag. Set a concrete " +
                "tag with Image (for example ravendb/ravendb:7.2.6-ubuntu.24.04-x64) or WithImageTag(...).");
        }

        if (deployment.Options.LicenseSecretName is null && server.LicenseParameter is null)
        {
            throw new DistributedApplicationException(
                $"The RavenDB operator reads the license of '{server.Name}' from a Secret. Pass the license as a " +
                "parameter with WithLicense(...), or name an existing Secret with LicenseSecretName.");
        }

        // The bootstrap script and its settings take the names as they are.
        foreach (var database in server.Databases.Values)
        {
            RavenDBPublishing.EnsureValidDatabaseName(database);
        }

        RavenDBClientCertificates.EnsureMountable(
            RavenDBConsumers.Find(model, server), server, RavenDBClientCertificateSource.KubernetesSecret);
        EnsureApplicationSecretsAreTheirOwn(model, deployment);
    }

    /// <summary>
    /// An application's Secret must not be one the cluster itself uses: the application would mount the admin
    /// certificate or the license, and the bootstrap would register an admin certificate as the application's.
    /// </summary>
    private static void EnsureApplicationSecretsAreTheirOwn(DistributedApplicationModel model, RavenDBClusterDeployment deployment)
    {
        string?[] clusterSecrets =
            [deployment.ClientCertificateSecret, deployment.Options.ServerCertificateSecret, deployment.CertificateAuthoritySecret, deployment.LicenseSecretName];

        foreach (var consumer in RavenDBConsumers.Find(model, deployment.Server).Where(c => c.Resource.Name != deployment.BootstrapName))
        {
            var secret = consumer.BringsOwnCertificate
                ? consumer.OwnCertificate(RavenDBClientCertificateSource.KubernetesSecret)?.Location
                : deployment.ApplicationSecretName(consumer.Resource);

            if (secret is not null && clusterSecrets.Contains(secret))
            {
                throw new DistributedApplicationException(
                    $"'{consumer.Resource.Name}' would get Secret '{secret}', which RavenDB cluster '{deployment.Server.Name}' " +
                    "uses for its own certificates or license. Give the application a certificate Secret of its own.");
            }
        }
    }

    /// <summary>The Kubernetes environment the cluster runs in; other kinds of compute environments may sit next to it.</summary>
    private static KubernetesEnvironmentResource TargetEnvironment(DistributedApplicationModel model, RavenDBServerResource server)
    {
        var environments = model.Resources.OfType<KubernetesEnvironmentResource>().ToList();

        return environments.Count == 1
            ? environments[0]
            : throw new DistributedApplicationException(
                $"RavenDB server '{server.Name}' is published as a RavenDB cluster, which needs exactly one Kubernetes " +
                $"environment, and the application has {environments.Count}.");
    }

    private static (string Image, string Tag) ResolveImage(RavenDBClusterDeployment deployment)
    {
        var image = deployment.Options.Image;

        if (image is null)
        {
            // Without the Docker Hub registry: the operator only accepts images named ravendb/...
            var annotation = deployment.Server.Annotations.OfType<ContainerImageAnnotation>().Last();
            image = annotation.Registry is { Length: > 0 } registry && !string.Equals(registry, "docker.io", StringComparison.OrdinalIgnoreCase)
                ? $"{registry}/{annotation.Image}:{annotation.Tag}"
                : $"{annotation.Image}:{annotation.Tag}";
        }

        var separator = image.LastIndexOf(':');

        return separator > image.LastIndexOf('/') ? (image[..separator], image[(separator + 1)..]) : (image, "latest");
    }

    private static string FullImage(RavenDBClusterDeployment deployment)
    {
        var (image, tag) = ResolveImage(deployment);
        return $"{image}:{tag}";
    }

    /// <summary>
    /// Turns the bootstrap's Deployment into a Job and adds the objects RavenDB needs in the cluster.
    /// </summary>
    internal static void ApplyToBootstrap(KubernetesResource k8s, RavenDBClusterDeployment deployment)
    {
        if (k8s.Workload?.PodTemplate is not { Spec: { } pod } template)
        {
            throw new InvalidOperationException($"Aspire generated no workload for '{deployment.BootstrapName}'.");
        }

        // A one-shot Job has no endpoints.
        k8s.Service = null;

        var license = TakeLicense(k8s, pod);

        pod.RestartPolicy = "OnFailure";
        pod.ServiceAccountName = deployment.BootstrapName;

        // The image's own user (ravendb, 999), nothing more. The script keeps the keys it handles in /tmp, which
        // lives in memory rather than on the node's disk.
        pod.SecurityContext = new PodSecurityContextV1
        {
            RunAsNonRoot = true,
            RunAsUser = 999,
            RunAsGroup = 999,
            SeccompProfile = new SeccompProfileV1 { Type = "RuntimeDefault" },
        };
        pod.Volumes.Add(new VolumeV1 { Name = "scratch", EmptyDir = new EmptyDirVolumeSourceV1 { Medium = "Memory" } });

        pod.Volumes.Add(new VolumeV1
        {
            Name = "bootstrap-script",
            ConfigMap = new ConfigMapVolumeSourceV1 { Name = deployment.ScriptConfigMapName, DefaultMode = 365 }, // 0555
        });
        pod.Volumes.Add(new VolumeV1
        {
            Name = "admin-certificate",
            Secret = RavenDBKubernetesCertificates.SecretFile(deployment.ClientCertificateSecret, "client.pfx"),
        });

        var container = pod.Containers.Single();
        container.SecurityContext = new SecurityContextV1
        {
            AllowPrivilegeEscalation = false,
            ReadOnlyRootFilesystem = true,
            Capabilities = new CapabilitiesV1 { Drop = { "ALL" } },
        };
        container.VolumeMounts.Add(new VolumeMountV1 { Name = "scratch", MountPath = "/tmp" });
        container.VolumeMounts.Add(new VolumeMountV1 { Name = "bootstrap-script", MountPath = Path.GetDirectoryName(ScriptPath)!.Replace('\\', '/'), ReadOnly = true });
        container.VolumeMounts.Add(new VolumeMountV1 { Name = "admin-certificate", MountPath = "/ravendb/admin", ReadOnly = true });

        if (deployment.CertificateAuthoritySecret is { } certificateAuthority)
        {
            pod.Volumes.Add(new VolumeV1 { Name = "certificate-authority", Secret = RavenDBKubernetesCertificates.SecretFile(certificateAuthority, "ca.crt") });
            container.VolumeMounts.Add(new VolumeMountV1 { Name = "certificate-authority", MountPath = "/ravendb/ca", ReadOnly = true });
        }

        // Jobs are immutable. A new name per configuration, the pod's included, makes an upgrade run the new bootstrap
        // instead of failing to patch the old Job; Helm removes the old one.
        var job = new KubernetesJob { Spec = { Template = template } };
        job.Metadata.Name = $"{deployment.BootstrapName}-{ConfigurationHash(deployment, template)}";

        foreach (var (key, value) in k8s.Workload.Metadata.Labels)
        {
            job.Metadata.Labels[key] = value;
        }

        k8s.Workload = job;
        deployment.BootstrapJobName = job.Metadata.Name;

        var script = new ConfigMap();
        script.Metadata.Name = deployment.ScriptConfigMapName;
        script.Data["bootstrap.sh"] = Script;
        k8s.AdditionalResources.Add(script);

        k8s.AdditionalResources.AddRange(CreateAccess(deployment));
        k8s.AdditionalResources.AddRange(CreateApplicationSecrets(deployment));
        k8s.AdditionalResources.Add(CreateCluster(deployment, k8s.Parent));

        if (deployment.Options.IngressController == RavenDBIngressController.Traefik)
        {
            k8s.AdditionalResources.Add(CreateTraefikRoutes(deployment));
        }

        if (deployment.Options.LicenseSecretName is null)
        {
            k8s.AdditionalResources.Add(CreateLicenseSecret(deployment, license));
        }
    }

    private static RavenDBClusterManifest CreateCluster(RavenDBClusterDeployment deployment, KubernetesEnvironmentResource environment)
    {
        var options = deployment.Options;

        var cluster = new RavenDBClusterManifest
        {
            Spec =
            {
                Image = FullImage(deployment),
                Mode = options.Mode!,
                Email = options.Email,
                Domain = options.Domain!,
                LicenseSecretRef = deployment.LicenseSecretName,
                ClientCertSecretRef = deployment.ClientCertificateSecret,
                ClusterCertSecretRef = options.ServerCertificateSecret,
                CaCertSecretRef = deployment.CertificateAuthoritySecret,
                Nodes = [.. Enumerable.Range(0, options.Nodes).Select(i => new RavenDBClusterNode
                {
                    Tag = RavenDBClusterDeployment.NodeTag(i),
                    PublicServerUrl = deployment.NodeUrls[i],
                    PublicServerUrlTcp = $"tcp://{deployment.NodeTcpHost(i)}:443",
                })],
                Storage = { Data = { Size = options.StorageSize, StorageClassName = options.StorageClassName ?? environment.DefaultStorageClassName } },
                ExternalAccessConfiguration = { IngressControllerContext = { IngressClassName = IngressClassName(options.IngressController) } },
            },
        };

        cluster.Metadata.Name = deployment.ResourceName;
        return cluster;
    }

    /// <summary>The names the operator's RavenDBCluster definition accepts.</summary>
    private static string IngressClassName(RavenDBIngressController controller) => controller switch
    {
        RavenDBIngressController.Traefik => "traefik",
        RavenDBIngressController.HAProxy => "haproxy",
        RavenDBIngressController.Nginx => "nginx",
        _ => throw new ArgumentOutOfRangeException(nameof(controller), controller, null),
    };

    /// <summary>
    /// Traefik ignores the SSL passthrough annotations of the Ingress the operator creates, and RavenDB must see the
    /// clients' certificates. These routes hand each connection to the node's Service untouched, picked by the host
    /// name the client asks for, on the <c>websecure</c> entry point (port 443).
    /// </summary>
    private static TraefikIngressRouteTcp CreateTraefikRoutes(RavenDBClusterDeployment deployment)
    {
        var routes = new TraefikIngressRouteTcp
        {
            Spec =
            {
                EntryPoints = ["websecure"],
                Routes = [.. Enumerable.Range(0, deployment.Options.Nodes).SelectMany(i => new[]
                {
                    Route(deployment.NodeHost(i), i, NodeHttpsPort),
                    Route(deployment.NodeTcpHost(i), i, NodeTcpPort),
                })],
            },
        };

        routes.Metadata.Name = $"{deployment.ResourceName}-nodes";
        return routes;

        // The operator names each node's Service ravendb-<tag>, whatever the cluster is called.
        static TraefikRoute Route(string host, int node, int port) => new()
        {
            Match = $"HostSNI(`{host}`)",
            Services = [new TraefikRouteService { Name = $"ravendb-{RavenDBClusterDeployment.NodeTag(node)}", Port = port }],
        };
    }

    private static Secret CreateLicenseSecret(RavenDBClusterDeployment deployment, (bool Encoded, string Value)? license)
    {
        if (license is not { } value)
        {
            throw new InvalidOperationException($"The license parameter of '{deployment.Server.Name}' did not reach the chart.");
        }

        var secret = new Secret { Type = "Opaque" };
        secret.Metadata.Name = deployment.LicenseSecretName;

        // A license is JSON. Rendered into a quoted YAML string, its quotes break the template, so it goes in
        // base64 encoded, which the template does for a value only known at deploy time.
        secret.Data["license.json"] = value.Encoded
            ? value.Value
            : HelmExpression().Match(value.Value) is { Success: true } expression
                ? $"{{{{ {expression.Groups["value"].Value} | b64enc }}}}"
                : Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Value));

        return secret;
    }

    [GeneratedRegex(@"^\{\{\s*(?<value>[^{}]+?)\s*\}\}$")]
    private static partial Regex HelmExpression();

    /// <summary>
    /// The applications' Secrets, empty: the bootstrap fills each in with the application's certificate. As part of
    /// the chart, Helm refuses to take over a Secret of the same name it did not create, and removes them on uninstall.
    /// </summary>
    private static IEnumerable<Secret> CreateApplicationSecrets(RavenDBClusterDeployment deployment)
    {
        foreach (var consumer in deployment.Consumers)
        {
            var secret = new Secret { Type = "Opaque" };
            secret.Metadata.Name = deployment.ApplicationSecretName(consumer.Resource);
            yield return secret;
        }
    }

    /// <summary>
    /// The ServiceAccount, Role and RoleBinding of the bootstrap: read and fill in the applications' Secrets, by name,
    /// and nothing else.
    /// </summary>
    private static IEnumerable<BaseKubernetesResource> CreateAccess(RavenDBClusterDeployment deployment)
    {
        // Distinct names: the chart has one file per object name.
        var name = deployment.BootstrapName;

        var account = new KubernetesServiceAccount();
        account.Metadata.Name = name;
        yield return account;

        var role = new Role();
        role.Metadata.Name = $"{name}-role";

        var secrets = deployment.Consumers.Select(c => deployment.ApplicationSecretName(c.Resource)).ToList();

        if (secrets.Count > 0)
        {
            role.Rules.Add(Rule("secrets", ["get", "patch"], secrets));
        }

        yield return role;

        var binding = new RoleBinding
        {
            RoleRef = new RoleRefV1 { ApiGroup = "rbac.authorization.k8s.io", Kind = "Role", Name = role.Metadata.Name },
        };
        binding.Metadata.Name = $"{name}-rolebinding";
        binding.Subjects.Add(new SubjectV1 { Kind = "ServiceAccount", Name = name, Namespace = "{{ .Release.Namespace }}" });
        yield return binding;
    }

    private static PolicyRuleV1 Rule(string resource, IEnumerable<string> verbs, IEnumerable<string> names)
    {
        var rule = new PolicyRuleV1();
        rule.ApiGroups.Add(string.Empty);
        rule.Resources.Add(resource);
        rule.Verbs.AddRange(verbs);
        rule.ResourceNames.AddRange(names);
        return rule;
    }

    /// <summary>
    /// Moves the license out of the bootstrap's Secret: the Job does not need it, the operator's Secret does.
    /// </summary>
    private static (bool Encoded, string Value)? TakeLicense(KubernetesResource k8s, PodSpecV1 pod)
    {
        if (k8s.Secret is not { } secret)
        {
            return null;
        }

        (bool, string)? license = null;

        if (secret.Data.Remove(LicenseVariable, out var encoded))
        {
            license = (true, encoded);
        }
        else if (secret.StringData.Remove(LicenseVariable, out var plain))
        {
            license = (false, plain);
        }

        if (secret.Data.Count == 0 && secret.StringData.Count == 0)
        {
            foreach (var container in pod.Containers)
            {
                container.EnvFrom.RemoveAll(e => e.SecretRef?.Name == secret.Metadata.Name);
            }

            k8s.Secret = null;
        }

        return license;
    }

    /// <remarks>
    /// Built from the values, not from the chart alone: the chart's ConfigMap only holds references to values.yaml.
    /// The pod template counts too, so that a new version of this integration that changes it renames the Job.
    /// </remarks>
    private static string ConfigurationHash(RavenDBClusterDeployment deployment, PodTemplateSpecV1 template)
    {
        var configuration = new StringBuilder()
            .AppendLine(JsonSerializer.Serialize(template))
            .AppendLine(Script)
            .AppendLine(FullImage(deployment))
            .AppendLine(deployment.ClientCertificateSecret)
            .AppendLine(deployment.CertificateAuthoritySecret)
            .AppendLine(string.Join(' ', deployment.NodeUrls));

        foreach (var (key, value) in Settings(deployment))
        {
            configuration.Append(key).Append('=').AppendLine(value);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(configuration.ToString()));
        return Convert.ToHexString(hash, 0, 5).ToLowerInvariant();
    }
}
