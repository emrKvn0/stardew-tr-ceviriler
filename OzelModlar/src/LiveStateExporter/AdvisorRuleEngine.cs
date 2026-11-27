using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;

namespace LiveStateExporter;

internal sealed class AdvisorRulesDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("rules")]
    public List<AdvisorRule> Rules { get; set; } = new();
}

internal sealed class AdvisorRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Target section: mustDo | strategic | gate | modHighlight</summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = "strategic";

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 100;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public string Progress { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("minBudget")]
    public int? MinBudget { get; set; }

    [JsonPropertyName("showAsFutureGoal")]
    public bool ShowAsFutureGoal { get; set; }

    [JsonPropertyName("allOf")]
    public List<string> AllOf { get; set; } = new();

    [JsonPropertyName("anyOf")]
    public List<string> AnyOf { get; set; } = new();
}

internal sealed class RuleEvaluationResult
{
    public List<string> MustDoRules { get; } = new();
    public List<string> StrategicRules { get; } = new();
    public List<ProgressionGateEntry> GateRules { get; } = new();
    public List<ModEcosystemEntry> ModRules { get; } = new();
}

internal sealed class RuleRuntimeContext
{
    public IModRegistry ModRegistry { get; init; } = null!;
    public Farmer Player { get; init; } = null!;
    public Farm Farm { get; init; } = null!;
    public ModEntry.OwnedInventoryIndex Owned { get; init; } = null!;
    public Dictionary<string, double> Numbers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Strings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FarmBuildings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PlacedMachines { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> CustomSkills { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class AdvisorRuleEngine
{
    private static readonly Regex TokenRegex = new(@"\{([a-zA-Z0-9_.:-]+)\}", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly object CacheLock = new();
    private static DateTime _lastLoadedWriteUtc = DateTime.MinValue;
    private static string _lastLoadedPath = string.Empty;
    private static AdvisorRulesDocument? _cachedDoc;

    public static RuleEvaluationResult Evaluate(
        string modDir,
        IMonitor? monitor,
        RuleRuntimeContext ctx,
        bool forceReload)
    {
        var result = new RuleEvaluationResult();
        AdvisorRulesDocument? doc = LoadRules(modDir, monitor, forceReload);
        if (doc is null || doc.Rules.Count == 0)
            return result;

        foreach (AdvisorRule rule in doc.Rules.Where(r => r is not null && r.Enabled).OrderBy(r => r.Priority))
        {
            try
            {
                if (!MatchesAll(rule.AllOf, ctx))
                    continue;
                if (rule.AnyOf.Count > 0 && !MatchesAny(rule.AnyOf, ctx))
                    continue;

                bool budgetShort = rule.MinBudget.HasValue && ctx.Player.Money < rule.MinBudget.Value;
                if (budgetShort && !rule.ShowAsFutureGoal)
                    continue;

                string message = ResolveTokens(rule.Message, ctx);
                if (budgetShort && rule.ShowAsFutureGoal)
                    message += $" *(Bütçe hedefi: {rule.MinBudget!.Value:N0}g — şu an {ctx.Player.Money:N0}g var)*";

                string title = ResolveTokens(rule.Title, ctx);
                string category = ResolveTokens(rule.Category, ctx);
                string progress = ResolveTokens(rule.Progress, ctx);

                switch (rule.Target.Trim().ToLowerInvariant())
                {
                    case "mustdo":
                        if (!string.IsNullOrWhiteSpace(message))
                            result.MustDoRules.Add(message);
                        break;
                    case "gate":
                        result.GateRules.Add(new ProgressionGateEntry(
                            string.IsNullOrWhiteSpace(category) ? "İlerleme" : category,
                            title,
                            progress,
                            message));
                        break;
                    case "modhighlight":
                        result.ModRules.Add(new ModEcosystemEntry(
                            title,
                            string.IsNullOrWhiteSpace(category) ? "Mod" : category,
                            progress,
                            message));
                        break;
                    default:
                        if (!string.IsNullOrWhiteSpace(message))
                            result.StrategicRules.Add(message);
                        break;
                }
            }
            catch (Exception ex)
            {
                monitor?.Log($"Advisor rule '{rule.Id}' failed: {ex.Message}", LogLevel.Trace);
            }
        }

        return result;
    }

    private static AdvisorRulesDocument? LoadRules(string modDir, IMonitor? monitor, bool forceReload)
    {
        if (string.IsNullOrWhiteSpace(modDir))
            return _cachedDoc;

        string path = Path.Combine(modDir, "advisor_rules.json");
        if (!File.Exists(path))
            return _cachedDoc;

        try
        {
            DateTime writeUtc = File.GetLastWriteTimeUtc(path);
            lock (CacheLock)
            {
                if (!forceReload
                    && _cachedDoc is not null
                    && string.Equals(_lastLoadedPath, path, StringComparison.OrdinalIgnoreCase)
                    && writeUtc == _lastLoadedWriteUtc)
                {
                    return _cachedDoc;
                }

                string json = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize<AdvisorRulesDocument>(json, JsonOptions);
                if (parsed is not null)
                {
                    _cachedDoc = parsed;
                    _lastLoadedPath = path;
                    _lastLoadedWriteUtc = writeUtc;
                }
                return _cachedDoc;
            }
        }
        catch (Exception ex)
        {
            monitor?.Log($"Failed to load advisor_rules.json (using previous valid cache if any): {ex.Message}", LogLevel.Warn);
            return _cachedDoc;
        }
    }

    private static bool MatchesAll(List<string> conditions, RuleRuntimeContext ctx)
    {
        foreach (string cond in conditions)
        {
            if (!EvaluateCondition(cond, ctx))
                return false;
        }
        return true;
    }

    private static bool MatchesAny(List<string> conditions, RuleRuntimeContext ctx)
    {
        foreach (string cond in conditions)
        {
            if (EvaluateCondition(cond, ctx))
                return true;
        }
        return false;
    }

    private static bool EvaluateCondition(string raw, RuleRuntimeContext ctx)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        string expr = raw.Trim();
        bool negate = false;
        if (expr.StartsWith('!'))
        {
            negate = true;
            expr = expr[1..].Trim();
        }

        bool result = EvaluatePositiveCondition(expr, ctx);
        return negate ? !result : result;
    }

    private static bool EvaluatePositiveCondition(string expr, RuleRuntimeContext ctx)
    {
        // Comparison operators: >=, <=, !=, ==, >, <
        string[] ops = { ">=", "<=", "!=", "==", ">", "<" };
        foreach (string op in ops)
        {
            int idx = expr.IndexOf(op, StringComparison.Ordinal);
            if (idx > 0)
            {
                string lhs = expr[..idx].Trim();
                string rhs = expr[(idx + op.Length)..].Trim();
                return CompareValues(lhs, op, rhs, ctx);
            }
        }

        // Prefixed dynamic lookups
        int colon = expr.IndexOf(':');
        if (colon > 0)
        {
            string prefix = expr[..colon].Trim().ToLowerInvariant();
            string arg = expr[(colon + 1)..].Trim();
            return prefix switch
            {
                "mod" => ctx.ModRegistry.IsLoaded(arg),
                "flag" or "mail" => ctx.Player.mailReceived.Contains(arg),
                "event" => ctx.Player.eventsSeen.Contains(arg),
                "visited" => ctx.Player.locationsVisited.Contains(arg),
                "building" => ctx.FarmBuildings.Contains(arg),
                "machine" => ctx.PlacedMachines.Contains(arg),
                "recipe" => ctx.Player.craftingRecipes.ContainsKey(arg) || ctx.Player.cookingRecipes.ContainsKey(arg),
                "hasitem" => ctx.Owned.GetStorageCount(arg) > 0 || ctx.Owned.FindMatches(arg).Total > 0,
                _ => ctx.Flags.Contains(expr)
            };
        }

        return ctx.Flags.Contains(expr);
    }

    private static bool CompareValues(string lhs, string op, string rhs, RuleRuntimeContext ctx)
    {
        // Try numeric comparison first
        if (TryGetNumericValue(lhs, ctx, out double leftNum) && double.TryParse(rhs, NumberStyles.Float, CultureInfo.InvariantCulture, out double rightNum))
        {
            return op switch
            {
                ">=" => leftNum >= rightNum,
                "<=" => leftNum <= rightNum,
                ">" => leftNum > rightNum,
                "<" => leftNum < rightNum,
                "==" => Math.Abs(leftNum - rightNum) < 0.0001,
                "!=" => Math.Abs(leftNum - rightNum) >= 0.0001,
                _ => false
            };
        }

        // String comparison
        string leftStr = GetStringValue(lhs, ctx);
        string rightStr = rhs.Trim('"', '\'');
        return op switch
        {
            "==" => string.Equals(leftStr, rightStr, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(leftStr, rightStr, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool TryGetNumericValue(string key, RuleRuntimeContext ctx, out double value)
    {
        if (ctx.Numbers.TryGetValue(key, out value))
            return true;

        int colon = key.IndexOf(':');
        if (colon > 0)
        {
            string prefix = key[..colon].Trim().ToLowerInvariant();
            string arg = key[(colon + 1)..].Trim();
            switch (prefix)
            {
                case "npchearts":
                    value = ctx.Player.friendshipData.TryGetValue(arg, out var f) ? f.Points / 250.0 : 0;
                    return true;
                case "npcpoints":
                    value = ctx.Player.friendshipData.TryGetValue(arg, out var fp) ? fp.Points : 0;
                    return true;
                case "itemcount":
                    value = ctx.Owned.GetStorageCount(arg);
                    return true;
                case "skill":
                    if (ctx.CustomSkills.TryGetValue(arg, out int cs))
                    {
                        value = cs;
                        return true;
                    }
                    break;
            }
        }

        value = 0;
        return false;
    }

    private static string GetStringValue(string key, RuleRuntimeContext ctx)
    {
        if (ctx.Strings.TryGetValue(key, out string? s))
            return s;
        if (ctx.Numbers.TryGetValue(key, out double d))
            return d.ToString("0.###", CultureInfo.InvariantCulture);
        return string.Empty;
    }

    private static string ResolveTokens(string text, RuleRuntimeContext ctx)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return TokenRegex.Replace(text, match =>
        {
            string key = match.Groups[1].Value;
            if (ctx.Strings.TryGetValue(key, out string? str))
                return str;
            if (TryGetNumericValue(key, ctx, out double num))
            {
                if (key is "money" or "totalEarned")
                    return num.ToString("N0", CultureInfo.InvariantCulture);
                return Math.Abs(num - Math.Round(num)) < 0.0001
                    ? ((long)Math.Round(num)).ToString(CultureInfo.InvariantCulture)
                    : num.ToString("0.##", CultureInfo.InvariantCulture);
            }
            return match.Value;
        });
    }
}
