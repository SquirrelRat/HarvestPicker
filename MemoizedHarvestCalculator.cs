using ExileCore.PoEMemory.MemoryObjects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HarvestPicker;

public record HarvestSequenceResult(double FinalPlotValue, List<Entity> Sequence, SeedData FinalPlotData, SeedData AggregatedSeedData);

public class MemoizedHarvestCalculator
{
    private readonly Dictionary<string, HarvestSequenceResult> _memoCache = new();
    private readonly Func<SeedData, int, SeedData> _upgradeFunc;
    private readonly Dictionary<Entity, Entity> _pairLookup;
    private readonly HarvestPicker _harvestPicker;
    private readonly bool _useWitherChance;
    private int _cacheHits = 0;
    private int _cacheMisses = 0;

    public MemoizedHarvestCalculator(Func<SeedData, int, SeedData> upgradeFunc,
                                      Dictionary<Entity, Entity> pairLookup,
                                      HarvestPicker harvestPicker,
                                      bool useWitherChance = false)
    {
        _upgradeFunc = upgradeFunc;
        _pairLookup = pairLookup;
        _harvestPicker = harvestPicker;
        _useWitherChance = useWitherChance;
    }

    public HarvestSequenceResult CalculateBestHarvestSequence(
        List<(SeedData Data, Entity Entity)> remaining,
        double chanceToNotWither,
        ref int currentPermutationCount,
        int maxPermutations,
        int depth = 0,
        double bestValueFound = 0)
    {
        currentPermutationCount++;

        if (currentPermutationCount > maxPermutations)
        {
            return null;
        }

        if (!remaining.Any())
        {
            return new HarvestSequenceResult(0, new List<Entity>(), null, null);
        }

        if (remaining.Count == 1)
        {
            var plot = remaining[0];
            var value = _harvestPicker.CalculateIrrigatorValue(plot.Data);
            if (_useWitherChance && depth > 0)
            {
                value *= Math.Pow(chanceToNotWither, depth);
            }
            return new HarvestSequenceResult(value, new List<Entity> { plot.Entity }, plot.Data, plot.Data);
        }

        string cacheKey = CreateCacheKey(remaining);

        if (_memoCache.TryGetValue(cacheKey, out var cachedResult))
        {
            _cacheHits++;
            return cachedResult;
        }

        _cacheMisses++;

        HarvestSequenceResult absoluteBestResult = null;

        var sortedRemaining = remaining
            .Select((item, index) => (item, index))
            .OrderByDescending(x => _harvestPicker.CalculateIrrigatorValue(x.item.Data))
            .ToList();

        double remainingMaxValueSum = remaining.Sum(r => CalculateMaxPossibleValue(r.Data));

        foreach (var ((chosenData, chosenEntity), originalIndex) in sortedRemaining)
        {
            var chosenValue = _harvestPicker.CalculateIrrigatorValue(chosenData);

            if (_useWitherChance && depth > 0)
            {
                chosenValue *= Math.Pow(chanceToNotWither, depth);
            }

            double upperBound = chosenValue + remainingMaxValueSum - CalculateMaxPossibleValue(chosenData);
            if (upperBound <= bestValueFound)
            {
                continue;
            }

            var baseRemaining = remaining.Where((_, idx) => idx != originalIndex).ToList();

            _pairLookup.TryGetValue(chosenEntity, out var paired);
            bool hasPaired = paired != null && baseRemaining.Exists(p => p.Entity == paired);

            var upgradedRemaining = baseRemaining
                .Where(x => !hasPaired || x.Entity != paired)
                .Select(x => (Data: _upgradeFunc(x.Data, chosenData.Type), x.Entity))
                .ToList();

            double updatedBestValueFound = depth == 0 && absoluteBestResult != null 
                ? Math.Max(bestValueFound, absoluteBestResult.FinalPlotValue) 
                : bestValueFound;

            var recursiveAbsoluteBest = CalculateBestHarvestSequence(
                upgradedRemaining, 
                chanceToNotWither, 
                ref currentPermutationCount, 
                maxPermutations, 
                depth + 1,
                updatedBestValueFound);

            if (recursiveAbsoluteBest != null)
            {
                double recursiveValue = recursiveAbsoluteBest.FinalPlotValue;
                var currentAbsoluteResult = new HarvestSequenceResult(
                    chosenValue + recursiveValue,
                    new List<Entity> { chosenEntity }.Concat(recursiveAbsoluteBest.Sequence).ToList(),
                    chosenData,
                    _harvestPicker.SumSeedData(chosenData, recursiveAbsoluteBest.AggregatedSeedData)
                );

                if (absoluteBestResult == null || currentAbsoluteResult.FinalPlotValue > absoluteBestResult.FinalPlotValue)
                {
                    absoluteBestResult = currentAbsoluteResult;
                }
            }
        }

        _memoCache[cacheKey] = absoluteBestResult;
        return absoluteBestResult;
    }

    private double CalculateMaxPossibleValue(SeedData data)
    {
        if (data == null) return 0;
        
        double baseValue = _harvestPicker.CalculateIrrigatorValue(data);
        
        double upgradedValue = _harvestPicker.CalculateIrrigatorValue(new SeedData(
            data.Type,
            0,
            data.T1Plants + data.T2Plants,
            data.T3Plants,
            data.T4Plants + data.T3Plants
        ));
        
        return Math.Max(baseValue, upgradedValue);
    }

    private string CreateCacheKey(List<(SeedData Data, Entity Entity)> remaining)
    {
        Span<(long EntityAddress, int Type, int T1, int T2, int T3, int T4)> values = stackalloc (long, int, int, int, int, int)[remaining.Count];
        for (int i = 0; i < remaining.Count; i++)
        {
            var (data, entity) = remaining[i];
            if (data == null) continue;
            values[i] = (
                entity.Address,
                data.Type,
                (int)(data.T1Plants * 1000),
                (int)(data.T2Plants * 1000),
                (int)(data.T3Plants * 1000),
                (int)(data.T4Plants * 1000)
            );
        }
        values.Sort((a, b) => a.EntityAddress.CompareTo(b.EntityAddress));
        var hash = new HashCode();
        foreach (var v in values)
        {
            hash.Add(v.EntityAddress);
            hash.Add(v.Type);
            hash.Add(v.T1);
            hash.Add(v.T2);
            hash.Add(v.T3);
            hash.Add(v.T4);
        }
        return hash.ToHashCode().ToString();
    }

    public void LogCacheStats(Action<string> logger)
    {
        logger($"Cache stats - Hits: {_cacheHits}, Misses: {_cacheMisses}, Size: {_memoCache.Count}");
    }
}
