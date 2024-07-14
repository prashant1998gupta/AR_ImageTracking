using UnityEngine.EventSystems;
 
//This script is used as a workaround to the unresponsive Unity UI in Safari and Brave caused by a Unity bug
//If you already upgraded to Unity 2022, you may no longer need this
//https://forum.unity.com/threads/ui-button-stops-working-permanently-after-switching-tabs-on-mobile-safari.1029688/
namespace Imagine.WebAR.Samples
{
    public class CustomEventSystem : EventSystem
    {
        protected override void OnApplicationFocus(bool hasFocus)
        {
            // Do Nothing
        }
    }
}