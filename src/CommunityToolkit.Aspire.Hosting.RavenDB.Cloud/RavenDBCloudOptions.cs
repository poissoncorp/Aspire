#pragma warning disable ASPIREATS001 // AspireExport is experimental

using Aspire.Hosting;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>Cloud provider a RavenDB Cloud product runs on.</summary>
public enum RavenDBCloudProvider
{
    /// <summary>Amazon Web Services.</summary>
    Aws,

    /// <summary>Microsoft Azure.</summary>
    Azure,

    /// <summary>Google Cloud Platform.</summary>
    Gcp,
}

/// <summary>RavenDB Cloud product tier.</summary>
public enum RavenDBCloudTier
{
    /// <summary>Free tier.</summary>
    Free,

    /// <summary>Development tier.</summary>
    Development,

    /// <summary>Production tier.</summary>
    Production,
}

/// <summary>Storage type of the product's data disk.</summary>
public enum RavenDBCloudStorageType
{
    /// <summary>Standard SSD.</summary>
    SsdStandard,

    /// <summary>Premium SSD with provisioned IOPS.</summary>
    SsdPremium,
}

/// <summary>
/// The RavenDB Cloud product a server is published to by <c>aspire deploy</c>.
/// </summary>
/// <remarks>
/// A product is looked up by <see cref="ProductName"/>. When it does not exist, the deployment creates it from the
/// remaining options; options left unset are resolved from the Cloud API metadata at deploy time.
/// </remarks>
[AspireExport(ExposeProperties = true, ExposeMethods = true)]
public sealed class RavenDBCloudOptions
{
    /// <summary>
    /// Display name of the product in the account. Defaults to <c>&lt;resource name&gt;-&lt;environment&gt;</c>, so
    /// staging and production get different products.
    /// </summary>
    public string? ProductName { get; set; }

    /// <summary>
    /// Subdomain of the product's node URLs, which RavenDB Cloud requires to create a paid product: up to 13 letters,
    /// digits and dashes, not starting or ending with a dash, and unique in the account. When unset, it is derived
    /// from <see cref="ProductName"/>, shortened with a hash of the name when the name is too long.
    /// </summary>
    public string? Subdomain { get; set; }

    /// <summary>Cloud provider. Default: <see cref="RavenDBCloudProvider.Aws"/>.</summary>
    public RavenDBCloudProvider Provider { get; set; } = RavenDBCloudProvider.Aws;

    /// <summary>Provider region. Default: <c>us-east-1</c>.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Product tier. Default: <see cref="RavenDBCloudTier.Development"/>.</summary>
    public RavenDBCloudTier Tier { get; set; } = RavenDBCloudTier.Development;

    /// <summary>
    /// Instance type. When unset, the smallest instance type of <see cref="Tier"/> offered in the region is used.
    /// </summary>
    public string? InstanceType { get; set; }

    /// <summary>Data disk size in GB. When unset, the smallest size offered for the instance type is used.</summary>
    public int? DiskSizeInGb { get; set; }

    /// <summary>Storage type of the data disk. Default: <see cref="RavenDBCloudStorageType.SsdStandard"/>.</summary>
    public RavenDBCloudStorageType StorageType { get; set; } = RavenDBCloudStorageType.SsdStandard;

    /// <summary>Release channel. When unset, the account's default release channel is used.</summary>
    public string? ReleaseChannel { get; set; }

    /// <summary>
    /// IP ranges allowed to reach the product, in CIDR notation. The Cloud API requires at least one to create a
    /// product and offers no way to change them afterwards, so they are set in the portal from then on.
    /// </summary>
    public IList<string> AllowedIps { get; } = [];

    /// <summary>RavenDB Cloud API endpoint. Default: <c>https://api.cloud.ravendb.net</c>.</summary>
    public string ApiEndpoint { get; set; } = "https://api.cloud.ravendb.net";

    /// <summary>How long a deployment waits for a new product to become active. Default: 30 minutes.</summary>
    [AspireExportIgnore(Reason = "A TimeSpan reaches polyglot app hosts as a number without a unit.")]
    public TimeSpan ProvisioningTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Terminates the product in <c>aspire destroy</c>, but only if this deployment created it. Off by default:
    /// terminating a product deletes its data.
    /// </summary>
    public bool TerminateOnDestroy { get; set; }

    /// <summary>Allows the given IP ranges to reach the product.</summary>
    /// <param name="allowedIps">IP ranges in CIDR notation.</param>
    /// <returns>These options, for chaining.</returns>
    public RavenDBCloudOptions WithAllowedIps(params string[] allowedIps)
    {
        ArgumentNullException.ThrowIfNull(allowedIps);

        foreach (var ip in allowedIps)
        {
            AllowedIps.Add(ip);
        }

        return this;
    }
}
