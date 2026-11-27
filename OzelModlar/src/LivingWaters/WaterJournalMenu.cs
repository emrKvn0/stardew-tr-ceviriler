using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Menus;

namespace LivingWaters;

internal sealed class WaterJournalMenu : IClickableMenu
{
    private sealed class JournalEntry
    {
        public string QualifiedId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int Count { get; init; }
        public int CaughtToday { get; init; }
        public bool IsProtected { get; init; }
        public bool IsStocked { get; init; }
        public bool IsInSeason { get; init; }
        public SpeciesHistory History { get; init; } = new();
    }

    private sealed class TabItem
    {
        public string Label { get; init; } = string.Empty;
        public string? ZoneKey { get; init; } // null => Valley / Migration overview tab
        public Rectangle Bounds { get; set; }
    }

    private readonly EcosystemManager Ecosystem;
    private readonly ModConfig Config;
    private readonly string TargetZoneKey;
    private readonly WaterBodyInfo? InspectedWaterBody;
    private readonly WaterCapacityProfile InspectedProfile;
    private readonly List<(string ZoneKey, ZonePopulation Zone)> Zones = new();
    private readonly List<(string ZoneKey, ZonePopulation Zone)> DiscoveredZones;
    private readonly List<TabItem> Tabs = new();

    private int SelectedTabIndex;
    private List<JournalEntry> CurrentEntries = new();
    private int ScrollOffset;
    private int MaxScrollOffset;

    private Rectangle ListViewport;
    private Rectangle UpButtonRect;
    private Rectangle DownButtonRect;

    private static readonly RasterizerState ScissorRasterizer = new() { ScissorTestEnable = true };
    private const int CardHeight = 110;
    private const int CardGap = 8;

    // Warm Stardew-native palette (premultiplied-safe opaque colors + float multipliers)
    private static readonly Color HeaderBg = new(226, 176, 110);
    private static readonly Color HeaderBorder = new(138, 82, 36);
    private static readonly Color CardBg = new(246, 214, 156);
    private static readonly Color CardBorder = new(164, 106, 52);
    private static readonly Color InkDark = new(74, 42, 18);
    private static readonly Color InkMuted = new(106, 68, 36);
    private static readonly Color AccentStocked = new(34, 134, 78);
    private static readonly Color AccentMigration = new(38, 114, 168);
    private static readonly Color AccentNatural = new(146, 92, 38);
    private static readonly Color AccentGold = new(186, 114, 18);

    public WaterJournalMenu(
        EcosystemManager ecosystem,
        ModConfig config,
        GameLocation? currentLoc,
        string targetZoneKey)
        : base(0, 0, 0, 0, showUpperRightCloseButton: true)
    {
        this.Ecosystem = ecosystem;
        this.Config = config;
        this.TargetZoneKey = targetZoneKey;
        this.InspectedWaterBody = currentLoc is not null
            ? EcosystemManager.GetWaterBodyByZoneKey(currentLoc, targetZoneKey)
            : null;
        this.InspectedProfile = this.Ecosystem.GetWaterCapacityProfile(currentLoc, targetZoneKey);

        ZonePopulation inspectedZone = this.Ecosystem.GetZoneByKey(targetZoneKey) ?? new ZonePopulation();
        this.Zones.Add((targetZoneKey, inspectedZone));
        this.DiscoveredZones = this.Ecosystem.GetJournalZones(currentLoc);

        this.BuildTabs();
        this.UpdateLayout();
        this.RebuildEntries();
    }

    private void BuildTabs()
    {
        this.Tabs.Clear();

        string label = EcosystemManager.GetDisplayLocationName(this.TargetZoneKey);
        if (label.Length > 42)
            label = label[..41] + ".";

        this.Tabs.Add(new TabItem
        {
            Label = label,
            ZoneKey = this.TargetZoneKey
        });

        this.Tabs.Add(new TabItem
        {
            Label = "Su Ağı ve Ayarlar",
            ZoneKey = null
        });

        this.SelectedTabIndex = 0;
    }

    private void UpdateLayout()
    {
        int vpW = Game1.uiViewport.Width;
        int vpH = Game1.uiViewport.Height;

        this.width = Math.Clamp(vpW - 60, 740, 980);
        this.height = Math.Clamp(vpH - 60, 500, 690);

        Vector2 topLeft = Utility.getTopLeftPositionForCenteringOnScreen(this.width, this.height);
        this.xPositionOnScreen = (int)topLeft.X;
        this.yPositionOnScreen = (int)topLeft.Y;

        this.initializeUpperRightCloseButton();

        int tabBarHeight = 44;
        int headerHeight = 86;
        int footerHeight = 34;

        this.ListViewport = new Rectangle(
            this.xPositionOnScreen + 28,
            this.yPositionOnScreen + 24 + tabBarHeight + headerHeight,
            this.width - 80,
            this.height - 48 - tabBarHeight - headerHeight - footerHeight);

        int scrollX = this.ListViewport.Right + 10;
        this.UpButtonRect = new Rectangle(scrollX, this.ListViewport.Y, 32, 32);
        this.DownButtonRect = new Rectangle(scrollX, this.ListViewport.Bottom - 32, 32, 32);

        this.RebuildTabBounds();
        this.RecalculateScrollLimits();
    }

    private void RebuildTabBounds()
    {
        int curX = this.xPositionOnScreen + 28;
        int curY = this.yPositionOnScreen + 22;
        int maxRight = this.xPositionOnScreen + this.width - 72;

        for (int i = 0; i < this.Tabs.Count; i++)
        {
            TabItem tab = this.Tabs[i];
            int w = (int)Math.Ceiling(Game1.smallFont.MeasureString(tab.Label).X) + 22;
            if (curX + w > maxRight)
            {
                tab.Bounds = Rectangle.Empty;
                continue;
            }

            tab.Bounds = new Rectangle(curX, curY, w, 36);
            curX += w + 8;
        }
    }

    private TabItem? ActiveTab =>
        this.SelectedTabIndex >= 0 && this.SelectedTabIndex < this.Tabs.Count
            ? this.Tabs[this.SelectedTabIndex]
            : null;

    private (string ZoneKey, ZonePopulation Zone)? ActiveZone
    {
        get
        {
            string? zk = this.ActiveTab?.ZoneKey;
            if (zk is null)
                return null;
            int idx = this.Zones.FindIndex(z => z.ZoneKey.Equals(zk, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? this.Zones[idx] : null;
        }
    }

    private void RebuildEntries()
    {
        this.CurrentEntries.Clear();
        this.ScrollOffset = 0;

        if (this.ActiveZone is not { } az)
        {
            this.MaxScrollOffset = 0;
            return;
        }

        string zoneKey = az.ZoneKey;
        ZonePopulation zone = az.Zone;
        string locName = EcosystemManager.GetLocationNameFromZoneKey(zoneKey);
        bool isFarm = locName.Equals("Farm", StringComparison.OrdinalIgnoreCase);
        Farmer player = Game1.player;

        foreach (var kv in zone.SpeciesCounts.Where(kv => kv.Value > 0))
        {
            string qid = kv.Key;
            var hist = EcosystemManager.GetOrCreateHistory(zone, qid);
            bool stocked = zone.PlayerStockedSpecies.Contains(qid) || hist.Origin == "Stocked";
            bool inSeason = (isFarm && this.Config.FarmAllowsAllSeasons)
                || this.Ecosystem.IsFishInSeasonForZone(locName, qid);

            this.CurrentEntries.Add(new JournalEntry
            {
                QualifiedId = qid,
                Name = ItemRegistry.GetData(qid)?.DisplayName ?? qid,
                Count = kv.Value,
                CaughtToday = zone.DailyCaughtToday.GetValueOrDefault(qid, 0),
                IsProtected = this.Ecosystem.IsProtectedFish(player, qid),
                IsStocked = stocked,
                IsInSeason = inSeason,
                History = hist
            });
        }

        this.CurrentEntries = this.CurrentEntries
            .OrderByDescending(e => e.IsStocked)
            .ThenByDescending(e => e.History.Origin == "Migration")
            .ThenByDescending(e => e.Count)
            .ThenBy(e => e.Name)
            .ToList();

        this.RecalculateScrollLimits();
    }

    private void RecalculateScrollLimits()
    {
        int totalHeight = this.CurrentEntries.Count * (CardHeight + CardGap);
        if (totalHeight > 0)
            totalHeight -= CardGap;

        this.MaxScrollOffset = Math.Max(0, totalHeight - this.ListViewport.Height);
        this.ScrollOffset = Math.Clamp(this.ScrollOffset, 0, this.MaxScrollOffset);
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        this.UpdateLayout();
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            Game1.playSound("bigDeSelect");
            this.exitThisMenu();
            return;
        }

        base.receiveKeyPress(key);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (this.MaxScrollOffset <= 0)
            return;

        int step = 52;
        this.ScrollOffset = direction > 0
            ? Math.Max(0, this.ScrollOffset - step)
            : Math.Min(this.MaxScrollOffset, this.ScrollOffset + step);
        Game1.playSound("shiny4");
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        for (int i = 0; i < this.Tabs.Count; i++)
        {
            if (this.Tabs[i].Bounds != Rectangle.Empty && this.Tabs[i].Bounds.Contains(x, y) && i != this.SelectedTabIndex)
            {
                this.SelectedTabIndex = i;
                Game1.playSound("smallSelect");
                this.RebuildEntries();
                return;
            }
        }

        if (this.UpButtonRect.Contains(x, y) && this.ScrollOffset > 0)
        {
            this.ScrollOffset = Math.Max(0, this.ScrollOffset - (CardHeight + CardGap));
            Game1.playSound("shiny4");
            return;
        }

        if (this.DownButtonRect.Contains(x, y) && this.ScrollOffset < this.MaxScrollOffset)
        {
            this.ScrollOffset = Math.Min(this.MaxScrollOffset, this.ScrollOffset + (CardHeight + CardGap));
            Game1.playSound("shiny4");
        }
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.65f);

        IClickableMenu.drawTextureBox(
            b,
            Game1.menuTexture,
            new Rectangle(0, 256, 60, 60),
            this.xPositionOnScreen,
            this.yPositionOnScreen,
            this.width,
            this.height,
            Color.White,
            1f,
            drawShadow: true);

        this.drawTabs(b);

        int headerY = this.yPositionOnScreen + 24 + 44;
        this.drawHeader(b, headerY);

        if (this.ActiveTab?.ZoneKey is null)
            this.drawOverviewTab(b);
        else
            this.drawEntryList(b);

        this.drawScrollbar(b);

        string footer = "Tekerlek: Kaydır  |  Suya Yaklaşıp Üstünde F6: Su Günlüğü  |  F6 / ESC: Kapat";
        Utility.drawTextWithShadow(
            b,
            footer,
            Game1.smallFont,
            new Vector2(this.xPositionOnScreen + 28, this.yPositionOnScreen + this.height - 38),
            InkMuted);

        base.draw(b);
        this.drawMouse(b);
    }

    private void drawTabs(SpriteBatch b)
    {
        for (int i = 0; i < this.Tabs.Count; i++)
        {
            TabItem tab = this.Tabs[i];
            if (tab.Bounds == Rectangle.Empty)
                continue;

            Rectangle rect = tab.Bounds;
            bool active = i == this.SelectedTabIndex;

            Color bgColor = active ? new Color(34, 114, 152) : new Color(214, 166, 102);
            Color borderColor = active ? new Color(20, 74, 108) : HeaderBorder;
            Color textColor = active ? Color.White : InkDark;

            b.Draw(Game1.staminaRect, rect, borderColor);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), bgColor);
            Utility.drawTextWithShadow(b, tab.Label, Game1.smallFont, new Vector2(rect.X + 11, rect.Y + 6), textColor);
        }
    }

    private void drawHeader(SpriteBatch b, int headerY)
    {
        int x = this.xPositionOnScreen + 28;
        int w = this.width - 56;

        b.Draw(Game1.staminaRect, new Rectangle(x, headerY, w, 74), HeaderBorder);
        b.Draw(Game1.staminaRect, new Rectangle(x + 2, headerY + 2, w - 4, 70), HeaderBg);

        if (this.ActiveZone is not { } az)
        {
            Utility.drawTextWithShadow(
                b,
                "Canlı Sular - Su Ağı ve Ekoloji Ayarları",
                Game1.dialogueFont,
                new Vector2(x + 14, headerY + 6),
                InkDark);

            string overviewSub = TrimToWidth(
                Game1.smallFont,
                $"İncelenen Su: {EcosystemManager.GetDisplayLocationName(this.TargetZoneKey)}  |  Keşfedilen Su: {this.DiscoveredZones.Count} havuz",
                w - 28);
            Utility.drawTextWithShadow(b, overviewSub, Game1.smallFont, new Vector2(x + 14, headerY + 42), InkMuted);
            return;
        }

        string zoneName = EcosystemManager.GetDisplayLocationName(az.ZoneKey);
        int totalFish = this.CurrentEntries.Sum(e => e.Count);
        int stockedTypes = this.CurrentEntries.Count(e => e.IsStocked);
        int migratedTypes = this.CurrentEntries.Count(e => !e.IsStocked && e.History.Origin == "Migration");
        int totalBornHere = this.CurrentEntries.Sum(e => e.History.TotalBorn);
        int totalCaughtHere = this.CurrentEntries.Sum(e => e.History.TotalCaught);

        string waterTypeInfo = this.InspectedWaterBody is { } body
            ? (body.TouchesMapEdge ? $"{this.InspectedProfile.SizeLabel} ({body.Tiles.Count} kare)" : $"{this.InspectedProfile.SizeLabel} ({body.Tiles.Count} kare)")
            : this.InspectedProfile.SizeLabel;

        Utility.drawTextWithShadow(
            b,
            TrimToWidth(Game1.dialogueFont, zoneName, w - 28),
            Game1.dialogueFont,
            new Vector2(x + 14, headerY + 6),
            InkDark);

        string summary = TrimToWidth(
            Game1.smallFont,
            $"{waterTypeInfo}  |  Tür: {this.CurrentEntries.Count}  |  Balık: {totalFish}/{this.InspectedProfile.MaxTotalFish}  |  Doğan: {totalBornHere}  |  Tutulan: {totalCaughtHere}",
            w - 28);
        Utility.drawTextWithShadow(b, summary, Game1.smallFont, new Vector2(x + 14, headerY + 42), InkMuted);
    }

    private void drawOverviewTab(SpriteBatch b)
    {
        int x = this.ListViewport.X;
        int y = this.ListViewport.Y;
        int w = this.ListViewport.Width;
        int textMaxW = Math.Max(200, w - 34);

        int DrawBox(int by, string title, string body, Color accent)
        {
            string wrapped = Game1.parseText(body, Game1.smallFont, textMaxW);
            int bodyH = (int)Math.Ceiling(Game1.smallFont.MeasureString(wrapped).Y);
            int bh = Math.Max(82, 46 + bodyH);

            b.Draw(Game1.staminaRect, new Rectangle(x, by, w, bh), CardBorder);
            b.Draw(Game1.staminaRect, new Rectangle(x + 2, by + 2, w - 4, bh - 4), CardBg);
            b.Draw(Game1.staminaRect, new Rectangle(x, by, 7, bh), accent);

            Utility.drawTextWithShadow(b, title, Game1.smallFont, new Vector2(x + 18, by + 10), accent);
            Utility.drawTextWithShadow(b, wrapped, Game1.smallFont, new Vector2(x + 18, by + 36), InkDark);
            return by + bh + 10;
        }

        int nextY = DrawBox(
            y,
            "Su Boyutuna Göre Ekoloji ve Göç Ağı",
            $"Küçük göletler doğal olarak daha az balık kapasitesine sahipken büyük göller ve akarsular çok daha kalabalık ekosistemler barındırır; bölgedeki tüm doğal türler suya yerleşebilir.\nYalnızca harita kenarına açılan akarsular göçle bağlıdır (gece %{this.Config.MigrationChancePercent} göç şansı).",
            AccentMigration);

        nextY = DrawBox(
            nextY,
            "Bu Suyun Kapasitesi ve Mod Oranları",
            $"Bu su ({this.InspectedProfile.SizeLabel}): Tür kapasitesi {this.InspectedProfile.MaxPerSpecies}  |  Toplam kapasite: {this.InspectedProfile.MaxTotalFish} balık\nÜreme: %{this.Config.BreedingChancePercent} (gece en fazla +3)  |  {this.InspectedProfile.QualityBonusThreshold}+ balıkta %30 şansla +1 yıldız  |  Korumalı tür: {(this.Config.ProtectNeededFish ? "Açık" : "Kapalı")}",
            AccentStocked);

        var stockedZones = this.DiscoveredZones
            .Where(z => z.Zone.PlayerStockedSpecies.Count > 0)
            .ToList();
        string stockedList = stockedZones.Count > 0
            ? string.Join(", ", stockedZones.Select(z => EcosystemManager.GetDisplayLocationName(z.ZoneKey)).Take(5))
            : "Henüz oyuncu tarafından balık salınan bir su yok";
        DrawBox(
            nextY,
            "Balık Salınan ve Keşfedilen Sular",
            $"Balık salınan sular ({stockedZones.Count}): {stockedList}\nToplam kayıtlı su havuzu: {this.DiscoveredZones.Count}. İstediğiniz suyun yanına yaklaşıp imleci üstüne getirerek F6 ile detayına bakabilirsiniz.",
            AccentNatural);
    }

    private void drawEntryList(SpriteBatch b)
    {
        if (this.CurrentEntries.Count == 0)
        {
            string emptyMsg = Game1.currentLocation is Farm && this.Config.EmptyFarmStart
                ? "Bu çiftlik suyunda henüz balık yok (başlangıçta boş)."
                : "Bu suda henüz kayıtlı bir balık türü yok.";
            Utility.drawTextWithShadow(
                b,
                emptyMsg,
                Game1.smallFont,
                new Vector2(this.ListViewport.X + 12, this.ListViewport.Y + 16),
                InkDark);

            if (Game1.currentLocation is Farm && this.Config.EmptyFarmStart)
            {
                string tip = this.InspectedWaterBody is { TouchesMapEdge: true }
                    ? "Eline balık alıp suya bırakarak üretebilir veya komşu nehirlerden göç bekleyebilirsin."
                    : "Kapalı çiftlik göleti: Eline bir balık alıp suya tıklayarak canlı popülasyon başlatabilirsin.";
                Utility.drawTextWithShadow(
                    b,
                    Game1.parseText(tip, Game1.smallFont, Math.Max(220, this.ListViewport.Width - 24)),
                    Game1.smallFont,
                    new Vector2(this.ListViewport.X + 12, this.ListViewport.Y + 48),
                    InkMuted);
            }
            return;
        }

        Rectangle clipRect = Rectangle.Intersect(b.GraphicsDevice.Viewport.Bounds, this.ListViewport);
        if (clipRect.Width <= 0 || clipRect.Height <= 0)
            return;

        Rectangle prevScissor = b.GraphicsDevice.ScissorRectangle;
        b.End();

        try
        {
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, ScissorRasterizer);
            b.GraphicsDevice.ScissorRectangle = clipRect;

            int maxCap = Math.Max(1, this.InspectedProfile.MaxPerSpecies);

            for (int i = 0; i < this.CurrentEntries.Count; i++)
            {
                int cardY = this.ListViewport.Y + i * (CardHeight + CardGap) - this.ScrollOffset;
                if (cardY + CardHeight < this.ListViewport.Y || cardY > this.ListViewport.Bottom)
                    continue;

                this.drawEntryCard(b, this.CurrentEntries[i], this.ListViewport.X, cardY, this.ListViewport.Width, maxCap);
            }

            b.End();
        }
        finally
        {
            b.GraphicsDevice.ScissorRectangle = prevScissor;
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);
        }
    }

    private void drawEntryCard(SpriteBatch b, JournalEntry entry, int x, int y, int width, int maxCap)
    {
        var hist = entry.History;

        // Crisp 2px wood border + warm Stardew golden-parchment card fill
        b.Draw(Game1.staminaRect, new Rectangle(x, y, width, CardHeight), CardBorder);
        b.Draw(Game1.staminaRect, new Rectangle(x + 2, y + 2, width - 4, CardHeight - 4), CardBg);

        Color accentColor = entry.IsStocked
            ? AccentStocked
            : hist.Origin == "Migration"
                ? AccentMigration
                : AccentNatural;
        b.Draw(Game1.staminaRect, new Rectangle(x, y, 7, CardHeight), accentColor);

        // Fish sprite slot (48x48)
        int iconBoxX = x + 18;
        int iconBoxY = y + 30;
        b.Draw(Game1.staminaRect, new Rectangle(iconBoxX - 3, iconBoxY - 3, 54, 54), HeaderBorder);
        b.Draw(Game1.staminaRect, new Rectangle(iconBoxX - 1, iconBoxY - 1, 50, 50), HeaderBg);

        ParsedItemData? itemData = ItemRegistry.GetData(entry.QualifiedId);
        if (itemData is not null)
            b.Draw(itemData.GetTexture(), new Rectangle(iconBoxX, iconBoxY, 48, 48), itemData.GetSourceRect(), Color.White);

        int textX = iconBoxX + 64;
        int row1Y = y + 10;
        int row2Y = y + 44;
        int row3Y = y + 74;

        // Right-aligned Count + Progress bar
        bool bonusActive = entry.Count >= this.InspectedProfile.QualityBonusThreshold;
        string countLabel = bonusActive ? $"{entry.Count}/{maxCap} [Yıldız+]" : $"{entry.Count}/{maxCap}";
        Color countColor = bonusActive ? AccentGold : InkDark;
        Vector2 countSize = Game1.smallFont.MeasureString(countLabel);
        int countX = x + width - (int)countSize.X - 14;
        Utility.drawTextWithShadow(b, countLabel, Game1.smallFont, new Vector2(countX, row1Y), countColor);

        int barW = 100;
        int barX = countX - barW - 10;
        int barY = row1Y + 8;
        b.Draw(Game1.staminaRect, new Rectangle(barX, barY, barW, 10), HeaderBorder * 0.45f);
        float ratio = Math.Clamp((float)entry.Count / maxCap, 0.06f, 1f);
        int fillW = Math.Max(4, (int)Math.Round(barW * ratio));
        Color fillCol = bonusActive ? new Color(218, 152, 26) : accentColor;
        b.Draw(Game1.staminaRect, new Rectangle(barX + 1, barY + 1, Math.Max(2, fillW - 2), 8), fillCol);

        // Row 1 left: Name + Badges (clamped so it never overlaps progress bar)
        int maxRow1Right = barX - 10;
        Utility.drawTextWithShadow(b, entry.Name, Game1.smallFont, new Vector2(textX, row1Y), InkDark);
        int cursorX = textX + (int)Math.Ceiling(Game1.smallFont.MeasureString(entry.Name).X) + 12;

        string originBadge = entry.IsStocked
            ? "Senin Saldığın"
            : hist.Origin == "Migration"
                ? $"Göç: {(!string.IsNullOrEmpty(hist.SourceLocation) ? EcosystemManager.GetDisplayLocationName(hist.SourceLocation) : "Komşu Su")}"
                : "Yerli Tür";

        cursorX = this.drawBadgeIfFits(b, originBadge, cursorX, row1Y, maxRow1Right, accentColor, Color.White);
        if (entry.IsProtected)
            this.drawBadgeIfFits(b, "Korumalı", cursorX + 6, row1Y, maxRow1Right, AccentGold, Color.White);

        int maxLineW = x + width - textX - 14;

        // Row 2: Origin date & Birth chronicle (compact & guaranteed to fit cleanly)
        string firstDate = EcosystemManager.FormatGameDate(hist.FirstSeenTotalDays);
        string originLine = hist.Origin == "Migration"
            ? $"Göç: {firstDate} ({hist.TotalMigratedIn} geldi)"
            : entry.IsStocked
                ? $"Salım: {firstDate} ({hist.TotalReleased} salındı)"
                : $"Kayıt: {firstDate}";

        string birthPart = hist.TotalBorn > 0
            ? $"Son doğum: {EcosystemManager.FormatGameDate(hist.LastBirthTotalDays)} ({hist.TotalBorn} yavru)"
            : "Henüz doğum yok";

        Utility.drawTextWithShadow(b, TrimToWidth(Game1.smallFont, $"{originLine}  |  {birthPart}", maxLineW), Game1.smallFont, new Vector2(textX, row2Y), InkDark);

        // Row 3: Breeding readiness, Migration threshold & Catch stats
        string breedStatus = entry.Count >= maxCap
            ? "Kapasite dolu"
            : !entry.IsInSeason
                ? "Mevsim dışı"
                : entry.Count < 2
                    ? "Eş bekliyor (1 tane daha sal)"
                    : $"Üreme: {entry.Count / 2} çift (gece %{this.Config.BreedingChancePercent})";

        int migThreshold = this.InspectedProfile.MigrationThreshold;
        string migStatus = this.InspectedWaterBody is { TouchesMapEdge: false }
            ? "Kapalı havuz (göç yok)"
            : entry.Count >= migThreshold ? $"Göç hazır ({migThreshold}+)" : $"Göç eşiği: {migThreshold}";
        string catchStatus = $"Tutulan: {hist.TotalCaught}" + (entry.CaughtToday > 0 ? $" (Bugün: {entry.CaughtToday})" : "");

        Utility.drawTextWithShadow(b, TrimToWidth(Game1.smallFont, $"{breedStatus}  |  {migStatus}  |  {catchStatus}", maxLineW), Game1.smallFont, new Vector2(textX, row3Y), InkMuted);
    }

    private int drawBadgeIfFits(SpriteBatch b, string text, int x, int y, int maxRight, Color bg, Color fg)
    {
        Vector2 size = Game1.smallFont.MeasureString(text);
        int w = (int)Math.Ceiling(size.X) + 14;
        int h = Math.Max(26, (int)Math.Ceiling(size.Y) + 2);
        if (x + w > maxRight)
            return x;

        b.Draw(Game1.staminaRect, new Rectangle(x, y - 1, w, h), bg);
        Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2(x + 7, y), fg);
        return x + w;
    }

    private static string TrimToWidth(SpriteFont font, string text, int maxWidth)
    {
        if (maxWidth <= 40 || font.MeasureString(text).X <= maxWidth)
            return text;

        string clipped = text;
        while (clipped.Length > 4 && font.MeasureString(clipped + "...").X > maxWidth)
            clipped = clipped[..^1];
        return clipped + "...";
    }

    private void drawScrollbar(SpriteBatch b)
    {
        if (this.MaxScrollOffset <= 0 || this.ActiveTab?.ZoneKey is null)
            return;

        int trackX = this.UpButtonRect.X + 8;
        int trackY = this.UpButtonRect.Bottom + 6;
        int trackH = Math.Max(20, this.DownButtonRect.Y - trackY - 6);

        b.Draw(Game1.staminaRect, new Rectangle(trackX, trackY, 16, trackH), HeaderBorder * 0.35f);

        float ratio = (float)this.ScrollOffset / this.MaxScrollOffset;
        int thumbH = Math.Max(26, (int)(trackH * 0.35f));
        int thumbY = trackY + (int)((trackH - thumbH) * ratio);
        b.Draw(Game1.staminaRect, new Rectangle(trackX + 2, thumbY, 12, thumbH), AccentMigration);

        b.Draw(
            Game1.mouseCursors,
            this.UpButtonRect,
            new Rectangle(421, 459, 11, 12),
            this.ScrollOffset > 0 ? Color.White : Color.White * 0.4f);

        b.Draw(
            Game1.mouseCursors,
            this.DownButtonRect,
            new Rectangle(421, 472, 11, 12),
            this.ScrollOffset < this.MaxScrollOffset ? Color.White : Color.White * 0.4f);
    }
}
