using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Xunit;

namespace Fct.Compat.Act.Tests
{
    // The remaining ACT-facade surface gaps: audio entry points (G-1), DpiScale (G-3),
    // corner-control round-trip (M4-3), and the spell-timer subsystem (G-2 / M4-2). These
    // exercise the binding/storage contract the unmodified plugins depend on.
    public class SpellTimerFacadeTests
    {
        // --- G-1: TTS/PlaySound route to the swappable delegate fields ---

        [Fact]
        public void TTS_routes_to_PlayTtsMethod()
        {
            var act = new FormActMain();
            string spoken = null;
            act.PlayTtsMethod = t => spoken = t;
            act.TTS("hello");
            Assert.Equal("hello", spoken);
        }

        [Fact]
        public void PlaySound_routes_to_PlaySoundMethod_with_volume_100()
        {
            var act = new FormActMain();
            string wav = null;
            int vol = -1;
            act.PlaySoundMethod = (w, v) => { wav = w; vol = v; };
            act.PlaySound(@"C:\x.wav");
            Assert.Equal(@"C:\x.wav", wav);
            Assert.Equal(100, vol);
        }

        [Fact]
        public void Audio_defaults_are_silent_noops()
        {
            var act = new FormActMain();
            // Default delegates are no-ops; calling through must not throw.
            act.TTS("x");
            act.PlaySound("x");
        }

        // --- G-3: DpiScale ---

        [Fact]
        public void DpiScale_is_one()
        {
            Assert.Equal(1f, new FormActMain().DpiScale);
        }

        // --- M4-3: corner control round-trip ---

        [Fact]
        public void CornerControl_add_then_remove_does_not_throw()
        {
            var act = new FormActMain();
            var c = new Button();
            act.CornerControlAdd(c);
            act.CornerControlRemove(c);
            // Removing an unknown control is a safe no-op.
            act.CornerControlRemove(new Button());
        }

        // --- G-2: oFormSpellTimers non-null + events bind ---

        [Fact]
        public void oFormSpellTimers_is_non_null_and_events_bind()
        {
            Assert.NotNull(ActGlobals.oFormSpellTimers);
            SpellTimerEventDelegate notify = _ => { };
            SpellTimerEventDelegate removed = _ => { };
            ActGlobals.oFormSpellTimers.OnSpellTimerNotify += notify;
            ActGlobals.oFormSpellTimers.OnSpellTimerRemoved += removed;
            ActGlobals.oFormSpellTimers.OnSpellTimerNotify -= notify;
            ActGlobals.oFormSpellTimers.OnSpellTimerRemoved -= removed;
        }

        // --- M4-2: TimerDefs storage round-trip + inert engine ---

        [Fact]
        public void TimerDefs_round_trip_by_key_prefix()
        {
            var form = new FormSpellTimers();
            var td = new TimerData("p_spell_x", "p_panel");

            form.AddEditTimerDef(td);
            var found = form.TimerDefs.Where(p => p.Key.StartsWith("p_")).Select(x => x.Value).ToList();
            Assert.Single(found);
            Assert.Same(td, found[0]);

            form.RemoveTimerDef(td);
            Assert.DoesNotContain(form.TimerDefs, p => p.Key.StartsWith("p_"));
        }

        [Fact]
        public void AddEditTimerDef_same_key_replaces()
        {
            var form = new FormSpellTimers();
            var a = new TimerData("p_spell_x", "p_panel") { TimerValue = 10 };
            var b = new TimerData("p_spell_x", "p_panel") { TimerValue = 20 };

            form.AddEditTimerDef(a);
            form.AddEditTimerDef(b);

            Assert.Single(form.TimerDefs);
            Assert.Equal(20, form.TimerDefs[b.Key].TimerValue);
        }

        [Fact]
        public void NotifySpell_and_RebuildSpellTreeView_are_inert()
        {
            var form = new FormSpellTimers();
            bool fired = false;
            form.OnSpellTimerNotify += _ => fired = true;

            form.RebuildSpellTreeView();
            form.NotifySpell("attacker", "spell", false, "victim", false);
            form.NotifySpell("attacker", "spell", false, "victim", false, new Dictionary<string, string>());

            Assert.False(fired);
        }

        [Fact]
        public void TimerData_key_is_lowercased_category_and_name()
        {
            var td = new TimerData("SpellName", "Category");
            Assert.Equal("category|spellname", td.Key);
        }
    }
}
