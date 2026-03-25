using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using HarvestPicker.Api.Response;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace HarvestPicker;

public class HarvestPrices
{
    public double YellowJuiceValue;
    public double PurpleJuiceValue;
    public double BlueJuiceValue;
    public double WhiteJuiceValue;
}

public record SeedData(int Type, float T1Plants, float T2Plants, float T3Plants, float T4Plants);

public class HarvestEntityData
{
    public Entity Entity { get; init; }
    public SeedData SeedData { get; set; }
    public double Value { get; set; }
    public bool IsValid => Entity?.IsValid == true;
}

public class RenderItem
{
    public Vector2 ScreenPos { get; init; }
    public Vector2 GridPos { get; init; }
    public Color Color { get; init; }
    public string Label { get; init; }
    public bool IsCurrentTarget { get; init; }
    public int PathIndex { get; init; }
    public SeedData SeedData { get; init; }
}

public class HarvestPicker : BaseSettingsPlugin<HarvestPickerSettings>
{
    private static readonly HttpClient _httpClient = new HttpClient();

    private const float IRRIGATION_PAIRING_DISTANCE = 85f;
    private const string HARVEST_EXTRACTOR_PATH = "Metadata/MiscellaneousObjects/Harvest/Extractor";
    private const string STATE_MACHINE_CURRENT_STATE = "current_state";
    private const int STATE_MACHINE_READY_VALUE = 0;

    public const int SEED_TYPE_PURPLE = 1;
    public const int SEED_TYPE_YELLOW = 2;
    public const int SEED_TYPE_BLUE = 3;

    private const long SSF_T4_WEIGHT = 1_000_000_000;
    private const long SSF_T3_WEIGHT = 1_000_000;
    private const long SSF_T2_WEIGHT = 1_000;
    private const long SSF_T1_WEIGHT = 1;

    private const float IRRIGATION_LABEL_MIN_DISTANCE = 15f;
    private const float MAP_RECT_SIZE = 24f;
    private const float PANEL_WIDTH = 350f;
    private const float PANEL_Y_OFFSET = 5f;
    private const float PANEL_PADDING = 10f;
    private const float LINE_HEIGHT_MULTIPLIER_1_5 = 1.5f;
    private const float LINE_HEIGHT_MULTIPLIER_2_5 = 2.5f;
    private const float LINE_HEIGHT_MULTIPLIER_1_2 = 1.2f;

    private const string PLOT_LETTER_PURPLE = "P";
    private const string PLOT_LETTER_YELLOW = "Y";
    private const string PLOT_LETTER_BLUE = "B";
    private const string PLOT_LETTER_UNKNOWN = "?";
    private const string PATH_ARROW = " -> ";

    private const string PLOT_NAME_WILD = "Wild";
    private const string PLOT_NAME_VIVID = "Vivid";
    private const string PLOT_NAME_PRIMAL = "Primal";
    private const string PLOT_NAME_UNKNOWN_TYPE = "Unknown";

    private int _currentCropRotationStep;
    public override bool Initialise()
    {
        _pricesGetter = LoadPricesFromDisk(false);
        Settings.ReloadPrices.OnPressed = () => { _pricesGetter = LoadPricesFromDisk(true); };
        return true;
    }

    public override void EntityAdded(Entity entity)
    {
        if (entity.Type != EntityType.MiscellaneousObjects) return;
        if (entity.Path != HARVEST_EXTRACTOR_PATH) return;
        if (!entity.HasComponent<HarvestWorldObject>()) return;

        var seedData = ExtractSeedData(entity);
        if (seedData == null) return;

        var data = new HarvestEntityData
        {
            Entity = entity,
            SeedData = seedData,
            Value = CalculateIrrigatorValue(seedData)
        };

        entity.SetHudComponent(data);
    }

    private readonly Stopwatch _lastRetrieveStopwatch = new Stopwatch();
    private Task _pricesGetter;
    private HarvestPrices _prices;
    private readonly object _pricesLock = new object();
    private DateTime _lastPoeNinjaDataUpdate = DateTime.MinValue;
    private List<((Entity, double, SeedData), (Entity, double, SeedData))> _irrigatorPairs;
    private HarvestSequenceResult _lastHarvestSequenceResult;
    private List<Entity> _cropRotationPath;
    private double _cropRotationValue;
    private SeedData _finalPlotSeedData;
    private HashSet<Entity> _lastProcessedEntities;
    private string _lastSeedDataHash;
    private bool _harvestRotationCompleted;
    private string CachePath => Path.Join(ConfigDirectory, "pricecache.json");

    private MemoizedHarvestCalculator _harvestCalculator;
    private Element _cachedIrrigatorLabel;
    private readonly Stopwatch _tickDelayStopwatch = new Stopwatch();
    private bool _tryFallbackLeague;
    private string _fallbackLeague;

    private readonly List<RenderItem> _renderList = new();
    private readonly List<(Entity Entity, HarvestEntityData Data)> _validIrrigators = new();
    private readonly Dictionary<Entity, SeedData> _entitySeedDataCache = new();

    private void ResetHarvestState(bool resetProcessedEntities = false, bool resetIrrigatorPairs = false, bool resetHash = false)
    {
        _cropRotationPath = null;
        _cropRotationValue = 0;
        _finalPlotSeedData = null;
        _currentCropRotationStep = 0;
        _cachedIrrigatorLabel = null;
        _lastHarvestSequenceResult = null;
        _harvestRotationCompleted = false;
        if (resetProcessedEntities) _lastProcessedEntities = null;
        if (resetIrrigatorPairs) _irrigatorPairs = [];
        if (resetHash) _lastSeedDataHash = null;
    }

    public override void AreaChange(AreaInstance area)
    {
        ResetHarvestState(resetProcessedEntities: true, resetIrrigatorPairs: true, resetHash: true);
        _harvestCalculator = null;
        _tickDelayStopwatch.Restart();
        Settings.League.Values = (Settings.League.Values ?? []).Union([PlayerLeague, "Standard", "Hardcore"]).Where(x => x != null).ToList();
        if (!string.IsNullOrWhiteSpace(PlayerLeague))
        {
            Settings.League.Value = PlayerLeague;
        }
        _tryFallbackLeague = false;
        _fallbackLeague = GetFallbackLeague(PlayerLeague);
    }

    private string PlayerLeague
    {
        get
        {
            var playerLeague = GameController.IngameState.ServerData.League;
            if (string.IsNullOrWhiteSpace(playerLeague))
            {
                playerLeague = null;
            }
            else
            {
                if (playerLeague.StartsWith("SSF "))
                {
                    playerLeague = playerLeague["SSF ".Length..];
                }
            }

            return playerLeague;
        }
    }

    private string GetFallbackLeague(string league)
    {
        if (string.IsNullOrWhiteSpace(league)) return "Standard";
        
        var hardcoreVariants = new[] { "Hardcore" };
        
        foreach (var variant in hardcoreVariants)
        {
            if (league.Contains(variant))
            {
                return league.Replace(variant, "").Trim();
            }
        }
        
        return "Standard";
    }

    private HarvestPrices Prices
    {
        get
        {
            if (_pricesGetter is { IsCompleted: true })
            {
                _pricesGetter = null;
            }

            if (ShouldFetchNewPrices())
            {
                _pricesGetter = FetchPrices();
                _lastRetrieveStopwatch.Reset();
            }

            return _prices;
        }
    }

    private bool ShouldFetchNewPrices()
    {
        var localRefreshDue = !_lastRetrieveStopwatch.IsRunning || _lastRetrieveStopwatch.Elapsed >= TimeSpan.FromMinutes(Settings.PriceRefreshPeriodMinutes.Value);
        var pricesNotLoaded = _prices == null;

        return _pricesGetter == null && (localRefreshDue || pricesNotLoaded);
    }

    private async Task FetchPrices()
    {
        await Task.Yield();
        string leagueToTry = Settings.League.Value;
        bool isFallbackAttempt = _tryFallbackLeague;
        
        if (isFallbackAttempt && !string.IsNullOrWhiteSpace(_fallbackLeague))
        {
            leagueToTry = _fallbackLeague;
        }

        try
        {
            var query = HttpUtility.ParseQueryString("");
            query["league"] = leagueToTry;
            query["type"] = "Currency";

            UriBuilder builder = new UriBuilder();
            builder.Path = "/api/data/currencyoverview";
            builder.Host = "poe.ninja";
            builder.Scheme = "https";
            builder.Query = query.ToString();

            string uri = builder.ToString();

            var request = _httpClient.GetAsync(uri);
            var response = await request;
            response.EnsureSuccessStatusCode();

            var str = await response.Content.ReadAsStringAsync();
            var fullResponse = JsonConvert.DeserializeObject<dynamic>(str);
            if (fullResponse?.last_updated != null)
            {
                _lastPoeNinjaDataUpdate = fullResponse.last_updated;
            }
            var responseObject = JsonConvert.DeserializeObject<PoeNinjaCurrencyResponse>(str);

            var dataMap = responseObject.Lines.ToDictionary(x => x.CurrencyTypeName, x => responseObject.FindLine(x)?.ChaosEquivalent);
            
            var tempPrices = new HarvestPrices
            {
                BlueJuiceValue = dataMap.GetValueOrDefault("Primal Crystallised Lifeforce") ?? 0,
                YellowJuiceValue = dataMap.GetValueOrDefault("Vivid Crystallised Lifeforce") ?? 0,
                PurpleJuiceValue = dataMap.GetValueOrDefault("Wild Crystallised Lifeforce") ?? 0,
                WhiteJuiceValue = dataMap.GetValueOrDefault("Sacred Crystallised Lifeforce") ?? 0,
            };

            bool hasValidLifeforceData = tempPrices.BlueJuiceValue > 0 || tempPrices.YellowJuiceValue > 0 ||
                                         tempPrices.PurpleJuiceValue > 0 || tempPrices.WhiteJuiceValue > 0;

            if (!hasValidLifeforceData && Settings.EvaluationMode.Value == HarvestPickerSettings.EVALUATION_MODE_TRADE && 
                !isFallbackAttempt && !string.IsNullOrWhiteSpace(_fallbackLeague))
            {
                Log($"No lifeforce data found for {leagueToTry}, trying fallback league {_fallbackLeague}");
                _tryFallbackLeague = true;
                Settings.League.Value = _fallbackLeague;
                _pricesGetter = FetchPrices();
                return;
            }

            if (!hasValidLifeforceData && Settings.EvaluationMode.Value == HarvestPickerSettings.EVALUATION_MODE_TRADE)
            {
                Log($"No lifeforce data found for {leagueToTry}. Falling back to SSF evaluation mode.");
                Settings.EvaluationMode.Value = HarvestPickerSettings.EVALUATION_MODE_SSF;
            }

            lock (_pricesLock)
            {
                _prices = tempPrices;
            }
            await File.WriteAllTextAsync(CachePath, JsonConvert.SerializeObject(tempPrices));
            _lastPoeNinjaDataUpdate = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(ex.ToString());
        }
        finally
        {
            _lastRetrieveStopwatch.Restart();
        }
    }

    private async Task LoadPricesFromDisk(bool force)
    {
        await Task.Yield();
        try
        {
            var cachePath = CachePath;
            if (File.Exists(cachePath))
            {
                var loadedPrices = JsonConvert.DeserializeObject<HarvestPrices>(await File.ReadAllTextAsync(cachePath));
                lock (_pricesLock)
                {
                    _prices = loadedPrices;
                }
                if (force)
                {
                    _lastRetrieveStopwatch.Reset();
                }
                else
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromMinutes(Settings.PriceRefreshPeriodMinutes.Value))
                    {
                        _lastRetrieveStopwatch.Restart();
                        _lastPoeNinjaDataUpdate = File.GetLastWriteTimeUtc(cachePath);
                    }
                }
            }
            else
            {
                _lastRetrieveStopwatch.Reset();
                _lastPoeNinjaDataUpdate = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(ex.ToString());
        }
    }


    public void Log(string message)
    {
        LogMessage($"[HarvestPicker] {message}");
    }

    public override Job Tick()
    {
        if (_tickDelayStopwatch.ElapsedMilliseconds < Settings.InitialLoadDelay.Value)
        {
            return null;
        }

        _renderList.Clear();
        _validIrrigators.Clear();

        var miscEntities = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.MiscellaneousObjects];

        int checkedCount = 0, pathMatchCount = 0, validCount = 0;
        int harvestPathCount = 0;
        int nullPathCount = 0, invalidCount = 0;
        var uniquePaths = new HashSet<string>();
        foreach (var entity in miscEntities)
        {
            checkedCount++;
            if (!entity.IsValid) { invalidCount++; continue; }
            if (entity.Path == null) { nullPathCount++; continue; }
            uniquePaths.Add(entity.Path);
            if (entity.Path.Contains("Harvest")) harvestPathCount++;
            if (entity.Path != HARVEST_EXTRACTOR_PATH) continue;
            pathMatchCount++;

            var data = entity.GetHudComponent<HarvestEntityData>();
            if (data == null)
            {
                var seedData = ExtractSeedData(entity);
                if (seedData == null) continue;

                data = new HarvestEntityData
                {
                    Entity = entity,
                    SeedData = seedData,
                    Value = CalculateIrrigatorValue(seedData)
                };
                entity.SetHudComponent(data);
            }

            if (entity.TryGetComponent<StateMachine>(out var sm))
            {
                var currentState = sm.States.FirstOrDefault(s => s.Name == STATE_MACHINE_CURRENT_STATE)?.Value;
                if (currentState != STATE_MACHINE_READY_VALUE) continue;
            }

            validCount++;
            _validIrrigators.Add((entity, data));
        }

        if (!_validIrrigators.Any())
        {
            ResetHarvestState();
            return null;
        }

        _irrigatorPairs = BuildIrrigatorPairs(_validIrrigators);

        if (!_irrigatorPairs.Any())
        {
            ResetHarvestState();
            return null;
        }

        var hasCropRotationMod = GameController.IngameState.Data.MapStats.GetValueOrDefault(
            GameStat.MapHarvestSeedsOfOtherColoursHaveChanceToUpgradeOnCompletingPlot) != STATE_MACHINE_READY_VALUE;

        if (hasCropRotationMod)
        {
            ProcessCropRotation();
        }
        else
        {
            _cropRotationPath = null;
        }

        AdvanceCropRotationPath();

        if (_cropRotationPath is { } path && _currentCropRotationStep >= path.Count)
        {
            ResetHarvestState();
        }

        // Build render list for Render() - no component access allowed in Render!
        BuildRenderList();

        return null;
    }

    private List<((Entity, double, SeedData), (Entity, double, SeedData))> BuildIrrigatorPairs(List<(Entity Entity, HarvestEntityData Data)> irrigators)
    {
        var pairs = new List<((Entity, double, SeedData), (Entity, double, SeedData))>();
        var remaining = irrigators.Select(i => (i.Entity, i.Data.Value, i.Data.SeedData)).ToList();

        while (remaining.Any() && remaining.LastOrDefault() is { } irrigator1)
        {
            remaining.RemoveAt(remaining.Count - 1);
            var closest = remaining
                .OrderBy(x => x.Entity.Distance(irrigator1.Entity))
                .FirstOrDefault();

            if (closest.Entity == null || irrigator1.Entity.Distance(closest.Entity) > IRRIGATION_PAIRING_DISTANCE)
            {
                pairs.Add(((irrigator1.Entity, irrigator1.Value, irrigator1.SeedData), default));
            }
            else
            {
                remaining.Remove(closest);
                pairs.Add(((irrigator1.Entity, irrigator1.Value, irrigator1.SeedData), 
                          (closest.Entity, closest.Value, closest.SeedData)));
            }
        }

        return pairs;
    }

    private void BuildRenderList()
    {
        _renderList.Clear();
        _entitySeedDataCache.Clear();
        var camera = GameController.IngameState.Camera;

        if (_cropRotationPath is { } path && path.Count > 0 && _currentCropRotationStep < path.Count)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var entity = path[i];
                if (entity == null || !entity.IsValid) continue;

                var data = entity.GetHudComponent<HarvestEntityData>();
                if (data?.SeedData == null) continue;

                _entitySeedDataCache[entity] = data.SeedData;

                var screenPos = camera.WorldToScreen(entity.PosNum);
                if (screenPos == Vector2.Zero) continue;

                var isCurrent = i == _currentCropRotationStep;

                _renderList.Add(new RenderItem
                {
                    ScreenPos = screenPos,
                    GridPos = entity.GridPosNum,
                    Color = GetColorForPlotType(data.SeedData.Type),
                    Label = (i + 1).ToString(),
                    IsCurrentTarget = isCurrent,
                    PathIndex = i,
                    SeedData = data.SeedData
                });
            }
        }
        else if (_irrigatorPairs.Any())
        {
            (Entity bestEntity, double bestValue, SeedData bestData) = (null, double.NegativeInfinity, null);

            foreach (var ((e1, v1, d1), (e2, v2, d2)) in _irrigatorPairs)
            {
                if (e1 != null && v1 > bestValue)
                {
                    bestValue = v1;
                    bestEntity = e1;
                    bestData = d1;
                }
                if (e2 != null && v2 > bestValue)
                {
                    bestValue = v2;
                    bestEntity = e2;
                    bestData = d2;
                }
            }

            if (bestEntity != null && bestEntity.IsValid && bestData != null)
            {
                _entitySeedDataCache[bestEntity] = bestData;

                var screenPos = camera.WorldToScreen(bestEntity.PosNum);
                if (screenPos != Vector2.Zero)
                {
                    _renderList.Add(new RenderItem
                    {
                        ScreenPos = screenPos,
                        GridPos = bestEntity.GridPosNum,
                        Color = GetColorForPlotType(bestData.Type),
                        Label = "1",
                        IsCurrentTarget = true,
                        PathIndex = 0,
                        SeedData = bestData
                    });
                }
            }
        }
    }

    private float GetCropRotationChance(int fromTier, int toTier)
    {
        if (fromTier == 1 && toTier == 2)
            return Settings.CropRotationT1UpgradeChance.Value;

        var gameStat = fromTier == 2 && toTier == 3
            ? GameStat.MapHarvestSeedT2UpgradePctChance
            : fromTier == 3 && toTier == 4
                ? GameStat.MapHarvestSeedT3UpgradePctChance
                : (GameStat?)null;

        if (gameStat != null)
        {
            var value = GameController.IngameState.Data.MapStats.GetValueOrDefault(gameStat.Value);
            if (value > 0)
                return value / 100.0f;
        }

        return fromTier == 2 && toTier == 3
            ? Settings.CropRotationT2UpgradeChance.Value
            : Settings.CropRotationT3UpgradeChance.Value;
    }

    private void ProcessCropRotation()
    {
        var oneWayPairLookup = _irrigatorPairs
            .Where(p => p.Item1.Item1 != null && p.Item2.Item1 != null)
            .ToDictionary(p => p.Item1.Item1, p => p.Item2.Item1);
        var pairLookup = new Dictionary<Entity, Entity>(oneWayPairLookup);
        foreach (var kvp in oneWayPairLookup)
        {
            pairLookup[kvp.Value] = kvp.Key;
        }

        var currentSet = new HashSet<Entity>(_irrigatorPairs.Select(p => p.Item1.Item1).Concat(_irrigatorPairs.Select(p => p.Item2.Item1)).Where(e => e != null));

        List<(SeedData Data, Entity Entity)> seedPlots = _irrigatorPairs.SelectMany(p => new[]
        {
            (p.Item1.Item3, p.Item1.Item1),
            (p.Item2.Item3, p.Item2.Item1)
        }).Where(t => t.Item1 != null && t.Item2 != null).ToList();

        string currentHash = ComputeSeedDataHash(seedPlots);
        if (_lastProcessedEntities == null || !_lastProcessedEntities.SetEquals(currentSet) || _lastSeedDataHash != currentHash || _cropRotationPath == null)
        {
            ResetHarvestState();

            if (!seedPlots.Any())
            {
                _lastProcessedEntities = currentSet;
                _lastSeedDataHash = currentHash;
                return;
            }

            SeedData Upgrade(SeedData source, int type) => source == null || type == source.Type
                ? source
                : new SeedData(source.Type,
                    source.T1Plants * (1 - GetCropRotationChance(1, 2)),
                    source.T2Plants * (1 - GetCropRotationChance(2, 3)) + source.T1Plants * GetCropRotationChance(1, 2),
                    source.T3Plants * (1 - GetCropRotationChance(3, 4)) + source.T2Plants * GetCropRotationChance(2, 3),
                    source.T4Plants + source.T3Plants * GetCropRotationChance(3, 4));

            _harvestCalculator = new MemoizedHarvestCalculator(Upgrade, pairLookup, this, Settings.UseWitherChance.Value);
            var chanceToNotWither = GameController.IngameState.Data.MapStats
                .GetValueOrDefault(GameStat.MapHarvestSeedsOfOtherColoursHaveChanceToUpgradeOnCompletingPlot) / 100.0;

            var currentPermutationCount = 0;
            var absoluteBestResult = _harvestCalculator.CalculateBestHarvestSequence(seedPlots, chanceToNotWither, ref currentPermutationCount, Settings.MaxPermutations.Value);

            HarvestSequenceResult chosenResult = absoluteBestResult;

            if (absoluteBestResult == null) 
            {
                _cropRotationPath = null;
                return; 
            }

            _lastHarvestSequenceResult = chosenResult with { AggregatedSeedData = chosenResult.AggregatedSeedData };

            if (currentPermutationCount >= Settings.MaxPermutations.Value)
            {
                Log($"Reached max permutations limit ({Settings.MaxPermutations.Value}). Result may be suboptimal.");
            }

            if (chosenResult.Sequence == null || !chosenResult.Sequence.Any())
            {
                _cropRotationPath = null; 
                return; 
            }
            
            var originalPath = chosenResult.Sequence;
            var filteredPath = new List<Entity>();
            var handledPairs = new HashSet<Entity>();

            foreach (var plot in originalPath)
            {
                if (plot == null) continue;
                
                Entity representative = plot;
                if (pairLookup.TryGetValue(plot, out var pairedPlot) && pairedPlot.Address < plot.Address)
                {
                    representative = pairedPlot;
                }
                
                if (handledPairs.Contains(representative))
                {
                    continue;
                }

                filteredPath.Add(plot);
                handledPairs.Add(representative);
            }

            _cropRotationPath = filteredPath;
            _cropRotationValue = chosenResult.FinalPlotValue;
            _finalPlotSeedData = chosenResult.FinalPlotData;
            _lastProcessedEntities = currentSet;
            _lastSeedDataHash = ComputeSeedDataHash(seedPlots);

            if (Settings.LogDetailedForCropRotation.Value)
            {
                _harvestCalculator?.LogCacheStats(Log);
            }
        }
    }

    private string ComputeSeedDataHash(List<(SeedData Data, Entity Entity)> seedPlots)
    {
        var hash = new HashCode();
        foreach (var (data, entity) in seedPlots.OrderBy(x => x.Entity.Address))
        {
            if (data == null || entity == null) continue;
            hash.Add(entity.Address);
            hash.Add(data.Type);
            hash.Add(data.T1Plants);
            hash.Add(data.T2Plants);
            hash.Add(data.T3Plants);
            hash.Add(data.T4Plants);
        }
        return hash.ToHashCode().ToString();
    }

    private void AdvanceCropRotationPath()
    {
        if (_cropRotationPath is { } path && _currentCropRotationStep < path.Count)
        {
            var currentTarget = path[_currentCropRotationStep];
            if (!currentTarget.IsValid || 
                (currentTarget.TryGetComponent<StateMachine>(out var sm) && 
                 sm.States.FirstOrDefault(s => s.Name == "current_state")?.Value != 0))
            {
                _currentCropRotationStep++;
            }
        }
    }

    private SeedData ExtractSeedData(Entity e)
    {
        if (e == null || !e.IsValid) return null;
        var harvest = e.GetComponent<HarvestWorldObject>();
        if (harvest == null)
        {
            return null;
        }

        var seeds = harvest.Seeds;
        if (seeds.Count == 0)
        {
            return null;
        }

        if (seeds.Any(x => x.Seed == null))
        {
            return null;
        }

        var type = seeds.GroupBy(x => x.Seed.Type).MaxBy(x => x.Count()).Key;
        var seedsByTier = seeds.ToLookup(x => x.Seed.Tier);
        return new SeedData(type,
            seedsByTier[1].Sum(x => x.Count),
            seedsByTier[2].Sum(x => x.Count),
            seedsByTier[3].Sum(x => x.Count),
            seedsByTier[4].Sum(x => x.Count));
    }

    public SeedData SumSeedData(SeedData s1, SeedData s2)
    {
        if (s1 == null && s2 == null) return null;
        if (s1 == null) return s2;
        if (s2 == null) return s1;

        var type = s1.Type != 0 ? s1.Type : s2.Type;

        return new SeedData(type,
            s1.T1Plants + s2.T1Plants,
            s1.T2Plants + s2.T2Plants,
            s1.T3Plants + s2.T3Plants,
            s1.T4Plants + s2.T4Plants);
    }

    public double CalculateIrrigatorValue(SeedData data)
    {
        if (data == null)
        {
            return 0;
        }
        
        var mode = Settings.EvaluationMode.Value;
        double calculatedValue = 0;
        if (mode == HarvestPickerSettings.EVALUATION_MODE_TRADE)
        {
            HarvestPrices prices;
            lock (_pricesLock)
            {
                prices = _prices;
            }
            if (prices == null)
            {
                return 0;
            }

            var typeToPrice = data.Type switch
            {
                SEED_TYPE_PURPLE => prices.PurpleJuiceValue,
                SEED_TYPE_YELLOW => prices.YellowJuiceValue,
                SEED_TYPE_BLUE => prices.BlueJuiceValue,
                _ => 0,
            };
            calculatedValue = Settings.SeedsPerT1Plant.Value * typeToPrice * data.T1Plants +
                              Settings.SeedsPerT2Plant.Value * typeToPrice * data.T2Plants +
                              Settings.SeedsPerT3Plant.Value * typeToPrice * data.T3Plants +
                              (Settings.SeedsPerT4Plant.Value * typeToPrice + Settings.T4PlantWhiteSeedChance.Value * prices.WhiteJuiceValue) * data.T4Plants;
        }
        else 
        {
            calculatedValue = data.T4Plants * SSF_T4_WEIGHT +
                              data.T3Plants * SSF_T3_WEIGHT +
                              data.T2Plants * SSF_T2_WEIGHT +
                              data.T1Plants * SSF_T1_WEIGHT;
        }
        return calculatedValue;
    }

    private Color GetColorForPlotType(int type)
    {
        return type switch
        {
            HarvestPicker.SEED_TYPE_PURPLE => Settings.PurplePlotColor.Value,
            HarvestPicker.SEED_TYPE_YELLOW => Settings.YellowPlotColor.Value,
            HarvestPicker.SEED_TYPE_BLUE => Settings.BluePlotColor.Value,
            _ => Color.White,
        };
    }


    
    private string GetPlotValueText(double value, SeedData data)
    {
        if (data == null) return "N/A";
        if (Settings.EvaluationMode.Value == HarvestPickerSettings.EVALUATION_MODE_SSF)
        {
            return $"T3+: {data.T3Plants + data.T4Plants:F0}";
        }
        
        var valueSuffix = Settings.EvaluationMode.Value == HarvestPickerSettings.EVALUATION_MODE_TRADE ? "c" : "LF";
        return $"{value:F1}{valueSuffix}";
    }

    private Element FindIrrigatorLabel(Entity targetEntity)
    {
        if (targetEntity == null || !targetEntity.IsValid) return null;

        Element bestLabel = null;
        float minDistance = float.MaxValue;

        foreach (var labelContainer in GameController.IngameState.IngameUi.ItemsOnGroundLabels)
        {
            if (labelContainer.Label is not { IsVisible: true }) continue;
            if (labelContainer.ItemOnGround is not { IsValid: true } itemOnGround) continue;
            if (!itemOnGround.Path.Contains("Harvest")) continue;

            float distance = targetEntity.Distance(itemOnGround);
            if (distance < minDistance)
            {
                minDistance = distance;
                bestLabel = labelContainer.Label;
            }
        }

        return minDistance < IRRIGATION_LABEL_MIN_DISTANCE ? bestLabel : null;
    }

    public override void Render()
    {
        if (!GameController.InGame) return;

        var isMapOpen = GameController.IngameState.IngameUi.Map.LargeMap.IsVisibleLocal;

        if (_renderList.Any())
        {
            if (isMapOpen && Settings.DrawTargetOnMap.Value)
            {
                foreach (var item in _renderList)
                {
                    var mapPos = GameController.IngameState.Data.GetGridMapScreenPosition(item.GridPos);
                    var textSize = Graphics.MeasureText(item.Label);
                    var textPos = mapPos - textSize / 2;
                    Graphics.DrawBox(textPos, textPos + textSize, Color.Black);
                    Graphics.DrawText(item.Label, textPos, Color.White);

                    if (item.IsCurrentTarget)
                    {
                        var rectSize = new Vector2(MAP_RECT_SIZE, MAP_RECT_SIZE);
                        Graphics.DrawFrame(mapPos - rectSize / 2, mapPos + rectSize / 2, item.Color, 2);
                        var playerMapPos = GameController.IngameState.Data.GetGridMapScreenPosition(GameController.Player.GridPosNum);
                        Graphics.DrawLine(playerMapPos, mapPos, 2, item.Color);
                    }
                }
            }

            foreach (var item in _renderList)
            {
                if (item.ScreenPos == Vector2.Zero) continue;

                var textSize = Graphics.MeasureText(item.Label);
                var textPos = item.ScreenPos - textSize / 2;
                Graphics.DrawBox(textPos, textPos + textSize, Color.Black);
                Graphics.DrawText(item.Label, textPos, item.Color);
            }
        }

        // Panel rendering
        if (_cropRotationPath is { } path && path.Count > 0 && _currentCropRotationStep < path.Count)
        {
            var currentTargetEntity = path[_currentCropRotationStep];

            if (currentTargetEntity != null)
            {
                _cachedIrrigatorLabel = FindIrrigatorLabel(currentTargetEntity);
            }
            else
            {
                _cachedIrrigatorLabel = null;
            }

            var irrigatorLabel = _cachedIrrigatorLabel;
            if (irrigatorLabel != null && irrigatorLabel.IsVisible)
            {
                var labelRect = irrigatorLabel.GetClientRect();
                Graphics.DrawFrame(labelRect, Settings.TargetFrameColor.Value, 2);

                if (Settings.ShowPathSummary.Value && path.Count > 0 && _currentCropRotationStep < path.Count)
                {
                    var lineHeight = Graphics.MeasureText("A").Y;
                    var allPlotsOriginal = _irrigatorPairs.Select(p => p.Item1).Concat(_irrigatorPairs.Select(p => p.Item2)).ToList();
                    var finalPlotEntity = path.Last();
                    var initialPlotData = allPlotsOriginal.FirstOrDefault(p => p.Item1 == finalPlotEntity).Item3;

                    var linesToDraw = new List<(string Text, Color Color)>();
                    float maxWidth = 0;
                    float totalContentHeight = 0;

                    linesToDraw.Add(("HARVEST ROTATION PLAN", Color.White));
                    linesToDraw.Add(("Harvest Order:", Color.White));
                    var pathTypes = path.Select(p => _entitySeedDataCache.TryGetValue(p, out var sd) ? sd.Type : 0).ToList();
                    var pathString = string.Join(PATH_ARROW, pathTypes.Select(t => t switch { 1 => "P", 2 => "Y", 3 => "B", _ => "?" })) + $" ({GetPlotValueText(_cropRotationValue, _finalPlotSeedData)})";
                    linesToDraw.Add((pathString, Color.White));
                    linesToDraw.Add(("", Color.Transparent));

                    if (initialPlotData != null && _finalPlotSeedData != null)
                    {
                        var finalPlotSeedInfo = _finalPlotSeedData;
                        var plotColorName = finalPlotSeedInfo.Type switch {HarvestPicker.SEED_TYPE_PURPLE => "Purple", HarvestPicker.SEED_TYPE_YELLOW => "Yellow", HarvestPicker.SEED_TYPE_BLUE => "Blue", _ => "Unknown"};

                        linesToDraw.Add(($"Predicted Yield ({plotColorName} Plot):", Color.White));



                        if (Settings.EvaluationMode.Value == HarvestPickerSettings.EVALUATION_MODE_SSF)
                        {
                            linesToDraw.Add(("", Color.Transparent));
                            linesToDraw.Add(($"Current:  {GetPlotValueText(CalculateIrrigatorValue(initialPlotData), initialPlotData)}", Color.Gray));
                            linesToDraw.Add(($"Expected: {GetPlotValueText(_cropRotationValue, _lastHarvestSequenceResult.AggregatedSeedData)}", Color.White));
                        }
                        else
                        {
                            var initialValue = CalculateIrrigatorValue(initialPlotData);
                            linesToDraw.Add(("", Color.Transparent));
                            linesToDraw.Add(($"Current:  ~{initialValue:F0}c", Color.Gray));
                            linesToDraw.Add(($"Expected: ~{_cropRotationValue:F0}c", Color.White));
                        }
                    }

                    foreach (var (text, _) in linesToDraw)
                    {
                        var textSize = Graphics.MeasureText(text);
                        if (textSize.X > maxWidth) maxWidth = textSize.X;
                        totalContentHeight += lineHeight * (string.IsNullOrWhiteSpace(text) ? 0.5f : 1.2f);
                    }

                    var dynamicPanelWidth = maxWidth + PANEL_PADDING * 2;
                    var panelHeight = totalContentHeight + PANEL_PADDING * 2;
                    var titleBarHeight = lineHeight + PANEL_PADDING;

                    var labelCenter = labelRect.X + labelRect.Width / 2;
                    var panelX = labelCenter - dynamicPanelWidth / 2;
                    var panelY = labelRect.Bottom + PANEL_Y_OFFSET;
                    var panelPos = new Vector2(panelX, panelY);

                    Graphics.DrawBox(new RectangleF(panelPos.X, panelPos.Y, dynamicPanelWidth, titleBarHeight), Settings.PurplePlotColor.Value);
                    var contentBgPos = new Vector2(panelPos.X, panelPos.Y + titleBarHeight);
                    Graphics.DrawBox(new RectangleF(contentBgPos.X, contentBgPos.Y, dynamicPanelWidth, panelHeight - titleBarHeight), new Color(0, 0, 0, 220));

                    var currentY = panelPos.Y + PANEL_PADDING / 2;

                    var title = linesToDraw[0].Text;
                    var titleSize = Graphics.MeasureText(title);
                    Graphics.DrawText(title, new Vector2(panelPos.X + (dynamicPanelWidth - titleSize.X) / 2, currentY), linesToDraw[0].Color);
                    currentY += titleBarHeight;

                    for (int i = 1; i < linesToDraw.Count; i++)
                    {
                        var (text, color) = linesToDraw[i];
                        var ySpacing = string.IsNullOrWhiteSpace(text) ? lineHeight * 0.5f : lineHeight * 1.2f;

                        if (i == 2)
                        {
                            var pathDrawingPos = new Vector2(panelPos.X + PANEL_PADDING, currentY);
                            for(int j = 0; j < path.Count; j++)
                            {
                                var plot = path[j];
                                if (!_entitySeedDataCache.TryGetValue(plot, out var data)) continue;

                                var plotColor = GetColorForPlotType(data.Type);
                                var letter = data.Type switch { 1 => "P", 2 => "Y", 3 => "B", _ => "?" };
                                var letterSize = Graphics.MeasureText(letter);

                                if (j == _currentCropRotationStep)
                                {
                                    var boxRect = new RectangleF(pathDrawingPos.X - 2, pathDrawingPos.Y, letterSize.X + 4, letterSize.Y);
                                    Graphics.DrawBox(boxRect, plotColor);
                                    Graphics.DrawText(letter, pathDrawingPos, Color.White);
                                    var caretX = pathDrawingPos.X + letterSize.X / 2 - Graphics.MeasureText("^").X / 2;
                                    Graphics.DrawText("^", new Vector2(caretX, pathDrawingPos.Y + lineHeight), Color.White);
                                }
                                else
                                {
                                    Graphics.DrawText(letter, pathDrawingPos, plotColor);
                                }
                                pathDrawingPos.X += letterSize.X;

                                if (j < path.Count - 1)
                                {
                                    Graphics.DrawText(PATH_ARROW, pathDrawingPos, Color.White);
                                    pathDrawingPos.X += Graphics.MeasureText(PATH_ARROW).X;
                                }
                            }
                        }
                        else
                        {
                            Graphics.DrawText(text, new Vector2(panelPos.X + PANEL_PADDING, currentY), color);
                        }
                        currentY += ySpacing;
                    }
                }
            }
        }
        else if (_irrigatorPairs.Any() && _renderList.Any())
        {
            // Non-crop-rotation mode: highlight best choice
            var bestItem = _renderList.FirstOrDefault(r => r.IsCurrentTarget);
            if (bestItem.ScreenPos != Vector2.Zero)
            {
                var irrigatorLabel = FindIrrigatorLabelByPos(bestItem.ScreenPos);
                if (irrigatorLabel != null)
                {
                    Graphics.DrawFrame(irrigatorLabel.GetClientRect(), Settings.TargetFrameColor.Value, 2);
                }
            }

            var choiceNum = 1;
            foreach (var ((irrigator1, value1, seedData1), (irrigator2, value2, seedData2)) in _irrigatorPairs)
            {
                if (seedData1 == null || irrigator1 == null) continue;

                var screenPos1 = GameController.IngameState.Camera.WorldToScreen(irrigator1.PosNum);
                if (screenPos1 == Vector2.Zero) continue;

                if (irrigator2 != null && seedData2 != null)
                {
                    var text1Str = GetPlotValueText(value1, seedData1);
                    var text2Str = GetPlotValueText(value2, seedData2);

                    var plotTypeName1 = GetPlotTypeName(seedData1.Type);
                    var plotColorName1 = GetPlotColorName(seedData1.Type);
                    var plotTypeName2 = GetPlotTypeName(seedData2.Type);
                    var plotColorName2 = GetPlotColorName(seedData2.Type);

                    string text1, text2;
                    Color color1, color2;

                    if (value1 >= value2)
                    {
                        text1 = $"Choice {choiceNum}: {plotTypeName1} ({plotColorName1}) {text1Str}";
                        text2 = $"Value: {plotTypeName2} ({plotColorName2}) {text2Str}";
                        color1 = Settings.GoodColor.Value;
                        color2 = Settings.BadColor.Value;
                    }
                    else
                    {
                        text1 = $"Value: {plotTypeName1} ({plotColorName1}) {text1Str}";
                        text2 = $"Choice {choiceNum}: {plotTypeName2} ({plotColorName2}) {text2Str}";
                        color1 = Settings.BadColor.Value;
                        color2 = Settings.GoodColor.Value;
                    }

                    Graphics.DrawBox(screenPos1, screenPos1 + Graphics.MeasureText(text1), Color.Black);
                    Graphics.DrawText(text1, screenPos1, color1);

                    var screenPos2 = GameController.IngameState.Camera.WorldToScreen(irrigator2.PosNum);
                    if (screenPos2 != Vector2.Zero)
                    {
                        Graphics.DrawBox(screenPos2, screenPos2 + Graphics.MeasureText(text2), Color.Black);
                        Graphics.DrawText(text2, screenPos2, color2);
                    }
                }
                else
                {
                    var plotTypeName = GetPlotTypeName(seedData1.Type);
                    var plotColorName = GetPlotColorName(seedData1.Type);
                    var text = $"Choice {choiceNum}: {plotTypeName} ({plotColorName}) {GetPlotValueText(value1, seedData1)}";
                    Graphics.DrawBox(screenPos1, screenPos1 + Graphics.MeasureText(text), Color.Black);
                    Graphics.DrawText(text, screenPos1, Settings.NeutralColor.Value);
                }
                choiceNum++;
            }
        }
    }

    private string GetPlotTypeName(int type) => type switch
    {
        SEED_TYPE_PURPLE => PLOT_NAME_WILD,
        SEED_TYPE_YELLOW => PLOT_NAME_VIVID,
        SEED_TYPE_BLUE => PLOT_NAME_PRIMAL,
        _ => PLOT_NAME_UNKNOWN_TYPE
    };

    private string GetPlotColorName(int type) => type switch
    {
        SEED_TYPE_PURPLE => "Purple",
        SEED_TYPE_YELLOW => "Yellow",
        SEED_TYPE_BLUE => "Blue",
        _ => "Unknown"
    };

    private Element FindIrrigatorLabelByPos(Vector2 screenPos)
    {
        Element bestLabel = null;
        float minDistance = float.MaxValue;

        foreach (var labelContainer in GameController.IngameState.IngameUi.ItemsOnGroundLabels)
        {
            if (labelContainer.Label is not { IsVisible: true }) continue;
            if (labelContainer.ItemOnGround is not { IsValid: true } itemOnGround) continue;
            if (!itemOnGround.Path.Contains("Harvest")) continue;

            var labelPos = labelContainer.Label.GetClientRect().Center;
            float dx = labelPos.X - screenPos.X;
            float dy = labelPos.Y - screenPos.Y;
            float distance = (float)Math.Sqrt(dx * dx + dy * dy);
            if (distance < minDistance)
            {
                minDistance = distance;
                bestLabel = labelContainer.Label;
            }
        }

        return minDistance < IRRIGATION_LABEL_MIN_DISTANCE ? bestLabel : null;
    }
}