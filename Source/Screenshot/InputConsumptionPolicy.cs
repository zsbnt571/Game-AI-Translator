namespace ScreenshotTranslationUiTester;

internal enum InputConsumptionMode { ObserveOnly, TriggerAndPassThrough, ExclusiveCapture }
internal enum InputContextKind { Idle, MainWindow, Preview, ScreenshotSelection, BindingCapture, HotkeyArming, ModalProtected }

internal sealed record InputConsumptionDecision(
    InputConsumptionMode Mode, bool BindingMatch, bool Handled, bool Suppressed,
    bool PassThrough, string Reason)
{
    internal static InputConsumptionDecision Observe(string reason = "No binding match") =>
        new(InputConsumptionMode.ObserveOnly, false, false, false, true, reason);
}

internal static class InputConsumptionPolicy
{
    internal static InputConsumptionDecision Decide(InputContextKind context, bool bindingMatch, bool cancelInput)
    {
        if (context is InputContextKind.ScreenshotSelection or InputContextKind.BindingCapture)
        {
            var ownsInput = context == InputContextKind.BindingCapture || cancelInput;
            return ownsInput
                ? new(InputConsumptionMode.ExclusiveCapture, bindingMatch, true, true, false,
                    context == InputContextKind.BindingCapture ? "Binding capture owns this physical input" : "Active exclusive context consumes cancel input")
                : InputConsumptionDecision.Observe("Exclusive context does not require this input");
        }

        return bindingMatch
            ? new(InputConsumptionMode.TriggerAndPassThrough, true, true, false, true,
                "Global binding triggers action without suppressing the foreground application")
            : InputConsumptionDecision.Observe();
    }
}


