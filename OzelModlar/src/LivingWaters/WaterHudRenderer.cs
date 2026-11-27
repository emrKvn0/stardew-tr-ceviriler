using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Menus;
using StardewValley.Tools;

namespace LivingWaters;

internal sealed class WaterHudRenderer
{
    private sealed record FishRow(
        string QualifiedId,
        string Name,
        int Count,
        bool IsProtected,
        bool IsStocked,
        bool IsMigrated,
        bool IsInSeason);

    private readonly EcosystemManager Ecosystem;
    private readonly Func<ModConfig> GetConfig;
    private readonly IInputHelper Input;

    public WaterHudRenderer(EcosystemManager ecosystem, Func<ModConfig> getConfig, IInputHelper input)
    {
        this.Ecosystem = ecosystem;
        this.GetConfig = getConfig;
        this.Input = input;
    }

    public void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        var cfg = this.GetConfig();
        if (!cfg.EnableMod || !cfg.ShowTooltipOnWaterHover || !Context.IsWorldReady || !Game1.displayHUD || Game1.activeClickableMenu is not null || Game1.eventUp)
            return;

        GameLocation? loc = Game1.currentLocation;
        Farmer player = Game1.player;
        if (loc is null || player is null)
            return;

        bool holdingRod = player.CurrentTool is FishingRod;
        StardewValley.Object? heldFish = player.ActiveObject is { } obj
            && obj.Category == StardewValley.Object.FishCategory
            && this.Ecosystem.IsValidSwimmingFish(obj.QualifiedItemId)
            ? obj
            : null;
        bool holdingFish = heldFish is not null;

        if (!holdingRod && !holdingFish)
            return;

        if (!holdingFish && holdingRod && cfg.RequireShiftForRodTooltip
            && !this.Input.IsDown(SButton.LeftShift) && !this.Input.IsDown(SButton.RightShift))
        {
            return;
        }

        Vector2 cursorTile = Game1.currentCursorTile;
        if (!EcosystemManager.IsWaterTileSafe(loc, (int)MathF.Floor(cursorTile.X), (int)MathF.Floor(cursorTile.Y)))
            return;

        string zoneKey = EcosystemManager.GetZoneKey(loc, cursorTile);
        ZonePopulation? zone = this.Ecosystem.GetOrInitZone(loc, cursorTile);
        if (zone is null)
            return;

        try
        {
            if (holdingFish)
                this.DrawCompactReleaseHint(e.SpriteBatch, cfg, loc, cursorTile, zone, player, heldFish!);
            else if (!this.Ecosystem.IsPlayerNearWaterBody(loc, cursorTile, player.Tile, 5.5f))
                this.DrawApproachWaterHint(e.SpriteBatch, zoneKey);
            else
                this.DrawPopulationPanel(e.SpriteBatch, cfg, loc, zoneKey, zone, player, holdingFish: false);
        }
        catch
        {
            // Never disrupt HUD rendering on transient font/texture states
        }
    }

    private void DrawApproachWaterHint(SpriteBatch b, string zoneKey)
    {
        string titleText = EcosystemManager.GetDisplayLocationName(zoneKey);
        const string primaryText = "Suyun nüfusunu görmek için yakına gelin";
        const string secondaryText = "Yaklaşıp suyun üstünde F6 ile detaylı günlüğü açın";

        SpriteFont font = Game1.smallFont;
        const int padX = 16;
        const int padY = 12;

        Vector2 tSize = font.MeasureString(titleText);
        Vector2 pSize = font.MeasureString(primaryText);
        Vector2 sSize = font.MeasureString(secondaryText);

        int textW = (int)Math.Ceiling(Math.Max(tSize.X, Math.Max(pSize.X, sSize.X)));
        int boxWidth = padX * 2 + textW;
        int boxHeight = padY * 2 + (int)Math.Ceiling(tSize.Y) + (int)Math.Ceiling(pSize.Y) + (int)Math.Ceiling(sSize.Y) + 8;

        int vpW = Game1.uiViewport.Width;
        int vpH = Game1.uiViewport.Height;
        int mx = Game1.getMouseX(ui_scale: true);
        int my = Game1.getMouseY(ui_scale: true);
        int boxX = mx + 24;
        int boxY = my + 20;

        if (boxX + boxWidth > vpW - 12)
            boxX = mx - boxWidth - 16;
        if (boxY + boxHeight > vpH - 12)
            boxY = my - boxHeight - 14;

        boxX = Math.Clamp(boxX, 12, Math.Max(12, vpW - boxWidth - 12));
        boxY = Math.Clamp(boxY, 12, Math.Max(12, vpH - boxHeight - 12));

        IClickableMenu.drawTextureBox(
            b,
            Game1.menuTexture,
            new Rectangle(0, 256, 60, 60),
            boxX,
            boxY,
            boxWidth,
            boxHeight,
            Color.White,
            1f,
            drawShadow: true);

        int textX = boxX + padX;
        int curY = boxY + padY;
        Utility.drawTextWithShadow(b, titleText, font, new Vector2(textX, curY), new Color(74, 42, 18));
        curY += (int)Math.Ceiling(tSize.Y) + 2;
        Utility.drawTextWithShadow(b, primaryText, font, new Vector2(textX, curY), new Color(106, 68, 36));
        curY += (int)Math.Ceiling(pSize.Y) + 2;
        Utility.drawTextWithShadow(b, secondaryText, font, new Vector2(textX, curY), new Color(36, 98, 134));
    }

    private void DrawCompactReleaseHint(
        SpriteBatch b,
        ModConfig cfg,
        GameLocation loc,
        Vector2 cursorTile,
        ZonePopulation zone,
        Farmer player,
        StardewValley.Object heldFish)
    {
        if (loc.objects.ContainsKey(cursorTile))
            return;

        string zoneKey = EcosystemManager.GetZoneKey(loc, cursorTile);
        var profile = this.Ecosystem.GetWaterCapacityProfile(loc, zoneKey);
        string qid = EcosystemManager.NormalizeItemId(heldFish.QualifiedItemId);
        int cur = zone.SpeciesCounts.GetValueOrDefault(qid, 0);
        int max = Math.Max(1, profile.MaxPerSpecies);
        int totalInZone = zone.SpeciesCounts.Values.Sum();
        bool tooFar = Vector2.Distance(player.Tile, cursorTile) > 5.5f;
        bool isBoss = this.Ecosystem.IsBossOrLegendaryFish(qid);
        bool isSpeciesFull = cur >= max;
        bool isPondFull = totalInZone >= profile.MaxTotalFish;

        string primaryText;
        Color primaryColor;
        if (isBoss)
        {
            primaryText = "Efsanevi balık salınamaz";
            primaryColor = new Color(158, 52, 34);
        }
        else if (isSpeciesFull)
        {
            primaryText = $"Bu suda tür kapasitesine ulaştı ({max}/{max})";
            primaryColor = new Color(142, 86, 12);
        }
        else if (isPondFull)
        {
            primaryText = $"Bu su toplam kapasiteye ulaştı ({totalInZone}/{profile.MaxTotalFish})";
            primaryColor = new Color(142, 86, 12);
        }
        else if (tooFar)
        {
            primaryText = "Suya bırakmak için biraz yaklaş";
            primaryColor = new Color(106, 68, 36);
        }
        else
        {
            primaryText = "Suya bırak (Sağ Tık)";
            primaryColor = new Color(24, 118, 48);
        }

        string secondaryText = cur == 1
            ? $"{heldFish.DisplayName} - Bu suda: {cur}/{max} (1 tane daha salınca ürer)"
            : $"{heldFish.DisplayName} - Bu suda: {cur}/{max}  |  Su: {totalInZone}/{profile.MaxTotalFish}";

        SpriteFont font = Game1.smallFont;
        const int padX = 16;
        const int padY = 12;
        const int iconSize = 24;

        Vector2 pSize = font.MeasureString(primaryText);
        Vector2 sSize = font.MeasureString(secondaryText);

        int textW = (int)Math.Ceiling(Math.Max(pSize.X, sSize.X));
        int boxWidth = padX * 2 + iconSize + 10 + textW;
        int boxHeight = padY * 2 + (int)Math.Ceiling(pSize.Y) + (int)Math.Ceiling(sSize.Y) + 4;

        int vpW = Game1.uiViewport.Width;
        int vpH = Game1.uiViewport.Height;
        int mx = Game1.getMouseX(ui_scale: true);
        int my = Game1.getMouseY(ui_scale: true);
        int boxX = mx + 24;
        int boxY = my + 20;

        if (boxX + boxWidth > vpW - 12)
            boxX = mx - boxWidth - 16;
        if (boxY + boxHeight > vpH - 12)
            boxY = my - boxHeight - 14;

        boxX = Math.Clamp(boxX, 12, Math.Max(12, vpW - boxWidth - 12));
        boxY = Math.Clamp(boxY, 12, Math.Max(12, vpH - boxHeight - 12));

        IClickableMenu.drawTextureBox(
            b,
            Game1.menuTexture,
            new Rectangle(0, 256, 60, 60),
            boxX,
            boxY,
            boxWidth,
            boxHeight,
            Color.White,
            1f,
            drawShadow: true);

        int iconX = boxX + padX;
        int iconY = boxY + (boxHeight - iconSize) / 2;
        ParsedItemData? data = ItemRegistry.GetData(qid);
        if (data is not null)
            b.Draw(data.GetTexture(), new Rectangle(iconX, iconY, iconSize, iconSize), data.GetSourceRect(), Color.White);

        int textX = iconX + iconSize + 10;
        Utility.drawTextWithShadow(b, primaryText, font, new Vector2(textX, boxY + padY), primaryColor);
        Utility.drawTextWithShadow(b, secondaryText, font, new Vector2(textX, boxY + padY + (int)Math.Ceiling(pSize.Y) + 2), new Color(86, 54, 28));
    }

    private void DrawPopulationPanel(
        SpriteBatch b,
        ModConfig cfg,
        GameLocation loc,
        string zoneKey,
        ZonePopulation zone,
        Farmer player,
        bool holdingFish)
    {
        bool IsInSeasonNow(string qid) =>
            (loc is Farm && cfg.FarmAllowsAllSeasons) || this.Ecosystem.IsFishInSeasonForZone(loc.Name, qid);

        var stocked = zone.PlayerStockedSpecies
            .Select(qid => (Qid: qid, Count: zone.SpeciesCounts.GetValueOrDefault(qid, 0)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .Select(x => new FishRow(
                x.Qid,
                ItemRegistry.GetData(x.Qid)?.DisplayName ?? x.Qid,
                x.Count,
                this.Ecosystem.IsProtectedFish(player, x.Qid),
                true,
                false,
                IsInSeasonNow(x.Qid)))
            .ToList();

        var natural = zone.SpeciesCounts
            .Where(kv => !zone.PlayerStockedSpecies.Contains(kv.Key) && kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv =>
            {
                bool migrated = zone.SpeciesDetails.TryGetValue(kv.Key, out var h) && h.Origin == "Migration";
                return new FishRow(
                    kv.Key,
                    ItemRegistry.GetData(kv.Key)?.DisplayName ?? kv.Key,
                    kv.Value,
                    this.Ecosystem.IsProtectedFish(player, kv.Key),
                    false,
                    migrated,
                    IsInSeasonNow(kv.Key));
            })
            .ToList();

        const int maxStockedShown = 6;
        const int maxNaturalShown = 8;
        var shownStocked = stocked.Take(maxStockedShown).ToList();
        var shownNatural = natural.Take(maxNaturalShown).ToList();
        int extraStocked = Math.Max(0, stocked.Count - shownStocked.Count);
        int extraNatural = Math.Max(0, natural.Count - shownNatural.Count);
        int totalFishCount = stocked.Sum(s => s.Count) + natural.Sum(n => n.Count);

        SpriteFont font = Game1.smallFont;
        const int padX = 22;
        const int padY = 20;
        const int iconSize = 28;
        const int rowHeight = 32;
        const int barWidth = 46;
        const int barHeight = 8;

        var profile = this.Ecosystem.GetWaterCapacityProfile(zoneKey);
        string titleText = $"Canlı Sular - {EcosystemManager.GetDisplayLocationName(zoneKey)}";
        string totalText = $"{totalFishCount}/{profile.MaxTotalFish} balık";
        string footerText = holdingFish
            ? "Sağ tık: balığı sal | Su üstünde F6: detaylı günlük"
            : "Bu suyun üstünde F6: detaylı su günlüğünü aç";

        float contentWidth = Math.Max(
            font.MeasureString(titleText).X + font.MeasureString(totalText).X + 44f,
            font.MeasureString(footerText).X);

        foreach (FishRow row in shownStocked)
        {
            string tag = !row.IsInSeason ? "Mevsim dışı" : row.Count >= 2 ? "Üreme hazır" : "Eş bekliyor";
            float w = iconSize + 10f + font.MeasureString(row.Name).X + 20f + barWidth + 10f + font.MeasureString($"{row.Count} ({tag})").X;
            if (w > contentWidth)
                contentWidth = w;
        }

        foreach (FishRow row in shownNatural)
        {
            string migTag = row.IsMigrated ? " [Göç]" : "";
            string protTag = row.IsProtected ? " [Korumalı]" : "";
            string seasonTag = !row.IsInSeason ? " [Mevsim dışı]" : "";
            float w = iconSize + 10f + font.MeasureString(row.Name).X + 20f + barWidth + 10f + font.MeasureString($"{row.Count}{migTag}{protTag}{seasonTag}").X;
            if (w > contentWidth)
                contentWidth = w;
        }

        int boxWidth = Math.Clamp((int)Math.Ceiling(contentWidth) + padX * 2, 330, Math.Max(340, Game1.uiViewport.Width - 32));

        int contentHeight = 34;
        if (shownStocked.Count > 0)
        {
            contentHeight += 24 + shownStocked.Count * rowHeight + (extraStocked > 0 ? 22 : 0) + 8;
        }
        if (shownNatural.Count > 0)
        {
            if (shownStocked.Count > 0)
                contentHeight += 8;
            contentHeight += 24 + shownNatural.Count * rowHeight + (extraNatural > 0 ? 22 : 0) + 6;
        }
        if (shownStocked.Count == 0 && shownNatural.Count == 0)
            contentHeight += 32;

        contentHeight += 26;
        int boxHeight = contentHeight + padY * 2;

        int vpW = Game1.uiViewport.Width;
        int vpH = Game1.uiViewport.Height;
        int mx = Game1.getMouseX(ui_scale: true);
        int my = Game1.getMouseY(ui_scale: true);
        int boxX = mx + 28;
        int boxY = my + 24;

        if (boxX + boxWidth > vpW - 14)
            boxX = mx - boxWidth - 20;
        if (boxY + boxHeight > vpH - 14)
            boxY = my - boxHeight - 16;

        boxX = Math.Clamp(boxX, 12, Math.Max(12, vpW - boxWidth - 12));
        boxY = Math.Clamp(boxY, 12, Math.Max(12, vpH - boxHeight - 12));

        IClickableMenu.drawTextureBox(
            b,
            Game1.menuTexture,
            new Rectangle(0, 256, 60, 60),
            boxX,
            boxY,
            boxWidth,
            boxHeight,
            Color.White,
            1f,
            drawShadow: true);

        int innerX = boxX + padX;
        int innerW = boxWidth - padX * 2;
        int curY = boxY + padY;

        b.Draw(Game1.staminaRect, new Rectangle(innerX - 6, curY - 4, innerW + 12, 28), new Color(214, 166, 102));
        Utility.drawTextWithShadow(b, titleText, font, new Vector2(innerX, curY - 1), new Color(74, 42, 18));
        Vector2 totalSize = font.MeasureString(totalText);
        Utility.drawTextWithShadow(b, totalText, font, new Vector2(innerX + innerW - totalSize.X, curY - 1), new Color(72, 52, 34));

        curY += 28;
        b.Draw(Game1.staminaRect, new Rectangle(innerX - 4, curY, innerW + 8, 2), new Color(138, 82, 36) * 0.55f);
        curY += 8;

        int maxCap = Math.Max(1, profile.MaxPerSpecies);

        if (shownStocked.Count > 0)
        {
            Utility.drawTextWithShadow(b, "Salınan Türler", font, new Vector2(innerX, curY), new Color(24, 102, 74));
            curY += 24;

            foreach (FishRow row in shownStocked)
            {
                this.DrawFishRow(b, font, row, innerX, curY, innerW, iconSize, barWidth, barHeight, maxCap);
                curY += rowHeight;
            }

            if (extraStocked > 0)
            {
                Utility.drawTextWithShadow(b, $"+{extraStocked} salınan tür daha", font, new Vector2(innerX + iconSize + 10, curY - 2), new Color(95, 70, 45));
                curY += 22;
            }

            curY += 4;
        }

        if (shownNatural.Count > 0)
        {
            if (shownStocked.Count > 0)
            {
                b.Draw(Game1.staminaRect, new Rectangle(innerX, curY, innerW, 1), new Color(138, 82, 36) * 0.45f);
                curY += 7;
            }

            Utility.drawTextWithShadow(b, "Doğal ve Göçen Türler", font, new Vector2(innerX, curY), new Color(92, 56, 24));
            curY += 24;

            foreach (FishRow row in shownNatural)
            {
                this.DrawFishRow(b, font, row, innerX, curY, innerW, iconSize, barWidth, barHeight, maxCap);
                curY += rowHeight;
            }

            if (extraNatural > 0)
            {
                Utility.drawTextWithShadow(b, $"+{extraNatural} tür daha (tümü F6'da)", font, new Vector2(innerX + iconSize + 10, curY - 2), new Color(95, 70, 45));
                curY += 22;
            }

            curY += 4;
        }

        if (shownStocked.Count == 0 && shownNatural.Count == 0)
        {
            string emptyTooltip = loc is Farm && cfg.EmptyFarmStart
                ? "Boş çiftlik suyu (balık salarak canlandır)."
                : "Bu suda kayıtlı tür yok.";
            Utility.drawTextWithShadow(b, emptyTooltip, font, new Vector2(innerX, curY), Game1.textColor);
            curY += 30;
        }

        b.Draw(Game1.staminaRect, new Rectangle(innerX - 4, curY, innerW + 8, 1), new Color(138, 82, 36) * 0.45f);
        curY += 5;

        Color footerColor = holdingFish ? new Color(20, 112, 54) : new Color(36, 98, 134);
        Utility.drawTextWithShadow(b, footerText, font, new Vector2(innerX, curY), footerColor);
    }

    private void DrawFishRow(
        SpriteBatch b,
        SpriteFont font,
        FishRow row,
        int x,
        int y,
        int width,
        int iconSize,
        int barWidth,
        int barHeight,
        int maxCap)
    {
        ParsedItemData? data = ItemRegistry.GetData(row.QualifiedId);
        if (data is not null)
        {
            b.Draw(data.GetTexture(), new Rectangle(x, y + 1, iconSize, iconSize), data.GetSourceRect(), Color.White);
        }

        int nameX = x + iconSize + 10;
        Utility.drawTextWithShadow(b, row.Name, font, new Vector2(nameX, y + 3), Game1.textColor);

        string statusText;
        Color statusColor;
        Color barFillColor;

        if (row.IsStocked)
        {
            bool ready = row.Count >= 2;
            string statusLabel = !row.IsInSeason ? "Mevsim dışı" : ready ? "Üreme hazır" : "Eş bekliyor";
            statusText = $"{row.Count} ({statusLabel})";
            statusColor = !row.IsInSeason
                ? new Color(130, 82, 56)
                : ready ? new Color(24, 118, 48) : new Color(168, 86, 12);
            barFillColor = !row.IsInSeason
                ? new Color(165, 130, 102)
                : ready ? new Color(62, 168, 86) : new Color(224, 148, 44);
        }
        else
        {
            string mig = row.IsMigrated ? " [Göç]" : "";
            string prot = row.IsProtected ? " [Korumalı]" : "";
            string season = !row.IsInSeason ? " [Mevsim dışı]" : "";
            statusText = $"{row.Count}{mig}{prot}{season}";
            statusColor = !row.IsInSeason
                ? new Color(130, 82, 56)
                : row.IsProtected
                    ? new Color(142, 86, 12)
                    : row.IsMigrated
                        ? new Color(26, 108, 164)
                        : new Color(56, 42, 30);
            barFillColor = row.IsMigrated ? new Color(46, 142, 208) : new Color(56, 148, 204);
        }

        Vector2 statusSize = font.MeasureString(statusText);
        int statusX = x + width - (int)statusSize.X;
        Utility.drawTextWithShadow(b, statusText, font, new Vector2(statusX, y + 3), statusColor);

        int barX = statusX - barWidth - 10;
        int barY = y + (iconSize - barHeight) / 2 + 1;
        b.Draw(Game1.staminaRect, new Rectangle(barX, barY, barWidth, barHeight), new Color(100, 62, 28) * 0.35f);

        float fillRatio = Math.Clamp((float)row.Count / maxCap, 0.08f, 1f);
        int fillW = Math.Max(3, (int)Math.Round(barWidth * fillRatio));
        b.Draw(Game1.staminaRect, new Rectangle(barX + 1, barY + 1, Math.Max(2, fillW - 2), barHeight - 2), barFillColor);
    }
}
