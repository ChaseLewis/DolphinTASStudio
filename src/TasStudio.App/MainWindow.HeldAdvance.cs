using TasStudio.Core;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private bool _advanceHeld;
    private bool _suppressF11UntilRelease;
    private ControllerState? _heldManualInput;
    private Task? _heldAdvanceTask;

    private void StartHeldAdvance()
    {
        if (_advanceHeld || _busy || !_execution.IsLoaded) return;
        _heldManualInput = ShownInput(_pendingTasInput);
        _advanceHeld = true;
        _suppressF11UntilRelease = true;
        if (_heldAdvanceTask?.IsCompleted != false) _heldAdvanceTask = AdvanceWhileHeld();
    }

    private void StopHeldAdvance()
    {
        if (_advanceHeld) _inspectorReloadPending = true;
        _advanceHeld = false; _heldManualInput = null;
    }

    private async Task AdvanceWhileHeld()
    {
        // One operation for the entire hold: per-frame Perform calls repeatedly
        // toggle every command's enabled state and restart its visual transition.
        await Perform(async () =>
        {
            try
            {
                Refresh();
                while (_advanceHeld && !_homeVisible && !_dialogOpen && !_closingApproved && _execution.IsLoaded)
                {
                    await Step();
                    if (!_advanceHeld) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(16));
                }
            }
            finally { StopHeldAdvance(); }
        });
    }
}
