using System.Diagnostics.CodeAnalysis;
namespace ScreenshotTranslationUiTester;

// Saved names (including unavailable fonts) remain selectable and round-trip.
// The display itself is never a native editable text field.
internal sealed class FontChoiceBox : GamePlanBox
{
    [AllowNull]
    public override string Text
    {
        get=>base.Text;
        set
        {
            if(string.IsNullOrEmpty(value)){SelectedIndex=-1;base.Text=string.Empty;return;}
            int index=FindStringExact(value);
            if(index<0)index=Items.Add(value);
            SelectedIndex=index;
        }
    }
}
