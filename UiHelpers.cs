using ImGuiNET;
using SharpDX;
using Vector4 = System.Numerics.Vector4;

namespace HarvestPicker;

internal static class UiHelpers
{
    public static bool Checkbox(string label, bool value)
    {
        ImGui.Checkbox(label, ref value);
        return value;
    }

    public static int IntDrag(string label, int value, int minValue, int maxValue, float dragSpeed)
    {
        var currentValue = value;
        ImGui.DragInt(label, ref currentValue, dragSpeed, minValue, maxValue);
        return currentValue;
    }

    public static float FloatDrag(string label, float value, float minValue, float maxValue, float dragSpeed)
    {
        var currentValue = value;
        ImGui.DragFloat(label, ref currentValue, dragSpeed, minValue, maxValue);
        return currentValue;
    }

    public static Color ColorPicker(string label, Color inputColor)
    {
        var color = inputColor.ToVector4();
        var pickerColor = new Vector4(color.X, color.Y, color.Z, color.W);
        if (ImGui.ColorEdit4(label, ref pickerColor, ImGuiColorEditFlags.AlphaBar))
        {
            return new Color(pickerColor.X, pickerColor.Y, pickerColor.Z, pickerColor.W);
        }
        return inputColor;
    }

    public static uint PackColor(float red, float green, float blue, float alpha)
    {
        return (uint)(alpha * 255) << 24 | (uint)(blue * 255) << 16 | (uint)(green * 255) << 8 | (uint)(red * 255);
    }
}
