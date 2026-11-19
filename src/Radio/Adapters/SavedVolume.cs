using BepInEx.Configuration;
using ExiledToolkit.Kit;

namespace Radio.Adapters;

/// <summary>
/// Puts the Radio tab's volume slider in the config file. A drag sends a new value every tenth of a second, so the
/// file is written once the slider has been still for a second.
/// </summary>
internal sealed class SavedVolume
{
    private const double SaveAfterSeconds = 1;

    private readonly ConfigEntry<int> _entry;
    private double _saveAt = double.NaN;

    public SavedVolume(ConfigEntry<int> entry) => _entry = entry;

    public void Keep(int percent)
    {
        var file = _entry.ConfigFile;
        var wasSaving = file.SaveOnConfigSet;

        file.SaveOnConfigSet = false;

        try
        {
            _entry.Value = percent;
        }
        finally
        {
            file.SaveOnConfigSet = wasSaving;
        }

        _saveAt = Clock.Now + SaveAfterSeconds;
    }

    /// <summary>Once a frame, and when the game closes with <paramref name="now"/> at infinity.</summary>
    public void SaveIfDue(double now)
    {
        if (double.IsNaN(_saveAt) || now < _saveAt) return;

        _saveAt = double.NaN;
        _entry.ConfigFile.Save();
    }
}
