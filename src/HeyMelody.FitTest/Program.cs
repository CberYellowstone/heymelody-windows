using HeyMelodyWindows.Calibration.UI;
namespace HeyMelodyWindows.FitTest;
internal static class Program
{
    [STAThread] public static int Main(string[] args) => CalibrationApplication.Run("fit", args);
}
