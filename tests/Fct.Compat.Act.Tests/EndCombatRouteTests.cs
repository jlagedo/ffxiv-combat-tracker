using System;
using Advanced_Combat_Tracker;
using Xunit;

namespace Fct.Compat.Act.Tests
{
    // ISOLATION-PLAN P9a: the facade's EndCombat route-up gate. With RouteEndCombatUp off (producer +
    // dev-standalone) EndCombat ends the local replica and fires CombatEndRaised in-band; with it on
    // (a consumer satellite) EndCombat routes UP through the installed ServiceRoute and does NOT end the
    // local replica — the host ends the authoritative encounter and fans EndCombatRequested back down,
    // which the satellite applies via EndCombatLocal. These pin both halves plugin-free.
    //
    // RouteEndCombatUp / ServiceRoute are static facade state; each test resets them in a finally so a
    // leaked flag can't route another test's EndCombat up a null/foreign route.
    [Collection("facade-static-state")]
    public class EndCombatRouteTests
    {
        private sealed class FakeRoute : IHostServiceRoute
        {
            public int EndCombatCalls;
            public bool LastExport;
            public void Speak(string text, int volume, int channel, bool synchronous) { }
            public void PlaySound(string filePath, int volume) { }
            public void RegisterCallback(string name, bool allowDuplicate) { }
            public void UnregisterCallback(string name) { }
            public void InvokeCallback(string name, object argument) { }
            public void EndCombat(bool export) { EndCombatCalls++; LastExport = export; }
        }

        [Fact]
        public void With_route_up_off_EndCombat_applies_locally_and_fires_CombatEndRaised()
        {
            var prevFlag = FormActMain.RouteEndCombatUp;
            var prevRoute = FormActMain.ServiceRoute;
            try
            {
                FormActMain.RouteEndCombatUp = false;
                FormActMain.ServiceRoute = new FakeRoute();   // present but must be ignored when flag is off
                var act = new FormActMain { CurrentZone = "Zone" };
                bool ended = false;
                act.CombatEndRaised += _ => ended = true;

                act.SetEncounter(DateTime.Now, "You", "Target");
                Assert.True(act.InCombat);
                act.EndCombat(true);

                Assert.False(act.InCombat);   // local replica ended
                Assert.True(ended);           // in-band producer-forward fired
                Assert.Equal(0, ((FakeRoute)FormActMain.ServiceRoute).EndCombatCalls);   // did NOT route up
            }
            finally
            {
                FormActMain.RouteEndCombatUp = prevFlag;
                FormActMain.ServiceRoute = prevRoute;
            }
        }

        [Fact]
        public void With_route_up_on_EndCombat_routes_up_and_does_not_end_the_local_replica()
        {
            var prevFlag = FormActMain.RouteEndCombatUp;
            var prevRoute = FormActMain.ServiceRoute;
            try
            {
                var route = new FakeRoute();
                FormActMain.RouteEndCombatUp = true;
                FormActMain.ServiceRoute = route;
                var act = new FormActMain { CurrentZone = "Zone" };
                bool ended = false;
                act.CombatEndRaised += _ => ended = true;

                act.SetEncounter(DateTime.Now, "You", "Target");
                Assert.True(act.InCombat);
                act.EndCombat(true);

                Assert.Equal(1, route.EndCombatCalls);   // routed up
                Assert.True(route.LastExport);
                Assert.True(act.InCombat);               // NOT ended locally — waits for the fan-back
                Assert.False(ended);                     // CombatEndRaised not fired on the route-up path

                // The fan-back (Program.FoldConsume calls EndCombatLocal) ends the replica without re-routing.
                act.EndCombatLocal(true);
                Assert.False(act.InCombat);
                Assert.True(ended);
                Assert.Equal(1, route.EndCombatCalls);   // no extra route-up from the fan-back
            }
            finally
            {
                FormActMain.RouteEndCombatUp = prevFlag;
                FormActMain.ServiceRoute = prevRoute;
            }
        }
    }
}
