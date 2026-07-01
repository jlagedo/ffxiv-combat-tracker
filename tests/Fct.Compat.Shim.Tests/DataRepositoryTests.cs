using System;
using Fct.Abstractions;
using Fct.Abstractions.Testing;
using FFXIV_ACT_Plugin.Common;
using Xunit;

namespace Fct.Compat.Shim.Tests;

/// <summary>
/// D7: <see cref="DataRepository"/> projects the SDK pull-state surface over the modern
/// <c>IGameSnapshot</c>. Each call reads a fresh snapshot, so a recompiled OverlayPlugin/Hojoring
/// polling the repository sees live state.
/// </summary>
public class DataRepositoryTests
{
    private static DataRepository NewRepo(FakeSnapshot state, out FakePluginHost host)
    {
        host = new FakePluginHost(game: new FakeGameSession(state: state));
        return new DataRepository(host);
    }

    [Fact]
    public void GetCombatantList_projects_actors()
    {
        var state = new FakeSnapshot
        {
            Actors = new[] { FakeActors.Player(10, "Alice"), FakeActors.Player(20, "Bob") },
        };
        var repo = NewRepo(state, out _);

        var list = repo.GetCombatantList();

        Assert.Equal(2, list.Count);
        Assert.Equal(10u, list[0].ID);
        Assert.Equal("Alice", list[0].Name);
        Assert.Equal(20u, list[1].ID);
    }

    [Fact]
    public void Player_zone_and_client_come_from_the_snapshot()
    {
        var state = new FakeSnapshot
        {
            Player = FakeActors.Player(7, "Hero", job: 19),
            Zone = new ZoneRef(132, "Limsa Lominsa"),
        };
        var repo = NewRepo(state, out _);

        Assert.Equal(7u, repo.GetCurrentPlayerID());
        Assert.Equal(19u, repo.GetPlayer().JobID);
        Assert.Equal(132u, repo.GetCurrentTerritoryID());
        // FakeSnapshot's default client is Global / English / "0.0".
        Assert.Equal(Language.English, repo.GetSelectedLanguageID());
        Assert.Equal((byte)GameRegion.Global, repo.GetGameRegion());
        Assert.Equal("0.0", repo.GetGameVersion());
    }

    [Fact]
    public void GetResourceDictionary_maps_resource_type_to_the_catalog_kind()
    {
        var catalog = new FakeResourceCatalog();
        catalog.Set(ResourceKind.Status, 42, "Sprint");
        catalog.Set(ResourceKind.Action, 99, "Fire");
        var repo = NewRepo(new FakeSnapshot { Resources = catalog }, out _);

        var buffs = repo.GetResourceDictionary(ResourceType.BuffList_EN);
        Assert.Equal("Sprint", buffs[42]);

        var skills = repo.GetResourceDictionary(ResourceType.SkillList_JP);
        Assert.Equal("Fire", skills[99]);

        // MountList has no catalog kind → empty.
        Assert.Empty(repo.GetResourceDictionary(ResourceType.MountList_EN));
    }

    [Fact]
    public void Server_timestamp_comes_from_the_clock()
    {
        var repo = NewRepo(new FakeSnapshot(), out _);
        // FakeClock is fixed at 2024-01-01T00:00:00Z.
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), repo.GetServerTimestamp());
    }

    [Fact]
    public void Unsourced_members_return_honest_defaults()
    {
        var repo = NewRepo(new FakeSnapshot(), out _);

        Assert.Null(repo.GetCurrentFFXIVProcess());   // no net10 game handle
        Assert.Empty(repo.GetAntiVirusNames());
        Assert.True(repo.IsChatLogAvailable());
    }
}
