using System.Text.Json;
using PuntersScraper.App;

namespace PuntersScraper.Tests;

/// <summary>Verifies the Dev/Prod toggle maps to the right S3 bucket and RabbitMQ host, and that
/// it persists in settings.json while the derived values never do.</summary>
public class TargetEnvironmentTests
{
    [Fact]
    public void DefaultsToProd()
    {
        var settings = new AppSettings();

        Assert.Equal(TargetEnvironment.Prod, settings.TargetEnvironment);
        Assert.Equal("queue", settings.S3BucketName);
        Assert.Equal("62.171.228.224", settings.RabbitMqHostName);
    }

    [Fact]
    public void Dev_UsesGotBucketAndDevBroker()
    {
        var settings = new AppSettings { TargetEnvironment = TargetEnvironment.Dev };

        Assert.Equal("got", settings.S3BucketName);
        Assert.Equal("138.226.222.210", settings.RabbitMqHostName);
    }

    [Fact]
    public void Custom_UsesTypedBucket_Trimmed()
    {
        var settings = new AppSettings { TargetEnvironment = TargetEnvironment.Custom, CustomS3BucketName = "  my-test-bucket " };

        Assert.Equal("my-test-bucket", settings.S3BucketName);
    }

    [Fact]
    public void Custom_NeverPublishesToRabbitMq()
    {
        var settings = new AppSettings { TargetEnvironment = TargetEnvironment.Custom, RabbitMqEnabled = true };

        Assert.False(settings.PublishesToRabbitMq);
    }

    [Theory]
    [InlineData(TargetEnvironment.Prod)]
    [InlineData(TargetEnvironment.Dev)]
    public void ProdAndDev_PublishWhenRabbitMqEnabled(TargetEnvironment environment)
    {
        Assert.True(new AppSettings { TargetEnvironment = environment, RabbitMqEnabled = true }.PublishesToRabbitMq);
        Assert.False(new AppSettings { TargetEnvironment = environment, RabbitMqEnabled = false }.PublishesToRabbitMq);
    }

    [Fact]
    public async Task Publisher_InCustomEnvironment_DoesNotPublish()
    {
        // No broker exists in tests — returning false without attempting a connection (which would
        // otherwise retry and time out) proves the Custom guard short-circuits before any publish.
        var settings = new AppSettings { TargetEnvironment = TargetEnvironment.Custom, CustomS3BucketName = "x" };
        await using var publisher = new PuntersScraper.App.Services.RabbitMqMeetingEventPublisher(settings);
        var evt = new PuntersScraper.Shared.Messaging.MeetingScrapedEvent { MeetingName = "Test" };

        var published = await publisher.PublishMeetingScrapedAsync(evt, progress: null, CancellationToken.None);

        Assert.False(published);
    }

    [Fact]
    public void Serializes_CustomBucketName_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new AppSettings { TargetEnvironment = TargetEnvironment.Custom, CustomS3BucketName = "my-bucket" });
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(TargetEnvironment.Custom, settings.TargetEnvironment);
        Assert.Equal("my-bucket", settings.S3BucketName);
    }

    [Fact]
    public void Serializes_EnvironmentAsString_AndOmitsDerivedValues()
    {
        var json = JsonSerializer.Serialize(new AppSettings { TargetEnvironment = TargetEnvironment.Dev });

        Assert.Contains("\"TargetEnvironment\":\"Dev\"", json);
        Assert.DoesNotContain("\"S3BucketName\"", json);
        Assert.DoesNotContain("\"RabbitMqHostName\"", json);
        Assert.DoesNotContain("\"PublishesToRabbitMq\"", json);
    }

    [Fact]
    public void Deserializes_LegacyBucketAndHostKeys_AreIgnored()
    {
        // An older settings.json (or the old installer defaults) carrying explicit values must not
        // override what the environment dictates.
        const string legacy =
            "{\"UploadToS3\":false,\"S3BucketName\":\"troyen-gen-prod\",\"RabbitMqHostName\":\"10.0.0.1\"}";

        var settings = JsonSerializer.Deserialize<AppSettings>(legacy)!;

        Assert.Equal(TargetEnvironment.Prod, settings.TargetEnvironment);
        Assert.Equal("queue", settings.S3BucketName);
        Assert.Equal("62.171.228.224", settings.RabbitMqHostName);
    }

    [Fact]
    public void Deserializes_DevEnvironment()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"TargetEnvironment\":\"Dev\"}")!;

        Assert.Equal("got", settings.S3BucketName);
        Assert.Equal("138.226.222.210", settings.RabbitMqHostName);
    }
}
