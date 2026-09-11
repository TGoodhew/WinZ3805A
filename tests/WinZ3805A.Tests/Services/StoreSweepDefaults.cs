using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>
/// The pre-#475 shapes of <see cref="ReceiverStateStore.UpdateFast"/> and
/// <see cref="ReceiverStateStore.UpdateFull"/>, for the tests that were written against them.
/// </summary>
/// <remarks>
/// <para>
/// #475 gave both methods a <see cref="FastFields"/> argument, because a null from a sweep means
/// "asked, and the receiver did not answer" only for the fields that sweep asks about. Every test
/// written before that describes a SmartClock-shaped sweep — one that asks all six — so these
/// overloads say <see cref="FastFields.All"/> once here rather than forty-odd times across a suite
/// where it is not the subject.
/// </para>
/// <para>
/// <b>Deliberately not a default on the store itself.</b> There the argument is required, because
/// the defect being fixed was exactly a driver silently inheriting the SmartClock's shape. A test
/// that is about tier coverage calls the real method and names the fields it means; these exist
/// for the tests that are about something else.
/// </para>
/// <para>
/// Declared in the production namespace so that the files already using the store pick them up
/// without a new <c>using</c>. They live in the test assembly and ship nowhere.
/// </para>
/// </remarks>
internal static class StoreSweepDefaults
{
    /// <summary>Records a sweep that carries every field, as the SmartClock's does.</summary>
    public static void UpdateFast(
        this ReceiverStateStore store,
        string? syncState,
        int? tfom,
        int? ffom,
        double? onePpsTiNanoseconds,
        double? oscillatorControl,
        int? trackedCount)
    {
        ArgumentNullException.ThrowIfNull(store);

        store.UpdateFast(
            syncState,
            tfom,
            ffom,
            onePpsTiNanoseconds,
            oscillatorControl,
            trackedCount,
            FastFields.All);
    }

    /// <summary>Records a full screen read by a driver whose sweep carries every field.</summary>
    public static void UpdateFull(this ReceiverStateStore store, ReceiverStatus status)
    {
        ArgumentNullException.ThrowIfNull(store);

        store.UpdateFull(status, FastFields.All);
    }
}
