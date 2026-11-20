using System.Buffers;
using DeskBox.Platform;

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
    public bool IsMicrosoftStore => CurrentChannel == AppDistributionChannel.MicrosoftStore;
    public string ChannelName => IsMicrosoftStore ? "Microsoft Store" : "Direct";

    public string? TryGetPackagedAppUserModelId() =>
        IsPackaged ? PackageIdentityHelper.TryGetApplicationUserModelId() : null;

    private static class PackageIdentityHelper
    {
        private const int AppModelErrorNoPackage = 15700;
        private const int ErrorInsufficientBuffer = 122;

        public static unsafe bool HasPackageIdentity()
        {
            uint length = 0;
            int result = Kernel32NativeMethods.GetCurrentPackageFullName(ref length, null);
            if (result == AppModelErrorNoPackage)
            {
                return false;
            }

            if (result == ErrorInsufficientBuffer && length > 0 && length <= 4096)
            {
                char[]? rented = null;
                Span<char> buffer = length <= 256
                    ? stackalloc char[(int)length]
                    : (rented = ArrayPool<char>.Shared.Rent((int)length)).AsSpan(0, (int)length);
                try
                {
                    fixed (char* ptr = buffer)
                    {
                        return Kernel32NativeMethods.GetCurrentPackageFullName(ref length, ptr) == 0;
                    }
                }
                finally
                {
                    if (rented is not null)
                    {
                        ArrayPool<char>.Shared.Return(rented);
                    }
                }
            }

            return result == 0;
        }

        public static unsafe string? TryGetApplicationUserModelId()
        {
            uint length = 0;
            int result = Kernel32NativeMethods.GetCurrentApplicationUserModelId(ref length, null);
            if (result != ErrorInsufficientBuffer || length == 0 || length > 4096)
            {
                return null;
            }

            char[]? rented = null;
            Span<char> buffer = length <= 256
                ? stackalloc char[(int)length]
                : (rented = ArrayPool<char>.Shared.Rent((int)length)).AsSpan(0, (int)length);
            try
            {
                fixed (char* ptr = buffer)
                {
                    if (Kernel32NativeMethods.GetCurrentApplicationUserModelId(ref length, ptr) != 0)
                    {
                        return null;
                    }
                }

                ReadOnlySpan<char> valid = buffer[..(int)Math.Min(length, (uint)buffer.Length)];
                int nullIndex = valid.IndexOf('\0');
                if (nullIndex >= 0)
                {
                    valid = valid[..nullIndex];
                }

                valid = valid.Trim();
                return valid.IsEmpty ? null : new string(valid);
            }
            finally
            {
                if (rented is not null)
                {
                    ArrayPool<char>.Shared.Return(rented);
                }
            }
        }
    }
}
