using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using SharpDX;
using System.Numerics;

namespace HarvestPicker;

public class HarvestPickerSettings : ISettings
{
    public const string EVALUATION_MODE_TRADE = "Value (Trade)";
    public const string EVALUATION_MODE_SSF = "Max T3/T4 (SSF)";

    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    public ListNode EvaluationMode { get; set; } = new ListNode { Value = EVALUATION_MODE_TRADE, Values = { EVALUATION_MODE_TRADE, EVALUATION_MODE_SSF } };

    [Menu("Reload Prices", "Manually fetches the latest prices from poe.ninja.")]
    [JsonIgnore]
    public ButtonNode ReloadPrices { get; set; } = new ButtonNode();
    
    public ToggleNode DrawTargetOnMap { get; set; } = new ToggleNode(true);
    [Menu("Show Attached Rotation Plan", "Shows the Harvest Rotation Plan panel attached to the current target.")]
    public ToggleNode ShowPathSummary { get; set; } = new ToggleNode(true);
    [Menu("Target Frame Color", "Color of the frame drawn around the target irrigator.")]
    public ColorNode TargetFrameColor { get; set; } = new ColorNode(Color.Purple);

    public ColorNode BadColor { get; set; } = new ColorNode(Color.Red);
    [Menu("Neutral Choice Color")]
    public ColorNode NeutralColor { get; set; } = new ColorNode(Color.Yellow);
    [Menu("Good Choice Color")]
    public ColorNode GoodColor { get; set; } = new ColorNode(Color.Green);

    public ColorNode YellowPlotColor { get; set; } = new ColorNode(Color.Yellow);
    [Menu("Purple Plot Color")]
    public ColorNode PurplePlotColor { get; set; } = new ColorNode(Color.Purple);
    [Menu("Blue Plot Color")]
    public ColorNode BluePlotColor { get; set; } = new ColorNode(Color.SkyBlue);

    [Menu("Price Fetching")]
    public ListNode League { get; set; } = new ListNode() { Value = "Necropolis" };
    [Menu("Price Refresh (Minutes)", "How often to automatically refresh prices from poe.ninja.")]
    public RangeNode<int> PriceRefreshPeriodMinutes { get; set; } = new RangeNode<int>(15, 5, 60);
    [Menu("Min Poe.ninja Data Freshness (Minutes)", "Prices will only be refreshed if the poe.ninja data itself is older than this value.")]
    public RangeNode<int> MinPoeNinjaDataFreshnessMinutes { get; set; } = new RangeNode<int>(30, 1, 120);
    
    [Menu("Value Calculation")]
    public RangeNode<int> SeedsPerT1Plant { get; set; } = new RangeNode<int>(7, 0, 300);
    [Menu("Lifeforce per T2", "Base lifeforce amount dropped by a Tier 2 plant.")]
    public RangeNode<int> SeedsPerT2Plant { get; set; } = new RangeNode<int>(18, 0, 300);
    [Menu("Lifeforce per T3", "Base lifeforce amount dropped by a Tier 3 plant.")]
    public RangeNode<int> SeedsPerT3Plant { get; set; } = new RangeNode<int>(47, 0, 300);
    [Menu("Lifeforce per T4", "Base lifeforce amount dropped by a Tier 4 plant.")]
    public RangeNode<int> SeedsPerT4Plant { get; set; } = new RangeNode<int>(235, 0, 900);
    [Menu("T4 Sacred Lifeforce Chance", "Chance for a T4 plant to drop Sacred Lifeforce.")]
    public RangeNode<float> T4PlantWhiteSeedChance { get; set; } = new RangeNode<float>(0.12f, 0, 1f);

    [Menu("Crop Rotation")]
    public RangeNode<float> CropRotationT1UpgradeChance { get; set; } = new RangeNode<float>(0.25f, 0, 1f);
    [Menu("T2->T3 Upgrade Chance", "Chance for a T2 plant to upgrade to T3 with Crop Rotation.")]
    public RangeNode<float> CropRotationT2UpgradeChance { get; set; } = new RangeNode<float>(0.204f, 0, 1f);
    [Menu("T3->T4 Upgrade Chance", "Chance for a T3 plant to upgrade to T4 with Crop Rotation.")]
    public RangeNode<float> CropRotationT3UpgradeChance { get; set; } = new RangeNode<float>(0.035f, 0, 1f);
    [Menu("Max Permutations (Performance)", "Limits how many crop rotation paths are checked. Lower is faster but may be less optimal.")]
    public RangeNode<int> MaxPermutations { get; set; } = new RangeNode<int>(50000, 0, 3628800);

    [Menu("Log Detailed Rotation Info", "Writes extra information about the crop rotation calculation to the logs.")]
    public ToggleNode LogDetailedForCropRotation { get; set; } = new ToggleNode(false);

    [Menu("Initial Load Delay (ms)", "Delay in milliseconds after entering an area before processing Harvest plots. Increase if plots are not detected consistently.")]
    public RangeNode<int> InitialLoadDelay { get; set; } = new RangeNode<int>(250, 0, 2000);
}