namespace Advanced_Combat_Tracker
{
    // The satellite-owned seam that carries the facade's host-routed service calls up the bridge (P6).
    // The facade (Fct.Compat.Act) holds no pipe or host reference, so the satellite (Fct.LegacyHost)
    // implements this over its bridge writer and installs it on FormActMain.ServiceRoute. Null in a
    // dev-standalone run and in unit tests, where TTS/PlaySound fall back to the local delegate slots.
    // Primitive-only signatures keep the facade free of any Fct.Abstractions dependency (the host maps
    // the channel byte back onto AudioChannel).
    public interface IHostServiceRoute
    {
        void Speak(string text, int volume, int channel, bool synchronous);
        void PlaySound(string filePath, int volume);
    }
}
