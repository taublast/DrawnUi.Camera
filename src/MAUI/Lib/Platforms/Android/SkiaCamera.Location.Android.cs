using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using Location = Android.Locations.Location;

namespace DrawnUi.Camera;

public partial class SkiaCamera
{
    /// <summary>
    /// Whether the app holds location permission, precise or approximate, read straight from Android without
    /// prompting. MAUI's Permissions.CheckStatusAsync&lt;LocationWhenInUse&gt; reports Denied for an approximate-only grant,
    /// and its RequestAsync (also called inside MAUI's Geolocation) asks the user again to upgrade to precise.
    /// </summary>
    static bool HasAndroidLocationPermission(out bool precise)
    {
        var context = Android.App.Application.Context;
        precise = context.CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Permission.Granted;
        return precise || context.CheckSelfPermission(Android.Manifest.Permission.AccessCoarseLocation) == Permission.Granted;
    }

    /// <summary>
    /// The location under an approximate-only grant, from Android's LocationManager, without any prompt: the most
    /// recent last known location of the enabled providers, else (Android 11+) a current location within the timeout.
    /// Android coarsens what an approximate grant receives.
    /// </summary>
    static async Task<Location> GetApproximateLocationAsync(int msTimeout)
    {
        var context = Android.App.Application.Context;
        if (context.GetSystemService(Context.LocationService) is not LocationManager manager)
            return null;

        Location best = null;
        foreach (var provider in manager.GetProviders(true))
        {
            try
            {
                var location = manager.GetLastKnownLocation(provider);
                if (location != null && (best == null || location.Time > best.Time))
                    best = location;
            }
            catch (Java.Lang.SecurityException)
            {
                // a provider this grant may not read
            }
        }

        if (best != null || Build.VERSION.SdkInt < BuildVersionCodes.R)
            return best;

        var current = Build.VERSION.SdkInt >= BuildVersionCodes.S && manager.IsProviderEnabled(LocationManager.FusedProvider)
            ? LocationManager.FusedProvider
            : LocationManager.NetworkProvider;
        if (!manager.IsProviderEnabled(current))
            return null;

        var result = new TaskCompletionSource<Location>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationSignal();
        manager.GetCurrentLocation(current, cancel, context.MainExecutor, new LocationConsumer(result));
        if (await Task.WhenAny(result.Task, Task.Delay(msTimeout)) != result.Task)
        {
            cancel.Cancel();
            return null;
        }
        return result.Task.Result;
    }

    sealed class LocationConsumer : Java.Lang.Object, Java.Util.Functions.IConsumer
    {
        readonly TaskCompletionSource<Location> _result;

        public LocationConsumer(TaskCompletionSource<Location> result) => _result = result;

        public void Accept(Java.Lang.Object t) => _result.TrySetResult(t as Location);
    }
}
