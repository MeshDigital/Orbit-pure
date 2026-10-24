using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SLSKDONET.Views.Avalonia;

/// <summary>
/// Makes every <see cref="NumericUpDown"/> follow ORBIT's number rule (see Utils/NumFormat and
/// DOCS/UI_DESIGN_SYSTEM.md "Numbers"): values display invariantly ("174.0", never "174,0" on a
/// Dutch/Belgian Windows), and typing the local decimal comma still works because a typed ","
/// is entered as ".". Thousands separators are not accepted in these fields (no BPM, gain or
/// buffer size needs one), so a pasted "1,5" cannot silently become 15.
/// </summary>
public static class NumericInputLocale
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        // Not OverrideDefaultValue: NumericUpDown owns these properties, and Avalonia throws
        // "Metadata is already set" when the owner type's default is overridden — which crashed
        // every launch. Apply them as each control loads instead, unless a view set its own.
        Control.LoadedEvent.AddClassHandler<NumericUpDown>((n, _) =>
        {
            if (!n.IsSet(NumericUpDown.NumberFormatProperty))
                n.NumberFormat = NumberFormatInfo.InvariantInfo;
            if (!n.IsSet(NumericUpDown.ParsingNumberStyleProperty))
                n.ParsingNumberStyle = NumberStyles.Float;
        });

        InputElement.TextInputEvent.AddClassHandler<NumericUpDown>(
            (_, e) => { if (e.Text == ",") e.Text = "."; },
            RoutingStrategies.Tunnel);
    }
}
