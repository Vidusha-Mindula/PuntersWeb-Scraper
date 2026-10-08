using System.Text.Json;
using PuntersScraper.App.Services;
using PuntersScraper.Shared.Messaging;
using PuntersScraper.Shared.Models;

namespace PuntersScraper.Tests;

/// <summary>Verifies the RabbitMQ wire payload — the producer identity fields are present, and
/// every pre-existing v1 field still serializes unchanged.</summary>
public class MeetingScrapedEventPayloadTests
{
    private static readonly ProducerIdentity Producer = new(
        MachineGuid: "0b5c4a2e-1f3d-4e6a-9b8c-7d2e1f0a3b4c",
        MachineName: "RACE-PC-01",
        UserName: "scraper",
        ApplicationVersion: "3.18.0");

    private static Meeting SampleMeeting() => new()
    {
        Id = "12345",
        Name = "Flemington",
        MeetingDateLocal = "2026-09-05",
        MeetingDateUtc = "2026-09-04T14:00:00Z",
        State = "VIC",
        Venue = new Venue { Country = new Country { Iso3 = "AUS" } },
        Events = { new RaceEvent(), new RaceEvent() },
    };

    private static JsonElement SerializeToJson(MeetingScrapedEvent evt) =>
        JsonDocument.Parse(RabbitMqMeetingEventPublisher.SerializeBody(evt)).RootElement;

    [Fact]
    public void Create_CopiesProducerIdentityOntoEvent()
    {
        var evt = MeetingScrapedEvent.Create(
            Discipline.Horses, SampleMeeting(), Guid.NewGuid(), producer: Producer);

        Assert.Equal(Producer.MachineGuid, evt.MachineGuid);
        Assert.Equal(Producer.MachineName, evt.MachineName);
        Assert.Equal(Producer.UserName, evt.UserName);
        Assert.Equal(Producer.ApplicationVersion, evt.ApplicationVersion);
    }

    [Fact]
    public void Payload_ContainsProducerIdentityFields()
    {
        var json = SerializeToJson(MeetingScrapedEvent.Create(
            Discipline.Horses, SampleMeeting(), Guid.NewGuid(), producer: Producer));

        Assert.Equal(Producer.MachineGuid, json.GetProperty("machineGuid").GetString());
        Assert.Equal(Producer.MachineName, json.GetProperty("machineName").GetString());
        Assert.Equal(Producer.UserName, json.GetProperty("userName").GetString());
        Assert.Equal(Producer.ApplicationVersion, json.GetProperty("applicationVersion").GetString());
    }

    [Fact]
    public void Payload_KeepsExistingFieldsUnchanged()
    {
        var correlationId = Guid.NewGuid();
        var evt = MeetingScrapedEvent.Create(
            Discipline.Horses, SampleMeeting(), correlationId, priority: 7,
            meetingFileName: "flemington-20260905-TR-meeting.json", meetingSlug: "flemington-20260905",
            producer: Producer);
        var json = SerializeToJson(evt);

        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(evt.EventId, json.GetProperty("eventId").GetString());
        Assert.Equal("meeting.scraped", json.GetProperty("eventType").GetString());
        Assert.Equal("PuntersScraper", json.GetProperty("source").GetString());
        Assert.True(json.TryGetProperty("occurredAtUtc", out _));
        Assert.Equal(7, json.GetProperty("priority").GetInt32());
        Assert.Equal("Horses", json.GetProperty("discipline").GetString());
        Assert.Equal("T", json.GetProperty("disciplineCode").GetString());
        Assert.Equal("12345", json.GetProperty("meetingId").GetString());
        Assert.Equal("Flemington", json.GetProperty("meetingName").GetString());
        Assert.Equal("flemington-20260905-TR-meeting.json", json.GetProperty("meetingFileName").GetString());
        Assert.Equal("flemington-20260905", json.GetProperty("meetingSlug").GetString());
        Assert.Equal("2026-09-05", json.GetProperty("meetingDateLocal").GetString());
        Assert.Equal("2026-09-04T14:00:00Z", json.GetProperty("meetingDateUtc").GetString());
        Assert.Equal("VIC", json.GetProperty("venueState").GetString());
        Assert.Equal("AUS", json.GetProperty("venueCountry").GetString());
        Assert.Equal(2, json.GetProperty("raceCount").GetInt32());
        Assert.Equal(correlationId.ToString(), json.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void Create_WithoutProducer_LeavesIdentityFieldsNull()
    {
        var json = SerializeToJson(MeetingScrapedEvent.Create(
            Discipline.Greyhounds, SampleMeeting(), Guid.NewGuid()));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("machineGuid").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("machineName").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("userName").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("applicationVersion").ValueKind);
    }
}
