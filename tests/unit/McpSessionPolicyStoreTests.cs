using System.Text.Json.Nodes;
using ModelContextProtocol;
using TeamsPhoneMcp.Core.Policy;

namespace TeamsPhoneMcp.UnitTests;

public class McpSessionPolicyStoreTests
{
    [Fact]
    public void Initialize_RejectsWhatIfModeWhenTheTransportIssuesNoSessionId()
    {
        var store = new McpSessionPolicyStore();

        // Accepting this would hand back a session that executes writes to a caller that asked
        // for one that cannot: the protocol revision that drops Mcp-Session-Id must not silently
        // shed the ceiling (issue #28).
        var exception = Assert.Throws<McpException>(
            () => store.Initialize(sessionId: null, Parameters(whatIfMode: true)));

        Assert.Contains("what-if", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Initialize_AllowsAnUnrestrictedSessionWhenTheTransportIssuesNoSessionId()
    {
        var store = new McpSessionPolicyStore();

        store.Initialize(sessionId: null, Parameters(whatIfMode: false));

        Assert.False(store.IsWhatIfMode(null));
    }

    [Fact]
    public void Initialize_ValidatesTheFlagTypeEvenWithoutASessionId()
    {
        var store = new McpSessionPolicyStore();
        var parameters = new JsonObject
        {
            ["_meta"] = new JsonObject { ["whatIfMode"] = "yes" },
        };

        Assert.Throws<McpException>(() => store.Initialize(sessionId: null, parameters));
    }

    [Fact]
    public void IsWhatIfMode_ReportsTheCeilingForAClaimedSession()
    {
        var store = new McpSessionPolicyStore();

        store.Initialize("session-a", Parameters(whatIfMode: true));
        store.Initialize("session-b", Parameters(whatIfMode: false));

        Assert.True(store.IsWhatIfMode("session-a"));
        Assert.False(store.IsWhatIfMode("session-b"));
        Assert.False(store.IsWhatIfMode("session-unknown"));
    }

    private static JsonObject Parameters(bool whatIfMode) => new()
    {
        ["_meta"] = new JsonObject { ["whatIfMode"] = whatIfMode },
    };
}
