# Aspire Community Toolkit Diagnostics

Some of the API's in the Aspire Community Toolkit are decorated with the [`ExperimentalAttribute`](https://learn.microsoft.com/dotnet/api/system.diagnostics.codeanalysis.experimentalattribute). This attribute is used to indicate that an API is not ready for production use and is subject to change in the future.

## CTASPIRE001

There are cases where there is an API provided by the Aspire Community Toolkit that is an intermediary solution until a feature is added to Aspire itself. This could be a private API that is not yet ready for public consumption, or a workaround waiting for the completion of a feature in Aspire.

In these cases, refer to the `<remarks>` docs section of the API for more information on the feature in Aspire and the issue(s) to track.

Once a release of Aspire with that API is available, the API in the Aspire Community Toolkit will be marked as obsolete and will be removed in a future release.

## CTASPIRE002

This API is marked as experimental as it does not have a stable implementation, meaning the underlying API is not stable or the test coverage is not consistent in passing. No guarantees are made regarding its functionality or stability.

## CTASPIRE003

The API is marked for deprecation and will be removed in a future release.

## CTASPIREPERL001

This API is marked as experimental for the Perl hosting integration and may change or be removed in future updates.

Current usage:

- `WithPerlCertificateTrust(...)`

Suppress this diagnostic only when you intentionally opt into the experimental API surface.

## CTASPIREPERL002

Dockerfile generation for Perl resources is marked as experimental. The publish-mode Dockerfile
builder (`BuildCpanmDockerfile`, `BuildCartonDockerfile`, `BuildContainerEntrypointArguments`)
has not been fully validated across all deployment targets and may change in future releases.

Current usage:

- `BuildCpanmDockerfile(...)` - single-stage cpanm Dockerfile generation
- `BuildCartonDockerfile(...)` - multi-stage Carton Dockerfile generation
- `BuildContainerEntrypointArguments(...)` - container entrypoint argument construction

Suppress this diagnostic only when you intentionally opt into the experimental Dockerfile generation.

## CTASPIRERAVENDB001

The RavenDB hosting integration's API for deployment packages, such as `CommunityToolkit.Aspire.Hosting.RavenDB.Cloud`, which publish a RavenDB server somewhere else than its container. It may change while those packages are previews.

Current usage:

- `WithPublishStrategy(...)` and `RavenDBPublishStrategy` - publish the server with a deployment package's URL instead of its container
- `RavenDBServerResource.PublishStrategy` - the strategy in use, if any
- `RavenDBServerResource.DatabasesToCreate` - the databases declared with `ensureCreated`, which a deployment package creates

AppHosts do not call these: they call the deployment package's own method, such as `PublishAsRavenDBCloud(...)`.
