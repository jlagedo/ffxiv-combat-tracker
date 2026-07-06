using System;
using System.Collections.Generic;
using System.Reflection;
using Advanced_Combat_Tracker;
using Xunit;

namespace Fct.Compat.Act.Tests
{
    // ISOLATION-PLAN P9a: pin the reflectable-multicast-field shape of BeforeLogLineRead. Hojoring
    // (LogBuffer.cs) and OverlayPlugin's LogParseOverlay rebuild the facade's private event backing
    // field via reflection — GetField the delegate, GetInvocationList, unhook every handler, insert
    // their own FIRST, then re-add the originals — casting each handler to LogLineEventDelegate. This
    // only works if BeforeLogLineRead is a field-like event (compiler-backed private field of the exact
    // name, of a MulticastDelegate type). This test reproduces LogBuffer.cs's exact sequence and asserts
    // the inserted handler runs first with the originals' relative order preserved, so a refactor that
    // turned the event into a custom add/remove accessor (which has no such backing field) is caught.
    public class BeforeLogLineReadReflectionTests
    {
        [Fact]
        public void BeforeLogLineRead_backing_field_is_reflectable_and_insert_first_preserves_order()
        {
            var act = new FormActMain();
            var order = new List<string>();

            LogLineEventDelegate a = (_, __) => order.Add("A");
            LogLineEventDelegate b = (_, __) => order.Add("B");
            act.BeforeLogLineRead += a;
            act.BeforeLogLineRead += b;

            // The exact reflection Hojoring's LogBuffer.cs:102 performs.
            var fi = act.GetType().GetField(
                "BeforeLogLineRead",
                BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.GetField | BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(fi);   // field-like event → a backing field of the exact name exists

            var del = fi.GetValue(act) as Delegate;
            Assert.NotNull(del);
            var handlers = del.GetInvocationList();
            Assert.Equal(2, handlers.Length);

            // Unhook every handler, insert our probe FIRST, then re-add the originals in order.
            foreach (var handler in handlers)
                act.BeforeLogLineRead -= (LogLineEventDelegate)handler;

            LogLineEventDelegate probe = (_, __) => order.Add("PROBE");
            act.BeforeLogLineRead += probe;

            foreach (var handler in handlers)
                act.BeforeLogLineRead += (LogLineEventDelegate)handler;

            var args = new LogLineEventArgs("00|line", 0, DateTime.Now, "Zone", false);
            act.FireBeforeLogLineRead(false, args);

            // Probe ran first; the originals kept their relative order.
            Assert.Equal(new[] { "PROBE", "A", "B" }, order.ToArray());
        }
    }
}
