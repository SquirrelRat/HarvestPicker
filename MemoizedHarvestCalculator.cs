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
    private int _cacheHits = 0;
    private int _cacheMisses = 0;
    private int _totalRecursiveCalls = 0;
    private Dictionary<int, int> _callsByDepth = new();

    public MemoizedHarvestCalculator(Func<SeedData, int, SeedData> upgradeFunc,
                                      Dictionary<Entity, Entity> pairLookup,
                                      HarvestPicker harvestPicker)
    {
        _upgradeFunc = upgradeFunc;
        _pairLookup = pairLookup;
        _harvestPicker = harvestPicker;
    }

        public HarvestSequenceResult CalculateBestHarvestSequence(

            List<(SeedData Data, Entity Entity)> remaining,

            double chanceToNotWither,

            ref int currentPermutationCount,

            int maxPermutations,

            int depth = 0)

        {

            _totalRecursiveCalls++;

            if (!_callsByDepth.ContainsKey(depth)) _callsByDepth[depth] = 0;

            _callsByDepth[depth]++;

    

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

                var singlePlotResult = new HarvestSequenceResult(_harvestPicker.CalculateIrrigatorValue(plot.Data), new List<Entity> { plot.Entity }, plot.Data, plot.Data);

    

                return singlePlotResult;

            }

    

            string cacheKey = CreateCacheKey(remaining);

            if (_memoCache.TryGetValue(cacheKey, out var cachedResult))

            {

                _cacheHits++;

                return cachedResult;

            }

            _cacheMisses++;

    

            HarvestSequenceResult absoluteBestResult = null;

    

            for (int i = 0; i < remaining.Count; i++)

            {

                var (chosenData, chosenEntity) = remaining[i];

                

                var baseRemaining = remaining.Where((_, idx) => idx != i).ToList();

                _pairLookup.TryGetValue(chosenEntity, out var paired);

                bool hasPaired = paired != null && baseRemaining.Exists(p => p.Entity == paired);

                

                var upgradedRemaining = baseRemaining

                    .Where(x => !hasPaired || x.Entity != paired)

                    .Select(x => (Data: _upgradeFunc(x.Data, chosenData.Type), x.Entity))

                    .ToList();

                

                var recursiveAbsoluteBest = CalculateBestHarvestSequence(upgradedRemaining, chanceToNotWither, ref currentPermutationCount, maxPermutations, depth + 1);

    

                if (recursiveAbsoluteBest != null)

                {

                                                                    var currentAbsoluteResult = new HarvestSequenceResult(

                                                                        _harvestPicker.CalculateIrrigatorValue(chosenData) + recursiveAbsoluteBest.FinalPlotValue,

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



    private string CreateCacheKey(List<(SeedData Data, Entity Entity)> remaining)
    {
        Span<(int EntityHash, int Type, int T1, int T2, int T3, int T4)> values = stackalloc (int, int, int, int, int, int)[remaining.Count];
        for (int i = 0; i < remaining.Count; i++)
        {
            var (data, entity) = remaining[i];
            if (data == null) continue;
                values[i] = (
                entity.Address.GetHashCode(),
                data.Type,
                (int)(data.T1Plants * 1000),
                (int)(data.T2Plants * 1000),
                (int)(data.T3Plants * 1000),
                (int)(data.T4Plants * 1000)
            );
        }
        values.Sort((a, b) => a.EntityHash.CompareTo(b.EntityHash));
        var hash = new HashCode();
        foreach (var v in values)
        {
            hash.Add(v.EntityHash);
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
    
    public void LogDetailedStats(Action<string> logAction)
    {
        logAction($"=== ALGORITHM STATISTICS ===");
        logAction($"Total recursive calls: {_totalRecursiveCalls}");
        logAction($"Cache hits: {_cacheHits}");
        logAction($"Cache misses: {_cacheMisses}");
        logAction($"Cache hit rate: {(_cacheHits + _cacheMisses > 0 ? (double)_cacheHits / (_cacheHits + _cacheMisses) * 100 : 0):F1}%");
        logAction("Calls by depth:");
        foreach (var kvp in _callsByDepth.OrderBy(x => x.Key))
        {
            logAction($"  Depth {kvp.Key}: {kvp.Value} calls");
        }
        _totalRecursiveCalls = 0;
        _callsByDepth.Clear();
    }
}