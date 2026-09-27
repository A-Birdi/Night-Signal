using UnityEngine;

namespace NightSignal.UI
{
    /// <summary>
    /// Turns drift scorer figures (banked, unbanked, lost, chain) into the HUD's drift readout, with a short note when a
    /// chain banks or is lost. Offline it reads the simulation directly; online it reads the server's per-driver drift
    /// message. Display only: the score that counts is the one the server reports at the end.
    /// </summary>
    public sealed class DriftHudFeed
    {
        long lastBanked, lastLost;
        float noteUntil;
        string note = "";

        public void Update(HudState hud, long banked, long unbanked, long lost, float chain, float now)
        {
            if (banked > lastBanked)
            {
                note = $"<color=#3EC6D8>BANKED +{banked - lastBanked:N0}</color>";
                noteUntil = now + 1.6f;
            }
            else if (lost > lastLost)
            {
                note = $"<color=#F2A541>CHAIN LOST  −{lost - lastLost:N0}</color>";
                noteUntil = now + 1.6f;
            }
            lastBanked = banked;
            lastLost = lost;
            hud.DriftEvent = true;
            hud.DriftBanked = banked;
            hud.DriftUnbanked = unbanked;
            hud.DriftChain = chain;
            hud.DriftNote = now < noteUntil ? note : "";
        }
    }
}
