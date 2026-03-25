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

    [Menu("Draw Target On Map", "Display Settings")]
    public ToggleNode DrawTargetOnMap { get; set; } = new ToggleNode(true);
    [Menu("Show Rotation Plan", "Shows the Harvest Rotation Plan panel attached to current target.")]
    public ToggleNode ShowPathSummary { get; set; } = new ToggleNode(true);
    [Menu("Target Frame Color", "Color of the frame drawn around the target irrigator.")]
    public ColorNode TargetFrameColor { get; set; } = new ColorNode(Color.Purple);

    [Menu("Bad Choice Color", "Choice Colors")]
    public ColorNode BadColor { get; set; } = new ColorNode(Color.Red);
    [Menu("Neutral Choice Color")]
    public ColorNode NeutralColor { get; set; } = new ColorNode(Color.Yellow);
    [Menu("Good Choice Color")]
    public ColorNode GoodColor { get; set; } = new ColorNode(Color.Green);

    [Menu("Yellow Plot Color", "Plot Colors")]
    public ColorNode YellowPlotColor { get; set; } = new ColorNode(Color.Yellow);
    [Menu("Purple Plot Color")]
    public ColorNode PurplePlotColor { get; set; } = new ColorNode(Color.Purple);
    [Menu("Blue Plot Color")]
    public ColorNode BluePlotColor { get; set; } = new ColorNode(Color.SkyBlue);

    [Menu("League", "Price Fetching")]
    public ListNode League { get; set; } = new ListNode() { Value = "Necropolis" };
    [Menu("Auto-Refresh Interval (Minutes)", "How often to automatically refresh prices from poe.ninja.")]
    public RangeNode<int> PriceRefreshPeriodMinutes { get; set; } = new RangeNode<int>(15, 5, 60);

    [Menu("T1 Lifeforce", "Value Calculation (Default values based on community research - adjust only with verified data)")]
    public RangeNode<int> SeedsPerT1Plant { get; set; } = new RangeNode<int>(7, 0, 300);
    [Menu("T2 Lifeforce", "Expected lifeforce drops from T2 monsters (avg: ~18).")]
    public RangeNode<int> SeedsPerT2Plant { get; set; } = new RangeNode<int>(18, 0, 300);
    [Menu("T3 Lifeforce", "Expected lifeforce drops from T3 monsters (avg: ~47).")]
    public RangeNode<int> SeedsPerT3Plant { get; set; } = new RangeNode<int>(47, 0, 300);
    [Menu("T4 Lifeforce", "Expected lifeforce drops from T4 monsters (avg: ~235).")]
    public RangeNode<int> SeedsPerT4Plant { get; set; } = new RangeNode<int>(235, 0, 900);
    [Menu("Sacred Lifeforce Chance (T4)", "Chance for T4 plants to drop Sacred Lifeforce (approx. 12%).")]
    public RangeNode<float> T4PlantWhiteSeedChance { get; set; } = new RangeNode<float>(0.12f, 0, 1f);

    [Menu("T1→T2 Chance (Fallback)", "Crop Rotation Settings (T2→T3 and T3→T4 are auto-detected from game - these are fallback values)")]
    public RangeNode<float> CropRotationT1UpgradeChance { get; set; } = new RangeNode<float>(0.25f, 0, 1f);
    [Menu("T2→T3 Chance (Fallback)", "Used if game value not available. Auto-detected from MapStats.")]
    public RangeNode<float> CropRotationT2UpgradeChance { get; set; } = new RangeNode<float>(0.204f, 0, 1f);
    [Menu("T3→T4 Chance (Fallback)", "Used if game value not available. Auto-detected from MapStats.")]
    public RangeNode<float> CropRotationT3UpgradeChance { get; set; } = new RangeNode<float>(0.035f, 0, 1f);
    [Menu("Max Paths Checked", "Limits how many crop rotation paths are checked. Lower is faster but may be less optimal.")]
    public RangeNode<int> MaxPermutations { get; set; } = new RangeNode<int>(50000, 0, 3628800);
    [Menu("Use Wither Chance", "Apply wither chance discount to deeper plots' value (riskier = less value).")]
    public ToggleNode UseWitherChance { get; set; } = new ToggleNode(false);

    [Menu("Log Detailed Rotation Info", "Writes extra information about the crop rotation calculation to the logs.")]
    public ToggleNode LogDetailedForCropRotation { get; set; } = new ToggleNode(false);

    [Menu("Initial Load Delay (ms)", "Performance Settings (Delay to handle inconsistent plot detection)")]
    public RangeNode<int> InitialLoadDelay { get; set; } = new RangeNode<int>(250, 0, 2000);
}
