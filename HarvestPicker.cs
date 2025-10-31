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

    private readonly Stopwatch _lastRetrieveStopwatch = new Stopwatch();
    private Task _pricesGetter;
    private HarvestPrices _prices;
    private DateTime _lastPoeNinjaDataUpdate = DateTime.MinValue;
    private List<((Entity, double, SeedData), (Entity, double, SeedData))> _irrigatorPairs;
    private HarvestSequenceResult _lastHarvestSequenceResult;
    private List<Entity> _cropRotationPath;
    private double _cropRotationValue;
    private SeedData _finalPlotSeedData;
    private HashSet<Entity> _lastProcessedEntities;
    private string CachePath => Path.Join(ConfigDirectory, "pricecache.json");

    private MemoizedHarvestCalculator _harvestCalculator;
    private Element _cachedIrrigatorLabel;
    private readonly Stopwatch _tickDelayStopwatch = new Stopwatch();

    public override void AreaChange(AreaInstance area)
    {
        _lastProcessedEntities = null;
        _cropRotationPath = null;
        _cropRotationValue = 0;
        _finalPlotSeedData = null;
        _currentCropRotationStep = 0;
        _irrigatorPairs = [];
        _harvestCalculator = null;
        _cachedIrrigatorLabel = null;
        _lastHarvestSequenceResult = null;
        _tickDelayStopwatch.Restart(); // Restart the delay stopwatch on area change
        Settings.League.Values = (Settings.League.Values ?? []).Union([PlayerLeague, "Standard", "Hardcore"]).Where(x => x != null).ToList();
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
        var poeNinjaDataStale = _lastPoeNinjaDataUpdate == DateTime.MinValue || (DateTime.UtcNow - _lastPoeNinjaDataUpdate).TotalMinutes >= Settings.MinPoeNinjaDataFreshnessMinutes.Value;
        var pricesNotLoaded = _prices == null;

        return _pricesGetter == null && (localRefreshDue || poeNinjaDataStale || pricesNotLoaded);
    }

    private async Task FetchPrices()
    {
        await Task.Yield();
        try
        {
            var query = HttpUtility.ParseQueryString("");
            query["league"] = Settings.League.Value;
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
            if (dataMap.Any(x => x.Value is 0 or null) || dataMap.Count < 4)
            {
                // Log($"Some data is missing: {str}"); // Keep this for debugging if needed
            }

            _prices = new HarvestPrices
            {
                BlueJuiceValue = dataMap.GetValueOrDefault("Primal Crystallised Lifeforce") ?? 0,
                YellowJuiceValue = dataMap.GetValueOrDefault("Vivid Crystallised Lifeforce") ?? 0,
                PurpleJuiceValue = dataMap.GetValueOrDefault("Wild Crystallised Lifeforce") ?? 0,
                WhiteJuiceValue = dataMap.GetValueOrDefault("Sacred Crystallised Lifeforce") ?? 0,
            };
            await File.WriteAllTextAsync(CachePath, JsonConvert.SerializeObject(_prices));
            _lastPoeNinjaDataUpdate = DateTime.UtcNow; // Update timestamp on successful fetch
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
                _prices = JsonConvert.DeserializeObject<HarvestPrices>(await File.ReadAllTextAsync(cachePath));
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
                _lastPoeNinjaDataUpdate = DateTime.UtcNow; // Mark as updated now to prevent immediate re-fetch
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

        var irrigators = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.MiscellaneousObjects]
            .Where(x => x.Path == HARVEST_EXTRACTOR_PATH && x.HasComponent<HarvestWorldObject>()).ToList();

        _irrigatorPairs = new List<((Entity, double, SeedData), (Entity, double, SeedData))>();
        var localIrrigators = irrigators.ToList();
        var validIrrigators = new List<(Entity Entity, SeedData SeedData, double Value)>();

        foreach (var irrigatorEntity in localIrrigators)
        {
            var seedData = ExtractSeedData(irrigatorEntity);
            if (seedData != null)
            {
                validIrrigators.Add((irrigatorEntity, seedData, CalculateIrrigatorValue(seedData)));
            }
        }

        while (validIrrigators.Any() && validIrrigators.LastOrDefault() is { } irrigator1)
        {
            validIrrigators.RemoveAt(validIrrigators.Count - 1);
            var closestIrrigator = validIrrigators
                .OrderBy(x => x.Entity.Distance(irrigator1.Entity))
                .FirstOrDefault();

            if (closestIrrigator.Entity == null || irrigator1.Entity.Distance(closestIrrigator.Entity) > IRRIGATION_PAIRING_DISTANCE)
            {
                _irrigatorPairs.Add(((irrigator1.Entity, irrigator1.Value, irrigator1.SeedData), default));
            }
            else
            {
                validIrrigators.Remove(closestIrrigator);
                _irrigatorPairs.Add(((irrigator1.Entity, irrigator1.Value, irrigator1.SeedData), (closestIrrigator.Entity, closestIrrigator.Value, closestIrrigator.SeedData)));
            }
        }

        if (!_irrigatorPairs.Any())
        {
            _cropRotationPath = null;
            _cropRotationValue = 0;
            _finalPlotSeedData = null;
            _currentCropRotationStep = 0;
            _cachedIrrigatorLabel = null;
            _lastHarvestSequenceResult = null;
            return null; // Exit early if no irrigators
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
            _cropRotationPath = null;
            _cropRotationValue = 0;
            _finalPlotSeedData = null;
            _currentCropRotationStep = 0;
            _cachedIrrigatorLabel = null;
            _lastHarvestSequenceResult = null;
        }

        return null;
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
        
        // Recalculate if the entities have changed OR if we don't have a valid path from the last attempt.
        if (_lastProcessedEntities == null || !_lastProcessedEntities.SetEquals(currentSet) || _cropRotationPath == null)
        {
            _cropRotationPath = null;
            _cropRotationValue = 0;
            _finalPlotSeedData = null;
            _currentCropRotationStep = 0;
            _cachedIrrigatorLabel = null;
            _lastHarvestSequenceResult = null;

            List<(SeedData Data, Entity Entity)> seedPlots = _irrigatorPairs.SelectMany(p => new[]
            {
                (p.Item1.Item3, p.Item1.Item1),
                (p.Item2.Item3, p.Item2.Item1)
            }).Where(t => t.Item1 != null && t.Item2 != null).ToList();

            if (!seedPlots.Any())
            {
                _lastProcessedEntities = currentSet; 
                return;
            }

            SeedData Upgrade(SeedData source, int type) => source == null || type == source.Type
                ? source
                : new SeedData(source.Type,
                    source.T1Plants * (1 - Settings.CropRotationT1UpgradeChance.Value),
                    source.T2Plants * (1 - Settings.CropRotationT2UpgradeChance.Value) + source.T1Plants * Settings.CropRotationT1UpgradeChance.Value,
                    source.T3Plants * (1 - Settings.CropRotationT3UpgradeChance.Value) + source.T2Plants * Settings.CropRotationT2UpgradeChance.Value,
                    source.T4Plants + source.T3Plants * Settings.CropRotationT3UpgradeChance.Value);

            _harvestCalculator = new MemoizedHarvestCalculator(Upgrade, pairLookup, this);
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

            if (!Settings.LogDetailedForCropRotation.Value)
            {
                _harvestCalculator?.LogCacheStats(Log);
                _harvestCalculator?.LogDetailedStats(Log);
            }

            
        }
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

        // Assuming the type of the aggregated seed data doesn't matter, or is determined by the last seed.
        // For simplicity, we'll just use the type of s1 if it exists, otherwise s2.
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
            var prices = Prices;
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
        var isMapOpen = GameController.IngameState.IngameUi.Map.LargeMap.IsVisibleLocal;

        if (_cropRotationPath is { } path && path.Count > 0)
        {
            if (_currentCropRotationStep >= path.Count)
            {
                _cropRotationPath = null;
                _cropRotationValue = 0;
                _finalPlotSeedData = null;
                _currentCropRotationStep = 0;
                _cachedIrrigatorLabel = null;
                return; 
            }


            Entity currentTargetEntity = null;
            if (_currentCropRotationStep < path.Count)
            {
                currentTargetEntity = path[_currentCropRotationStep];
            }

            if (isMapOpen && Settings.DrawTargetOnMap.Value)
            {
                for (int i = 0; i < path.Count; i++)
                {
                    var entityOnPath = path[i];
                    if (entityOnPath == null || !entityOnPath.IsValid) continue;

                    var mapPos = GameController.IngameState.Data.GetGridMapScreenPosition(entityOnPath.GridPosNum);
                    var mapText = (i + 1).ToString();
                    var textSize = Graphics.MeasureText(mapText);
                    var textPos = mapPos - textSize / 2;
                    Graphics.DrawBox(textPos, textPos + textSize, Color.Black);
                    Graphics.DrawText(mapText, textPos, Color.White);

                    if (i == _currentCropRotationStep)
                    {
                        var seedData = ExtractSeedData(entityOnPath);
                        if (seedData == null) continue;
                        var plotColor = GetColorForPlotType(seedData.Type);
                        var rectSize = new Vector2(MAP_RECT_SIZE, MAP_RECT_SIZE);
                        Graphics.DrawFrame(mapPos - rectSize / 2, mapPos + rectSize / 2, plotColor, 2);
                        var playerMapPos = GameController.IngameState.Data.GetGridMapScreenPosition(GameController.Player.GridPosNum);
                        Graphics.DrawLine(playerMapPos, mapPos, 2, plotColor);
                    }
                }
            }
            
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

                    // --- PASS 1: GATHER CONTENT & CALCULATE DIMENSIONS ---
                    var linesToDraw = new List<(string Text, Color Color)>();
                    float maxWidth = 0;
                    float totalContentHeight = 0;

                    // Title
                    linesToDraw.Add(("HARVEST ROTATION PLAN", Color.White));

                    // Line 1: Harvest Order
                    linesToDraw.Add(("Harvest Order:", Color.White));
                    var pathString = string.Join(PATH_ARROW, path.Select(p => ExtractSeedData(p)?.Type switch { 1 => "P", 2 => "Y", 3 => "B", _ => "?" })) + $" ({GetPlotValueText(_cropRotationValue, _finalPlotSeedData)})";
                    linesToDraw.Add((pathString, Color.White)); 
                    linesToDraw.Add(("", Color.Transparent)); 

                    // Predicted Yield & Status
                    if (initialPlotData != null && _finalPlotSeedData != null)
                    {
                        var finalPlotSeedInfo = _finalPlotSeedData;
                        var plotColorName = finalPlotSeedInfo.Type switch {HarvestPicker.SEED_TYPE_PURPLE => "Purple", HarvestPicker.SEED_TYPE_YELLOW => "Yellow", HarvestPicker.SEED_TYPE_BLUE => "Blue", _ => "Unknown"};
                        
                        // Line 2: Predicted Yield title
                        linesToDraw.Add(($"Predicted Yield ({plotColorName} Plot):", Color.White));



                        // Actual vs Expected
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

                    // Calculate dimensions from gathered lines
                    foreach (var (text, _) in linesToDraw)
                    {
                        var textSize = Graphics.MeasureText(text);
                        if (textSize.X > maxWidth) maxWidth = textSize.X;
                        totalContentHeight += lineHeight * (string.IsNullOrWhiteSpace(text) ? 0.5f : 1.2f); 
                    }

                    var dynamicPanelWidth = maxWidth + PANEL_PADDING * 2;
                    var panelHeight = totalContentHeight + PANEL_PADDING * 2;
                    var titleBarHeight = lineHeight + PANEL_PADDING;

                    // --- PASS 2: DRAW --- 
                    var labelCenter = labelRect.X + labelRect.Width / 2;
                    var panelX = labelCenter - dynamicPanelWidth / 2;
                    var panelY = labelRect.Bottom + PANEL_Y_OFFSET;
                    var panelPos = new Vector2(panelX, panelY);

                    // Draw backgrounds
                    Graphics.DrawBox(new RectangleF(panelPos.X, panelPos.Y, dynamicPanelWidth, titleBarHeight), Settings.PurplePlotColor.Value);
                    var contentBgPos = new Vector2(panelPos.X, panelPos.Y + titleBarHeight);
                    Graphics.DrawBox(new RectangleF(contentBgPos.X, contentBgPos.Y, dynamicPanelWidth, panelHeight - titleBarHeight), new Color(0, 0, 0, 220));

                    // Draw content
                    var currentY = panelPos.Y + PANEL_PADDING / 2;
                    
                    // Title
                    var title = linesToDraw[0].Text;
                    var titleSize = Graphics.MeasureText(title);
                    Graphics.DrawText(title, new Vector2(panelPos.X + (dynamicPanelWidth - titleSize.X) / 2, currentY), linesToDraw[0].Color);
                    currentY += titleBarHeight;

                    // Draw rest of the lines
                    for (int i = 1; i < linesToDraw.Count; i++)
                    {
                        var (text, color) = linesToDraw[i];
                        var ySpacing = string.IsNullOrWhiteSpace(text) ? lineHeight * 0.5f : lineHeight * 1.2f;

                        // Special handling for the path line to draw it character by character with colors
                        if (i == 2) 
                        {
                            var pathDrawingPos = new Vector2(panelPos.X + PANEL_PADDING, currentY);
                            for(int j = 0; j < path.Count; j++)
                            {
                                var plot = path[j];
                                var data = ExtractSeedData(plot);
                                if(data == null) continue;
                                
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
        else if (_irrigatorPairs.Any())
        {
            // Find the best individual irrigator when crop rotation is not active
            (Entity bestIrrigator, double bestValue, SeedData bestSeedData) = (null, double.NegativeInfinity, null);

            foreach (var ((irrigator1, value1, seedData1), (irrigator2, value2, seedData2)) in _irrigatorPairs)
            {
                if (irrigator1 == null) continue;

                if (value1 > bestValue)
                {
                    bestValue = value1;
                    bestIrrigator = irrigator1;
                    bestSeedData = seedData1;
                }

                if (irrigator2 != null && value2 > bestValue)
                {
                    bestValue = value2;
                    bestIrrigator = irrigator2;
                    bestSeedData = seedData2;
                }
            }

            if (bestIrrigator != null)
            {
                // Check if the best irrigator is still valid before rendering its highlight
                if (!bestIrrigator.IsValid || 
                    (bestIrrigator.TryGetComponent<StateMachine>(out var sm) && 
                     sm.States.FirstOrDefault(s => s.Name == STATE_MACHINE_CURRENT_STATE)?.Value != STATE_MACHINE_READY_VALUE))
                {
                    bestIrrigator = null; // Invalidate bestIrrigator to stop rendering its highlight
                }
            }

            if (bestIrrigator != null)
            {
                if (isMapOpen && Settings.DrawTargetOnMap.Value)
                {
                    var mapPos = GameController.IngameState.Data.GetGridMapScreenPosition(bestIrrigator.GridPosNum);
                    var mapText = "1"; // Always 1 for the single best choice
                    var textSize = Graphics.MeasureText(mapText);
                    var textPos = mapPos - textSize / 2;
                    Graphics.DrawBox(textPos, textPos + textSize, Color.Black);
                    Graphics.DrawText(mapText, textPos, Color.White);

                    var plotColor = GetColorForPlotType(bestSeedData.Type);
                    var rectSize = new Vector2(MAP_RECT_SIZE, MAP_RECT_SIZE);
                    Graphics.DrawFrame(mapPos - rectSize / 2, mapPos + rectSize / 2, plotColor, 2);
                    var playerMapPos = GameController.IngameState.Data.GetGridMapScreenPosition(GameController.Player.GridPosNum);
                    Graphics.DrawLine(playerMapPos, mapPos, 2, plotColor);
                }

                var irrigatorLabel = FindIrrigatorLabel(bestIrrigator);
                if (irrigatorLabel != null)
                {
                    Graphics.DrawFrame(irrigatorLabel.GetClientRect(), Settings.TargetFrameColor.Value, 2);
                }
            }

            var choiceNum = 1;
            foreach (var ((irrigator1, value1, seedData1), (irrigator2, value2, seedData2)) in _irrigatorPairs)
            {
                if (seedData1 == null) continue; // Prevent crash if seed data is not ready

                if (irrigator1 == null) continue;
                
                if (irrigator2 != null)
                {
                    if (seedData2 == null) continue; // Ensure the second part of the pair is also valid

                    string text1, text2;
                    Color color1, color2;
                    
                    var text1Str = GetPlotValueText(value1, seedData1);
                    var text2Str = GetPlotValueText(value2, seedData2);

                    var plotTypeName1 = seedData1?.Type switch {HarvestPicker.SEED_TYPE_PURPLE => PLOT_NAME_WILD, HarvestPicker.SEED_TYPE_YELLOW => PLOT_NAME_VIVID, HarvestPicker.SEED_TYPE_BLUE => PLOT_NAME_PRIMAL, _ => PLOT_NAME_UNKNOWN_TYPE};
                    var plotColorName1 = seedData1?.Type switch {HarvestPicker.SEED_TYPE_PURPLE => "Purple", HarvestPicker.SEED_TYPE_YELLOW => "Yellow", HarvestPicker.SEED_TYPE_BLUE => "Blue", _ => "Unknown"};
                    var plotTypeName2 = seedData2?.Type switch {HarvestPicker.SEED_TYPE_PURPLE => PLOT_NAME_WILD, HarvestPicker.SEED_TYPE_YELLOW => PLOT_NAME_VIVID, HarvestPicker.SEED_TYPE_BLUE => PLOT_NAME_PRIMAL, _ => PLOT_NAME_UNKNOWN_TYPE};
                    var plotColorName2 = seedData2?.Type switch {HarvestPicker.SEED_TYPE_PURPLE => "Purple", HarvestPicker.SEED_TYPE_YELLOW => "Yellow", HarvestPicker.SEED_TYPE_BLUE => "Blue", _ => "Unknown"};

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
                    
                    var textPos1 = GameController.IngameState.Camera.WorldToScreen(irrigator1.PosNum);
                    Graphics.DrawBox(textPos1, textPos1 + Graphics.MeasureText(text1), Color.Black);
                    Graphics.DrawText(text1, textPos1, color1);

                    var textPos2 = GameController.IngameState.Camera.WorldToScreen(irrigator2.PosNum);
                    Graphics.DrawBox(textPos2, textPos2 + Graphics.MeasureText(text2), Color.Black);
                    Graphics.DrawText(text2, textPos2, color2);
                }
                else
                {
                    var plotTypeName = seedData1?.Type switch {HarvestPicker.SEED_TYPE_PURPLE => PLOT_NAME_WILD, HarvestPicker.SEED_TYPE_YELLOW => PLOT_NAME_VIVID, HarvestPicker.SEED_TYPE_BLUE => PLOT_NAME_PRIMAL, _ => PLOT_NAME_UNKNOWN_TYPE};
                    var plotColorName = seedData1?.Type switch {HarvestPicker.SEED_TYPE_PURPLE => "Purple", HarvestPicker.SEED_TYPE_YELLOW => "Yellow", HarvestPicker.SEED_TYPE_BLUE => "Blue", _ => "Unknown"};
                    var text = $"Choice {choiceNum}: {plotTypeName} ({plotColorName}) {GetPlotValueText(value1, seedData1)}";
                    if (irrigator1 == null) continue; // Add null check for irrigator1
                    var textPos = GameController.IngameState.Camera.WorldToScreen(irrigator1.PosNum);
                    Graphics.DrawBox(textPos, textPos + Graphics.MeasureText(text), Color.Black);
                    Graphics.DrawText(text, textPos, Settings.NeutralColor.Value);
                }
                choiceNum++;
            }
        }
    }
}