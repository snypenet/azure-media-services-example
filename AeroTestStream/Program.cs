using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Rest;

static async Task<ServiceClientCredentials> GetCredentialsAsync(IConfiguration configuration)
{
    // Use ConfidentialClientApplicationBuilder.AcquireTokenForClient to get a token using a service principal with symmetric key

    var scopes = new[] { configuration["AZURE_ARM_TOKEN_AUDIENCE"] + "/.default" };

    var app = ConfidentialClientApplicationBuilder.Create(configuration["AZURE_CLIENT_ID"])
        .WithClientSecret(configuration["AZURE_CLIENT_SECRET"])
        .WithAuthority(AzureCloudInstance.AzurePublic, configuration["AZURE_TENANT_ID"])
        .Build();

    var authResult = await app.AcquireTokenForClient(scopes)
                                             .ExecuteAsync()
                                             .ConfigureAwait(false);

    return new TokenCredentials(authResult.AccessToken, "Bearer");
}

static async Task<AzureMediaServicesClient> GetMediaClient(IConfiguration configuration)
{
    return new AzureMediaServicesClient(await GetCredentialsAsync(configuration))
    {
        SubscriptionId = configuration["AZURE_SUBSCRIPTION_ID"]
    };
}

static LiveEventInputAccessControl GetLiveEventAccessControl()
{
    var allAllowIPRange = new IPRange(
        name: "AllowAll",
        address: "0.0.0.0",
        subnetPrefixLength: 0
    );

    var liveEventInputAccess = new LiveEventInputAccessControl()
    {
        Ip = new IPAccessControl(
            allow: new IPRange[]
            {
                // re-use the same range here for the sample, but in production you can lock this
                // down to the ip range for your on-premises live encoder, laptop, or device that is sending
                // the live stream
                allAllowIPRange
            }
        )

    };

    return liveEventInputAccess;
}

static LiveEventPreviewAccessControl GetLiveEventPreviewAccessControl()
{
    var allAllowIPRange = new IPRange(
        name: "AllowAll",
        address: "0.0.0.0",
        subnetPrefixLength: 0
    );

    var liveEventInputAccess = new LiveEventPreviewAccessControl()
    {
        Ip = new IPAccessControl(
            allow: new IPRange[]
            {
                // re-use the same range here for the sample, but in production you can lock this
                // down to the ip range for your on-premises live encoder, laptop, or device that is sending
                // the live stream
                allAllowIPRange
            }
        )

    };

    return liveEventInputAccess;
}

static LiveEvent CreateLiveEvent(string accessToken, string mediaServiceLocation, LiveEventInputAccessControl eventAccessControl, LiveEventPreviewAccessControl previewAccessControl)
{
    var liveEvent = new LiveEvent(
        location: mediaServiceLocation,
        description: "Sample LiveEvent from .NET SDK sample",
        // Set useStaticHostname to true to make the ingest and preview URL host name the same. 
        // This can slow things down a bit. 
        useStaticHostname: true,
        // 1) Set up the input settings for the Live event...
        input: new LiveEventInput(
            streamingProtocol: LiveEventInputProtocol.RTMP,
            accessControl: eventAccessControl,
            keyFrameIntervalDuration: "PT2S",
            accessToken: accessToken
        ),
        encoding: new LiveEventEncoding(
            encodingType: LiveEventEncodingType.PassthroughStandard
        ),
        preview: new LiveEventPreview(accessControl: previewAccessControl),
        streamOptions: new List<StreamOptionsFlag?>()
        {
            StreamOptionsFlag.LowLatency
        }
    );

    return liveEvent;
}

static AssetFilter GetAssetFilter()
{
    var drvAssetFilter = new AssetFilter(
                   presentationTimeRange: new PresentationTimeRange(
                       forceEndTimestamp: false,
                       presentationWindowDuration: 6000000000L,
                       liveBackoffDuration: 20000000L)
                );

    return drvAssetFilter;
}

static async Task<T?> Execute<T>(Func<Task<T>> action) where T : class
{
    try
    {
        var result = await action();
        return result;
    }
    catch(Microsoft.Azure.Management.Media.Models.ErrorResponseException ex)
    {
        if (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        throw;
    }
}

static (string hlsManifest, string dashManifest) BuildManifestPaths(string scheme, string hostname, string streamingLocatorId, string manifestName)
{
    const string hlsFormat = "format=m3u8-cmaf";
    const string dashFormat = "format=mpd-time-cmaf";

    string manifestBase = $"{scheme}://{hostname}/{streamingLocatorId}/{manifestName}.ism/manifest";
    string hlsManifest = $"{manifestBase}({hlsFormat})";

    string dashManifest = $"{manifestBase}({dashFormat})";

    return (hlsManifest, dashManifest);
}

static async Task Run()
{
    var config = new ConfigurationBuilder()
                   .SetBasePath(Directory.GetCurrentDirectory())
                   .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                   .AddEnvironmentVariables() // parses the values from the optional .env file at the solution root
                   .Build();

    var mediaClient = await GetMediaClient(config);

    Console.WriteLine("Getting Media Service");
    var mediaService = await mediaClient.Mediaservices.GetAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"]);

    var liveEventAccessControl = GetLiveEventAccessControl();
    var previewEventAccessControl = GetLiveEventPreviewAccessControl();

    string liveEventName = "SomeEventName";
    var liveEvent = await Execute(async () => await mediaClient.LiveEvents.GetAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], liveEventName));
    if (liveEvent == null)
    {
        Console.WriteLine("Creating Live Event");
        string accessToken = Guid.NewGuid().ToString().Replace("-", "");
        liveEvent = CreateLiveEvent(accessToken, mediaService.Location, liveEventAccessControl, previewEventAccessControl);
        await mediaClient.LiveEvents.CreateAsync(config["AZURE_RESOURCE_GROUP"],
            config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"],
            liveEventName,
            liveEvent,
            autoStart: false);
    }    
    else
    {
        Console.WriteLine("Live Event already exists");
    }

    string id = Guid.NewGuid().ToString().Replace("-", "");

    string assetName = "SomeAssetName-" + id;
    string outputAssetName = assetName + "_output";

    // media to record live output
    Console.WriteLine("Creating Asset");
    var asset = await mediaClient.Assets.CreateOrUpdateAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], assetName, new Asset());
    var outputAssest = await mediaClient.Assets.CreateOrUpdateAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], outputAssetName, new Asset());

    string manifestName = "SomeManifestName";
    string liveOutputName = "SomeLiveoutputName";

    var liveOutput = await Execute(async () => await mediaClient.LiveOutputs.GetAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], liveEventName, liveOutputName));
    if (liveOutput != null)
    {
        Console.WriteLine("Deleting existing Live Output");
        await mediaClient.LiveOutputs.DeleteAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], liveEventName, liveOutputName);
    }

    Console.WriteLine("Creating Live Output");
    liveOutput = new LiveOutput(asset.Name, TimeSpan.FromHours(1), manifestName: manifestName);
    await mediaClient.LiveOutputs.CreateAsync(
        config["AZURE_RESOURCE_GROUP"],
        config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"],
        liveEventName,
        liveOutputName,
        liveOutput);
    

    if (liveEvent.ResourceState != LiveEventResourceState.Running)
    {
        Console.WriteLine("Starting Live Event");
        await mediaClient.LiveEvents.StartAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], liveEventName);
    }

    // refresh live event object
    liveEvent = await mediaClient.LiveEvents.GetAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], liveEventName);

    Console.WriteLine($"Ingestion URL: {liveEvent.Input.Endpoints.First().Url}");
    Console.WriteLine($"Preview URL: {liveEvent.Preview.Endpoints.First().Url}");
    Console.WriteLine("Press ENTER to continue");
    Console.ReadLine();

    string assetFilterName = "SomeAssetFilter-" + id;
    Console.WriteLine("Creating Asset Filter");
        var assetFilter = GetAssetFilter();
        await mediaClient.AssetFilters.CreateOrUpdateAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"],
                        assetName, assetFilterName, assetFilter);

    string locatorName = "LocatorName-" + id;

    Console.WriteLine("Creating Locator");
    var locator = await mediaClient.StreamingLocators.CreateAsync(config["AZURE_RESOURCE_GROUP"],
    config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"],
    locatorName,
    new StreamingLocator
    {
        AssetName = assetName,
        StreamingPolicyName = PredefinedStreamingPolicy.ClearStreamingOnly,
        Filters = new List<string>
            {
                assetFilterName
            }   // Associate the dvr filter with StreamingLocator.
    });

    string endpointName = "default";
    var streamingEndpoint = await Execute(async () => await mediaClient.StreamingEndpoints.GetAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], endpointName));
    if (streamingEndpoint == null)
    {
        streamingEndpoint = await mediaClient.StreamingEndpoints.CreateAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], endpointName, new StreamingEndpoint());
    }
    
    if (streamingEndpoint.ResourceState != StreamingEndpointResourceState.Running)
    {
        Console.WriteLine("Starting Streaming Endpoint");
        await mediaClient.StreamingEndpoints.StartAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], endpointName);
    }

    if (locator.StreamingLocatorId == null)
    {
        throw new Exception("Stream Locator is null");
    }

    var (hlsManifest, dashManifest) = BuildManifestPaths("https", streamingEndpoint.HostName, locator.StreamingLocatorId.Value.ToString(), manifestName);

    Console.WriteLine($"HLS: {hlsManifest}");
    Console.WriteLine($"DASH: {dashManifest}");
    Console.WriteLine("Press ENTER to shutdown stream....");
    Console.ReadLine();

    Console.WriteLine("Stopping Streaming Endpoint");
    await mediaClient.StreamingEndpoints.StopAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], endpointName);

    Console.WriteLine("Stopping Live Event");
    await mediaClient.LiveEvents.StopAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], liveEventName);

    Console.WriteLine("Deleting Live Output");
    await mediaClient.LiveOutputs.DeleteAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], liveEventName, liveOutputName);

    string transformName = "tomp4singlebrate";
    string jobName = transformName + id;
    var job = await mediaClient.Jobs.CreateAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], transformName, jobName, new Job(
        new JobInputAsset(assetName),
        outputs: new List<JobOutput> { new JobOutputAsset(outputAssetName) }
    ));

    Console.WriteLine("Converting Output to MP4");
    const int UpperLimit = 300;
    int count = 0;
    while (job.State != JobState.Finished && count++ <= UpperLimit)
    {
        job = await mediaClient.Jobs.GetAsync(config["AZURE_RESOURCE_GROUP"], config["AZURE_MEDIA_SERVICES_ACCOUNT_NAME"], transformName, jobName);
        await Task.Delay(1000);
    }

    Console.WriteLine($"Job State: {job.State}");
}

await Run();


