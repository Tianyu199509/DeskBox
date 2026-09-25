using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FileSessionRegistrationTests
{
    [Fact]
    public void ExactReplayIsIdempotent_ButNewContentOnSameHostReplacesSession()
    {
        var (registration, byId) = Create();
        var host = new FakeHost();
        object firstContent = new();
        var first = new FakeSession(host, firstContent);
        Assert.True(registration.RegisterOrReplace("file-a", first));
        Assert.False(registration.RegisterOrReplace("file-a", new(host, firstContent)));
        Assert.Same(first, byId["file-a"]);

        var replacement = new FakeSession(host, new());
        Assert.True(registration.RegisterOrReplace("file-a", replacement));
        Assert.False(registration.UnregisterIfMatch("file-a", first));
        Assert.Same(replacement, byId["file-a"]);
        Assert.Equal(["file-a"], registration.UnregisterHost(host));
        Assert.Empty(byId);
    }

    [Fact]
    public void NewHostCanReplaceId_WithoutOldHostClosingTheNewSession()
    {
        var (registration, byId) = Create();
        var oldHost = new FakeHost();
        var newHost = new FakeHost();
        var old = new FakeSession(oldHost, new());
        var replacement = new FakeSession(newHost, new());
        registration.RegisterOrReplace("file-a", old);
        registration.RegisterOrReplace("file-a", replacement);

        Assert.Empty(registration.UnregisterHost(oldHost));
        Assert.False(registration.UnregisterIfMatch("file-a", old));
        Assert.Same(replacement, byId["file-a"]);
    }

    [Fact]
    public void HostHasOnlyOneStandaloneAlias_GroupCommitAndDetachCanRebindIt()
    {
        var (registration, byId) = Create();
        var host = new FakeHost();
        var first = new FakeSession(host, new());
        registration.RegisterOrReplace("old-member", first);
        var candidate = new FakeSession(host, new());

        Assert.Throws<InvalidOperationException>(() =>
            registration.RegisterOrReplace("new-member", candidate));
        Assert.False(registration.UnregisterIfMatch("new-member", candidate));
        Assert.Same(first, byId["old-member"]);
        Assert.Equal(["old-member"], registration.UnregisterHost(host));

        Assert.True(registration.RegisterOrReplace("new-member", candidate));
        Assert.False(byId.ContainsKey("old-member"));
        Assert.Same(candidate, byId["new-member"]);
        registration.Clear();
        registration.Clear();
        Assert.Empty(byId);
    }

    private static (FileSessionRegistration<FakeSession, FakeHost> Registration,
        Dictionary<string, FakeSession> ById) Create()
    {
        var byId = new Dictionary<string, FakeSession>(StringComparer.Ordinal);
        return (new(byId, session => session.Host, session => session.Content), byId);
    }

    private sealed class FakeHost { }
    private sealed record FakeSession(FakeHost Host, object Content);
}
