using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace HeyMelodyWindows.App;

/// <summary>Commits user input without treating a state push or initial binding as an edit.</summary>
internal static class UserInputCommit
{
    public static void Combo(ComboBox control, Func<Task> commit)
    {
        var userInput = false;
        control.AddHandler(InputElement.PointerPressedEvent, (_, _) => userInput = true,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        control.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.Enter or Key.Space)
                userInput = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        control.SelectionChanged += async (_, _) =>
        {
            if (!userInput || !control.IsEnabled || control.SelectedItem is null) return;
            userInput = false;
            await commit();
        };
        control.LostFocus += (_, _) => { if (!control.IsDropDownOpen) userInput = false; };
        control.DropDownClosed += (_, _) => userInput = false;
    }

    public static void Slider(Slider control, Func<Task> commit)
    {
        var initial = control.Value;
        control.AddHandler(InputElement.PointerPressedEvent, (_, _) => initial = control.Value,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        control.AddHandler(InputElement.PointerReleasedEvent, async (_, _) =>
        {
            if (control.IsEnabled && control.Value != initial) await commit();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        control.AddHandler(InputElement.KeyUpEvent, async (_, e) =>
        {
            if (control.IsEnabled && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
                await commit();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
