using Bifurcate.Core;

namespace Bifurcate.Tests;

public class HostRoutePlannerTests
{
    private const string Corp = "10.50.0.0/16";
    private const string Sql = "sql.example.com";
    private const string Gateway = "192.0.2.10/32";
    private const string GatewayB = "192.0.2.11/32";

    [Fact]
    public void ANewResolutionIsAddedToTheProfile()
    {
        HostRoutePlan plan = Plan(
            profile: [Corp],
            resolutions: [new HostResolution(Sql, [Gateway])]);

        Assert.Equal([Gateway], plan.ToAdd);
        Assert.Empty(plan.ToRemove);
        Assert.Empty(plan.Unresolved);
        Assert.Equal([Gateway], plan.NextState[Sql]);
    }

    [Fact]
    public void APrefixAlreadyOnTheProfileIsNotAddedAgain()
    {
        HostRoutePlan plan = Plan(
            profile: [Corp, Gateway],
            resolutions: [new HostResolution(Sql, [Gateway])]);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToRemove);
    }

    [Fact]
    public void AChangedAddressKeepsTheOldPrefixSoACachedClientDoesNotFallOffTheTunnel()
    {
        HostRoutePlan plan = Plan(
            profile: [Corp, Gateway],
            previous: SqlState(Gateway),
            resolutions: [new HostResolution(Sql, [GatewayB])]);

        Assert.Equal([GatewayB], plan.ToAdd);
        Assert.Empty(plan.ToRemove);
        Assert.Equal([Gateway, GatewayB], plan.NextState[Sql]);
    }

    [Fact]
    public void AnExpiredRememberedPrefixIsRemovedWhenItIsStillOnTheLedger()
    {
        HostRoutePlan plan = Plan(
            profile: [Corp, Gateway, GatewayB],
            previous: SqlState(GatewayB),
            resolutions: [new HostResolution(Sql, [GatewayB])],
            managed: new Dictionary<string, string[]> { [Sql] = [Gateway, GatewayB] });

        Assert.Empty(plan.ToAdd);
        Assert.Equal([Gateway], plan.ToRemove);
        Assert.Equal([GatewayB], plan.NextState[Sql]);
    }

    [Fact]
    public void ForgetExtrasDropsRememberedPrefixesThatAreNotInDnsNow()
    {
        HostRoutePlan plan = Plan(
            profile: [Corp, Gateway, GatewayB],
            previous: [],
            resolutions: [new HostResolution(Sql, [GatewayB])],
            managed: new Dictionary<string, string[]> { [Sql] = [Gateway, GatewayB] });

        Assert.Empty(plan.ToAdd);
        Assert.Equal([Gateway], plan.ToRemove);
        Assert.Equal([GatewayB], plan.NextState[Sql]);
    }

    [Fact]
    public void AFailedLookupKeepsTheLastKnownPrefixSoTheDestinationDoesNotFallOffTheTunnel()
    {
        HostRoutePlan plan = Plan(
            profile: [Corp, Gateway],
            previous: SqlState(Gateway),
            resolutions: [new HostResolution(Sql, [])]);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToRemove);
        Assert.Empty(plan.Unresolved);
        Assert.Equal([Gateway], plan.NextState[Sql]);
    }

    [Fact]
    public void AHostThatHasNeverResolvedIsReported()
    {
        HostRoutePlan plan = Plan(resolutions: [new HostResolution(Sql, [])]);

        Assert.Empty(plan.ToAdd);
        Assert.Equal([Sql], plan.Unresolved);
        Assert.Empty(plan.NextState);
    }

    [Fact]
    public void RemovingAHostDropsOnlyThePrefixesThisToolAdded()
    {
        HostRoutePlan plan = Plan(
            hosts: [],
            profile: [Corp, Gateway],
            previous: SqlState(Gateway),
            resolutions: []);

        Assert.Empty(plan.ToAdd);
        Assert.Equal([Gateway], plan.ToRemove);
        Assert.Empty(plan.NextState);
    }

    [Fact]
    public void AUserConfiguredSubnetIsNeverRemovedEvenIfItMatchesAFormerHostRoute()
    {
        HostRoutePlan plan = Plan(
            routes: [Corp, Gateway],
            hosts: [],
            profile: [Corp, Gateway],
            previous: SqlState(Gateway),
            resolutions: []);

        Assert.Empty(plan.ToRemove);
    }

    [Fact]
    public void ExtraProfileRoutesTheUserOrVpnAddedAreLeftAlone()
    {
        const string other = "198.51.100.9/32";

        HostRoutePlan plan = Plan(
            profile: [Corp, Gateway, other],
            previous: SqlState(Gateway),
            resolutions: [new HostResolution(Sql, [Gateway])]);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToRemove);
    }

    [Fact]
    public void TwoHostsSharingAnAddressDoNotDropItWhenOnlyOneHostGoesAway()
    {
        const string other = "app.example.com";

        HostRoutePlan plan = Plan(
            hosts: [other],
            profile: [Corp, Gateway],
            previous: new Dictionary<string, string[]>
            {
                [Sql] = [Gateway],
                [other] = [Gateway],
            },
            resolutions: [new HostResolution(other, [Gateway])]);

        Assert.Empty(plan.ToRemove);
        Assert.True(plan.NextState.ContainsKey(other));
        Assert.False(plan.NextState.ContainsKey(Sql));
    }

    [Fact]
    public void HostMatchingIsCaseInsensitive()
    {
        HostRoutePlan plan = Plan(
            hosts: ["SQL.Example.COM"],
            profile: [Corp, Gateway],
            previous: new Dictionary<string, string[]> { ["sql.example.com"] = [Gateway] },
            resolutions: [new HostResolution("SQL.Example.COM", [])]);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToRemove);
        Assert.Equal([Gateway], plan.NextState["SQL.Example.COM"]);
    }

    private static Dictionary<string, string[]> SqlState(params string[] prefixes) =>
        new() { [Sql] = prefixes };

    private static HostRoutePlan Plan(
        string[]? routes = null,
        string[]? hosts = null,
        string[]? profile = null,
        Dictionary<string, string[]>? previous = null,
        HostResolution[]? resolutions = null,
        Dictionary<string, string[]>? managed = null) =>
        HostRoutePlanner.Plan(
            routes ?? [Corp],
            hosts ?? [Sql],
            profile ?? [Corp],
            previous ?? new Dictionary<string, string[]>(),
            resolutions ?? [],
            managed);
}
