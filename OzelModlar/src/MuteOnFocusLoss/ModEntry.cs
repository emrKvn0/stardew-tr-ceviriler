using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace MuteOnFocusLoss;

public class ModEntry : Mod
{
    private bool isMuted = false;

    public override void Entry(IModHelper helper)
    {
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (Game1.game1 == null)
            return;

        bool active = Game1.game1.IsActive;

        if (!active && !isMuted)
        {
            Mute();
            isMuted = true;
        }
        else if (active && isMuted)
        {
            Unmute();
            isMuted = false;
        }
    }

    private void Mute()
    {
        try
        {
            Game1.soundCategory?.SetVolume(0f);
            Game1.musicCategory?.SetVolume(0f);
            Game1.ambientCategory?.SetVolume(0f);
            Game1.footstepCategory?.SetVolume(0f);
        }
        catch (Exception ex)
        {
            Monitor.Log("Failed to mute: " + ex.Message, LogLevel.Trace);
        }
    }

    private void Unmute()
    {
        try
        {
            if (Game1.options != null)
            {
                Game1.soundCategory?.SetVolume(Game1.options.soundVolumeLevel);
                Game1.musicCategory?.SetVolume(Game1.options.musicVolumeLevel);
                Game1.ambientCategory?.SetVolume(Game1.options.ambientVolumeLevel);
                Game1.footstepCategory?.SetVolume(Game1.options.footstepVolumeLevel);
            }
        }
        catch (Exception ex)
        {
            Monitor.Log("Failed to unmute: " + ex.Message, LogLevel.Trace);
        }
    }
}
