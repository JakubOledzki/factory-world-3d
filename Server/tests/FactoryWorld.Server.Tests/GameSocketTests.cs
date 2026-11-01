using System.Net;

namespace FactoryWorld.Server.Tests;

public sealed class GameSocketTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task Health_is_ok()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Hello_with_current_protocol_gets_welcome()
    {
        using var client = await factory.ConnectAsync();

        var reply = await client.SendRawAsync("""{"type":"hello","id":"1","payload":{"protocol":1}}""");

        Assert.Equal("welcome", reply.Type());
        Assert.Equal("1", reply.GetProperty("id").GetString());
        Assert.Equal(1, reply.Payload().GetProperty("protocol").GetInt32());
    }

    [Fact]
    public async Task Hello_with_old_protocol_is_rejected()
    {
        using var client = await factory.ConnectAsync();

        var reply = await client.SendRawAsync("""{"type":"hello","id":"1","payload":{"protocol":0}}""");

        Assert.Equal("protocol_mismatch", reply.ErrorCode());
    }

    [Fact]
    public async Task Ping_gets_pong_with_server_time()
    {
        using var client = await factory.ConnectAsync();

        var reply = await client.SendRawAsync("""{"type":"ping","id":"7"}""");

        Assert.Equal("pong", reply.Type());
        Assert.Equal("7", reply.GetProperty("id").GetString());
        Assert.True(reply.Payload().TryGetProperty("serverTime", out _));
    }

    [Theory]
    [InlineData("not json", "bad_json")]
    [InlineData("""{"id":"1"}""", "bad_envelope")]
    [InlineData("""{"type":"teleport","id":"1"}""", "unknown_type")]
    [InlineData("""{"type":"character.get","id":"1"}""", "not_logged_in")]
    public async Task Bad_messages_get_an_error_and_keep_the_connection(string frame, string code)
    {
        using var client = await factory.ConnectAsync();

        var reply = await client.SendRawAsync(frame);
        Assert.Equal(code, reply.ErrorCode());

        var pong = await client.SendRawAsync("""{"type":"ping"}""");
        Assert.Equal("pong", pong.Type());
    }
}
