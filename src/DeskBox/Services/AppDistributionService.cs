using DeskBox.Platform;
using System.Text;

namespace DeskBox.Services;

public sealed class AppDistributionService
{
    public static AppDistributionService Current { get; } = new();

    public AppDistributionService(AppDistributionChannel? channel = null)
    {
        IsPackaged = PackageIdentityHelper.HasPackageIdentity();
#if DESKBOX_STORE
        CurrentChannel = AppDistributionChannel.MicrosoftStore;
#else
        CurrentChannel = channel ?? AppDistributionChannel.Direct;
#endif
    }

    public AppDistributionChannel CurrentChannel { get; }
    public bool IsPackaged { get; }
    public bool IsDirect => CurrentChannel == AppDistributionChannel.Direct;
    public bool IsMicrosoftStore => CurrentChannel == AppDistributionChannel.MicrosoftStore;
    public string ChannelName => IsMicrosoftStore ? "Microsoft Store" : "Direct";

    private static class PackageIdentityHelper
    {
        private const int AppModelErrorNoPackage = 15700;
        private const int ErrorInsufficientBuffer = 122;

        public static bool HasPackageIdentity()
        {
            int length = 0;
            int result = Kernel32NativeMethods.GetCurrentPackageFullName(ref length, null);
            if (result == AppModelErrorNoPackage)
            {
                return false;
            }

            if (result == ErrorInsufficientBuffer && length > 0)
            {
                var packageFullName = new StringBuilder(length);
                return Kernel32NativeMethods.GetCurrentPackageFullName(ref length, packageFullName) == 0;
            }

            return result == 0;
        }
    }
}
