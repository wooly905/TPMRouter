using TPMRouter.Api.Options;

namespace TPMRouter.UnitTests;

public class RelayOptionsValidatorTests
{
    private readonly RelayOptionsValidator _validator = new();

    [Fact]
    public void Validate_ValidOptions_Succeeds()
    {
        Assert.True(_validator.Validate(null, Valid()).Succeeded);
    }

    [Fact]
    public void Validate_NoDeployments_Fails()
    {
        RelayOptions options = Valid();
        options.Endpoints[0].Deployments.Clear();

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Validate_MissingTokensPerMinute_Fails()
    {
        RelayOptions options = Valid();
        options.Endpoints[0].Deployments[0].TokensPerMinute = null;

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Validate_MissingRequestsPerMinute_Fails()
    {
        RelayOptions options = Valid();
        options.Endpoints[0].Deployments[0].RequestsPerMinute = null;

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(0, 10, 100)]
    [InlineData(1000, -1, 100)]
    [InlineData(1000, 10, 0)]
    public void Validate_InvalidDeploymentValues_Fails(long tpm, int rpm, int maxOutputTokens)
    {
        RelayOptions options = Valid();
        options.Endpoints[0].Deployments[0] = TestData.Deployment("a", tpm, rpm, maxOutputTokens);

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Validate_DeadlineNotAboveUpstreamTimeout_Fails()
    {
        RelayOptions options = Valid();
        options.UpstreamTimeoutSeconds = 120;
        options.RequestDeadlineSeconds = 120;

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Validate_EmptyEndpointOrApiKey_Fails()
    {
        RelayOptions noEndpoint = Valid();
        noEndpoint.Endpoints[0].Endpoint = "";
        RelayOptions noApiKey = Valid();
        noApiKey.Endpoints[0].ApiKey = "";

        Assert.True(_validator.Validate(null, noEndpoint).Failed);
        Assert.True(_validator.Validate(null, noApiKey).Failed);
    }

    private static RelayOptions Valid() => TestData.Options(0.9, TestData.Deployment("a", 1000, 10));

    [Fact]
    public void Validate_NoEndpoints_Fails()
    {
        RelayOptions options = Valid();
        options.Endpoints.Clear();

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Validate_DuplicateEndpointId_Fails()
    {
        RelayOptions options = Valid();
        options.Endpoints.Add(TestData.Endpoint("test", TestData.Deployment("b", 1000, 0)));

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("https://example.invalid")]
    [InlineData("https://EXAMPLE.invalid:443/")]
    public void Validate_DuplicateResourceUri_Fails(string endpoint)
    {
        RelayOptions options = Valid();
        EndpointOptions other = TestData.Endpoint("other", TestData.Deployment("a", 1000, 0));
        other.Endpoint = endpoint;
        options.Endpoints.Add(other);

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Validate_DuplicateDeploymentInSameEndpoint_Fails()
    {
        RelayOptions options = Valid();
        options.Endpoints[0].Deployments.Add(TestData.Deployment("a", 1000, 0));

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Validate_SameDeploymentModelAndPriorityAcrossEndpoints_Succeeds()
    {
        RelayOptions options = Valid();
        options.Endpoints.Add(TestData.Endpoint("other", TestData.Deployment("a", 1000, 0)));

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    public void Validate_MissingOrReservedModel_Fails(string model)
    {
        RelayOptions options = Valid();
        options.Endpoints[0].Deployments[0].Model = model;

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("file:///azure")]
    [InlineData("https://example.invalid/openai/v1/")]
    [InlineData("https://example.invalid/?api-version=1")]
    public void Validate_InvalidEndpointRoot_Fails(string endpoint)
    {
        RelayOptions options = Valid();
        options.Endpoints[0].Endpoint = endpoint;

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Validate_MissingEndpointId_Fails()
    {
        RelayOptions options = Valid();
        options.Endpoints[0].Id = "";

        Assert.True(_validator.Validate(null, options).Failed);
    }
}
