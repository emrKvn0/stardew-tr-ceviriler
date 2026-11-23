using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.ItemTypeDefinitions;

namespace LivingWaters;

internal sealed class FishReleaseAnimator
{
    private sealed class FlyingFish
    {
        public GameLocation Location { get; init; } = null!;
        public string QualifiedItemId { get; init; } = string.Empty;
        public Vector2 StartWorld { get; init; }
        public Vector2 EndWorld { get; init; }
        public Vector2 TargetTile { get; init; }
        public float ArcPeak { get; init; }
        public float Duration { get; init; }
        public float Elapsed { get; set; }
    }

    private sealed class SplashDroplet
    {
        public GameLocation Location { get; init; } = null!;
        public Vector2 WorldPos { get; set; }
        public Vector2 Velocity { get; set; }
        public float Elapsed { get; set; }
        public float Duration { get; init; }
        public float Scale { get; init; }
    }

    private readonly VisibleFishBridge VisibleFish;
    private readonly IMonitor Monitor;
    private readonly List<FlyingFish> ActiveFlights = new();
    private readonly List<SplashDroplet> ActiveDroplets = new();

    public FishReleaseAnimator(IMonitor monitor, VisibleFishBridge visibleFish)
    {
        this.Monitor = monitor;
        this.VisibleFish = visibleFish;
    }

    public void Clear()
    {
        this.ActiveFlights.Clear();
        this.ActiveDroplets.Clear();
    }

    public void LaunchFish(GameLocation location, Farmer player, Vector2 targetTile, string qid)
    {
        Vector2 startWorld = player.Position + new Vector2(32f, -52f);
        Vector2 endWorld = targetTile * 64f + new Vector2(32f, 32f);
        float dist = Vector2.Distance(startWorld, endWorld);
        float arcPeak = Math.Clamp(dist * 0.36f, 52f, 118f);

        this.ActiveFlights.Add(new FlyingFish
        {
            Location = location,
            QualifiedItemId = qid,
            StartWorld = startWorld,
            EndWorld = endWorld,
            TargetTile = targetTile,
            ArcPeak = arcPeak,
            Duration = 0.42f
        });

        try
        {
            Game1.playSound("dwop");
        }
        catch
        {
            // Ignore sound errors if audio device is busy
        }
    }

    public void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
        {
            if (this.ActiveFlights.Count > 0 || this.ActiveDroplets.Count > 0)
                this.Clear();
            return;
        }

        float dt = (float)Game1.currentGameTime.ElapsedGameTime.TotalSeconds;
        if (dt <= 0f || dt > 0.25f)
            dt = 1f / 60f;

        for (int i = this.ActiveFlights.Count - 1; i >= 0; i--)
        {
            FlyingFish flight = this.ActiveFlights[i];
            if (!ReferenceEquals(flight.Location, Game1.currentLocation))
            {
                this.ActiveFlights.RemoveAt(i);
                continue;
            }

            flight.Elapsed += dt;
            if (flight.Elapsed >= flight.Duration)
            {
                this.ActiveFlights.RemoveAt(i);
                this.OnSplashImpact(flight);
            }
        }

        for (int i = this.ActiveDroplets.Count - 1; i >= 0; i--)
        {
            SplashDroplet drop = this.ActiveDroplets[i];
            if (!ReferenceEquals(drop.Location, Game1.currentLocation))
            {
                this.ActiveDroplets.RemoveAt(i);
                continue;
            }

            drop.Elapsed += dt;
            if (drop.Elapsed >= drop.Duration)
            {
                this.ActiveDroplets.RemoveAt(i);
                continue;
            }

            drop.WorldPos += drop.Velocity * dt;
            drop.Velocity = new Vector2(drop.Velocity.X * 0.96f, drop.Velocity.Y + 420f * dt);
        }
    }

    private void OnSplashImpact(FlyingFish flight)
    {
        try
        {
            Game1.playSound("dropItemInWater");
            EcosystemManager.SpawnWaterRipple(flight.Location, flight.TargetTile);
            this.SpawnSplashDroplets(flight.Location, flight.EndWorld);
            this.VisibleFish.OnFishReleasedLive(flight.Location, flight.TargetTile, flight.QualifiedItemId);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Suya sıçrama efekti hatası: {ex.Message}", LogLevel.Trace);
        }
    }

    private void SpawnSplashDroplets(GameLocation location, Vector2 impactWorld)
    {
        for (int i = 0; i < 7; i++)
        {
            float vx = (float)(Game1.random.NextDouble() * 140.0 - 70.0);
            float vy = (float)(-95.0 - Game1.random.NextDouble() * 90.0);
            this.ActiveDroplets.Add(new SplashDroplet
            {
                Location = location,
                WorldPos = impactWorld + new Vector2(0f, -6f),
                Velocity = new Vector2(vx, vy),
                Duration = 0.28f + (float)Game1.random.NextDouble() * 0.12f,
                Scale = 4f + (float)Game1.random.NextDouble() * 3f
            });
        }
    }

    public void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.currentLocation is null)
            return;

        if (this.ActiveFlights.Count == 0 && this.ActiveDroplets.Count == 0)
            return;

        try
        {
            foreach (FlyingFish flight in this.ActiveFlights)
            {
                if (!ReferenceEquals(flight.Location, Game1.currentLocation))
                    continue;

                float t = Math.Clamp(flight.Elapsed / flight.Duration, 0f, 1f);
                Vector2 worldPos = Vector2.Lerp(flight.StartWorld, flight.EndWorld, t);
                worldPos.Y -= MathF.Sin(t * MathF.PI) * flight.ArcPeak;

                ParsedItemData? data = ItemRegistry.GetDataOrErrorItem(flight.QualifiedItemId);
                if (data is null)
                    continue;

                Texture2D texture = data.GetTexture();
                Rectangle sourceRect = data.GetSourceRect();
                Vector2 origin = new(sourceRect.Width / 2f, sourceRect.Height / 2f);
                Vector2 screenPos = Game1.GlobalToLocal(Game1.viewport, worldPos);

                bool movingLeft = flight.EndWorld.X < flight.StartWorld.X;
                float tilt = MathHelper.Lerp(-0.45f, 0.95f, t);
                float rotation = movingLeft ? -tilt : tilt;
                SpriteEffects flip = movingLeft ? SpriteEffects.FlipHorizontally : SpriteEffects.None;

                // Shrink slightly and fade smoothly in the final 15% as the fish plunges under the surface
                float scale = MathHelper.Lerp(3.4f, 2.3f, t);
                float alpha = t > 0.85f ? MathHelper.Lerp(1f, 0.35f, (t - 0.85f) / 0.15f) : 1f;

                // Subtle water-drop shadow on the target tile during flight
                Vector2 shadowScreen = Game1.GlobalToLocal(Game1.viewport, flight.EndWorld + new Vector2(0f, 8f));
                float shadowScale = MathHelper.Lerp(0.8f, 1.8f, MathF.Sin(t * MathF.PI));
                e.SpriteBatch.Draw(
                    Game1.shadowTexture,
                    shadowScreen,
                    Game1.shadowTexture.Bounds,
                    Color.White * (0.45f * (1f - t * 0.4f)),
                    0f,
                    new Vector2(Game1.shadowTexture.Width / 2f, Game1.shadowTexture.Height / 2f),
                    shadowScale,
                    SpriteEffects.None,
                    0.99f);

                e.SpriteBatch.Draw(
                    texture,
                    screenPos,
                    sourceRect,
                    Color.White * alpha,
                    rotation,
                    origin,
                    scale,
                    flip,
                    0.995f);
            }

            foreach (SplashDroplet drop in this.ActiveDroplets)
            {
                if (!ReferenceEquals(drop.Location, Game1.currentLocation))
                    continue;

                float t = Math.Clamp(drop.Elapsed / drop.Duration, 0f, 1f);
                Vector2 screenPos = Game1.GlobalToLocal(Game1.viewport, drop.WorldPos);
                Color color = Color.Lerp(new Color(215, 245, 255), new Color(110, 195, 235), t) * (1f - t);

                e.SpriteBatch.Draw(
                    Game1.staminaRect,
                    new Rectangle((int)screenPos.X, (int)screenPos.Y, (int)drop.Scale, (int)drop.Scale),
                    color);
            }
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Balık uçuş çizim hatası: {ex.Message}", LogLevel.Trace);
        }
    }
}
