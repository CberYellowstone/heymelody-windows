using HeyMelodyWindows.Calibration.UI;
namespace HeyMelodyWindows.HearingSetup;
internal static class Program
{
    [STAThread] public static int Main(string[] args) => CalibrationApplication.Run("hearing", args);
}
