using System;
using System.Reflection;

namespace Steamworks
{
    public static class SteamExtensions
    {
        public enum OverlayToStoreFlag
        {
            None,
            AddToCart,
            AddToCartAndShow
        }


        public static void OpenStoreOverlay(AppId id, OverlayToStoreFlag overlayToStoreFlag)
        {
            // Get SteamFriends.Internal
            var steamFriendsType = typeof(SteamFriends);
            var internalProperty =
                steamFriendsType.GetProperty("Internal", BindingFlags.NonPublic | BindingFlags.Static);
            var internalValue = internalProperty.GetValue(null);
            var internalType = internalValue.GetType();

            // Convert the OverlayToStoreFlag
            var overlayToStoreFlagType = internalType.Assembly.GetType("Steamworks.OverlayToStoreFlag");
            var flagValue = (int) overlayToStoreFlag;
            var flag = Enum.ToObject(overlayToStoreFlagType, flagValue);

            // Call the ActivateGameOverlayToStore method
            var activateGameOverlayToStoreMethod = internalType.GetMethod("ActivateGameOverlayToStore",
                BindingFlags.Public | BindingFlags.Instance);
            activateGameOverlayToStoreMethod.Invoke(internalValue, new[] {(AppId) id.Value, flag});
        }
    }
}