using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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

        server.PublishAsExternal(deployment.Url, createsDatabases: !deployment.IsExisting);
        builder
            .WithAnnotation(deployment)
            .WithPipelineStepFactory(_ => RavenDBClusterPipelineSteps.Create(deployment))
            .WithPipelineConfiguration(context => RavenDBClusterPipelineSteps.Configure(deployment, context))
            .ExcludeFromManifest();

        var (image, tag) = ResolveImage(deployment);

        var bootstrap = applicationBuilder.AddContainer(deployment.BootstrapName, image, tag)
            .WithEntrypoint("/bin/bash")
            .WithArgs(ScriptPath)
            .WithEnvironment("RAVENDB_URLS", deployment.IsExisting ? deployment.Url : ReferenceExpression.Create($"{string.Join(' ', deployment.NodeUrls)}"))
            .WithEnvironment(context =>
            {
                // Evaluated when the chart is written: databases and references declared after this call count.
                foreach (var (name, value) in Settings(deployment))
                {
                    context.EnvironmentVariables[name] = value;
                }

                // Carried to the operator's license Secret, see ApplyToBootstrap.
                if (!deployment.IsExisting && deployment.Options!.LicenseSecretName is null && server.LicenseParameter is { } license)
                {
                    context.EnvironmentVariables[LicenseVariable] = license;
                }
            })
            .PublishAsKubernetesService(k8s => ApplyToBootstrap(k8s, deployment));

        applicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            Validate(@event.Model, deployment);

            deployment.Consumers = RavenDBConsumers.Find(@event.Model, server)
                .Where(c => !ReferenceEquals(c.Resource, bootstrap.Resource))
                .ToList();

            foreach (var consumer in deployment.Consumers)
            {
                if (consumer.Resource is IComputeResource compute)
                {
                    applicationBuilder.CreateResourceBuilder(compute)
                        .PublishAsKubernetesService(k8s => ApplyToConsumer(k8s, deployment, consumer));
                }
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>The bootstrap's settings, apart from the URLs and the license.</summary>
    private static SortedDictionary<string, string> Settings(RavenDBClusterDeployment deployment) => new(StringComparer.Ordinal)
    {
        ["RAVENDB_WAIT_FOR_NODES"] = deployment.IsExisting ? "false" : "true",
        ["RAVENDB_SECRET_OWNER"] = deployment.BootstrapName,
        ["RAVENDB_DATABASES"] = deployment.IsExisting ? string.Empty : string.Join(' ', deployment.Server.DatabasesToCreate),
        ["RAVENDB_REPLICATION_FACTOR"] = (deployment.Options?.Nodes ?? 1).ToString(CultureInfo.InvariantCulture),
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

        if (deployment.IsExisting)
        {
            return;
        }

        var clusters = model.Resources
            .OfType<RavenDBServerResource>()
            .Where(r => r.Annotations.OfType<RavenDBClusterDeployment>().Any(d => !d.IsExisting))
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

        if (deployment.Options!.LicenseSecretName is null && server.LicenseParameter is null)
        {
            throw new DistributedApplicationException(
                $"The RavenDB operator reads the license of '{server.Name}' from a Secret. Pass the license as a " +
                "parameter with WithLicense(...), or name an existing Secret with LicenseSecretName.");
        }
    }

    private static (string Image, string Tag) ResolveImage(RavenDBClusterDeployment deployment)
    {
        var image = deployment.Options?.Image;

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

        pod.Volumes.Add(new VolumeV1
        {
            Name = "bootstrap-script",
            ConfigMap = new ConfigMapVolumeSourceV1 { Name = deployment.ScriptConfigMapName, DefaultMode = 365 }, // 0555
        });
        pod.Volumes.Add(new VolumeV1
        {
            Name = "admin-certificate",
            Secret = new SecretVolumeSourceV1 { SecretName = deployment.ClientCertificateSecret },
        });

        var container = pod.Containers.Single();
        container.VolumeMounts.Add(new VolumeMountV1 { Name = "bootstrap-script", MountPath = Path.GetDirectoryName(ScriptPath)!.Replace('\\', '/'), ReadOnly = true });
        container.VolumeMounts.Add(new VolumeMountV1 { Name = "admin-certificate", MountPath = "/ravendb/admin", ReadOnly = true });

        if (deployment.CertificateAuthoritySecret is { } certificateAuthority)
        {
            pod.Volumes.Add(new VolumeV1 { Name = "certificate-authority", Secret = new SecretVolumeSourceV1 { SecretName = certificateAuthority } });
            container.VolumeMounts.Add(new VolumeMountV1 { Name = "certificate-authority", MountPath = "/ravendb/ca", ReadOnly = true });
        }

        // Jobs are immutable. A new name per configuration makes an upgrade run the new bootstrap instead of
        // failing to patch the old Job; Helm removes the old one.
        var job = new KubernetesJob { Spec = { Template = template } };
        job.Metadata.Name = $"{deployment.BootstrapName}-{ConfigurationHash(deployment)}";

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

        if (deployment.Options is { } options)
        {
            k8s.AdditionalResources.Add(CreateCluster(deployment, options));

            if (options.LicenseSecretName is null)
            {
                k8s.AdditionalResources.Add(CreateLicenseSecret(deployment, license));
            }
        }
    }

    /// <summary>
    /// Mounts the application's certificate (and the certificate authority, when the cluster has its own) and points
    /// the RavenDB client integration at it.
    /// </summary>
    internal static void ApplyToConsumer(KubernetesResource k8s, RavenDBClusterDeployment deployment, RavenDBConsumer consumer)
    {
        if (k8s.Workload?.PodTemplate?.Spec is not { } pod)
        {
            return;
        }

        var certificateVolume = $"{deployment.ResourceName}-client-certificate";
        var certificateAuthorityVolume = $"{deployment.ResourceName}-certificate-authority";

        pod.Volumes.Add(new VolumeV1
        {
            Name = certificateVolume,
            Secret = new SecretVolumeSourceV1 { SecretName = deployment.ApplicationSecretName(consumer.Resource) },
        });

        if (deployment.CertificateAuthoritySecret is { } certificateAuthority)
        {
            pod.Volumes.Add(new VolumeV1 { Name = certificateAuthorityVolume, Secret = new SecretVolumeSourceV1 { SecretName = certificateAuthority } });
        }

        foreach (var container in pod.Containers)
        {
            container.VolumeMounts.Add(new VolumeMountV1 { Name = certificateVolume, MountPath = deployment.CertificateDirectory, ReadOnly = true });

            foreach (var connectionName in consumer.ConnectionNames)
            {
                container.Env.Add(new EnvVarV1 { Name = RavenDBConsumers.CertificatePathVariable(connectionName), Value = deployment.CertificatePath });
            }

            if (deployment.CertificateAuthoritySecret is not null)
            {
                container.VolumeMounts.Add(new VolumeMountV1 { Name = certificateAuthorityVolume, MountPath = deployment.CertificateAuthorityDirectory, ReadOnly = true });

                // .NET on Linux reads its trusted roots from the directories in SSL_CERT_DIR: keep the image's own
                // and add the cluster's certificate authority.
                container.Env.RemoveAll(e => e.Name == "SSL_CERT_DIR");
                container.Env.Add(new EnvVarV1 { Name = "SSL_CERT_DIR", Value = $"/etc/ssl/certs:{deployment.CertificateAuthorityDirectory}" });
            }
        }
    }

    private static RavenDBClusterManifest CreateCluster(RavenDBClusterDeployment deployment, RavenDBClusterOptions options)
    {
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
                CaCertSecretRef = options.CertificateAuthoritySecret,
                Nodes = [.. Enumerable.Range(0, options.Nodes).Select(i => new RavenDBClusterNode
                {
                    Tag = RavenDBClusterDeployment.NodeTag(i),
                    PublicServerUrl = deployment.NodeUrls[i],
                    PublicServerUrlTcp = $"tcp://{RavenDBClusterDeployment.NodeTag(i)}-tcp.{options.Domain}:443",
                })],
                Storage = { Data = { Size = options.StorageSize, StorageClassName = options.StorageClassName } },
                ExternalAccessConfiguration = { IngressControllerContext = { IngressClassName = options.IngressClassName } },
            },
        };

        cluster.Metadata.Name = deployment.ResourceName;
        return cluster;
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
    /// The ServiceAccount, Role and RoleBinding of the bootstrap: read and create the applications' Secrets, and read
    /// the ServiceAccount that owns them.
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
        role.Rules.Add(Rule("serviceaccounts", "get", [name]));

        var secrets = deployment.Consumers.Select(c => deployment.ApplicationSecretName(c.Resource)).ToList();

        if (secrets.Count > 0)
        {
            role.Rules.Add(Rule("secrets", "get", secrets));

            // Kubernetes cannot restrict create to names.
            role.Rules.Add(Rule("secrets", "create"));
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

    private static PolicyRuleV1 Rule(string resource, string verb, IEnumerable<string>? names = null)
    {
        var rule = new PolicyRuleV1();
        rule.ApiGroups.Add(string.Empty);
        rule.Resources.Add(resource);
        rule.Verbs.Add(verb);
        rule.ResourceNames.AddRange(names ?? []);
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
    /// Built from the values, not from the chart: the chart's ConfigMap only holds references to values.yaml.
    /// </remarks>
    private static string ConfigurationHash(RavenDBClusterDeployment deployment)
    {
        var configuration = new StringBuilder()
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
